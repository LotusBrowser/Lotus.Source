<#
.SYNOPSIS
    Publishes Lotus and builds a Windows installer (Setup-Lotus-<version>.exe).

.DESCRIPTION
    1. dotnet publish - self-contained, single-file, win-x64 release build.
    2. Runs Inno Setup (ISCC.exe) against LotusSetup.iss to produce the installer.

.NOTES
    Requires Inno Setup (https://jrsoftware.org/isinfo.php) to be installed.
    Default install path checked: C:\Program Files (x86)\Inno Setup 6\ISCC.exe
    Run this from the repo root (same folder as TerminalOS.csproj).
#>

param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root        = $PSScriptRoot
$csproj      = Join-Path $root "TerminalOS.csproj"
$publishDir  = Join-Path $root "publish\$Runtime"
$issScript   = Join-Path $root "LotusSetup.iss"

Write-Host "==> Publishing Lotus v$Version ($Configuration, $Runtime)..." -ForegroundColor Cyan

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $csproj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -p:FileVersion=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Write-Host "==> Publish complete: $publishDir" -ForegroundColor Green

# --- Locate Inno Setup compiler ---
$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Could not find ISCC.exe. Install Inno Setup 6 from https://jrsoftware.org/isdl.php, or edit `$isccCandidates in this script to point at your install."
}

Write-Host "==> Building installer with Inno Setup..." -ForegroundColor Cyan

& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishDir" $issScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compile failed."
}

Write-Host "==> Done. Installer is in the 'installer-output' folder." -ForegroundColor Green
