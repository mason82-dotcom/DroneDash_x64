$ErrorActionPreference = "Stop"
Push-Location "$PSScriptRoot\..\rc-agent"
try {
  if (Test-Path ".\gradlew.bat") {
    .\gradlew.bat :app:assembleDebug
  } elseif (Get-Command gradle -ErrorAction SilentlyContinue) {
    gradle :app:assembleDebug
  } else {
    throw "Gradle Wrapper fehlt. Einmal 'gradle wrapper --gradle-version 8.12' im rc-agent Ordner ausführen."
  }
}
finally {
  Pop-Location
}
