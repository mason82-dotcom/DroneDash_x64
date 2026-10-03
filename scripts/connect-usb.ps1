param(
    [string]$Serial
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Get-Command adb -ErrorAction SilentlyContinue)) {
    throw "adb wurde nicht gefunden. Android Platform Tools installieren und adb in PATH aufnehmen."
}

$DeviceOutput = @(& adb devices)
if ($LASTEXITCODE -ne 0) {
    throw "adb devices fehlgeschlagen."
}

$Devices = @(
    $DeviceOutput |
        Select-Object -Skip 1 |
        ForEach-Object {
            if ($_ -match '^\s*(\S+)\s+device\s*$') {
                $Matches[1]
            }
        }
)

$Unavailable = @(
    $DeviceOutput |
        Select-Object -Skip 1 |
        Where-Object { $_ -match '\s+(offline|unauthorized)\s*$' }
)

if ($Unavailable.Count -gt 0) {
    Write-Warning ("Nicht verwendbare ADB-Geräte:" + [Environment]::NewLine + ($Unavailable -join [Environment]::NewLine))
}

if (-not [string]::IsNullOrWhiteSpace($Serial)) {
    if ($Devices -notcontains $Serial) {
        throw "ADB-Gerät '$Serial' ist nicht im Status 'device'."
    }
    $Selected = $Serial
}
elseif ($Devices.Count -eq 1) {
    $Selected = $Devices[0]
}
elseif ($Devices.Count -eq 0) {
    throw "Kein autorisiertes ADB-Gerät gefunden. RC Pro verbinden und USB-Debugging autorisieren."
}
else {
    throw "Mehrere ADB-Geräte gefunden: $($Devices -join ', '). Mit -Serial <device-id> gezielt auswählen."
}

Write-Host "ADB-Gerät: $Selected"

& adb -s $Selected forward --remove tcp:49152 2>$null | Out-Null

& adb -s $Selected forward tcp:49152 tcp:49152
if ($LASTEXITCODE -ne 0) {
    throw "ADB-Portweiterleitung für $Selected konnte nicht eingerichtet werden."
}

Write-Host "Bridge weitergeleitet: http://127.0.0.1:49152/"

try {
    $Health = Invoke-RestMethod -Uri "http://127.0.0.1:49152/api/v1/health" -TimeoutSec 5

    Write-Host (
        "Bridge Health: SDK={0}, Phase={1}, Registered={2}, Aircraft={3}" -f
        $Health.sdkVersion,
        $Health.sdkPhase,
        $Health.registered,
        $Health.productConnected
    )
}
catch {
    Write-Warning "Portweiterleitung steht, aber /api/v1/health antwortet noch nicht. DroneDash RC Bridge auf dem Controller starten."
}
