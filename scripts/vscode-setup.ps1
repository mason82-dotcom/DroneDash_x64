param(
    [switch]$InstallRecommendedExtensions,
    [switch]$SkipDoctor,
    [switch]$OpenWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Workspace = Join-Path $Root "DroneDash_x64.code-workspace"

$RecommendedExtensions = @(
    "ms-dotnettools.csdevkit",
    "ms-vscode.powershell",
    "ms-python.python",
    "vscjava.vscode-java-pack",
    "vscjava.vscode-gradle",
    "fwcd.kotlin"
)

Write-Host "DroneDash_x64 VS Code preflight"
Write-Host "Root: $Root"
Write-Host ""

$Code = Get-Command code -ErrorAction SilentlyContinue
if (-not $Code) {
    Write-Warning "VS Code command 'code' is not in PATH. The repository can still be opened manually with DroneDash_x64.code-workspace."
}
else {
    $Installed = @(
        & $Code.Source --list-extensions 2>$null |
            ForEach-Object { $_.Trim().ToLowerInvariant() } |
            Where-Object { $_ }
    )

    foreach ($Extension in $RecommendedExtensions) {
        if ($Installed -contains $Extension.ToLowerInvariant()) {
            Write-Host "[ OK ] VS Code extension: $Extension" -ForegroundColor Green
            continue
        }

        if ($InstallRecommendedExtensions) {
            Write-Host "[....] Installing VS Code extension: $Extension"
            & $Code.Source --install-extension $Extension
            if ($LASTEXITCODE -ne 0) {
                throw "VS Code extension installation failed: $Extension"
            }
            Write-Host "[ OK ] Installed: $Extension" -ForegroundColor Green
        }
        else {
            Write-Warning "Recommended VS Code extension is missing: $Extension"
        }
    }
}

if (-not $SkipDoctor) {
    Write-Host ""
    & (Join-Path $PSScriptRoot "doctor.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "DroneDash environment doctor reported blocking failures."
    }
}

Write-Host ""
Write-Host "[ OK ] VS Code repository configuration is ready."
Write-Host "Open: $Workspace"
Write-Host "Build: Ctrl+Shift+B"
Write-Host "Debug: F5 -> 'DroneDash + Mock RC' or 'DroneDash Desktop (Debug x64)'"

if ($OpenWorkspace) {
    if (-not $Code) {
        throw "Cannot open the workspace automatically because 'code' is not available in PATH."
    }

    & $Code.Source $Workspace
}
