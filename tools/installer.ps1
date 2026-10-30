param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.0"

function Resolve-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $local = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (Test-Path $local) { return $local }
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

# Debug symbols are not useful in a shipped install.
Get-ChildItem $payloadDir -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

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
Copy-Item (Join-Path $setupOut "ConnectIPN-Setup.exe") $release -Force

$size = [math]::Round((Get-Item (Join-Path $release "ConnectIPN-Setup.exe")).Length / 1MB, 1)
Write-Host ""
Write-Host "Setup ready: release\ConnectIPN-Setup.exe ($size MB)" -ForegroundColor Green
