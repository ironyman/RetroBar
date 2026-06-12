#Requires -Version 5.1
<#
.SYNOPSIS
    Build RetroBar and its ManagedShell dependencies.

.PARAMETER Target
    Component(s) to build. Accepted values:
      All           - Build everything in dependency order (default)
      RetroBar      - RetroBar only
      ManagedShell  - ManagedShell meta-project
      WindowsTasks  - ManagedShell.WindowsTasks (contains the RegisterTab fix)
      AppBar        - ManagedShell.AppBar
      UWPInterop    - ManagedShell.UWPInterop
      Common        - ManagedShell.Common
      Interop       - ManagedShell.Interop
      WindowsTray   - ManagedShell.WindowsTray
      ShellFolders  - ManagedShell.ShellFolders

.PARAMETER Configuration
    Build configuration: Debug (default) or Release.

.PARAMETER Framework
    Target framework. Defaults to net6.0-windows10.0.19041.0.

.PARAMETER Verbosity
    MSBuild verbosity level passed to dotnet build: quiet, minimal (default), normal, detailed, or diagnostic.

.PARAMETER Background
    Build everything in a background window, then launch RetroBar when the build succeeds.

.PARAMETER Stop
    Kill RetroBar, restore the Windows Explorer taskbar to its normal visible state, and
    disable taskbar auto-hide so Explorer re-appears immediately.

.PARAMETER Relaunch
    Stop RetroBar (same as -Stop), rebuild the specified targets, then start RetroBar again.
    Combines -Stop + build + launch in one step.

.PARAMETER Log
    Tail the RetroBar log. Alone, implies -NoRebuild and tails the current log without
    building or launching. Combined with -Launch, -Relaunch, or -Background, the build
    runs as normal and log tailing begins after RetroBar starts.

.PARAMETER Paths
    Print the paths to RetroBar's data files (settings, logs, themes) and exit.

.PARAMETER Settings
    Open RetroBar's settings.json in the code editor and exit.

.PARAMETER OpenLog
    Open the latest RetroBar log file in the code editor and exit.

.PARAMETER BuildInstaller
    Publish a Release build for x64, x86, and ARM64, then compile the Inno Setup installer
    (installer.iss) to produce bin\RetroBarInstaller.exe. Requires ISCC.exe (Inno Setup 6)
    on PATH or in its default install location.

.PARAMETER UninstallRelease
    Stop RetroBar (if running) and silently run the installed release uninstaller.
    Looks up the Inno Setup uninstall entry in HKCU (per-user install) then HKLM.

.PARAMETER Help
    Show this help message.

.EXAMPLE
    .\build.ps1                                # build everything (foreground, no launch)
    .\build.ps1 -Target WindowsTasks           # rebuild just WindowsTasks
    .\build.ps1 -Configuration Release         # release build of everything
    .\build.ps1 -Verbosity normal              # show full build output
    .\build.ps1 -Verbosity diagnostic          # show maximum build detail
    .\build.ps1 -Background                    # build + launch RetroBar in background window
    .\build.ps1 -Target RetroBar -Background   # rebuild RetroBar then launch it
    .\build.ps1 -Stop                          # kill RetroBar + restore Explorer taskbar
    .\build.ps1 -Relaunch                      # stop, rebuild RetroBar only, start RetroBar
    .\build.ps1 -Target All -Relaunch          # stop, rebuild all, start RetroBar
    .\build.ps1 -Relaunch -NoRebuild           # stop and start RetroBar without rebuilding
    .\build.ps1 -Log                           # tail the current RetroBar log (no build)
    .\build.ps1 -Relaunch -Log                 # stop, rebuild, start, then tail new log
    .\build.ps1 -Launch -Log                   # build, start, then tail new log
    .\build.ps1 -Paths                         # show paths to settings, logs, and themes
    .\build.ps1 -Settings                      # open settings.json in the code editor
    .\build.ps1 -OpenLog                       # open the latest log file in the code editor
    .\build.ps1 -BuildInstaller                # publish Release + compile Inno Setup installer. Use /silent to install automatically.
    .\build.ps1 -UninstallRelease              # stop RetroBar and silently uninstall the release build
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('All', 'RetroBar', 'ManagedShell',
                 'WindowsTasks', 'AppBar', 'UWPInterop',
                 'Common', 'Interop', 'WindowsTray', 'ShellFolders')]
    [string[]]$Target = @('All'),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$Framework = 'net6.0-windows10.0.19041.0',

    [ValidateSet('quiet', 'minimal', 'normal', 'detailed', 'diagnostic')]
    [string]$Verbosity = 'minimal',

    [switch]$Background,
    [switch]$Stop,
    [switch]$Relaunch,
    [switch]$NoRebuild,
    [switch]$Log,
    [switch]$Paths,
    [switch]$Settings,
    [switch]$OpenLog,
    [switch]$BuildInstaller,
    [switch]$UninstallRelease,
    [switch]$Help,

    # Internal: passed by -Background to tell the spawned process to launch RetroBar after building.
    [switch]$Launch
)

