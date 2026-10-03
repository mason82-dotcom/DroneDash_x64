Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RcRoot = (Resolve-Path "$PSScriptRoot\..\rc-agent").Path
Push-Location $RcRoot

try {
    if (Test-Path ".\gradlew.bat") {
        $GradleCommand = ".\gradlew.bat"
    }
    elseif (Get-Command gradle -ErrorAction SilentlyContinue) {
        $GradleCommand = "gradle"
    }
    else {
        throw "Gradle 8.12 wurde nicht gefunden. Im rc-agent Ordner einmal 'gradle wrapper --gradle-version 8.12' ausführen."
    }

    $VersionText = (& $GradleCommand --version 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Gradle konnte nicht gestartet werden."
    }

    if ($VersionText -notmatch '(?m)^Gradle 8\.12\s*$') {
        $Detected = (($VersionText -split "\r?\n") | Where-Object { $_ -match '^Gradle ' } | Select-Object -First 1)
        throw "Falsche Gradle-Version: '$Detected'. DroneDash RC Agent ist auf Gradle 8.12 festgelegt."
    }

    Write-Host "Android Build: Gradle 8.12 / AGP 8.10.1 / API 36"
    & $GradleCommand :app:assembleDebug

    if ($LASTEXITCODE -ne 0) {
        throw "Android-Build fehlgeschlagen."
    }

    $Apk = Join-Path $RcRoot "app\build\outputs\apk\debug\app-debug.apk"
    if (-not (Test-Path $Apk)) {
        throw "Build meldete keinen Fehler, aber die Debug-APK fehlt: $Apk"
    }

    Get-Item $Apk |
        Select-Object FullName, Length, LastWriteTime
}
finally {
    Pop-Location
}
