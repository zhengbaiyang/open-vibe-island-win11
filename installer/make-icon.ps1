#Requires -Version 5.1
<#
.SYNOPSIS
    Generates installer/app icon (.ico) for Open Island from code (no binary assets needed).
.DESCRIPTION
    Draws the v6 "three bars" island glyph on a rounded obsidian squircle at 1024px,
    then emits a multi-resolution PNG-compressed ICO (16/24/32/48/64/128/256).
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot "OpenIsland.ico")
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([System.Drawing.RectangleF]$rect, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-MasterBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [float]($size * 0.055)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - $pad * 2), ($size - $pad * 2)
    $radius = [float]($size * 0.235)

    # Soft outer glow
    for ($i = 6; $i -ge 1; $i--) {
        $glowRect = New-Object System.Drawing.RectangleF ($rect.X - $i), ($rect.Y - $i), ($rect.Width + $i * 2), ($rect.Height + $i * 2)
        $glowPath = New-RoundedRectPath $glowRect ($radius + $i)
        $alpha = [int](10 / $i)
        $glowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alpha, 0, 0, 0))
        $g.FillPath($glowBrush, $glowPath)
        $glowBrush.Dispose(); $glowPath.Dispose()
    }

    # Obsidian squircle body with a subtle vertical gradient
    $bodyPath = New-RoundedRectPath $rect $radius
    $gradRect = New-Object System.Drawing.RectangleF $rect.X, $rect.Y, $rect.Width, $rect.Height
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $gradRect, ([System.Drawing.Color]::FromArgb(255, 22, 25, 32)), ([System.Drawing.Color]::FromArgb(255, 7, 8, 11)), 90.0
    $g.FillPath($grad, $bodyPath)
    $grad.Dispose()

    # Hairline edge highlight
    $edgePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(46, 255, 255, 255)), ([float]($size * 0.006))
    $g.DrawPath($edgePen, $bodyPath)
    $edgePen.Dispose()

    # Three "island" bars (paper colour), rounded caps
    $barWidth = [float]($size * 0.075)
    $cx = $size / 2.0
    $gap = [float]($size * 0.155)
    $heights = @(
        [float]($size * 0.30),
        [float]($size * 0.42),
        [float]($size * 0.30)
    )
    $xPositions = @(($cx - $gap - $barWidth), ($cx - $barWidth / 2.0), ($cx + $gap))
    $paper = [System.Drawing.Color]::FromArgb(255, 241, 234, 217)
    $barBrush = New-Object System.Drawing.SolidBrush $paper
    for ($i = 0; $i -lt 3; $i++) {
        $h = $heights[$i]
        $x = $xPositions[$i]
        $y = ($size - $h) / 2.0
        $barRect = New-Object System.Drawing.RectangleF $x, $y, $barWidth, $h
        $barPath = New-RoundedRectPath $barRect ($barWidth / 2.0)
        $g.FillPath($barBrush, $barPath)
        $barPath.Dispose()
    }
    $barBrush.Dispose()
    $bodyPath.Dispose()
    $g.Dispose()
    return $bmp
}

function Resize-Bitmap([System.Drawing.Bitmap]$source, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

$master = New-MasterBitmap 1024
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngData = @{}
foreach ($s in $sizes) {
    $small = Resize-Bitmap $master $s
    $ms = New-Object System.IO.MemoryStream
    $small.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngData[$s] = $ms.ToArray()
    $ms.Dispose()
    $small.Dispose()
}
$master.Dispose()

$dir = Split-Path -Parent $Output
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$fs = [System.IO.File]::Create($Output)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)            # reserved
$bw.Write([UInt16]1)            # type: icon
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
foreach ($s in $sizes) {
    $data = $pngData[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)       # width
    $bw.Write([byte]$dim)       # height
    $bw.Write([byte]0)          # colors
    $bw.Write([byte]0)          # reserved
    $bw.Write([UInt16]1)        # planes
    $bw.Write([UInt16]32)       # bpp
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngData[$s]) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host "Generated icon: $Output ($((Get-Item $Output).Length) bytes, $($sizes.Count) sizes)"
