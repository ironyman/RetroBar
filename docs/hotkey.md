Early path (restarts explorer if needed - safe)
App()                                          // App.xaml.cs:36
  └─ new EarlyWinBReservation()                // App.xaml.cs:41
       ├─ TryUnregisterFromProcess("ShellExperienceHost", KEY_B)  // EarlyWinBReservation.cs:29
       ├─ TryUnregisterFromProcess("sihost", KEY_B)              // :30
       ├─ TryUnregisterFromProcess("explorer", KEY_B)            // :31
       ├─ TryRegister() → RegisterHotKey(...)                    // :33, :47-48
       │   └─ FAILS (explorer still holds Win+B)
       └─ ExplorerHotkeyStealer.StealWinBFromExplorer(() => IsHeld = TryRegister())  // :40
            ├─ p.Kill() / p.WaitForExit()    // ExplorerHotkeyStealer.cs:41
            ├─ registerHotkey()               // :47  → RegisterHotKey on ReservationWindow
            └─ Process.Start(ExplorerPath)    // :49  → Explorer starts as shell (no Shell_TrayWnd yet)
  // Shell_TrayWnd does NOT exist yet at this point, so explorer correctly takes over as shell
  // instead of opening a File Explorer browser window.

Late path (no restart - safe)
App()                                          // App.xaml.cs:36
  └─ new EarlyWinBReservation()                // :41  → succeeds, IsHeld=true, Win+B held on ReservationWindow
  └─ _shellManager = SetupManagedShell()       // :43  → ShellManager ctor
       └─ TrayService ctor                     // ShellManager.cs:48
            └─ RegisterTrayWnd()              // TrayService.cs:299
                 └─ CreateWindowEx("Shell_TrayWnd")  // Shell_TrayWnd NOW EXISTS
  └─ _hotkeyManager = new HotkeyManager(_earlyWinBReservation)  // :54
       └─ dispatcher.BeginInvoke(ApplicationIdle, InitializeHotkeys)  // HotkeyManager.cs:44
  // ... dispatcher runs, ApplicationIdle fires ...
  InitializeHotkeys()                          // HotkeyManager.cs:52
    ├─ _earlyWinBReservation.Release()         // :61  → UnregisterHotKey + DestroyHandle on ReservationWindow,
    │                                              freeing Win+B right before HotkeyListenerWindow claims it
    └─ RegisterSystemHotkeys()                 // :65  → HotkeyListenerWindow.cs:285
         ├─ EnsureExplorerResourcesLoaded()    // :290  → TryFindTrayWindows, TryBuildHotkeyTable
         ├─ TryUnregisterFromProcess("ShellExperienceHost", KEY_B)  // :294
         ├─ TryUnregisterFromProcess("sihost", KEY_B)              // :295
         ├─ TryUnregisterFromProcess("explorer", KEY_B)            // :296
         └─ RegisterWinKey(KEY_B, HOTKEY_ID_FOCUS_TRAY)            // :299
              ├─ TryUnregisterTrayHotkey()      // :445  → SendMessage WMTRAY_UNREGISTERHOTKEY
              └─ RegisterHotKey(...)            // :447  → registers on HotkeyListenerWindow
                   ├─ SUCCEEDS (normal case): Win+B handed off from ReservationWindow to
                   │    HotkeyListenerWindow with only a few instructions' gap.
                   └─ FAILS (rare): sihost/explorer re-grabbed Win+B in the gap between Release()
                        and RegisterHotKey(). HotkeyManager logs a warning and leaves Win+B
                        unregistered (HotkeyManager.cs:301-312). It deliberately does NOT call
                        ExplorerHotkeyStealer here — by this point Shell_TrayWnd exists, so
                        killing/restarting explorer would make it open a File Explorer browser
                        window instead of resuming as the shell. ExplorerHotkeyStealer is only
                        ever invoked from the early path, before Shell_TrayWnd exists.

