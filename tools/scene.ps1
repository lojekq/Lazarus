# Scene GIF (spec 03 §10): SecondWind for real, not --demo, restores an Explorer window and a Windows Terminal with three tabs
# in the centre of DISPLAY1. Owner's slot only (00-shared §9.6): Do not disturb on, plain wallpaper, nothing personal on DISPLAY1,
# the working SecondWind closed (it holds the mutex and the hotkeys). Run from the repo root: pwsh tools/scene.ps1 [-Lang ru]
# The app closes the windows it opened ~10 s after the take, so the next take starts from the same desktop.
param(
  [ValidateSet('en', 'ru')][string]$Lang = 'en',
  [int]$X = 1080, [int]$Y = 344, [int]$W = 1280, [int]$H = 720,   # Demo/Scene.cs uses the same area
  [int]$Seconds = 24,
  [int]$Fps = 12, [int]$Width = 800,   # over 5 MB: -Fps 10 or -Width 720
  [string]$Out = "docs/demo-$Lang.gif"
)
$ErrorActionPreference = 'Stop'; $PSNativeCommandUseErrorActionPreference = $true
$App = 'SecondWind'
if (Get-Process $App -ErrorAction Ignore) { throw "close the running $App first (tray → Exit)" }
dotnet build "src/$App" -c Release | Out-Null
$exe = "src/$App/bin/Release/net10.0-windows/$App.exe"
$raw = "$env:TEMP\$App-scene.mkv"; $pal = "$env:TEMP\$App-scene-pal.png"

$lock = 'C:\Demo\desktop.lock'
while ($true) {
  try { New-Item $lock -Value $App -ErrorAction Stop | Out-Null; break }
  catch { if (-not (Test-Path $lock)) { throw }; Write-Host "desktop.lock is held by $(Get-Content $lock -ErrorAction Ignore), waiting..."; Start-Sleep 10 }
}
try {
  $ff = Start-Process ffmpeg -PassThru -NoNewWindow -ArgumentList '-y', '-loglevel', 'error', '-f', 'gdigrab', '-framerate', '30', '-draw_mouse', '0',
    '-offset_x', $X, '-offset_y', $Y, '-video_size', "${W}x${H}", '-t', $Seconds, '-i', 'desktop', '-c:v', 'libx264rgb', '-crf', '0', '-preset', 'ultrafast', $raw
  Start-Sleep 1
  $proc = Start-Process $exe -PassThru -ArgumentList '--autoplay', '--background', '--lang', $Lang
  $ff.WaitForExit()
  Start-Sleep 12   # the app closes its windows
  $proc | Stop-Process -ErrorAction Ignore
} finally { Remove-Item $lock }

$f = "fps=$Fps,scale=${Width}:-1:flags=lanczos"
ffmpeg -y -loglevel error -i $raw -vf "$f,palettegen=stats_mode=diff" $pal
ffmpeg -y -loglevel error -i $raw -i $pal -map_metadata -1 `
  -lavfi "$f[v];[v][1:v]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" $Out
Remove-Item $raw, $pal
$mb = (Get-Item $Out).Length / 1MB
Write-Host ("{0}: {1:N2} MB{2}" -f $Out, $mb, $(if ($mb -gt 5) { ' (over 5 MB: try -Fps 10 or -Width 720)' }))
Write-Host 'Check every frame before committing: nothing personal, the screen was on (a frozen frame = the monitor slept).'
