param(
    [string]$Remote = "https://github.com/mason82-dotcom/DroneDash_x64.git",
    [string]$Branch = "main",
    [string]$Message = "Update DroneDash_x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $Root

try {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "Git was not found in PATH."
    }

    if (-not (Test-Path (Join-Path $Root ".git"))) {
        & git init
        if ($LASTEXITCODE -ne 0) {
            throw "git init failed."
        }
    }

    & git symbolic-ref HEAD "refs/heads/$Branch"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not set branch '$Branch'."
    }

    $Remotes = @(& git remote)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list Git remotes."
    }

    if ($Remotes -contains "origin") {
        & git remote set-url origin $Remote
        if ($LASTEXITCODE -ne 0) {
            throw "Could not set origin URL."
        }
    }
    else {
        & git remote add origin $Remote
        if ($LASTEXITCODE -ne 0) {
            throw "Could not add origin remote."
        }
    }

    Write-Host "Origin configured:"
    & git remote -v
    if ($LASTEXITCODE -ne 0) {
        throw "Could not display Git remotes."
    }

    & git add -A
    if ($LASTEXITCODE -ne 0) {
        throw "git add failed."
    }

    $Changes = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed."
    }

    if ($Changes.Count -gt 0) {
        & git commit -m $Message
        if ($LASTEXITCODE -ne 0) {
            throw "git commit failed. Configure your Git identity outside this script."
        }
    }
    else {
        Write-Host "No local changes to commit."
    }

    & git fetch origin
    if ($LASTEXITCODE -ne 0) {
        throw "git fetch failed. Check network access and GitHub authentication."
    }

    $RemoteRef = "refs/remotes/origin/$Branch"
    & git show-ref --verify --quiet $RemoteRef
    $RemoteBranchExists = ($LASTEXITCODE -eq 0)

    if ($RemoteBranchExists) {
        & git merge "origin/$Branch" --allow-unrelated-histories --no-edit
        if ($LASTEXITCODE -ne 0) {
            throw "git merge failed. Resolve conflicts, commit them, then rerun this script."
        }
    }

    & git push -u origin $Branch
    if ($LASTEXITCODE -ne 0) {
        throw "git push failed."
    }

    Write-Host ""
    Write-Host "Upload completed:"
    Write-Host $Remote
}
finally {
    Pop-Location
}
