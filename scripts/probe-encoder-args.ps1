# Verifies that every low-latency encoder option ToPlay passes to ffmpeg is
# actually accepted by the bundled build. An unknown option would make ffmpeg
# exit instantly, which on a phone looks like a permanently black screen — so
# this is worth checking whenever the encoder arguments change.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-encoder-args.ps1

$ff = Join-Path $PSScriptRoot '..\src\ToPlay.Host\tools\ffmpeg.exe'
if (-not (Test-Path $ff)) { Write-Host "ffmpeg not found at $ff"; exit 1 }

Write-Host ((& $ff -version 2>&1 | Select-Object -First 1))
Write-Host ''

$cases = [ordered]@{
  'libx264'    = '-preset ultrafast -tune zerolatency -bf 0 -x264-params "nal-hrd=cbr:repeat-headers=1:sliced-threads=1:sync-lookahead=0:rc-lookahead=0"'
  'h264_nvenc' = '-preset p1 -tune ull -rc cbr -zerolatency 1 -delay 0 -forced-idr 1 -rc-lookahead 0 -no-scenecut 1 -bf 0'
  'h264_qsv'   = '-preset veryfast -low_power 0 -async_depth 1 -look_ahead 0 -bf 0'
  'h264_amf'   = '-usage ultralowlatency -rc cbr -quality speed -preanalysis 0 -bf 0'
}

foreach ($codec in $cases.Keys) {
  $opts = $cases[$codec]
  $pix  = if ($codec -eq 'libx264') { 'yuv420p' } else { 'nv12' }
  $args = "-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x144:r=30 " +
          "-frames:v 5 -an -c:v $codec $opts -b:v 4000k -maxrate 4000k -bufsize 2000k " +
          "-g 60 -keyint_min 30 -pix_fmt $pix -bsf:v h264_metadata=aud=insert -f h264 NUL"

  $err = & cmd /c "`"$ff`" $args 2>&1"
  $text = ($err | Out-String).Trim()

  # A missing GPU/driver is fine: that encoder is simply unavailable on this PC
  # and ToPlay's own probe already falls back to the next one. Check this FIRST,
  # because a hardware failure also prints generic "Invalid argument" noise.
  $noHw = $text -match 'Cannot load|No capable devices|Error creating a MFX session|mfx implementation|failed to open|No device available|InitializeEncoder|DLL'
  # An option ffmpeg does not know about is a real bug in our argument builder.
  $bad = (-not $noHw) -and ($text -match 'Unrecognized option|Unknown encoder|Option not found|Unable to parse|Invalid argument')


  if ($bad)      { Write-Host ("{0,-12} BAD OPTION" -f $codec) -ForegroundColor Red; Write-Host "   $text" }
  elseif ($noHw) { Write-Host ("{0,-12} options ok (no hardware on this PC)" -f $codec) -ForegroundColor Yellow }
  elseif ($text) { Write-Host ("{0,-12} options ok (warnings)" -f $codec) -ForegroundColor Yellow; Write-Host "   $text" }
  else           { Write-Host ("{0,-12} OK - encoded successfully" -f $codec) -ForegroundColor Green }
}
