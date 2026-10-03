param(
    [switch]$Strict
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Failures = 0
$Warnings = 0

function Write-Check {
    param(
        [ValidateSet("OK","WARN","FAIL")]
        [string]$Level,
        [string]$Name,
        [string]$Detail
    )

    switch ($Level) {
        "OK"   { Write-Host "[ OK ] $Name - $Detail" -ForegroundColor Green }
        "WARN" { Write-Host "[WARN] $Name - $Detail" -ForegroundColor Yellow; $script:Warnings++ }
        "FAIL" { Write-Host "[FAIL] $Name - $Detail" -ForegroundColor Red; $script:Failures++ }
    }
}

Write-Host "DroneDash_x64 environment check"
Write-Host "Root: $Root"
Write-Host ""

if (Get-Command git -ErrorAction SilentlyContinue) {
    if (Test-Path (Join-Path $Root ".git")) {
        $Branch = (& git -C $Root branch --show-current).Trim()
        $Status = @(& git -C $Root status --porcelain)
        $Detail = if ($Status.Count -eq 0) { "$Branch, clean" } else { "$Branch, $($Status.Count) local change(s)" }
        Write-Check OK "Git repository" $Detail
    }
    else {
        Write-Check FAIL "Git repository" "No .git directory. Use a real clone instead of a ZIP copy."
    }
}
else {
    Write-Check FAIL "Git" "git is not available in PATH."
}

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $DotnetVersion = (& dotnet --version).Trim()
    if ($DotnetVersion -match '^10\.') {
        Write-Check OK ".NET SDK" $DotnetVersion
    }
    else {
        Write-Check WARN ".NET SDK" "$DotnetVersion detected; desktop targets .NET 10."
    }

    $Sources = (& dotnet nuget list source 2>&1 | Out-String)
    if ($Sources -match 'nuget\.org') {
        Write-Check OK "NuGet" "nuget.org source visible (repo NuGet.Config is present)."
    }
    else {
        Write-Check FAIL "NuGet" "nuget.org source is not visible."
    }
}
else {
    Write-Check FAIL ".NET SDK" "dotnet is not available in PATH."
}

if (Get-Command java -ErrorAction SilentlyContinue) {
    $JavaText = (& java -version 2>&1 | Out-String).Trim()
    if ($JavaText -match 'version "17\.' -or $JavaText -match 'openjdk version "17\.') {
        Write-Check OK "Java" (($JavaText -split "\r?\n")[0])
    }
    else {
        Write-Check WARN "Java" "Java is available, but Java 17 is expected. First line: $(($JavaText -split "\r?\n")[0])"
    }
}
else {
    Write-Check FAIL "Java" "Java 17 is required for the Android build."
}

$Sdk = $null
if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME) -and (Test-Path $env:ANDROID_HOME)) {
    $Sdk = $env:ANDROID_HOME
}
elseif (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT) -and (Test-Path $env:ANDROID_SDK_ROOT)) {
    $Sdk = $env:ANDROID_SDK_ROOT
}
elseif ($env:LOCALAPPDATA) {
    $Candidate = Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if (Test-Path $Candidate) {
        $Sdk = $Candidate
    }
}

if ($Sdk) {
    Write-Check OK "Android SDK" $Sdk

    $Api36 = Join-Path $Sdk "platforms\android-36"
    if (Test-Path $Api36) {
        Write-Check OK "Android API 36" $Api36
    }
    else {
        Write-Check FAIL "Android API 36" "Platform android-36 is not installed."
    }

    $BuildTools = Join-Path $Sdk "build-tools\35.0.0"
    if (Test-Path $BuildTools) {
        Write-Check OK "Android Build Tools" "35.0.0"
    }
    else {
        Write-Check WARN "Android Build Tools" "35.0.0 not found. AGP 8.10 uses Build Tools 35.0.0 by default."
    }
}
else {
    Write-Check FAIL "Android SDK" "ANDROID_HOME/ANDROID_SDK_ROOT not found and the standard Windows SDK path is missing."
}

$RcRoot = Join-Path $Root "rc-agent"
$Wrapper = Join-Path $RcRoot "gradlew.bat"
if (Test-Path $Wrapper) {
    $GradleText = (& $Wrapper --version 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0 -and $GradleText -match 'Gradle 8\.12') {
        Write-Check OK "Gradle wrapper" "8.12"
    }
    elseif ($LASTEXITCODE -eq 0) {
        Write-Check WARN "Gradle wrapper" "Wrapper exists, but is not Gradle 8.12."
    }
    else {
        Write-Check FAIL "Gradle wrapper" "Wrapper exists but could not run."
    }
}
elseif (Get-Command gradle -ErrorAction SilentlyContinue) {
    $GradleText = (& gradle --version 2>&1 | Out-String)
    if ($GradleText -match 'Gradle 8\.12') {
        Write-Check OK "Gradle" "Global Gradle 8.12"
    }
    else {
        Write-Check WARN "Gradle" "No wrapper; global Gradle is present but 8.12 is expected."
    }
}
else {
    Write-Check WARN "Gradle" "No wrapper in this clone and no global Gradle command found. Generate the 8.12 wrapper in rc-agent."
}

$SecretFile = Join-Path $HOME ".gradle\gradle.properties"
if (Test-Path $SecretFile) {
    $SecretText = Get-Content $SecretFile -Raw
    $HasKey = $SecretText -match '(?m)^\s*DJI_API_KEY\s*=\s*\S+'
    $HasToken = $SecretText -match '(?m)^\s*BRIDGE_TOKEN\s*=\s*\S+'

    if ($HasKey) {
        Write-Check OK "DJI_API_KEY" "Configured in user Gradle properties (value hidden)."
    }
    else {
        Write-Check WARN "DJI_API_KEY" "Not found in user Gradle properties."
    }

    if ($HasToken) {
        Write-Check OK "BRIDGE_TOKEN" "Configured in user Gradle properties (value hidden)."
    }
    else {
        Write-Check WARN "BRIDGE_TOKEN" "Not found in user Gradle properties."
    }
}
else {
    Write-Check WARN "Gradle secrets" "$SecretFile does not exist."
}

if (Get-Command adb -ErrorAction SilentlyContinue) {
    $AdbVersion = ((& adb version 2>&1 | Select-Object -First 1) -join "").Trim()
    Write-Check OK "ADB" $AdbVersion
}
else {
    Write-Check WARN "ADB" "adb is not available in PATH; RC installation/USB forwarding will not work."
}

$ThermalDll = Join-Path $Root "desktop\DroneDash_x64.Desktop\third_party\dji-tsdk\runtime\libdirp.dll"
if (Test-Path $ThermalDll) {
    Write-Check OK "DJI Thermal SDK" "Runtime staged locally."
}
else {
    Write-Check WARN "DJI Thermal SDK" "Optional runtime not staged; thermal analysis remains disabled."
}

Write-Host ""
Write-Host "Summary: $Failures failure(s), $Warnings warning(s)."

if ($Failures -gt 0 -or ($Strict -and $Warnings -gt 0)) {
    exit 1
}

exit 0
