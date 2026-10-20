<#
.SYNOPSIS
  Regenerates the RemoteConnect app icons (PNG for tray/window, ICO for Windows, ICNS for macOS).
  Uses System.Drawing, so run it on Windows (Windows PowerShell 5.1 or PowerShell 7).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\Desktop\RemoteConnect.Desktop\Assets'
$macDir = Join-Path $root 'deployment\macOS'
New-Item -ItemType Directory -Force $assets, $macDir | Out-Null

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    # Rounded square background with a diagonal gradient.
    $r = 56 * $s; $pad = 8 * $s; $w = $size - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $r, $r, 180, 90)
    $path.AddArc($pad + $w - $r, $pad, $r, $r, 270, 90)
    $path.AddArc($pad + $w - $r, $pad + $w - $r, $r, $r, 0, 90)
    $path.AddArc($pad, $pad + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 29, 78, 216)), ([System.Drawing.Color]::FromArgb(255, 6, 182, 212))
    $g.FillPath($brush, $path)

    # Monitor outline.
    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen $white, ([Math]::Max(1.5, 14 * $s))
    $pen.LineJoin = 'Round'
    $g.DrawRectangle($pen, [float](52 * $s), [float](62 * $s), [float](152 * $s), [float](104 * $s))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), [float](116 * $s), [float](166 * $s), [float](24 * $s), [float](22 * $s))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $white), [float](88 * $s), [float](186 * $s), [float](80 * $s), [float](12 * $s))

    # Connection arrows inside the screen.
    $arrow = New-Object System.Drawing.Pen $white, ([Math]::Max(1.2, 10 * $s))
    $arrow.StartCap = 'Round'; $arrow.EndCap = 'ArrowAnchor'
    if ($size -ge 32) {
        $g.DrawLine($arrow, [float](84 * $s), [float](100 * $s), [float](168 * $s), [float](100 * $s))
        $g.DrawLine($arrow, [float](172 * $s), [float](132 * $s), [float](88 * $s), [float](132 * $s))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

# Window / tray PNG
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app-icon.png'), (New-IconPng 256))

# Windows ICO with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 24, 32, 48, 64, 256
$images = @{}; foreach ($sz in $sizes) { $images[$sz] = New-IconPng $sz }
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
    $data = $images[$sz]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($sz in $sizes) { $bw.Write($images[$sz]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $ico.ToArray())

# macOS ICNS with PNG entries: ic07=128, ic08=256, ic09=512, ic10=1024.
function Write-BE32([System.IO.BinaryWriter]$w, [int]$v) { $b = [BitConverter]::GetBytes([int]$v); [Array]::Reverse($b); $w.Write($b) }
$entries = @(@('ic07', 128), @('ic08', 256), @('ic09', 512), @('ic10', 1024))
$body = New-Object System.IO.MemoryStream
$bb = New-Object System.IO.BinaryWriter $body
foreach ($e in $entries) {
    $png = New-IconPng $e[1]
    $bb.Write([System.Text.Encoding]::ASCII.GetBytes($e[0])); Write-BE32 $bb ($png.Length + 8); $bb.Write($png)
}
$bb.Flush()
$icns = New-Object System.IO.MemoryStream
$iw = New-Object System.IO.BinaryWriter $icns
$iw.Write([System.Text.Encoding]::ASCII.GetBytes('icns')); Write-BE32 $iw ($body.Length + 8); $iw.Write($body.ToArray()); $iw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $macDir 'AppIcon.icns'), $icns.ToArray())

Write-Host "Icons written to $assets and $macDir"
