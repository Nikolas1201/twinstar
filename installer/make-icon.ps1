# Draws assets\twinstar.ico: the intro's T, in the cyan accent, on a dark rounded tile.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force (Join-Path $root 'assets') | Out-Null

function Render([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = $s * 0.22; $w = $s - 1
    $tile = New-Object Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0, 0, $r, $r, 180, 90); $tile.AddArc($w - $r, 0, $r, $r, 270, 90)
    $tile.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $tile.AddArc(0, $w - $r, $r, $r, 90, 90); $tile.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Point]::new(0, 0)), ([Drawing.Point]::new(0, $s)), ([Drawing.Color]::FromArgb(14, 34, 40)), ([Drawing.Color]::FromArgb(6, 14, 22))
    $g.FillPath($bg, $tile)
    # Same proportions as the T in the boot intro.
    $outline = @(-.98, 1), @(.98, 1), @(.98, .46), @(.3, .46), @(.3, -1), @(-.3, -1), @(-.3, .46), @(-.98, .46)
    $points = foreach ($p in $outline) { [Drawing.PointF]::new($s / 2 + $p[0] * $s * 0.31, $s / 2 - $p[1] * $s * 0.31) }
    $fg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.Point]::new(0, [int]($s * 0.18))), ([Drawing.Point]::new(0, [int]($s * 0.82))), ([Drawing.Color]::FromArgb(214, 252, 246)), ([Drawing.Color]::FromArgb(45, 212, 191))
    $g.FillPolygon($fg, [Drawing.PointF[]]$points)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { , (Render $s) }
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
[IO.File]::WriteAllBytes((Join-Path $root 'assets\twinstar.ico'), $out.ToArray())
