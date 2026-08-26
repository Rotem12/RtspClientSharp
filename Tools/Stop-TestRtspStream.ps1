param(
    [switch]$Quiet
)

$ErrorActionPreference = "SilentlyContinue"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$mediaMtxPath = Join-Path $root ".tools\mediamtx\mediamtx.exe"

$processes = Get-CimInstance Win32_Process |
    Where-Object {
        ($_.ExecutablePath -eq $mediaMtxPath) -or
        ($_.Name -ieq "ffmpeg.exe" -and $_.CommandLine -like "*rtsp://127.0.0.1:8554/test*")
    }

foreach ($process in $processes) {
    Stop-Process -Id $process.ProcessId -Force
    if (-not $Quiet) {
        Write-Host "Stopped PID $($process.ProcessId): $($process.Name)"
    }
}

if (-not $Quiet -and -not $processes) {
    Write-Host "No local RTSP test stream processes were running."
}
