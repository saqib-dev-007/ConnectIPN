param(
    [switch]$SkipIcons,
    [switch]$Setup
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

if (-not $SkipIcons) {
    Write-Host "Generating icons from Icon\Icon.png..." -ForegroundColor Cyan
    & $dotnet run --project (Join-Path $root "tools\IconGen") -c Release -v q -- $root
    if ($LASTEXITCODE -ne 0) { throw "Icon generation failed" }
}

Write-Host "Building the Android release APK..." -ForegroundColor Cyan
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = "C:\Program Files\Java\jdk-17" }
Push-Location (Join-Path $root "android")
try {
    & ".\gradlew.bat" assembleRelease
    if ($LASTEXITCODE -ne 0) { throw "Android release build failed" }
}
finally {
    Pop-Location
}

Write-Host "Publishing the Windows app (self-contained)..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj") -c Release -r win-x64 --self-contained true -o (Join-Path $root "release\windows")
if ($LASTEXITCODE -ne 0) { throw "Windows publish failed" }

$androidOut = Join-Path $root "release\android"
New-Item -ItemType Directory -Force -Path $androidOut | Out-Null
Copy-Item (Join-Path $root "android\app\build\outputs\apk\release\ConnectIPN-$version.apk") $androidOut -Force

if ($Setup) {
    Write-Host ""
    & (Join-Path $PSScriptRoot "installer.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Setup build failed" }
}

Write-Host ""
Write-Host "Release ready:" -ForegroundColor Green
Write-Host "  release\android\ConnectIPN-$version.apk"
Write-Host "  release\windows\ConnectIPN.exe"
