#Requires -Version 5.1
<#
.SYNOPSIS
    Save or restore RetroBar user settings.

.PARAMETER Save
    Copy %LOCALAPPDATA%\RetroBar\settings.json into this script's directory as settings.json.

.PARAMETER Restore
    Copy settings.json from this script's directory back to %LOCALAPPDATA%\RetroBar\settings.json.

.EXAMPLE
    .\settings.ps1 -Save      # back up current user settings into scripts\settings.json
    .\settings.ps1 -Restore   # restore settings from scripts\settings.json
#>
param(
    [switch]$Save,
    [switch]$Restore
)

$UserSettings = Join-Path $env:LOCALAPPDATA 'RetroBar\settings.json'
$Backup       = Join-Path $PSScriptRoot 'settings.json'

if ($Save) {
    if (-not (Test-Path $UserSettings)) {
        Write-Warning "Settings file not found: $UserSettings (has RetroBar been run yet?)"
        exit 1
    }
    Copy-Item $UserSettings $Backup -Force
    Write-Host "Saved: $UserSettings -> $Backup" -ForegroundColor Green
    exit 0
}

if ($Restore) {
    if (-not (Test-Path $Backup)) {
        Write-Warning "No backup found at: $Backup (run .\settings.ps1 -Save first)"
        exit 1
    }
    Copy-Item $Backup $UserSettings -Force
    Write-Host "Restored: $Backup -> $UserSettings" -ForegroundColor Green
    exit 0
}

Get-Help $PSCommandPath -Detailed
