<#
.SYNOPSIS
  Downloads a static Windows ffmpeg build and installs ffmpeg.exe into the
  host's ./tools folder (which the build copies to the app output directory).

.NOTES
  Run from anywhere:  powershell -ExecutionPolicy Bypass -File scripts\get-ffmpeg.ps1

  Speed note: PowerShell's Invoke-WebRequest paints a progress bar that it
  redraws on EVERY received chunk. On a large file that rendering overhead can
  slow the download by 10-50x (it looks like the download is "stuck"). Setting
  $ProgressPreference = 'SilentlyContinue' disables it and restores full speed.
  We also prefer BITS (Start-BitsTransfer) which is resumable and far more
  reliable for a ~100 MB file on a slow link.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # <-- critical: keeps the download fast

$toolsDir = Join-Path $PSScriptRoot '..\src\ToPlay.Host\tools'
$toolsDir = [System.IO.Path]::GetFullPath($toolsDir)
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

$exe = Join-Path $toolsDir 'ffmpeg.exe'
if (Test-Path $exe) {
    Write-Host "ffmpeg already installed at: $exe"
    Write-Host "(delete it and re-run this script to update.)"
    exit 0
}

# Gyan.dev 'essentials' release build includes h264 encoders (nvenc/qsv/amf/x264).
# We try the gyan.dev vanity URL first, then a GitHub mirror as a fallback in
# case gyan.dev is throttling or unreachable.
$urls = @(
    'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip',
    'https://github.com/GyanD/codexffmpeg/releases/latest/download/ffmpeg-release-essentials.zip'
)

$tmp = Join-Path $env:TEMP ("toplay-ffmpeg-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zip = Join-Path $tmp 'ffmpeg.zip'

# A real User-Agent matters: some CDNs stall connections that send none.
$headers = @{ 'User-Agent' = 'ToPlay-Setup/1.0'; 'Accept' = '*/*' }

# Download the archive. We prefer BITS (Start-BitsTransfer): it runs in the
# Windows background service, is resumable, and is far more reliable for a
# ~100 MB file on a slow link than Invoke-WebRequest. If BITS is unavailable we
# fall back to Invoke-WebRequest (with the progress bar disabled, see above).
function Get-Archive([string]$url, [string]$outFile) {
    if (Get-Command Start-BitsTransfer -ErrorAction SilentlyContinue) {
        try {
            Write-Host "  (using BITS background transfer)"
            Start-BitsTransfer -Source $url -Destination $outFile -Priority Foreground -ErrorAction Stop
            return
        }
        catch {
            Write-Host ("  BITS failed ({0}); falling back to Invoke-WebRequest." -f $_.Exception.Message) -ForegroundColor Yellow
            if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }
        }
    }
    Invoke-WebRequest -Uri $url -OutFile $outFile -Headers $headers -UseBasicParsing -TimeoutSec 1800
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $downloaded = $false
    foreach ($url in $urls) {
        try {
            Write-Host "Downloading ffmpeg (~100 MB) from:"
            Write-Host "  $url"
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            Get-Archive $url $zip
            $sw.Stop()
            $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
            Write-Host ("Downloaded {0} MB in {1}s." -f $mb, [int]$sw.Elapsed.TotalSeconds) -ForegroundColor Green
            $downloaded = $true
            break
        }
        catch {
            Write-Host ("  download failed from this source: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
            if (Test-Path $zip) { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
        }
    }
    if (-not $downloaded) { throw "Could not download ffmpeg from any source." }

    Write-Host "Extracting..."
    Expand-Archive -Path $zip -DestinationPath $tmp -Force

    $found = Get-ChildItem -Path $tmp -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $found) { throw "ffmpeg.exe was not found inside the downloaded archive." }

    Copy-Item $found.FullName $exe -Force
    Write-Host ""
    Write-Host "Installed ffmpeg -> $exe" -ForegroundColor Green
}
finally {
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
