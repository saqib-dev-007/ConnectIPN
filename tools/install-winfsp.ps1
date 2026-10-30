# Downloads the WinFsp installer and runs it elevated (one-time driver install).
$ErrorActionPreference = "Stop"

$version = "2.1.25156"
$url = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-$version.msi"
$msi = Join-Path $env:TEMP "winfsp-$version.msi"

Write-Host "Downloading WinFsp $version..."
Invoke-WebRequest -Uri $url -OutFile $msi

Write-Host "Starting the installer (accept the UAC prompt)..."
$arguments = @("/i", $msi, "/passive")
$process = Start-Process msiexec.exe -ArgumentList $arguments -Verb RunAs -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "WinFsp installer exited with code $($process.ExitCode)"
}
Write-Host "WinFsp installed. Reboot if the drive does not mount." -ForegroundColor Green