$Root = Split-Path $PSScriptRoot -Parent

# -Log alone implies -NoRebuild (just tail; no build or launch)
if ($Log -and -not $Launch -and -not $Relaunch -and -not $Background) {
    $NoRebuild = $true
}

# Dependency order for 'All' — leaves first.
$AllTargets = @(
    'Interop', 'Common', 'ShellFolders', 'UWPInterop',
    'WindowsTray', 'WindowsTasks', 'AppBar', 'ManagedShell', 'RetroBar'
)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Get-ProjectPath([string]$name) {
    $map = @{
        RetroBar     = 'RetroBar\RetroBar.csproj'
        ManagedShell = 'ManagedShell\src\ManagedShell\ManagedShell.csproj'
        WindowsTasks = 'ManagedShell\src\ManagedShell.WindowsTasks\ManagedShell.WindowsTasks.csproj'
        AppBar       = 'ManagedShell\src\ManagedShell.AppBar\ManagedShell.AppBar.csproj'
        UWPInterop   = 'ManagedShell\src\ManagedShell.UWPInterop\ManagedShell.UWPInterop.csproj'
        Common       = 'ManagedShell\src\ManagedShell.Common\ManagedShell.Common.csproj'
        Interop      = 'ManagedShell\src\ManagedShell.Interop\ManagedShell.Interop.csproj'
        WindowsTray  = 'ManagedShell\src\ManagedShell.WindowsTray\ManagedShell.WindowsTray.csproj'
        ShellFolders = 'ManagedShell\src\ManagedShell.ShellFolders\ManagedShell.ShellFolders.csproj'
    }
    if (-not $map.ContainsKey($name)) { throw "Unknown target: $name" }
    Join-Path $Root $map[$name]
}

function Invoke-Build([string[]]$targets, [string]$cfg, [string]$fw, [string]$verbosity) {
    $projects = if ($targets -contains 'All') {
        $AllTargets | ForEach-Object { Get-ProjectPath $_ }
    } else {
        $targets | ForEach-Object { Get-ProjectPath $_ }
    }

    $failed = [System.Collections.Generic.List[string]]::new()
    foreach ($proj in $projects) {
        Write-Host "`n==> dotnet build $(Split-Path $proj -Leaf) -c $cfg -f $fw -v $verbosity" -ForegroundColor Cyan
        dotnet build $proj -c $cfg -f $fw -v $verbosity
        if ($LASTEXITCODE -ne 0) {
            $failed.Add($proj)
            Write-Warning "Build failed: $proj"
        }
    }

    if ($failed.Count -gt 0) {
        Write-Error "Failed projects:`n  $($failed -join "`n  ")"
        $script:BuildOk = $false
        return
    }
    Write-Host "`nAll targets built successfully." -ForegroundColor Green
}

function Start-RetroBar([string]$cfg, [string]$fw) {
    $exe = Join-Path $Root "RetroBar\bin\$cfg\$fw\RetroBar.exe"
    if (-not (Test-Path $exe)) {
        Write-Warning "RetroBar.exe not found at: $exe"
        return
    }
    Start-Process $exe
    Write-Host "RetroBar launched: $exe" -ForegroundColor Green
}

function Start-LogTail {
    param([switch]$WaitForNew)

    $logDir = Join-Path $env:LOCALAPPDATA "RetroBar\Logs"
    $logFile = $null

    if ($WaitForNew) {
        $startTime = [DateTime]::Now
        $deadline  = $startTime.AddSeconds(8)
        Write-Host "Waiting for RetroBar log..." -ForegroundColor DarkCyan
        while ([DateTime]::Now -lt $deadline -and -not $logFile) {
            $logFile = Get-ChildItem $logDir -Filter "*.log" -ErrorAction SilentlyContinue |
                       Where-Object { $_.CreationTime -ge $startTime.AddSeconds(-2) } |
                       Sort-Object CreationTime -Descending |
                       Select-Object -First 1
            if (-not $logFile) { Start-Sleep -Milliseconds 300 }
        }
    }

    if (-not $logFile) {
        $logFile = Get-ChildItem $logDir -Filter "*.log" -ErrorAction SilentlyContinue |
                   Sort-Object LastWriteTime -Descending |
                   Select-Object -First 1
    }

    if (-not $logFile) {
        Write-Warning "No log file found in: $logDir"
        return
    }

    Write-Host "Tailing: $($logFile.FullName)" -ForegroundColor Cyan
    Get-Content $logFile.FullName -Wait -Tail 30
}

function Disable-TaskbarAutoHide {
    if (-not ('TaskbarAutoHide' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TaskbarAutoHide {
    const uint ABM_GETSTATE = 0x00000004;
    const uint ABM_SETSTATE = 0x0000000A;
    const int  ABS_AUTOHIDE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA {
        public int    cbSize;
        public IntPtr hWnd;
        public uint   uCallbackMessage;
        public uint   uEdge;
        public int    rcLeft, rcTop, rcRight, rcBottom;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern uint SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll")]  static extern IntPtr FindWindow(string cls, string wnd);

    public static void Disable() {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        var d = new APPBARDATA();
        d.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(d);
        d.hWnd   = tray;
        uint state = SHAppBarMessage(ABM_GETSTATE, ref d);
        d.lParam  = (IntPtr)((int)state & ~ABS_AUTOHIDE);
        SHAppBarMessage(ABM_SETSTATE, ref d);
    }
}
'@ -Language CSharp
    }
    [TaskbarAutoHide]::Disable()
    Write-Host "Taskbar auto-hide disabled." -ForegroundColor Green
}

function Restore-WindowsTaskbar {
    if (-not ('TaskbarRestore' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TaskbarRestore {
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string wnd);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string wnd);
    [DllImport("user32.dll")] public static extern bool   ShowWindow(IntPtr hWnd, int cmd);
    public const int SW_SHOW = 5;
    public static void Show() {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) ShowWindow(tray, SW_SHOW);
        IntPtr sec = IntPtr.Zero;
        do {
            sec = FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null);
            if (sec != IntPtr.Zero) ShowWindow(sec, SW_SHOW);
        } while (sec != IntPtr.Zero);
    }
}
'@ -Language CSharp
    }
    [TaskbarRestore]::Show()
    Write-Host "Explorer taskbar restored." -ForegroundColor Green
}

