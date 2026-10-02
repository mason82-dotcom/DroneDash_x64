param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot "DJI_20261002154302_0001_T.JPG"

& (Join-Path $PSScriptRoot "verify-m3t-thermal-fixture.ps1")

$runtime = Join-Path $repoRoot "desktop\DroneDash_x64.Desktop\third_party\dji-tsdk\runtime"
$libdirp = Join-Path $runtime "libdirp.dll"

if (-not (Test-Path -LiteralPath $libdirp -PathType Leaf)) {
    Write-Warning "DJI Thermal SDK v1.8 runtime is not staged. Run scripts\install-dji-thermal-sdk.ps1 first."
    exit 2
}

Write-Host ""
Write-Host "Running real dirp_measure_ex smoke test against the M3T fixture..."
Push-Location $repoRoot
try {
    dotnet run --project desktop/DroneDash_x64.ThermalSmoke/DroneDash_x64.ThermalSmoke.csproj --configuration $Configuration -- $fixture
    if ($LASTEXITCODE -ne 0) {
        throw "Thermal smoke test failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
