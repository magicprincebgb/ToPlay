<#
.SYNOPSIS
  Generates every ToPlay app icon by DRAWING the brand mark directly with GDI+
  (System.Drawing, ships with Windows PowerShell) — no logo.jpg and no external
  tools required. Produces:
    - wwwroot\icon-192.png, icon-512.png   (PWA / maskable)
    - wwwroot\apple-touch-icon.png          (iOS "Add to Home Screen", 180x180)
    - wwwroot\favicon.ico                   (browser tab)
    - src\ToPlay.App\app.ico                (ToPlay.exe application icon)

  The mark matches wwwroot\icon.svg: a blue screen with a circular play button.
  The background is a FULL-BLEED opaque square (no transparent rounded corners)
  so it renders correctly as a maskable PWA icon and on the iOS home screen,
  where the OS applies its own rounded-corner mask.

  Re-run any time to refresh all icons.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$www  = Join-Path $root 'src\ToPlay.Host\wwwroot'
$app  = Join-Path $root 'src\ToPlay.App'
New-Item -ItemType Directory -Force -Path $www | Out-Null
New-Item -ItemType Directory -Force -Path $app | Out-Null

# Rounded-rectangle fill helper (no dash in the name => no verb warning).
function FillRoundRect($g, $brush, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = 2 * $r
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x,          $y,          $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,          $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0,   90)
    $path.AddArc($x,          $y + $h - $d, $d, $d, 90,  90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    $path.Dispose()
}

# Draw the ToPlay mark at an arbitrary square size (coords scaled from 512).
function New-Icon([int]$size) {
    $scale = $size / 512.0
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $bg   = [System.Drawing.Color]::FromArgb(255, 11, 15, 23)   # #0b0f17
    $blue = [System.Drawing.Color]::FromArgb(255, 31, 111, 235) # #1f6feb
    $bgBrush   = New-Object System.Drawing.SolidBrush($bg)
    $blueBrush = New-Object System.Drawing.SolidBrush($blue)

    # Full-bleed opaque background.
    $g.Clear($bg)

    # Screen + stand.
    FillRoundRect $g $blueBrush (96 * $scale)  (128 * $scale) (320 * $scale) (200 * $scale) (16 * $scale)
    FillRoundRect $g $blueBrush (176 * $scale) (336 * $scale) (160 * $scale) (20 * $scale)  (10 * $scale)

    # Circular play-button recess (dark) with a blue triangle.
    $cx = 256 * $scale; $cy = 228 * $scale; $r = 46 * $scale
    $g.FillEllipse($bgBrush, [single]($cx - $r), [single]($cy - $r), [single](2 * $r), [single](2 * $r))
    $pts = @(
        (New-Object System.Drawing.PointF([single](243 * $scale), [single](206 * $scale))),
        (New-Object System.Drawing.PointF([single](243 * $scale), [single](250 * $scale))),
        (New-Object System.Drawing.PointF([single](281 * $scale), [single](228 * $scale)))
    )
    $g.FillPolygon($blueBrush, $pts)

    $g.Dispose()
    $bgBrush.Dispose(); $blueBrush.Dispose()
    return $bmp
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "  wrote $path"
}

function Get-Bgra([System.Drawing.Bitmap]$bmp) {
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    return @{ Bytes = $bytes; Stride = $data.Stride }
}

# One uncompressed 32bpp BGRA DIB image blob (color + AND mask) for the ICO.
function New-IcoImage([System.Drawing.Bitmap]$bmp) {
    $N = $bmp.Width
    $px = Get-Bgra $bmp
    $stride = [int]$px.Stride
    $srcBytes = $px.Bytes
    $rowBytes = $N * 4
    $maskRow = [int]([Math]::Floor(($N + 31) / 32)) * 4
    $xorSize = $rowBytes * $N
    $andSize = $maskRow * $N

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40)
    $bw.Write([int]$N)
    $bw.Write([int]($N * 2))       # height = color + mask
    $bw.Write([int16]1)            # planes
    $bw.Write([int16]32)           # bit count
    $bw.Write([int]0)              # BI_RGB
    $bw.Write([int]($xorSize + $andSize))
    $bw.Write([int]0); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $N - 1; $y -ge 0; $y--) {
        $bw.Write($srcBytes, ($y * $stride), $rowBytes)
    }
    $bw.Write((New-Object byte[] $andSize), 0, $andSize)
    $bw.Flush()
    return $ms.ToArray()
}

function Write-Ico([System.Drawing.Bitmap[]]$bmps, [string]$path) {
    $images = @()
    foreach ($b in $bmps) { $images += , (New-IcoImage $b) }
    $count = $images.Count
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int16]0)
    $bw.Write([int16]1)
    $bw.Write([int16]$count)
    $offset = 6 + (16 * $count)
    for ($i = 0; $i -lt $count; $i++) {
        $w = $bmps[$i].Width; $h = $bmps[$i].Height
        $bw.Write([byte]$(if ($w -ge 256) { 0 } else { $w }))
        $bw.Write([byte]$(if ($h -ge 256) { 0 } else { $h }))
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([int16]1)
        $bw.Write([int16]32)
        $bw.Write([int]$images[$i].Length)
        $bw.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $bw.Write($img, 0, $img.Length) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    Write-Host "  wrote $path"
}

Write-Host "Generating web icons..."
Save-Png (New-Icon 192) (Join-Path $www 'icon-192.png')
Save-Png (New-Icon 512) (Join-Path $www 'icon-512.png')
Save-Png (New-Icon 180) (Join-Path $www 'apple-touch-icon.png')
# iOS picks the apple-touch-icon whose declared size matches the device; iPads
# ask for 152/167, and a missing file there makes "Add to Home Screen" fall back
# to a screenshot instead of the ToPlay mark.
Save-Png (New-Icon 167) (Join-Path $www 'apple-touch-icon-167.png')
Save-Png (New-Icon 152) (Join-Path $www 'apple-touch-icon-152.png')
Write-Ico @((New-Icon 16), (New-Icon 32), (New-Icon 48)) (Join-Path $www 'favicon.ico')

Write-Host "Generating application icon..."
Write-Ico @(
    (New-Icon 16), (New-Icon 32), (New-Icon 48),
    (New-Icon 64), (New-Icon 128), (New-Icon 256)
) (Join-Path $app 'app.ico')

Write-Host "Done." -ForegroundColor Green
