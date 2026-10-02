param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$ArchivePath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repoRoot "desktop\DroneDash_x64.Desktop\third_party\dji-tsdk\runtime"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("DroneDash-TSDK-" + [Guid]::NewGuid().ToString("N"))

Write-Host "DJI Thermal SDK v1.8 staging"
Write-Host "Source: $ArchivePath"
Write-Host "Destination: $destination"
Write-Host ""
Write-Host "By continuing you confirm that you have reviewed DJI's Thermal SDK License.txt / SDK EULA."

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    Expand-Archive -LiteralPath (Resolve-Path $ArchivePath) -DestinationPath $tempRoot -Force

    $libDirp = Get-ChildItem -Path $tempRoot -Recurse -File -Filter "libdirp.dll" |
        Where-Object { $_.DirectoryName -match "release_x64" } |
        Select-Object -First 1

    if (-not $libDirp) {
        throw "Windows x64 libdirp.dll was not found in the supplied archive."
    }

    $runtimeSource = $libDirp.Directory.FullName
    $required = @(
        "libdirp.dll",
        "libv_list.ini"
    )

    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $runtimeSource $file))) {
            throw "Required DJI TSDK runtime file missing: $file"
        }
    }

    if (Test-Path $destination) {
        Remove-Item -Path $destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null

    Get-ChildItem -Path $runtimeSource -File |
        Where-Object { $_.Extension -in ".dll", ".ini" } |
        Copy-Item -Destination $destination -Force

    Set-Content -Path (Join-Path $destination "VERSION.txt") -Value "1.8" -Encoding ASCII

    $copied = Get-ChildItem -Path $destination -File
    Write-Host ""
    Write-Host "Installed $($copied.Count) runtime file(s):"
    $copied | ForEach-Object { Write-Host "  $($_.Name)" }
    Write-Host ""
    Write-Host "Rebuild DroneDash_x64.Desktop. The runtime will be copied to output\thermal-sdk."
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force
    }
}
