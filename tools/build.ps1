param(
    [switch]$SkipAndroid,
    [switch]$SkipWindows,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$releaseWindows = Join-Path $root "release\windows"
$releaseDevWindows = Join-Path $root "release\windows-dev"
$releaseAndroid = Join-Path $root "release\android"

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

# Every artifact lands under release\ so there is one place to pick a build from.
# The quick build and the shippable publish use different folders on purpose: a
# framework-dependent build dropped on top of a self-contained publish would leave
# a folder that no longer runs on a machine without .NET.
if (-not $SkipWindows) {
    $dotnet = Resolve-Dotnet
    $project = Join-Path $root "windows\ConnectIPN.Desktop\ConnectIPN.Desktop.csproj"
    if ($Publish) {
        Write-Host "Publishing the Windows app (self-contained) to release/windows..." -ForegroundColor Cyan
        & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $releaseWindows
    }
    else {
        Write-Host "Building the Windows app to release/windows-dev..." -ForegroundColor Cyan
        & $dotnet build $project -c Release -o $releaseDevWindows
    }
    if ($LASTEXITCODE -ne 0) { throw "Windows build failed" }
}

if (-not $SkipAndroid) {
    Write-Host "Building the Android app..." -ForegroundColor Cyan
    if (-not $env:JAVA_HOME) { $env:JAVA_HOME = "C:\Program Files\Java\jdk-17" }
    Push-Location (Join-Path $root "android")
    try {
        # Gradle writes deprecation notes to stderr; with ErrorActionPreference=Stop that
        # would abort the build even when Gradle exits 0. Judge by the exit code instead.
        $ErrorActionPreference = "Continue"
        & ".\gradlew.bat" assembleDebug
        $gradleExit = $LASTEXITCODE
        $ErrorActionPreference = "Stop"
        if ($gradleExit -ne 0) { throw "Android build failed" }
    }
    finally {
        Pop-Location
    }

    # A distinct name so a debug build never overwrites the release APK.
    New-Item -ItemType Directory -Force -Path $releaseAndroid | Out-Null
    Copy-Item (Join-Path $root "android\app\build\outputs\apk\debug\ConnectIPN-1.0.apk") `
        (Join-Path $releaseAndroid "ConnectIPN-1.0-debug.apk") -Force
}

Write-Host ""
Write-Host "Artifacts are in the release folder:" -ForegroundColor Green
if (-not $SkipWindows) {
    if ($Publish) { Write-Host "  release/windows/ConnectIPN.exe (self-contained)" }
    else { Write-Host "  release/windows-dev/ConnectIPN.exe (needs .NET 8)" }
}
if (-not $SkipAndroid) { Write-Host "  release/android/ConnectIPN-1.0-debug.apk" }
Write-Host "Done." -ForegroundColor Green
