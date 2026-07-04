#Requires -Version 5.1
<#
.SYNOPSIS
    Write a Unicode code point (e.g. a Segoe Fluent Icons glyph) into a C# string
    constant, encoded as an actual UTF-8 character rather than a "\uXXXX" escape.

.DESCRIPTION
    Icon fonts like Segoe Fluent Icons map glyphs to Private Use Area code points
    (U+E000-U+F8FF). These have no visible glyph in a terminal or most text
    viewers, so pasting them directly into a prompt/tool call is unreliable: the
    character can silently be dropped, replaced with a placeholder, or otherwise
    mangled before it reaches disk.

    This script sidesteps that by taking the code point as plain hex text (safe
    to type/copy anywhere) and using .NET to convert it to the actual character,
    which it then writes straight into the target file's bytes. The result is a
    real UTF-8-encoded character in the source file - not an escape sequence -
    matching how the glyph constants in RetroBar.Controls.ResourceMeter and
    RetroBar.Controls.NetworkMeter are written today.

    Only code points in the Basic Multilingual Plane (0000-FFFF) are supported,
    which covers all current Segoe Fluent Icons / Segoe MDL2 Assets glyphs.

    https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font

.PARAMETER Path
    Path to the source file to edit.

.PARAMETER ConstantName
    The C# constant name to update, e.g. "CpuGlyph". The script matches a line of
    the form:
        private const string <ConstantName> = "...";
    (any existing quoted content is replaced) and preserves everything else on
    the line (indentation, access modifier, trailing semicolon).

.PARAMETER CodePoint
    The glyph's code point in hex, with or without a "U+"/"0x" prefix, e.g.
    "EEA1", "U+EEA1", or "0xEEA1".

.EXAMPLE
    # How the CPU/RAM/disk/network glyph constants in this repo were set:
    .\scripts\Set-GlyphConstant.ps1 -Path RetroBar\Controls\ResourceMeter.xaml.cs -ConstantName CpuGlyph    -CodePoint EEA1
    .\scripts\Set-GlyphConstant.ps1 -Path RetroBar\Controls\ResourceMeter.xaml.cs -ConstantName MemoryGlyph -CodePoint EEA0
    .\scripts\Set-GlyphConstant.ps1 -Path RetroBar\Controls\ResourceMeter.xaml.cs -ConstantName DiskGlyph   -CodePoint EE94
    .\scripts\Set-GlyphConstant.ps1 -Path RetroBar\Controls\NetworkMeter.xaml.cs  -ConstantName NetworkGlyph -CodePoint EDA3

.EXAMPLE
    # Verify what actually landed on disk (prints the codepoint(s) found for the constant):
    .\scripts\Set-GlyphConstant.ps1 -Path RetroBar\Controls\ResourceMeter.xaml.cs -ConstantName CpuGlyph -CodePoint EEA1 -WhatIf
#>
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$ConstantName,

    [Parameter(Mandatory)]
    [string]$CodePoint,

    # Report what would change and what code point is currently stored, without writing.
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "File not found: $Path"
    exit 2
}

$hex = $CodePoint -replace '^(U\+|0x)', ''
$codePointValue = [Convert]::ToInt32($hex, 16)
if ($codePointValue -lt 0 -or $codePointValue -gt 0xFFFF) {
    Write-Error "Only BMP code points (0000-FFFF) are supported; got $hex. Segoe Fluent Icons glyphs are all in this range."
    exit 2
}

$glyphChar = [char]$codePointValue

# Read/write as raw UTF-8 without a BOM, matching this repo's existing C# files.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$content = [System.IO.File]::ReadAllText($Path, $utf8NoBom)

$pattern = "(private const string $ConstantName = "")(.*?)("";)"
$match = [System.Text.RegularExpressions.Regex]::Match($content, $pattern)
if (-not $match.Success) {
    Write-Error "No line matching 'private const string $ConstantName = ""..."";' found in $Path"
    exit 2
}

$currentValue = $match.Groups[2].Value
$currentCodePoints = ($currentValue.ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' '
Write-Host "Current value of ${ConstantName}: '$currentValue' (code points: $currentCodePoints)"

if ($WhatIf) {
    Write-Host "WhatIf: would set $ConstantName to code point U+$('{0:X4}' -f $codePointValue)"
    exit 0
}

$newContent = $content.Substring(0, $match.Index) + $match.Groups[1].Value + $glyphChar + $match.Groups[3].Value + $content.Substring($match.Index + $match.Length)

[System.IO.File]::WriteAllText($Path, $newContent, $utf8NoBom)

Write-Host "Set $ConstantName in $Path to code point U+$('{0:X4}' -f $codePointValue)" -ForegroundColor Green
