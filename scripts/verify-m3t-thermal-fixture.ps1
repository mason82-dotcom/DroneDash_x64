$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot "DJI_20261002154302_0001_T.JPG"

if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) {
    throw "M3T thermal fixture missing: $fixture"
}

$bytes = [System.IO.File]::ReadAllBytes($fixture)
if ($bytes.Length -lt 500000) {
    throw "Fixture is unexpectedly small: $($bytes.Length) bytes"
}

if ($bytes[0] -ne 0xFF -or $bytes[1] -ne 0xD8) {
    throw "Fixture is not a JPEG (SOI marker missing)"
}

function Find-JpegDimensions([byte[]]$Data) {
    $position = 2

    while ($position + 8 -lt $Data.Length) {
        if ($Data[$position] -ne 0xFF) {
            $position++
            continue
        }

        $marker = $Data[$position + 1]
        if ($marker -eq 0xDA -or $marker -eq 0xD9) {
            break
        }

        if ($marker -ge 0xD0 -and $marker -le 0xD7) {
            $position += 2
            continue
        }

        $length = ($Data[$position + 2] -shl 8) -bor $Data[$position + 3]
        if ($length -lt 2 -or $position + 2 + $length -gt $Data.Length) {
            throw "Invalid JPEG segment length at offset $position"
        }

        if ($marker -in @(0xC0,0xC1,0xC2,0xC3,0xC5,0xC6,0xC7,0xC9,0xCA,0xCB,0xCD,0xCE,0xCF)) {
            $height = ($Data[$position + 5] -shl 8) -bor $Data[$position + 6]
            $width  = ($Data[$position + 7] -shl 8) -bor $Data[$position + 8]
            return @($width, $height)
        }

        $position += 2 + $length
    }

    throw "JPEG SOF dimensions not found"
}

$dimensions = Find-JpegDimensions $bytes
if ($dimensions[0] -ne 640 -or $dimensions[1] -ne 512) {
    throw "Unexpected thermal resolution: $($dimensions[0])x$($dimensions[1]); expected 640x512"
}

# Binary-safe token checks. We deliberately do not print serial numbers or private metadata.
$latin1 = [System.Text.Encoding]::Latin1.GetString($bytes)
$requiredTokens = @(
    "DJI",
    "M3T",
    'drone-dji:ImageSource="InfraredCamera"',
    "iirp"
)

foreach ($token in $requiredTokens) {
    if (-not $latin1.Contains($token, [System.StringComparison]::Ordinal)) {
        throw "Required M3T R-JPEG token missing: $token"
    }
}

$sha256 = [System.Security.Cryptography.SHA256]::HashData($bytes)
$hash = [Convert]::ToHexString($sha256).ToLowerInvariant()

Write-Host "PASS real M3T thermal fixture"
Write-Host "  file: DJI_20261002154302_0001_T.JPG"
Write-Host "  bytes: $($bytes.Length)"
Write-Host "  resolution: $($dimensions[0])x$($dimensions[1])"
Write-Host "  DJI/M3T/Infrared/iirp markers: present"
Write-Host "  sha256: $hash"
