param(
    # Release tag on github.com/colmap/colmap, e.g. "3.11.1". Default: latest release.
    [string]$Version = "latest",

    # Offline installation from an already downloaded COLMAP Windows CUDA zip.
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$ArchivePath,

    [string]$Destination = (Join-Path $env:LOCALAPPDATA "DroneDash\colmap"),

    # Do not set the user environment variable DRONEDASH_COLMAP.
    [switch]$NoEnvironment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("DroneDash-COLMAP-" + [Guid]::NewGuid().ToString("N"))

Write-Host "COLMAP (CUDA) installation for DroneDash"
Write-Host "Destination: $Destination"
Write-Host ""

if (-not (Get-Command nvidia-smi -ErrorAction SilentlyContinue) -and
    -not (Test-Path (Join-Path $env:WINDIR "System32\nvidia-smi.exe"))) {
    Write-Warning "nvidia-smi not found. COLMAP's dense reconstruction needs an NVIDIA GPU with a current driver."
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

    $archive = $ArchivePath
    if ([string]::IsNullOrWhiteSpace($archive)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $api = if ($Version -eq "latest") {
            "https://api.github.com/repos/colmap/colmap/releases/latest"
        }
        else {
            "https://api.github.com/repos/colmap/colmap/releases/tags/$Version"
        }

        Write-Host "Reading release information: $api"
        $release = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "DroneDash" }

        # Asset names differ between releases (COLMAP-3.9.1-windows-cuda.zip,
        # colmap-x64-windows-cuda.zip); never pick the no-cuda build.
        $asset = $release.assets |
            Where-Object { $_.name -match 'windows' -and $_.name -match 'cuda' -and $_.name -notmatch 'no-?cuda' -and $_.name -like '*.zip' } |
            Select-Object -First 1

        if (-not $asset) {
            throw "Release $($release.tag_name) has no Windows CUDA zip. Download it manually and pass -ArchivePath."
        }

        $archive = Join-Path $tempRoot $asset.name
        Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB) from release $($release.tag_name) ..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing
    }

    $extract = Join-Path $tempRoot "extract"
    Write-Host "Extracting ..."
    Expand-Archive -LiteralPath (Resolve-Path $archive) -DestinationPath $extract -Force

    # The archive contains one top-level folder with COLMAP.bat (or bin\colmap.exe).
    $launcher = Get-ChildItem -Path $extract -Recurse -File -Filter "COLMAP.bat" | Select-Object -First 1
    if (-not $launcher) {
        $launcher = Get-ChildItem -Path $extract -Recurse -File -Filter "colmap.exe" | Select-Object -First 1
    }

    if (-not $launcher) {
        throw "Neither COLMAP.bat nor colmap.exe was found in the archive."
    }

    $installRoot = if ($launcher.Name -eq "colmap.exe" -and $launcher.Directory.Name -eq "bin") {
        $launcher.Directory.Parent.FullName
    }
    else {
        $launcher.Directory.FullName
    }

    if (Test-Path $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Move-Item -LiteralPath $installRoot -Destination $Destination

    $executable = Join-Path $Destination "COLMAP.bat"
    if (-not (Test-Path $executable)) {
        $executable = Join-Path $Destination "bin\colmap.exe"
    }

    Write-Host ""
    Write-Host "Checking installation ..."
    $help = (& $executable -h 2>&1 | Out-String)
    $header = ($help -split "`r?`n" | Where-Object { $_ -match 'COLMAP|CUDA' } | Select-Object -First 2) -join " "
    Write-Host $header

    if ($help -match 'without CUDA') {
        Write-Warning "This COLMAP build has no CUDA support; DroneDash needs the CUDA build for dense reconstruction."
    }
    elseif ($help -notmatch 'with CUDA') {
        Write-Warning "CUDA support could not be confirmed from 'colmap -h'."
    }

    if (-not $NoEnvironment) {
        [Environment]::SetEnvironmentVariable("DRONEDASH_COLMAP", $Destination, "User")
        Write-Host "User environment variable DRONEDASH_COLMAP = $Destination"
        Write-Host "Restart DroneDash so it picks up the variable."
    }

    Write-Host ""
    Write-Host "[ OK ] COLMAP installed: $executable"
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
