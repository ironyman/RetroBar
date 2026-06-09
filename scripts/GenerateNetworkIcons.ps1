#Requires -Version 5.1
Add-Type -AssemblyName System.Drawing

$Root    = Split-Path $PSScriptRoot -Parent
$iconDir = Join-Path $Root "RetroBar\Icons\Network"

function Create-WifiBitmap {
    param(
        [int]$bars,
        [int]$size,
        [System.Drawing.Color]$color,
        [float]$strokeWidth,
        [bool]$disconnected = $false
    )

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $scale = $size / 16.0
    $cx = $size / 2.0
    $cy = $size - 2.0 * $scale

    $r0 = [float](3.0 * $scale); $r1 = [float](5.0 * $scale); $r2 = [float](7.0 * $scale); $r3 = [float](9.0 * $scale)
    $radii = @($r0, $r1, $r2, $r3)

    if ($disconnected) {
        $fadedColor = [System.Drawing.Color]::FromArgb(90, $color.R, $color.G, $color.B)
        $fadedPen = New-Object System.Drawing.Pen($fadedColor, $strokeWidth)
        $fadedPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $fadedPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        foreach ($r in $radii) {
            $g.DrawArc($fadedPen, [float]($cx - $r), [float]($cy - $r), [float]($r * 2), [float]($r * 2), [float]210, [float]120)
        }
        $fadedPen.Dispose()

        # X mark in the upper-center of the fan
        $xHalf = 2.5 * $scale
        $midY = $cy - ($radii[1] + $radii[2]) / 2.0
        $xPen = New-Object System.Drawing.Pen($color, $strokeWidth)
        $xPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $xPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($xPen, [float]($cx - $xHalf), [float]($midY - $xHalf), [float]($cx + $xHalf), [float]($midY + $xHalf))
        $g.DrawLine($xPen, [float]($cx + $xHalf), [float]($midY - $xHalf), [float]($cx - $xHalf), [float]($midY + $xHalf))
        $xPen.Dispose()
    } else {
        $pen = New-Object System.Drawing.Pen($color, $strokeWidth)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        for ($i = 0; $i -lt $bars; $i++) {
            $r = $radii[$i]
            $g.DrawArc($pen, [float]($cx - $r), [float]($cy - $r), [float]($r * 2), [float]($r * 2), [float]210, [float]120)
        }
        $pen.Dispose()
    }

    # Center dot
    $dotR = [Math]::Max(1.2, $strokeWidth * 0.75)
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillEllipse($brush, [float]($cx - $dotR), [float]($cy - $dotR), [float]($dotR * 2), [float]($dotR * 2))
    $brush.Dispose()
    $g.Dispose()

    return $bmp
}

function Write-IcoFile {
    param(
        [System.Drawing.Bitmap[]]$bitmaps,
        [string]$outputPath
    )

    $pngList = @()
    foreach ($bmp in $bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngList += , $ms.ToArray()
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Create($outputPath)
    $bw = New-Object System.IO.BinaryWriter($fs)

    $bw.Write([uint16]0)                        # reserved
    $bw.Write([uint16]1)                        # type = ICO
    $bw.Write([uint16]$bitmaps.Count)           # image count

    $dataOffset = 6 + $bitmaps.Count * 16
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $bmp = $bitmaps[$i]
        $w = if ($bmp.Width  -ge 256) { [byte]0 } else { [byte]$bmp.Width }
        $h = if ($bmp.Height -ge 256) { [byte]0 } else { [byte]$bmp.Height }
        $bw.Write($w)
        $bw.Write($h)
        $bw.Write([byte]0)     # color count (0 = use bit count)
        $bw.Write([byte]0)     # reserved
        $bw.Write([uint16]1)   # planes
        $bw.Write([uint16]32)  # bits per pixel
        $bw.Write([uint32]$pngList[$i].Length)
        $bw.Write([uint32]$dataOffset)
        $dataOffset += $pngList[$i].Length
    }

    foreach ($png in $pngList) {
        $bw.Write($png)
    }

    $bw.Flush()
    $bw.Dispose()
}

$white    = [System.Drawing.Color]::White
$darkGrey = [System.Drawing.Color]::FromArgb(40, 40, 40)

# stroke widths by size (2px at 16, scaled up)
$sizes = @(16, 24, 32)
function Get-Stroke($sz) { [float]([Math]::Round($sz / 8.0)) }   # 2 at 16, 3 at 24, 4 at 32

# ── Dark-theme wifi (white arcs): 6000–6004
for ($bars = 0; $bars -le 4; $bars++) {
    $bmps = @()
    foreach ($sz in $sizes) {
        $bmps += Create-WifiBitmap -bars $bars -size $sz -color $white -strokeWidth (Get-Stroke $sz)
    }
    $path = "$iconDir\$((6000 + $bars)).ico"
    Write-IcoFile -bitmaps $bmps -outputPath $path
    Write-Host "Generated $path"
    foreach ($b in $bmps) { $b.Dispose() }
}

# ── Dark-theme disconnected (white, faded + X): 6301
$bmps = @()
foreach ($sz in $sizes) {
    $bmps += Create-WifiBitmap -bars 0 -size $sz -color $white -strokeWidth (Get-Stroke $sz) -disconnected $true
}
Write-IcoFile -bitmaps $bmps -outputPath "$iconDir\6301.ico"
Write-Host "Generated $iconDir\6301.ico"
foreach ($b in $bmps) { $b.Dispose() }

# ── Light-theme wifi (dark arcs): 6020–6024
for ($bars = 0; $bars -le 4; $bars++) {
    $bmps = @()
    foreach ($sz in $sizes) {
        $bmps += Create-WifiBitmap -bars $bars -size $sz -color $darkGrey -strokeWidth (Get-Stroke $sz)
    }
    $path = "$iconDir\$((6020 + $bars)).ico"
    Write-IcoFile -bitmaps $bmps -outputPath $path
    Write-Host "Generated $path"
    foreach ($b in $bmps) { $b.Dispose() }
}

# ── Light-theme disconnected (dark, faded + X): 6303
$bmps = @()
foreach ($sz in $sizes) {
    $bmps += Create-WifiBitmap -bars 0 -size $sz -color $darkGrey -strokeWidth (Get-Stroke $sz) -disconnected $true
}
Write-IcoFile -bitmaps $bmps -outputPath "$iconDir\6303.ico"
Write-Host "Generated $iconDir\6303.ico"
foreach ($b in $bmps) { $b.Dispose() }

Write-Host "All network icons generated."
