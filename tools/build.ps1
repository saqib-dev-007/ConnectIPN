param(
    [switch]$SkipAndroid,
    [switch]$SkipWindows,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Resolve-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $local = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (Test-Path $local) { return $local }
    throw "The .NET 8 SDK was not found. Install it from https://dot.net"
}

if (-not $SkipWindows) {
    $dotnet = Resolve-Dotnet
    $project = Join-Path $root "windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj"
    if ($Publish) {
        Write-Host "Publishing the Windows app (self-contained)..." -ForegroundColor Cyan
        & $dotnet publish $project -c Release -r win-x64 --self-contained true -o (Join-Path $root "dist\windows")
    }
    else {
        Write-Host "Building the Windows app..." -ForegroundColor Cyan
        & $dotnet build $project -c Release
    }
    if ($LASTEXITCODE -ne 0) { throw "Windows build failed" }
}

if (-not $SkipAndroid) {
    Write-Host "Building the Android app..." -ForegroundColor Cyan
    if (-not $env:JAVA_HOME) { $env:JAVA_HOME = "C:\Program Files\Java\jdk-17" }
    Push-Location (Join-Path $root "android")
    try {
        & ".\gradlew.bat" assembleDebug
        if ($LASTEXITCODE -ne 0) { throw "Android build failed" }
    }
    finally {
        Pop-Location
    }
}

Write-Host "Done." -ForegroundColor Green
