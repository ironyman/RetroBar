#Requires -Version 5.1
<#
.SYNOPSIS
    Install RetroBar build prerequisites: Git, .NET SDK, and Inno Setup 6.

.DESCRIPTION
    Uses winget to install missing tools, then initialises the ManagedShell git
    submodule if it has not been populated yet.  Already-installed tools are
    skipped.  Run once after cloning the repository before using build.ps1.

.PARAMETER SkipSubmodule
    Do not initialise the ManagedShell git submodule.

.EXAMPLE
    .\scripts\install-prereqs.ps1
    .\scripts\install-prereqs.ps1 -SkipSubmodule
#>
param(
    [switch]$SkipSubmodule
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg) {
    Write-Host "    OK  $msg" -ForegroundColor Green
}

function Write-Skip([string]$msg) {
    Write-Host "    --  $msg (already installed)" -ForegroundColor DarkGray
}

function Test-WingetAvailable {
    return $null -ne (Get-Command winget -ErrorAction SilentlyContinue)
}

function Install-WithWinget([string]$id, [string]$label) {
    Write-Step "Installing $label"
    if (-not (Test-WingetAvailable)) {
        Write-Warning "winget not found. Install $label manually."
        return
    }
    winget install --id $id --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$label installation may have failed (winget exit $LASTEXITCODE)."
    } else {
        Write-Ok "$label installed."
    }
}

# ---------------------------------------------------------------------------
# Git
# ---------------------------------------------------------------------------

Write-Step 'Checking Git'
if (Get-Command git -ErrorAction SilentlyContinue) {
    Write-Skip "git $(git --version)"
} else {
    Install-WithWinget 'Git.Git' 'Git'
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH', 'User')
}

# ---------------------------------------------------------------------------
# .NET SDK (>= 6.0 required for net6.0-windows targets)
# ---------------------------------------------------------------------------

Write-Step 'Checking .NET SDK'
$hasSdk = $false
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdks = dotnet --list-sdks 2>$null
    $hasSdk = [bool]($sdks | Where-Object { $_ -match '^([6-9]|\d{2,})\.' })
    if ($hasSdk) {
        $sdkLines = ($sdks | ForEach-Object { "        $_" }) -join "`n"
        Write-Skip "dotnet SDKs:`n$sdkLines"
    }
}
if (-not $hasSdk) {
    Install-WithWinget 'Microsoft.DotNet.SDK.8' '.NET 8 SDK'
    $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' +
                [System.Environment]::GetEnvironmentVariable('PATH', 'User')
}

# ---------------------------------------------------------------------------
# Inno Setup 6
# ---------------------------------------------------------------------------

Write-Step 'Checking Inno Setup 6'
$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$isccOnPath = $null -ne (Get-Command ISCC.exe -ErrorAction SilentlyContinue)
$isccOnDisk = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($isccOnPath -or $isccOnDisk) {
    $loc = if ($isccOnPath) { 'PATH' } else { $isccOnDisk }
    Write-Skip "Inno Setup 6 ($loc)"
} else {
    Install-WithWinget 'JRSoftware.InnoSetup' 'Inno Setup 6'
}

# ---------------------------------------------------------------------------
# Git submodule (ManagedShell)
# ---------------------------------------------------------------------------

if (-not $SkipSubmodule) {
    Write-Step 'Checking ManagedShell submodule'
    $sentinel = Join-Path $Root 'ManagedShell\src'
    if (Test-Path $sentinel) {
        Write-Skip 'ManagedShell submodule already populated'
    } elseif (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Warning 'git not found -- cannot initialise submodule. Re-run after installing Git.'
    } else {
        Write-Host '    Initialising ManagedShell submodule...' -ForegroundColor Yellow
        Push-Location $Root
        try {
            git submodule update --init --recursive
            if ($LASTEXITCODE -eq 0) {
                Write-Ok 'ManagedShell submodule initialised.'
            } else {
                Write-Warning "git submodule update exited with $LASTEXITCODE"
            }
        } finally {
            Pop-Location
        }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'Prerequisites check complete.' -ForegroundColor Green
Write-Host 'If any tools were just installed, open a new terminal before running build.ps1.' -ForegroundColor Yellow
