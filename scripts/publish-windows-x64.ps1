param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputPath,

    [switch]$SelfContained,

    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Project = Join-Path $Root "desktop\DroneDash_x64.Desktop\DroneDash_x64.Desktop.csproj"
$RuntimeIdentifier = "win-x64"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $Flavor = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
    $OutputPath = Join-Path $Root "artifacts\windows-x64\$Flavor"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $Root $OutputPath
}

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

if ($Clean -and (Test-Path $OutputPath)) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

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

Write-Host "DroneDash_x64 Windows publish"
Write-Host "Project:       $Project"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $RuntimeIdentifier"
Write-Host "Self-contained:$($SelfContained.IsPresent)"
Write-Host "Output:        $OutputPath"
Write-Host ""

& dotnet restore $Project --runtime $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed."
}

$SelfContainedValue = if ($SelfContained) { "true" } else { "false" }
$PublishArgs = @(
    "publish",
    $Project,
    "--configuration", $Configuration,
    "--runtime", $RuntimeIdentifier,
    "--self-contained", $SelfContainedValue,
    "--no-restore",
    "--output", $OutputPath,
    "-p:PlatformTarget=x64",
    "-p:Prefer32Bit=false"
)

& dotnet @PublishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$ExePath = Join-Path $OutputPath "DroneDash_x64.Desktop.exe"
if (-not (Test-Path $ExePath -PathType Leaf)) {
    throw "Publish completed without DroneDash_x64.Desktop.exe: $ExePath"
}

function Get-PeMachine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $Stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)

    try {
        if ($Stream.Length -lt 64) {
            throw "File is too small to be a PE executable: $Path"
        }

        $Reader = New-Object System.IO.BinaryReader($Stream)

        if ($Reader.ReadUInt16() -ne 0x5A4D) {
            throw "Missing DOS MZ signature: $Path"
        }

        $Stream.Position = 0x3C
        $PeOffset = $Reader.ReadInt32()

        if ($PeOffset -lt 0 -or ($PeOffset + 6) -gt $Stream.Length) {
            throw "Invalid PE header offset in $Path"
        }

        $Stream.Position = $PeOffset
        if ($Reader.ReadUInt32() -ne 0x00004550) {
            throw "Missing PE signature: $Path"
        }

        return $Reader.ReadUInt16()
    }
    finally {
        $Stream.Dispose()
    }
}

$Machine = Get-PeMachine -Path $ExePath
if ($Machine -ne 0x8664) {
    throw ("Published apphost is not AMD64/x64. PE machine=0x{0:X4}" -f $Machine)
}

$RequiredFiles = @(
    "DroneDash_x64.Desktop.exe",
    "DroneDash_x64.Desktop.dll",
    "DroneDash_x64.Desktop.runtimeconfig.json",
    "planning\route-editor.html",
    "pv\pv-map.html",
    "smart-farming\opencv_m3m.py",
    "smart-farming\dronedash_worker\cli.py"
)

foreach ($RelativePath in $RequiredFiles) {
    $FullPath = Join-Path $OutputPath $RelativePath
    if (-not (Test-Path $FullPath -PathType Leaf)) {
        throw "Required Windows x64 publish file is missing: $RelativePath"
    }
}

$ThermalSdkIncluded = Test-Path (Join-Path $OutputPath "thermal-sdk\libdirp.dll") -PathType Leaf

$Manifest = [ordered]@{
    product = "DroneDash_x64"
    configuration = $Configuration
    runtimeIdentifier = $RuntimeIdentifier
    platformTarget = "x64"
    peMachine = "AMD64 (0x8664)"
    selfContained = [bool]$SelfContained
    dotnetSdk = $DotnetVersion
    thermalSdkIncluded = [bool]$ThermalSdkIncluded
}

$ManifestPath = Join-Path $OutputPath "dronedash-publish.json"
$Manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8

Write-Host ""
Write-Host "[ OK ] Windows x64 publish validated."
Write-Host "[ OK ] PE machine: AMD64 (0x8664)"
Write-Host "[ OK ] Required UI/worker resources are present."
if ($ThermalSdkIncluded) {
    Write-Host "[ OK ] DJI Thermal SDK runtime included."
}
else {
    Write-Host "[INFO] DJI Thermal SDK runtime not staged; thermal analysis remains optional."
}
Write-Host "Output: $OutputPath"
