# Shell crash recovery failure: work area lost, Windows key opens built-in Start

Reported symptoms: maximized/snapped windows extend under/behind RetroBar's bar
(the work area no longer excludes it), and pressing the physical Windows key
opens the Windows 11 built-in Start menu instead of Open-Shell's classic menu.

Both trace back to one event and one pre-existing, already-known constraint
this install was violating: **RetroBar was running elevated.**

---

## Root event

```
8/27/2026 7:07:36 AM  Winlogon (Event ID 1002, Application log):
"The shell stopped unexpectedly and C:\WINDOWS\system32\userinit.exe was restarted."
```

`explorer.exe` (the registered shell) crashed and Winlogon auto-relaunched it.
A fresh `explorer.exe` (PID 65696) and a fresh `StartMenuExperienceHost.exe`
(PID 70316, 7:07:38 AM) came up immediately after. Two things that should have
reacted to that did **not** restart alongside it:

- **RetroBar** — process was already 3 days old (started 8/24 7:07:33 AM)
- **Open-Shell's `StartMenu.exe`** — process was already 6 days old (started 8/21 12:24:47 PM)

Retrieved with:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date "2026-08-27 07:00:00"); EndTime=(Get-Date "2026-08-27 07:15:00")} |
    Select-Object TimeCreated, Id, ProviderName, LevelDisplayName, Message | Format-List
```

Cross-checked against live process start times:

```powershell
Get-Process -Name RetroBar, explorer, StartMenu, StartMenuExperienceHost -ErrorAction SilentlyContinue |
    Select-Object ProcessName, Id, StartTime
```

---

## Why the Windows key opens the built-in Start menu

RetroBar has no code path that hooks the bare Windows key at all — only chords
(Win+B, Win+D, Win+F1, Win+[0-9], Win+Shift+F1-9) go through `HotkeyManager.cs`
/ `LowLevelKeyboardHook.cs`. A plain Win press falls straight through to
whatever the OS already has registered for it — normally Open-Shell's
`StartMenu.exe`, which installs its own low-level hook to intercept the raw
Windows key before `StartMenuExperienceHost` sees it.

`StartMenu.exe` (PID 7552) never restarted after the 7:07:36 AM shell crash, so
it's out of sync with the explorer.exe/StartMenuExperienceHost pair that *did*
come up fresh. The newly-registered built-in Start host wins the race instead.

**Fix:** restart Open-Shell (or just the `StartMenu.exe` process) so it
re-attaches to the current session.

---

## Why the work area stopped excluding RetroBar

Live-probed directly while reproducing:

```powershell
Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@
$rect = New-Object Win32.Native+RECT
[Win32.Native]::SystemParametersInfo(0x0030, 0, [ref]$rect, 0) # SPI_GETWORKAREA
$rect
```

Result: `(0,0)-(1536,960)` — the full screen. RetroBar's own taskbar window was
confirmed visible at `(0,930)-(1536,960)` (bottom 30px), and Explorer's
`Shell_TrayWnd` was confirmed hidden — so RetroBar's basic hide-taskbar logic
was working, but the AppBar work-area reservation (`SHAppBarMessage(ABM_SETPOS)`,
`AppBarManager.cs`) was never re-applied after the crash.

The designed recovery path for exactly this case is `ExplorerMonitor.cs`: it
listens for the registered `"TaskbarCreated"` broadcast that a respawned
explorer sends, and calls `WindowManager.ReopenTaskbars()`
(`WindowManager.cs:60`) to re-hide Explorer's bar and re-run AppBar
registration. Grepping the live log for that path shows it firing correctly in
an earlier session, but never once in the session covering the crash:

```powershell
Select-String -Path 'C:\Users\admin\AppData\Local\RetroBar\Logs\<current-log>.log' `
    -Pattern 'TaskbarCreated|ReopenTaskbars|ABM_SETPOS|ExplorerMonitor'
```

`"ExplorerMonitor: Received TaskbarCreated"` (an Info-level line logged
unconditionally whenever the message arrives) does not appear anywhere in the
crash-session log — not even for the 7:07:36 AM event. It does appear (twice)
in the log from the previous, non-elevated session. `ABM_SETPOS` likewise never
fires again after the crash in this session, though it fires normally earlier.

### Root cause: RetroBar was running at High integrity

