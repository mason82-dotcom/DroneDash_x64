param(
    [string]$Configuration = "Debug"
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
Write-Host "Fixture and TSDK runtime are present."
Write-Host "Launch DroneDash_x64.Desktop and click: Medien -> M3T Testbild"
Write-Host "The viewer will load the real R-JPEG and immediately run the TSDK thermal analysis."
Write-Host ""
Write-Host "dotnet run --project desktop/DroneDash_x64.Desktop/DroneDash_x64.Desktop.csproj --configuration $Configuration"
