param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Test,

    [switch]$Publish,

    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Solution = Join-Path $Root "DroneDash_x64.slnx"
$Tests = Join-Path $Root "desktop\DroneDash_x64.Tests\DroneDash_x64.Tests.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found in PATH."
}

$DotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $DotnetVersion -notmatch '^10\.') {
    throw ".NET 10 SDK is required. Detected: '$DotnetVersion'."
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw "DroneDash_x64 requires a 64-bit Windows operating system."
}

Write-Host "DroneDash_x64 desktop build"
Write-Host "Configuration: $Configuration"
Write-Host "OS x64:        $([Environment]::Is64BitOperatingSystem)"
Write-Host "Process x64:   $([Environment]::Is64BitProcess)"
Write-Host ""

& dotnet restore $Solution
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed."
}

& dotnet build $Solution --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed."
}

if ($Test) {
    & dotnet test $Tests --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed."
    }
}

if ($Publish) {
    $PublishScript = Join-Path $PSScriptRoot "publish-windows-x64.ps1"
    if ($SelfContained) {
        & $PublishScript -Configuration $Configuration -SelfContained -Clean
    }
    else {
        & $PublishScript -Configuration $Configuration -Clean
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Windows x64 publish failed."
    }
}

Write-Host ""
Write-Host "[ OK ] DroneDash_x64 desktop build completed."