The critical invariant: ExplorerHotkeyStealer.StealWinBFromExplorer is called from exactly one
place (EarlyWinBReservation.cs:40), which always runs before SetupManagedShell() creates
Shell_TrayWnd. The late path only ever transfers an already-held Win+B from ReservationWindow to
HotkeyListenerWindow (or gives up); it never restarts explorer.


Another issue

on reboot retrobar starts on startup but retrobar doesn't appear above windows task bar and explore browser opens


The root cause is almost certainly a process-boundary race between EarlyWinBReservation's explorer kill/relaunch and App.xaml.cs's IsAppRunningAsShell check, and it's wired into nearly every behavioral branch in the app:

1. App() ctor runs new EarlyWinBReservation() first (App.xaml.cs:41). When Win+B is already held (the path your pasted log shows), this calls ExplorerHotkeyStealer.StealWinBFromExplorer, which kills explorer.exe, registers the hotkey, then calls Process.Start(ExplorerPath) — but returns immediately, without waiting for the new explorer.exe to actually finish initializing or call SetShellWindow().
2. Immediately after, SetupManagedShell() runs (App.xaml.cs:126, now :128): EnvironmentHelper.IsAppRunningAsShell = GetShellWindow() == IntPtr.Zero. If the relaunched explorer.exe hasn't gotten around to calling SetShellWindow() yet, this reads 0x0 and wrongly concludes RetroBar itself is the shell.
3. IsAppRunningAsShell is a static flag read everywhere (ExplorerHelper.HideTaskbar/ShowTaskbar, AppBarManager, AppBarWindow, TrayService, ShellHelper, NotificationArea) — once it's wrong, it stays wrong for the whole session. This directly explains why the usual "hide the Windows taskbar" logic does nothing on a bad boot: ExplorerHelper.HideTaskbar() is gated by if (!EnvironmentHelper.IsAppRunningAsShell) and silently no-ops.
4. Separately, ShellManager's ctor (called right after, in the same method) creates ManagedShell's own fake Shell_TrayWnd fast (no disk I/O). If the relaunched explorer.exe is slow to reach its own "does a shell already exist?" check — plausible under boot-time CPU/disk contention — it finds that fake tray window already there and opens as a plain File Explorer browser window instead of becoming the shell, exactly matching the existing code comments' worry, just defeated by wall-clock timing rather than code order.

Both symptoms (no taskbar hiding, stray browser window) trace back to the same race, and it only shows up at boot because that's when explorer.exe's relaunch is slow relative to RetroBar's own (cheap, in-process) steps. A manual restart post-boot is fast enough that explorer usually wins the race.

What I changed (logging only, no behavior change)

- New RetroBar/Utilities/ShellTrayWindowDiagnostics.cs — EnumWindows-based scan that logs every Shell_TrayWnd window found, with HWND/PID/process name (or logs "none found").
- ExplorerHotkeyStealer.cs — logs GetShellWindow() and explorer PIDs before kill, a tray-window snapshot right after kill (before registering the hotkey), and the relaunched PID + GetShellWindow() + a tray-window snapshot immediately after Process.Start.
- EarlyWinBReservation.cs — logs GetShellWindow() and a tray-window snapshot at both the start and end of the constructor.
- App.xaml.cs SetupManagedShell() — logs GetShellWindow() and the resulting IsAppRunningAsShell value (the critical measurement), a tray-window snapshot just before that computation, and another snapshot right after ShellManager is constructed.
- ExplorerMonitor.cs — added a log line on WM_TASKBARCREATEDMESSAGE (previously silent) showing GetShellWindow(), the current IsAppRunningAsShell, and a tray-window snapshot, so a delayed/duplicate explorer shell recovery shows up in the log too.

These changes compile cleanly. To pick them up, RetroBar needs to be rebuilt and restarted (it's currently running and locking the output DLLs) — let me know if you want me to do that now, and then reboot to capture the next boot's log with this new detail.
