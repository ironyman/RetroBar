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
    .\build.ps1 -Relaunch                      # stop, rebuild all, start RetroBar
    .\build.ps1 -Target RetroBar -Relaunch     # stop, rebuild RetroBar only, start
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
    [switch]$Help,

    # Internal: passed by -Background to tell the spawned process to launch RetroBar after building.
    [switch]$Launch
)

$Root = Split-Path $PSScriptRoot -Parent

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
        dotnet build $proj -c $cfg -f $fw --no-incremental -v $verbosity
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

# ---------------------------------------------------------------------------
# Entry points
# ---------------------------------------------------------------------------

if ($Help) {
    Get-Help $PSCommandPath -Detailed
    exit 0
}

if ($Stop) {
    Stop-RetroBar
    Disable-TaskbarAutoHide
    Restore-WindowsTaskbar
    exit 0
}

if ($Relaunch) {
    Stop-RetroBar
    Disable-TaskbarAutoHide
    Restore-WindowsTaskbar
    $script:BuildOk = $true
    Invoke-Build -targets $Target -cfg $Configuration -fw $Framework -verbosity $Verbosity
    if ($script:BuildOk) {
        Start-RetroBar -cfg $Configuration -fw $Framework
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
    Start-Process powershell -ArgumentList $scriptArgs -WindowStyle Normal
    Write-Host "Background build started. Use .\build.ps1 -Stop to kill RetroBar and restore the taskbar." -ForegroundColor Cyan
    exit 0
}

# Foreground build (with optional -Launch)
$script:BuildOk = $true
Invoke-Build -targets $Target -cfg $Configuration -fw $Framework -verbosity $Verbosity
if ($Launch -and $script:BuildOk) {
    Start-RetroBar -cfg $Configuration -fw $Framework
}
