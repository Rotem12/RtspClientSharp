param(
    [string]$PathName = "test",
    [int]$Port = 8554,
    [string]$Resolution = "1280x720",
    [int]$FrameRate = 30
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$toolsDir = Join-Path $root ".tools"
$mediaMtxDir = Join-Path $toolsDir "mediamtx"
$mediaMtxExe = Join-Path $mediaMtxDir "mediamtx.exe"
$mediaMtxLog = Join-Path $root "rtsp-test-mediamtx.log"
$mediaMtxOutLog = Join-Path $root "rtsp-test-mediamtx.out.log"
$publisherLog = Join-Path $root "rtsp-test-publisher.log"
$publisherOutLog = Join-Path $root "rtsp-test-publisher.out.log"
$url = "rtsp://127.0.0.1:$Port/$PathName"

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    throw "ffmpeg was not found on PATH."
}

if (-not (Test-Path $mediaMtxExe)) {
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $mediaMtxDir | Out-Null

    $version = "1.18.1"
    $zipPath = Join-Path $toolsDir "mediamtx_v$version`_windows_amd64.zip"
    $downloadUrl = "https://github.com/bluenviron/mediamtx/releases/download/v$version/mediamtx_v$version`_windows_amd64.zip"

    Write-Host "Downloading MediaMTX $version..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $mediaMtxDir -Force
}

& "$PSScriptRoot\Stop-TestRtspStream.ps1" -Quiet

$server = Start-Process -FilePath $mediaMtxExe `
    -WorkingDirectory $mediaMtxDir `
    -RedirectStandardOutput $mediaMtxOutLog `
    -RedirectStandardError $mediaMtxLog `
    -WindowStyle Hidden `
    -PassThru

Start-Sleep -Seconds 2

$publisherArgs = @(
    "-hide_banner",
    "-loglevel", "info",
    "-re",
    "-f", "lavfi",
    "-i", "testsrc2=size=$Resolution`:rate=$FrameRate",
    "-c:v", "libx264",
    "-preset", "veryfast",
    "-tune", "zerolatency",
    "-pix_fmt", "yuv420p",
    "-g", "$($FrameRate * 2)",
    "-an",
    "-f", "rtsp",
    "-rtsp_transport", "tcp",
    $url
)

$publisher = Start-Process -FilePath "ffmpeg" `
    -ArgumentList $publisherArgs `
    -RedirectStandardOutput $publisherOutLog `
    -RedirectStandardError $publisherLog `
    -WindowStyle Hidden `
    -PassThru

Start-Sleep -Seconds 3

if ($server.HasExited) {
    throw "MediaMTX exited early. See $mediaMtxLog"
}

if ($publisher.HasExited) {
    throw "FFmpeg publisher exited early. See $publisherLog"
}

Write-Host "RTSP test stream is running."
Write-Host "URL: $url"
Write-Host "MediaMTX PID: $($server.Id)"
Write-Host "Publisher PID: $($publisher.Id)"
Write-Host "Logs:"
Write-Host "  $mediaMtxLog"
Write-Host "  $mediaMtxOutLog"
Write-Host "  $publisherLog"
