<#
.SYNOPSIS
  Generates all app icons from logo.jpg (repo root):
    - wwwroot\icon-192.png, icon-512.png  (PWA)
    - wwwroot\apple-touch-icon.png         (iOS home screen)
    - wwwroot\favicon.ico                  (browser tab / navicon)
    - src\ToPlay.App\app.ico               (ToPlay.exe application icon)

  Uses GDI+ (System.Drawing) which ships with Windows PowerShell, so there are
  no extra tools to install. Re-run after replacing logo.jpg to refresh icons.
#>
param(
    [string]$Logo
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
if (-not $Logo) { $Logo = Join-Path $root 'logo.jpg' }
if (-not (Test-Path $Logo)) { throw "logo not found: $Logo" }

$www = Join-Path $root 'src\ToPlay.Host\wwwroot'
$app = Join-Path $root 'src\ToPlay.App'
New-Item -ItemType Directory -Force -Path $www | Out-Null
New-Item -ItemType Directory -Force -Path $app | Out-Null

$src = [System.Drawing.Image]::FromFile($Logo)
Write-Host ("Source logo: {0} x {1}" -f $src.Width, $src.Height)

function New-Square([System.Drawing.Image]$img, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode   = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode     = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode       = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality  = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    # "cover": center-crop the source to a square so the icon is filled edge-to-edge.
    $s = [Math]::Min($img.Width, $img.Height)
    $sx = [int](($img.Width - $s) / 2)
    $sy = [int](($img.Height - $s) / 2)
    $srcRect = New-Object System.Drawing.Rectangle($sx, $sy, $s, $s)
    $dstRect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $g.DrawImage($img, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
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

# Build one uncompressed 32bpp BGRA DIB image blob (color + AND mask) for the ICO.
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
    # BITMAPINFOHEADER (40 bytes)
    $bw.Write([int]40)
    $bw.Write([int]$N)
    $bw.Write([int]($N * 2))       # height = color + mask
    $bw.Write([int16]1)            # planes
    $bw.Write([int16]32)           # bit count
    $bw.Write([int]0)              # BI_RGB
    $bw.Write([int]($xorSize + $andSize))
    $bw.Write([int]0); $bw.Write([int]0)   # ppm x/y
    $bw.Write([int]0); $bw.Write([int]0)   # clr used/important
    # XOR (color) rows, bottom-up
    for ($y = $N - 1; $y -ge 0; $y--) {
        $bw.Write($srcBytes, ($y * $stride), $rowBytes)
    }
    # AND mask, all opaque (zero)
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
    $bw.Write([int16]0)       # reserved
    $bw.Write([int16]1)       # type = icon
    $bw.Write([int16]$count)
    $offset = 6 + (16 * $count)
    for ($i = 0; $i -lt $count; $i++) {
        $w = $bmps[$i].Width; $h = $bmps[$i].Height
        $bw.Write([byte]$(if ($w -ge 256) { 0 } else { $w }))
        $bw.Write([byte]$(if ($h -ge 256) { 0 } else { $h }))
        $bw.Write([byte]0)    # palette
        $bw.Write([byte]0)    # reserved
        $bw.Write([int16]1)   # planes
        $bw.Write([int16]32)  # bit count
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
Save-Png (New-Square $src 192) (Join-Path $www 'icon-192.png')
Save-Png (New-Square $src 512) (Join-Path $www 'icon-512.png')
Save-Png (New-Square $src 180) (Join-Path $www 'apple-touch-icon.png')
Write-Ico @((New-Square $src 16), (New-Square $src 32), (New-Square $src 48)) (Join-Path $www 'favicon.ico')

Write-Host "Generating application icon..."
Write-Ico @(
    (New-Square $src 16), (New-Square $src 32), (New-Square $src 48),
    (New-Square $src 64), (New-Square $src 128), (New-Square $src 256)
) (Join-Path $app 'app.ico')

$src.Dispose()
Write-Host "Done." -ForegroundColor Green