```powershell
# TokenIntegrityLevel (25) via OpenProcessToken/GetTokenInformation, one call per PID
# S-1-16-4096=Low  8192=Medium  8448=MediumPlus  12288=High  16384=System
```

Results:

| Process | PID | Integrity |
|---|---|---|
| RetroBar | 63924 | **High (S-1-16-12288)** |
| explorer.exe | 65696 | Medium (S-1-16-8192) |
| Open-Shell `StartMenu.exe` | 7552 | Medium (S-1-16-8192) |

`app.manifest` requests `asInvoker` (not elevated) — RetroBar doesn't ask to
run elevated. It ended up High anyway because whatever launched it this time
(an elevated dev/debug session, going by the parent PID having already exited
by the time this was checked) was itself elevated, and `asInvoker` just
inherits the launcher's level.

A Medium-integrity process (explorer.exe) cannot send window messages —
including the `HWND_BROADCAST` "TaskbarCreated" registered message — to a
High-integrity window (RetroBar's `ExplorerMonitorWindow`) unless the receiver
opts in via `ChangeWindowMessageFilterEx`. RetroBar does not do this. UIPI
silently drops the broadcast, `ExplorerMonitor.WndProc` never sees
`WM_TASKBARCREATEDMESSAGE`, `ReopenTaskbars()` never runs, and the AppBar/work
area reservation is never reasserted. (The taskbar still *looked* mostly
correct — Explorer's bar stayed hidden — because that particular re-hide is
also driven by the ordinary shell-hook window-creation path, which isn't a
cross-integrity broadcast and isn't blocked the same way.)

This is not a new discovery — it's a documented constraint already called out
in [`missing-tray-icons.md`](missing-tray-icons.md):

> RetroBar must run in medium integrity level. Do not run elevated.

This install was violating it, silently, for at least the last 3 days.

**Fix:** don't launch RetroBar from an elevated process/terminal. Restarting
RetroBar from a normal (Medium) session re-registers the AppBar cleanly.

---

## Diagnosing why the log looked empty (false alarm)

The first pass at this investigation found the current session's log file
(`Logs\2026-08-24_070747281.log`) reporting as 0 bytes via:

```powershell
Get-ChildItem -Path 'C:\Users\admin\AppData\Local\RetroBar\Logs' -File |
    Select-Object FullName, Length, LastWriteTime
```

despite RetroBar having been running continuously for 3 days. Revisiting the
same file later in the same investigation showed it at 2.1 MB and actively
growing, with content going all the way back to the 7:07:33 AM startup
timestamp — so logging was never actually broken; nothing was lost.

What happened: the file is held open by RetroBar (confirmed — an exclusive
open attempt from another process fails with "used by another process," while
a shared read succeeds and returns the real length):

```powershell
[System.IO.File]::Open($path, 'Open', 'Read', 'None')   # -> throws, in use
[System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite') # -> succeeds, real length
```

A plain directory listing (`Get-ChildItem`/`FindFirstFile`) reads the
directory's cached metadata entry for the file, not its live length; for a
file another process holds open and keeps appending to, that cached size can
lag behind the real one until something forces a refresh (the handle closing,
an explicit flush reaching the directory entry, or another process touching
the file, as the exclusive-open probe here likely did). `FileLog.cs` does call
`Flush()` after every write, which flushes the file's *data*, but that doesn't
guarantee another process's directory-listing cache is immediately current.

This is a benign Windows/NTFS metadata-visibility artifact, not a defect in
`ManagedShellLogger`/`FileLog`. Lesson for next time: don't trust a single
`Get-ChildItem` size read on a file a live process has open — re-check with a
shared-mode open, or `Get-Content -Tail`, before concluding logging died.

---

## Summary / remediation (this incident)

1. Don't run RetroBar elevated — this is the actual root cause of the
   `ExplorerMonitor` failure and, transitively, the lost work-area reservation.
   Already documented in [`missing-tray-icons.md`](missing-tray-icons.md);
   this incident is a second, more consequential instance of the same rule
   being violated.
2. Restart RetroBar (from a non-elevated context) to re-register the AppBar
   and reclaim the work area.
3. Restart Open-Shell / `StartMenu.exe` to resync its Windows-key hook with
   the current explorer.exe/StartMenuExperienceHost.

---

## Root-causing *how* RetroBar ended up elevated

The remediation above fixes this occurrence. This section is about why it
happened at all, so it doesn't recur.

### Command used to determine RetroBar's integrity/elevation

Two independent checks were used, both querying the token of RetroBar's
already-running process from an unelevated script — this is the same
"PROCESS_QUERY_LIMITED_INFORMATION + TOKEN_QUERY work across integrity levels"
trick Task Manager uses, and it's also already implemented once in this
codebase (`RetroBar/Utilities/ElevationHelper.cs:24`, `IsWindowElevated`,
used today only in `WorkspaceManager.cs:512` to check *other* windows, never
RetroBar's own process).

**Simple elevated/not-elevated check** (mirrors `ElevationHelper.cs` exactly —
`TokenElevation`, `TOKEN_INFORMATION_CLASS` 20):

```powershell
Add-Type -Namespace Tok -Name Helper -MemberDefinition @'
[DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
[DllImport("advapi32.dll", SetLastError=true)] public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);
[DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr hObject);
'@
function Test-ProcElevated([int]$procId) {
    $hProc = [Tok.Helper]::OpenProcess(0x0400, $false, $procId)   # PROCESS_QUERY_LIMITED_INFORMATION
    $hTok = [IntPtr]::Zero
    [Tok.Helper]::OpenProcessToken($hProc, 0x0008, [ref]$hTok) | Out-Null   # TOKEN_QUERY
    $buf = [Runtime.InteropServices.Marshal]::AllocHGlobal(4)
    [Tok.Helper]::GetTokenInformation($hTok, 20, $buf, 4, [ref]([int]0)) | Out-Null   # TokenElevation
    [bool][Runtime.InteropServices.Marshal]::ReadInt32($buf)
}
Test-ProcElevated (Get-Process RetroBar).Id   # -> True
Test-ProcElevated (Get-Process explorer).Id   # -> False
```

**Integrity-level SID check** (finer-grained; `TokenIntegrityLevel`, class 25 —
used first, during the initial investigation):

```powershell
# ... same Add-Type shape, but call GetTokenInformation with class 25 (TokenIntegrityLevel)
# and read the RID off the returned SID_AND_ATTRIBUTES' Sid:
#   S-1-16-4096=Low  S-1-16-8192=Medium  S-1-16-12288=High  S-1-16-16384=System
```

Result at the time of the incident (and again, reproduced fresh just now —
this is a persistent, ongoing state, not a one-off blip):

| Process | Integrity | Elevated |
|---|---|---|
| RetroBar | High (S-1-16-12288) | **True** |
| explorer.exe | Medium (S-1-16-8192) | False |

### Is the autostart entry (Run key) the cause? No.

RetroBar autostarts via a plain string value in the registry, not a scheduled
task or a shortcut with a compatibility shim — both of which were checked and
ruled out as contributing mechanisms:

```powershell
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' | Select RetroBar
# RetroBar : "C:\Users\admin\source\repos\remap_win_e\RetroBar\RetroBar\bin\Debug\net6.0-windows10.0.19041.0\RetroBar.exe"

Get-ScheduledTask | Where-Object { ($_.Actions.Execute) -match 'RetroBar' }
# (no results - RetroBar is not launched via Task Scheduler, so no <RunLevel>HighestAvailable</RunLevel> is in play)

Get-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers' |
    Select-Object -Property * | Where-Object { $_ -match 'RetroBar' }
# (no RUNASADMIN compatibility shim set for RetroBar.exe or Open-Shell's StartMenu.exe either)
```

A raw `HKCU\...\Run` string value is executed by explorer's own startup logic
via `CreateProcess`, at whatever integrity level explorer itself is running at
(Medium, for a normal split-token admin logon) — there's no verb, no shim, no
scheduled-task run-level to elevate it. `app.manifest` also requests
`asInvoker`, not `requireAdministrator`/`highestAvailable`
(`RetroBar/app.manifest:19`), so the exe doesn't self-elevate via UAC either.

This is corroborated by timing: `(Get-CimInstance Win32_OperatingSystem).LastBootUpTime`
shows the machine has been up since **8/21 12:23:54 PM** — no reboot, and no
fresh interactive logon, has happened since. The Run key only fires once, at
logon. RetroBar's *first* launch that session (8/21 12:24:47 PM) is exactly
the session that had matching (Medium) integrity throughout and where
`ExplorerMonitor` worked correctly (see the two successful
`"ExplorerMonitor: Received TaskbarCreated"` lines in that log). The elevated
instance (PID 63924, started 8/24 7:07:33 AM — three days into the same
still-not-rebooted session) was necessarily a **manual relaunch**, not a fresh
logon — so it did not go through the Run key at all.

**Conclusion: the autostart mechanism is not the problem and starts RetroBar
at the correct (Medium) integrity.** The elevation was introduced by whatever
manually relaunched RetroBar mid-session.

### The actual mechanism: `scripts\build.ps1 -Relaunch` inherits the caller's shell

`scripts/build.ps1` is this repo's normal dev workflow for restarting RetroBar
after a code change (`-Relaunch`, and `-Background`/`-Install`/`-Uninstall`
which follow the same pattern). Its launch step is a plain, unguarded
`Start-Process`:

```powershell
function Start-RetroBar([string]$cfg, [string]$fw) {
    $exe = Find-RetroBarExe $cfg $fw
    ...
    Start-Process $exe          # <- no -Verb, no elevation check; inherits caller's token
}
```

`Start-Process` with no `-Verb RunAs` launches the child at the **same**
integrity level as the PowerShell session that ran the script — exactly the
same inheritance rule that made the Run key safe, except here the "caller" is
whatever terminal the developer happened to be using.

The same script explicitly documents that two of its other flags need
elevation:

> `-SetupDebugger` ... Requires Administrator privileges (writes to HKLM).
> `-RemoveDebugger` ... Requires Administrator privileges.

This is the gap: a developer opens an elevated terminal once to run
`.\build.ps1 -SetupDebugger` (or `-RemoveDebugger`), then reuses that same
still-open elevated window later for an ordinary `.\build.ps1 -Relaunch` (or
`-Background`) — a completely natural thing to do, since nothing in the
script warns that it matters. `Start-RetroBar`'s `Start-Process $exe` then
silently inherits High integrity, with no error, no warning, and (per the
`shell-crash-recovery-failure.md` log evidence above) no visible symptom until
something like an unrelated explorer.exe crash exposes it days later via a
completely different failure path.

This matches the incident precisely: RetroBar's own explorer-kill-and-relaunch
dance for Win+B (`ExplorerHotkeyStealer`) ran at 8/24 7:07:46 AM, right after
RetroBar's own restart at 7:07:33 AM — consistent with a `-Relaunch` (which
calls `Stop-RetroBar` then rebuilds then `Start-RetroBar`) rather than a
system event.

### Recommended fixes (not yet implemented)

1. **Guard `scripts\build.ps1`.** Before `Start-RetroBar` actually launches
   the exe (in `-Relaunch`, `-Background`, `-Install`, `-Uninstall`, and the
   plain `-Launch` path), check whether the current process is elevated
   (`[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)`)
   and refuse (or loudly warn) instead of silently launching RetroBar
   elevated. This closes the actual hole that caused this incident.
2. **Add a startup self-check in RetroBar itself**, as defense in depth for
   every other way it could end up elevated (manual double-click from an
   elevated Explorer window, an elevated scheduled task added later, etc).
   The building block already exists —
   `ElevationHelper.IsWindowElevated(hwnd)` (`RetroBar/Utilities/ElevationHelper.cs:24`)
   — it's just never pointed at RetroBar's own window. Call it once in
   `App.xaml.cs` startup (e.g. right after the main window handle exists) and:
   - Always `ShellLogger.Warning(...)` immediately if elevated, so this is
     visible in the log from the very first line next time instead of
     requiring an external token-integrity probe to discover, and
   - Consider surfacing it in the UI (a one-time balloon/notice) since this
     silently degrades several features (`missing-tray-icons.md`'s tray
     pre-population, this incident's `ExplorerMonitor`/work-area recovery)
     with no in-app indication anything is wrong.
3. **Optional, narrower mitigation:** call `ChangeWindowMessageFilterEx` for
   the `"TaskbarCreated"` message ID on `ExplorerMonitorWindow`'s handle so
   the broadcast still gets through even if RetroBar does end up elevated
   again despite (1) and (2). This only patches the `ExplorerMonitor` symptom
   specifically — running elevated has other documented side effects
   (`missing-tray-icons.md`) it wouldn't address, so it's a backstop, not a
   substitute for (1)/(2).
