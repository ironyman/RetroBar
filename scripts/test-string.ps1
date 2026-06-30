#Requires -Version 5.1
<#
.SYNOPSIS
    Test whether a literal string is present in a binary file (e.g. a compiled
    .NET DLL), searching at the raw byte level so alignment never hides a match.

.DESCRIPTION
    .NET metadata stores string literals in the #US heap as UTF-16LE, but those
    strings are NOT guaranteed to start on an even byte offset. Decoding the whole
    file as UTF-16 from offset 0 therefore misses every string that happens to sit
    at an odd offset. To avoid that, this script encodes the search term into the
    candidate encodings (UTF-16LE and UTF-8/ASCII) and scans the raw bytes for that
    exact byte sequence at every offset.

    The byte scan is done by mapping both haystack and needle through Latin1
    (ISO-8859-1), a lossless 1:1 byte<->char mapping, then using the runtime's
    fast ordinal String.IndexOf.

.PARAMETER Path
    Path to the binary file to search.

.PARAMETER Pattern
    One or more literal strings to look for.

.PARAMETER SelfTest
    Ignore -Pattern and instead run a built-in self-test against the given file,
    asserting that strings known to be present are found and a string known to be
    absent is not. Exits 0 only if all assertions pass.

.EXAMPLE
    .\test-string.ps1 -Path foo.dll -Pattern "Initialize failed"
    .\test-string.ps1 -Path foo.dll -SelfTest

.EXAMPLE
    # --- Confirming a new debug/log string actually made it into a build ---
    #
    # The workflow: you add a log line in source, rebuild, and want to prove the
    # running binary contains it (instead of silently loading a stale DLL).
    #
    # 1) Add the string in C#, e.g. in NotificationArea.cs:
    #        ShellLogger.Error($"NotificationArea: Initialize failed: {e}");
    #    Only the LITERAL part lands in the binary. An interpolated $"...{e}"
    #    compiles to a format string, so search for the literal prefix
    #    ("NotificationArea: Initialize failed: ") and NOT the "{e}" part.
    #
    # 2) Rebuild the project that owns the file:
    #        dotnet build ManagedShell\src\ManagedShell.WindowsTray\ManagedShell.WindowsTray.csproj `
    #            -c Debug -f net6.0-windows10.0.19041.0
    #
    # 3) Verify the COMPILER output (obj or bin) contains the literal:
    $dll = 'ManagedShell\src\ManagedShell.WindowsTray\bin\Debug\net6.0-windows10.0.19041.0\ManagedShell.WindowsTray.dll'
    .\scripts\test-string.ps1 -Path $dll -Pattern 'NotificationArea: Initialize failed, tray icons will not load: '
    #    -> "FOUND (UTF-16LE) ..."  means the edit is compiled in.
    #    -> "MISSING ..."           means the build skipped/used a stale DLL.
    #
    # 4) Verify the COPY the app actually loads has it too (catches the case where
    #    the project built but the consuming app's bin wasn't refreshed):
    .\scripts\test-string.ps1 -Path 'RetroBar\bin\Debug\net6.0-windows10.0.19041.0\ManagedShell.WindowsTray.dll' -Pattern 'NotificationArea: Initialize failed, tray icons will not load: '
    #
    # 5) The exit code is 0 only if every -Pattern was found, so it scripts cleanly
    #    in a build gate:
    #        if (.\scripts\test-string.ps1 -Path $dll -Pattern 'my new log') { 'present' }
    #
    # NOTE: do NOT use Unix `strings` here for this check — it is not installed in
    # this repo's Bash environment, and `strings ... 2>/dev/null | grep` silently
    # reports every string as absent. This script reads raw bytes instead, so it
    # finds UTF-16LE literals regardless of byte alignment.
#>
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [string[]]$Pattern,

    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# Resolve to a full filesystem path against the *PowerShell* location. .NET APIs
# like File.ReadAllBytes resolve relative paths against [Environment]::CurrentDirectory
# (often the user profile dir), not $PWD, so a bare relative path would otherwise
# read from the wrong place. GetUnresolvedProviderPathFromPSPath handles this.
$Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "File not found: $Path"
    exit 2
}

$Latin1 = [System.Text.Encoding]::GetEncoding('iso-8859-1')
$bytes  = [System.IO.File]::ReadAllBytes($Path)
$hay    = $Latin1.GetString($bytes)   # 1:1 byte->char, no alignment loss

function Test-Contains {
    param([string]$Needle)

    $encodings = [ordered]@{
        'UTF-16LE' = [System.Text.Encoding]::Unicode
        'UTF-8'    = [System.Text.Encoding]::UTF8
    }

    foreach ($name in $encodings.Keys) {
        $needleBytes = $encodings[$name].GetBytes($Needle)
        $needleStr   = $Latin1.GetString($needleBytes)
        if ($hay.IndexOf($needleStr, [System.StringComparison]::Ordinal) -ge 0) {
            return $name
        }
    }
    return $null
}

if ($SelfTest) {
    # Known-present strings: pick stable literals that must exist in any
    # ManagedShell.WindowsTray build. A random GUID must be absent.
    $present = @('AllIcons', 'PinnedIcons', 'UnpinnedIcons', 'NotificationArea')
    $absent  = @('zz_this_string_should_never_appear_' + [guid]::NewGuid().ToString('N'))

    $ok = $true
    Write-Host "Self-test against: $Path" -ForegroundColor Cyan

    foreach ($s in $present) {
        $hit = Test-Contains $s
        if ($hit) {
            Write-Host ("  PASS  present '{0}' found ({1})" -f $s, $hit) -ForegroundColor Green
        } else {
            Write-Host ("  FAIL  present '{0}' NOT found" -f $s) -ForegroundColor Red
            $ok = $false
        }
    }
    foreach ($s in $absent) {
        $hit = Test-Contains $s
        if (-not $hit) {
            Write-Host ("  PASS  absent  '{0}...' correctly not found" -f $s.Substring(0, 30)) -ForegroundColor Green
        } else {
            Write-Host ("  FAIL  absent  '{0}...' unexpectedly found ({1})" -f $s.Substring(0, 30), $hit) -ForegroundColor Red
            $ok = $false
        }
    }

    if ($ok) { Write-Host "Self-test passed." -ForegroundColor Green; exit 0 }
    else     { Write-Host "Self-test FAILED." -ForegroundColor Red;   exit 1 }
}

if (-not $Pattern) {
    Write-Error "Provide -Pattern or use -SelfTest."
    exit 2
}

$anyMissing = $false
foreach ($p in $Pattern) {
    $hit = Test-Contains $p
    if ($hit) {
        Write-Host ("FOUND   ({0})  {1}" -f $hit, $p) -ForegroundColor Green
    } else {
        Write-Host ("MISSING       {0}" -f $p) -ForegroundColor Yellow
        $anyMissing = $true
    }
}
exit ([int]$anyMissing)