function Stop-RetroBar {
    $procs = @(Get-Process -Name 'RetroBar' -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        $procs | Stop-Process -Force
        Write-Host "Stopped $($procs.Count) RetroBar process(es)." -ForegroundColor Yellow
        Start-Sleep -Milliseconds 600
    } else {
        Write-Host "RetroBar is not running." -ForegroundColor DarkYellow
    }
}

function Invoke-Installer {
    $profiles = @('x64', 'x86', 'ARM64')
    $proj = Join-Path $Root 'RetroBar\RetroBar.csproj'

    foreach ($profile in $profiles) {
        # installer.iss reads from RetroBar\bin\Release\net6.0-windows\publish-<arch>
        $publishDir = Join-Path $Root "RetroBar\bin\Release\net6.0-windows\publish-$profile"

        Write-Host "`n==> dotnet publish -p:PublishProfile=$profile -f $Framework" -ForegroundColor Cyan
        # Use the project's actual TFM so the restore+build chain works with SDK 10.
        # Override PublishDir explicitly so the output location is always deterministic
        # regardless of how the pubxml resolves it for the given framework/platform combo.
        dotnet publish $proj `
            -p:PublishProfile=$profile -f $Framework `
            -p:PublishDir="$publishDir" `
            -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Publish failed for profile: $profile"
            return
        }

        $licSrc = Join-Path $Root 'DistLicense.txt'
        Copy-Item $licSrc (Join-Path $publishDir 'License.txt') -Force
    }

    $iscc = $null
    if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) {
        $iscc = 'ISCC.exe'
    } else {
        $isccCandidates = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        )
        $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) {
        Write-Error "ISCC.exe not found. Install Inno Setup 6 or run .\scripts\install-prereqs.ps1."
        return
    }

    Write-Host "`n==> $iscc installer.iss" -ForegroundColor Cyan
    & $iscc (Join-Path $Root 'installer.iss')
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup compilation failed."
        return
    }

    $output = Join-Path $Root 'bin\RetroBarInstaller.exe'
    Write-Host "`nInstaller built: $output" -ForegroundColor Green
}

