param(
    [string]$Remote = "https://github.com/mason82-dotcom/DroneDash_x64.git",
    [string]$Branch = "main",
    [string]$Message = "Update DroneDash_x64",
    [switch]$AllowNonNoreplyEmail
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
        throw "This folder is not a Git clone. Clone the repository first; this script will never run 'git init' or merge unrelated histories."
    }

    $CurrentBranch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($CurrentBranch)) {
        throw "Could not determine the current Git branch. Detached HEAD is not supported."
    }

    if ($CurrentBranch -ne $Branch) {
        throw "Current branch is '$CurrentBranch', expected '$Branch'. Switch branches explicitly before pushing."
    }

    $Remotes = @(& git remote)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list Git remotes."
    }

    if ($Remotes -contains "origin") {
        $OriginUrl = (& git remote get-url origin).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Could not read origin URL."
        }

        if ($OriginUrl -ne $Remote) {
            throw "origin points to '$OriginUrl', expected '$Remote'. Refusing to rewrite the remote automatically."
        }
    }
    else {
        & git remote add origin $Remote
        if ($LASTEXITCODE -ne 0) {
            throw "Could not add origin remote."
        }
    }

    $Email = (& git config --get user.email).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Email)) {
        throw "Git user.email is not configured. Configure a GitHub noreply address before committing."
    }

    if (-not $AllowNonNoreplyEmail -and
        -not $Email.EndsWith("@users.noreply.github.com", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Git user.email is '$Email'. Refusing to create a commit with a non-noreply address. Configure your GitHub noreply address or pass -AllowNonNoreplyEmail deliberately."
    }

    & git fetch origin $Branch
    if ($LASTEXITCODE -ne 0) {
        throw "git fetch failed. Check network access and GitHub authentication."
    }

    & git show-ref --verify --quiet "refs/remotes/origin/$Branch"
    if ($LASTEXITCODE -eq 0) {
        $Counts = ((& git rev-list --left-right --count "HEAD...origin/$Branch") -join " ").Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Could not compare local and remote history."
        }

        $Parts = $Counts -split "\s+"
        if ($Parts.Count -lt 2) {
            throw "Unexpected git rev-list output: '$Counts'."
        }

        $Ahead = [int]$Parts[0]
        $Behind = [int]$Parts[1]

        if ($Behind -gt 0) {
            throw "Local '$Branch' is behind origin/$Branch by $Behind commit(s). Pull/rebase deliberately before pushing. No automatic merge will be attempted."
        }

        Write-Host "History check: ahead=$Ahead, behind=$Behind"
    }

    & git diff --check
    if ($LASTEXITCODE -ne 0) {
        throw "git diff --check failed. Fix whitespace/conflict-marker errors before committing."
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
        Write-Host "Staged changes:"
        & git diff --cached --stat

        & git commit -m $Message
        if ($LASTEXITCODE -ne 0) {
            throw "git commit failed."
        }
    }
    else {
        Write-Host "No local changes to commit."
    }

    & git push origin "HEAD:refs/heads/$Branch"
    if ($LASTEXITCODE -ne 0) {
        throw "git push failed. The remote may have changed; fetch and reconcile explicitly."
    }

    Write-Host ""
    Write-Host "Push completed:"
    Write-Host $Remote
}
finally {
    Pop-Location
}
