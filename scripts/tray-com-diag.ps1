#Requires -Version 5.1
<#
.SYNOPSIS
    Diagnose (and optionally repair) the COM registration that RetroBar/ManagedShell
    uses to enumerate Explorer's existing notification-area icons.

.DESCRIPTION
    When RetroBar runs alongside Explorer (i.e. NOT as the shell) it cannot see the
    tray icons Explorer already owns by listening for new NIM_ADD messages - those go
    to Explorer's Shell_TrayWnd, not RetroBar's. To show pre-existing icons it must
    read them out of Explorer once at startup. ManagedShell tries two ways:

      1. Classic toolbar scraping: Shell_TrayWnd -> TrayNotifyWnd -> SysPager ->
         ToolbarWindow32, then TB_BUTTONCOUNT/TB_GETBUTTON. Win11 has no such toolbar.
      2. COM: CoCreate CLSID_TrayNotify {25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD} and call
         ITrayNotify::RegisterCallback, which synchronously calls back each existing icon.

    On Windows 11 24H2+ (e.g. build 26200) path 1 does not exist and path 2 fails with
    0x80040154 REGDB_E_CLASSNOTREG, so pre-population returns 0 icons and only icons that
    register directly to RetroBar (such as RetroBar's own injected network icon) appear.

    -------------------------------------------------------------------------------
    The registry keys involved, and what they mean:

    HKLM\SOFTWARE\Classes\CLSID\{25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD}
        The COM class registration for CLSID_TrayNotify.
        (default)        Friendly name, "CLSID_TrayNotify".
        AppId            Links the class to an AppID (below) that holds activation/
                         security policy (identity, surrogate, service).
        InprocServer32   [normally ABSENT here] If present, a DLL loaded into the
                         caller's process to implement the class, plus ThreadingModel.
        LocalServer32    [normally ABSENT here] If present, an EXE that hosts the class
                         out-of-process.

    HKLM\SOFTWARE\Classes\AppID\{a2b77517-6d12-4c60-b0c6-725e971ec8fe}
        AppID for TrayNotify.
        (default)        "TrayNotify".
        RunAs            "Interactive User" - when activated as a local server, run in
                         the interactive user's session.

    Because neither InprocServer32 nor LocalServer32 exists, CLSID_TrayNotify has no
    *static* server. Historically Explorer registered the class object at runtime via
    CoRegisterClassObject, so CoCreate would bind to the running Explorer. On Win11
    24H2+ Explorer no longer registers it, so there is no server at all -> CLASSNOTREG.

    IMPORTANT: There is no in-box DLL/EXE that implements a CoCreatable CLSID_TrayNotify
    on these builds, so a registry edit alone cannot restore functionality - pointing
    InprocServer32 at a binary that does not implement the class will just change the
    failure (CLASSNOTREG -> a load/E_NOINTERFACE error). The real fix is code-side
    (enumerate the Win11 tray a different way). The -Fix switch here exists only for
    experimentation with a server DLL you supply, and always backs up first.

.PARAMETER Diagnose
    (Default) Inspect the CLSID/AppID registration and attempt a CoCreate from an STA,
    reporting the HRESULT. Read-only.

.PARAMETER Backup
    Export the CLSID and AppID keys to a .reg file (path from -Path, or a timestamped
    file under the repo's bin\). Use before any change. Read-only w.r.t. the registry.

.PARAMETER Restore
    Re-import a .reg backup produced by -Backup (path from -Path). Requires admin.

.PARAMETER Fix
    EXPERIMENTAL. Back up, then register -ServerDll as the InprocServer32 for
    CLSID_TrayNotify (ThreadingModel=Apartment). Requires admin and -ServerDll. Refuses
    to run without -ServerDll because no in-box server exists to point at. Reversible
    with -Restore.

.PARAMETER ServerDll
    Path to a DLL that actually implements CLSID_TrayNotify, for use with -Fix.

.PARAMETER Path
    File path for -Backup (output) or -Restore (input).

.EXAMPLE
    .\tray-com-diag.ps1                       # diagnose (read-only)
    .\tray-com-diag.ps1 -Backup               # export keys to bin\tray-com-backup_<ts>.reg
    .\tray-com-diag.ps1 -Restore -Path x.reg  # restore from backup (admin)
    .\tray-com-diag.ps1 -Fix -ServerDll C:\path\to\trayhost.dll   # experimental (admin)
#>
[CmdletBinding(DefaultParameterSetName = 'Diagnose')]
param(
    [Parameter(ParameterSetName = 'Diagnose')] [switch]$Diagnose,
    [Parameter(ParameterSetName = 'Backup')]   [switch]$Backup,
    [Parameter(ParameterSetName = 'Restore')]  [switch]$Restore,
    [Parameter(ParameterSetName = 'Fix')]      [switch]$Fix,
    [Parameter(ParameterSetName = 'Fix')]      [string]$ServerDll,
    [Parameter(ParameterSetName = 'Backup')]
    [Parameter(ParameterSetName = 'Restore')]  [string]$Path
)

$ErrorActionPreference = 'Stop'

$ClsidGuid = '25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD'
$AppIdGuid = 'a2b77517-6d12-4c60-b0c6-725e971ec8fe'
$ClsidKey  = "HKLM:\SOFTWARE\Classes\CLSID\{$ClsidGuid}"
$AppIdKey  = "HKLM:\SOFTWARE\Classes\AppID\{$AppIdGuid}"
$ClsidReg  = "HKLM\SOFTWARE\Classes\CLSID\{$ClsidGuid}"
$AppIdReg  = "HKLM\SOFTWARE\Classes\AppID\{$AppIdGuid}"
$RegdbClassNotReg = '0x80040154'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-Key([string]$psPath, [string]$label) {
    Write-Host "`n[$label] $psPath" -ForegroundColor Cyan
    if (-not (Test-Path $psPath)) { Write-Host "  (key does not exist)" -ForegroundColor Yellow; return }
    $props = Get-ItemProperty $psPath
    foreach ($p in $props.PSObject.Properties) {
        if ($p.Name -like 'PS*') { continue }
        $name = if ($p.Name -eq '(default)') { '(default)' } else { $p.Name }
        Write-Host ("    {0,-16} = {1}" -f $name, $p.Value)
    }
    $sub = Get-ChildItem $psPath -ErrorAction SilentlyContinue | Select-Object -Expand PSChildName
    if ($sub) { Write-Host "    subkeys: $($sub -join ', ')" }
    else      { Write-Host "    subkeys: (none - no InprocServer32/LocalServer32)" -ForegroundColor Yellow }
}

function Invoke-CoCreateTest {
    Write-Host "`nCoCreate CLSID_TrayNotify (INPROC|LOCAL|REMOTE) from an STA thread:" -ForegroundColor Cyan
    $code = @'
using System;
using System.Runtime.InteropServices;
public static class TrayCoTest {
  [DllImport("ole32.dll")]
  static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
  public static string Try() {
    Guid clsid = new Guid("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD");
    Guid iunk  = new Guid("00000000-0000-0000-C000-000000000046");
    IntPtr ppv;
    int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x1|0x4|0x10, ref iunk, out ppv);
    if (ppv != IntPtr.Zero) Marshal.Release(ppv);
    return "0x" + hr.ToString("X8");
  }
}
'@
    if (-not ('TrayCoTest' -as [type])) { Add-Type -TypeDefinition $code }
    $rs = [runspacefactory]::CreateRunspace(); $rs.ApartmentState = 'STA'; $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript('[TrayCoTest]::Try()')
    $hr = ($ps.Invoke() | Select-Object -First 1)
    $ps.Dispose(); $rs.Close()

    $hrColor = if ($hr -eq '0x00000000') { 'Green' } else { 'Yellow' }
    Write-Host "    HRESULT = $hr" -ForegroundColor $hrColor
    switch ($hr) {
        '0x00000000'      { Write-Host "    -> Success: CLSID_TrayNotify is activatable; icon enumeration should work." -ForegroundColor Green }
        $RegdbClassNotReg { Write-Host "    -> REGDB_E_CLASSNOTREG: no server is registered/running for this class on this OS build." -ForegroundColor Yellow }
        default           { Write-Host "    -> Non-zero HRESULT; see https://learn.microsoft.com/windows/win32/com/com-error-codes-1" -ForegroundColor Yellow }
    }
    return $hr
}

function Get-DefaultBackupPath {
    $root = Split-Path $PSScriptRoot -Parent
    $binDir = Join-Path $root 'bin'
    if (-not (Test-Path $binDir)) { New-Item -ItemType Directory -Path $binDir -Force | Out-Null }
    Join-Path $binDir ("tray-com-backup_{0}.reg" -f (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
}

function Invoke-Backup([string]$outFile) {
    if (-not $outFile) { $outFile = Get-DefaultBackupPath }
    # reg export writes one key per call; concatenate both into one .reg file.
    $tmp1 = [System.IO.Path]::GetTempFileName()
    $tmp2 = [System.IO.Path]::GetTempFileName()
    & reg.exe export $ClsidReg $tmp1 /y | Out-Null
    & reg.exe export $AppIdReg $tmp2 /y | Out-Null
    $header = 'Windows Registry Editor Version 5.00'
    $body1 = (Get-Content $tmp1 | Select-Object -Skip 1)
    $body2 = (Get-Content $tmp2 | Select-Object -Skip 1)
    Set-Content -Path $outFile -Value (@($header) + $body1 + $body2) -Encoding Unicode
    Remove-Item $tmp1, $tmp2 -Force -ErrorAction SilentlyContinue
    Write-Host "Backed up CLSID + AppID to: $outFile" -ForegroundColor Green
    return $outFile
}

# ---------------------------------------------------------------------------

if ($Backup) {
    Invoke-Backup $Path
    return
}

if ($Restore) {
    if (-not $Path)            { Write-Error "Provide -Path to the .reg backup to restore."; exit 2 }
    if (-not (Test-Path $Path)){ Write-Error "Backup file not found: $Path"; exit 2 }
    if (-not (Test-Admin))     { Write-Error "Restore writes to HKLM and requires an elevated (Administrator) shell."; exit 3 }
    & reg.exe import $Path
    Write-Host "Restored registry from: $Path" -ForegroundColor Green
    return
}

if ($Fix) {
    if (-not (Test-Admin)) { Write-Error "-Fix writes to HKLM and requires an elevated (Administrator) shell."; exit 3 }
    if (-not $ServerDll) {
        Write-Warning "-Fix needs -ServerDll <path-to-dll-that-implements-CLSID_TrayNotify>."
        Write-Host    "No in-box server implements a CoCreatable CLSID_TrayNotify on Win11 24H2+." -ForegroundColor Yellow
        Write-Host    "A registry edit alone cannot restore enumeration; the durable fix is code-side." -ForegroundColor Yellow
        exit 2
    }
    if (-not (Test-Path $ServerDll)) { Write-Error "ServerDll not found: $ServerDll"; exit 2 }

    $backup = Invoke-Backup $null
    Write-Host "Registering InprocServer32 -> $ServerDll (ThreadingModel=Apartment)..." -ForegroundColor Cyan
    $inproc = Join-Path $ClsidKey 'InprocServer32'
    if (-not (Test-Path $inproc)) { New-Item -Path $inproc -Force | Out-Null }
    New-ItemProperty -Path $inproc -Name '(default)'     -Value (Resolve-Path $ServerDll).Path -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $inproc -Name 'ThreadingModel' -Value 'Apartment'                   -PropertyType String -Force | Out-Null
    Write-Host "Done. This is experimental - revert with: .\tray-com-diag.ps1 -Restore -Path `"$backup`"" -ForegroundColor Green
    Write-Host "Re-run diagnosis to see whether the HRESULT changed:" -ForegroundColor Cyan
    [void](Invoke-CoCreateTest)
    return
}

# Default: diagnose (read-only)
Write-Host "RetroBar tray COM diagnosis - CLSID_TrayNotify {$ClsidGuid}" -ForegroundColor White
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "OS: $($os.Caption) build $($os.BuildNumber)"
Show-Key $ClsidKey 'CLSID'
Show-Key $AppIdKey 'AppID'
$hr = Invoke-CoCreateTest

Write-Host "`nSummary:" -ForegroundColor White
if ($hr -eq $RegdbClassNotReg) {
    Write-Host "  CLSID_TrayNotify is not activatable on this build. RetroBar cannot pre-populate" -ForegroundColor Yellow
    Write-Host "  Explorer's existing tray icons via ITrayNotify; only icons that register directly" -ForegroundColor Yellow
    Write-Host "  to RetroBar will appear. This is an OS-compatibility gap, not a settings problem." -ForegroundColor Yellow
    Write-Host "  -Fix can experiment with a server DLL you supply, but the durable fix is code-side." -ForegroundColor Yellow
} elseif ($hr -eq '0x00000000') {
    Write-Host "  CLSID_TrayNotify activates fine here - if icons are still missing the cause is elsewhere." -ForegroundColor Green
}