function Invoke-Uninstall {
    $appId = '{574527FE-00A4-4F85-92AD-B4B8B4077D73}_is1'
    $uninstallKey = $null
    foreach ($hive in @('HKCU:\', 'HKLM:\')) {
        $key = Join-Path $hive "Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
        if (Test-Path $key) { $uninstallKey = $key; break }
    }
    if (-not $uninstallKey) {
        Write-Warning "RetroBar does not appear to be installed (uninstall registry key not found)."
        return
    }
    $uninstallExe = (Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue).UninstallString
    if (-not $uninstallExe) {
        Write-Warning "UninstallString not found in registry key: $uninstallKey"
        return
    }
    # Strip surrounding quotes so Start-Process receives a clean path
    $uninstallExe = $uninstallExe.Trim('"')
    Write-Host "Running uninstaller: $uninstallExe" -ForegroundColor Cyan
    Start-Process $uninstallExe -ArgumentList '/SILENT' -Wait
    Write-Host "Uninstall complete." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Entry points
# ---------------------------------------------------------------------------

if ($Help) {
    Get-Help $PSCommandPath -Detailed
    exit 0
}

if ($Paths) {
    $appData = Join-Path $env:LOCALAPPDATA 'RetroBar'
    Write-Host ""
    Write-Host "RetroBar data paths:" -ForegroundColor Cyan
    Write-Host "  Settings : $(Join-Path $appData 'settings.json')"
    Write-Host "  Logs     : $(Join-Path $appData 'Logs')"
    Write-Host "  Themes   : $(Join-Path $appData 'Themes')"
    Write-Host "  AppData  : $appData"
    Write-Host ""
    exit 0
}

if ($Settings) {
    $settingsFile = Join-Path $env:LOCALAPPDATA 'RetroBar\settings.json'
    if (-not (Test-Path $settingsFile)) {
        Write-Warning "Settings file not found: $settingsFile (RetroBar may not have been run yet)"
        exit 1
    }
    $editor = if (Get-Command code -ErrorAction SilentlyContinue) { 'code' }
              elseif (Get-Command code-insiders -ErrorAction SilentlyContinue) { 'code-insiders' }
              else { $null }
    if ($editor) {
        & $editor $settingsFile
    } else {
        Start-Process $settingsFile
    }
    exit 0
}

if ($OpenLog) {
    $logDir = Join-Path $env:LOCALAPPDATA 'RetroBar\Logs'
    $logFile = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1
    if (-not $logFile) {
        Write-Warning "No log file found in: $logDir (RetroBar may not have been run yet)"
        exit 1
    }
    $editor = if (Get-Command code -ErrorAction SilentlyContinue) { 'code' }
              elseif (Get-Command code-insiders -ErrorAction SilentlyContinue) { 'code-insiders' }
              else { $null }
    if ($editor) {
        & $editor $logFile.FullName
    } else {
        Start-Process $logFile.FullName
    }
    exit 0
}

if ($Stop) {
    Stop-RetroBar
    Disable-TaskbarAutoHide
    Restore-WindowsTaskbar
    exit 0
}

if ($BuildInstaller) {
    Invoke-Installer
    exit 0
}

if ($UninstallRelease) {
    Stop-RetroBar
    Disable-TaskbarAutoHide
    Restore-WindowsTaskbar
    Invoke-Uninstall
    exit 0
}

if ($Relaunch) {
    Stop-RetroBar
    Disable-TaskbarAutoHide
    Restore-WindowsTaskbar
    $script:BuildOk = $true
    if (-not $NoRebuild) {
        $relaunchTarget = if ($PSBoundParameters.ContainsKey('Target')) { $Target } else { @('RetroBar') }
        Invoke-Build -targets $relaunchTarget -cfg $Configuration -fw $Framework -verbosity $Verbosity
    }
    if ($script:BuildOk) {
        Start-RetroBar -cfg $Configuration -fw $Framework
    }
    if ($Log -and $script:BuildOk) {
        Start-LogTail -WaitForNew
    }
    exit 0
}

if ($Background) {
    $targetsArg = $Target -join ','
    $scriptArgs = @(
        '-NonInteractive'
        '-File', "`"$PSCommandPath`""
        '-Target', $targetsArg
        '-Configuration', $Configuration
        '-Framework', $Framework
        '-Verbosity', $Verbosity
        '-Launch'
    )
    if ($Log) { $scriptArgs += '-Log' }
    Start-Process powershell -ArgumentList $scriptArgs -WindowStyle Normal
    Write-Host "Background build started. Use .\build.ps1 -Stop to kill RetroBar and restore the taskbar." -ForegroundColor Cyan
    exit 0
}

# Foreground build (with optional -Launch / -Log)
$script:BuildOk = $true
if (-not $NoRebuild) {
    Invoke-Build -targets $Target -cfg $Configuration -fw $Framework -verbosity $Verbosity
}
if ($Launch -and $script:BuildOk) {
    Start-RetroBar -cfg $Configuration -fw $Framework
}
if ($Log) {
    Start-LogTail -WaitForNew:($Launch.IsPresent -and $script:BuildOk)
}
