param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "2.0"

function Resolve-Dotnet {
    # A runtime-only dotnet on PATH has no SDK, so return the first candidate that
    # actually reports one. The user-local install is tried first when present.
    $candidates = @()
    $local = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (Test-Path $local) { $candidates += $local }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -ne $local) { $candidates += $cmd.Source }
    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
    }
    if ($candidates.Count -gt 0) { return $candidates[0] }
    throw "The .NET 8 SDK was not found. Install it from https://dot.net"
}

$dotnet = Resolve-Dotnet
$payloadDir = Join-Path $root ".tmp\payload"
$payloadZip = Join-Path $root ".tmp\payload.zip"
$setupOut = Join-Path $root ".tmp\setup"

Write-Host "Publishing ConnectIPN..." -ForegroundColor Cyan
Remove-Item $payloadDir -Recurse -Force -ErrorAction SilentlyContinue
$publishArgs = @(
    "publish", (Join-Path $root "windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj"),
    "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-o", $payloadDir
)
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Windows publish failed" }

# Debug symbols are not useful in a shipped install. A locked-down machine can refuse
# the delete, and that must never fail the build over a few megabytes.
try {
    Get-ChildItem $payloadDir -Filter *.pdb -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop }
} catch {
    Write-Host "  (kept .pdb files: $($_.Exception.Message))" -ForegroundColor DarkGray
}

Write-Host "Compressing the payload..." -ForegroundColor Cyan
Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host "Building the setup program..." -ForegroundColor Cyan
Remove-Item $setupOut -Recurse -Force -ErrorAction SilentlyContinue
$setupArgs = @(
    "publish", (Join-Path $root "installer\ConnectIPN.Setup\ConnectIPN.Setup.csproj"),
    "-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:PayloadZip=$payloadZip",
    "-p:AppVersion=$version",
    "-o", $setupOut
)
& $dotnet @setupArgs
if ($LASTEXITCODE -ne 0) { throw "Setup build failed" }

$release = Join-Path $root "release"
New-Item -ItemType Directory -Force -Path $release | Out-Null
Copy-Item (Join-Path $setupOut "ConnectIPN-Setup.exe") (Join-Path $release "ConnectIPN-Setup-$version.exe") -Force

$size = [math]::Round((Get-Item (Join-Path $release "ConnectIPN-Setup-$version.exe")).Length / 1MB, 1)
Write-Host ""
Write-Host "Setup ready: release\ConnectIPN-Setup-$version.exe ($size MB)" -ForegroundColor Green
