$ErrorActionPreference = "Stop"

if (-not (Get-Command adb -ErrorAction SilentlyContinue)) {
  throw "adb wurde nicht gefunden. Android Platform Tools installieren und adb in PATH aufnehmen."
}

$devices = adb devices
Write-Host $devices
adb forward tcp:49152 tcp:49152
Write-Host "Bridge weitergeleitet: http://127.0.0.1:49152/"
