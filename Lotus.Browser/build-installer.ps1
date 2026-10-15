<#
.SYNOPSIS
    Publishes Lotus and builds the custom installer (Setup-Lotus-<version>.exe).

.DESCRIPTION
    1. dotnet publish Lotus  - self-contained, single-file, win-x64.
    2. Zips the publish output into payload.zip.
    3. dotnet publish Lotus.Installer - the small WPF setup stub.
    4. Appends payload.zip to the stub: [exe][payload.zip][int64 length]["LOTUSPAY"].

    No Inno Setup (or any other tool) required - just the .NET SDK.
    Output: Lotus.Browser\installer-output\Setup-Lotus-<version>.exe

    Installer switches (for scripting / winget):
      Setup-Lotus-1.0.0.exe /silent [/dir=C:\path] [/nodesktop]
      Uninstall.exe /uninstall /silent [/deletedata]
#>

param(
    [string]$Version = "0.3.1",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

# Stops the Lotus.Browser.csproj post-build hook from calling this script again.
$env:LOTUS_PUBLISHING = "1"

$root       = $PSScriptRoot
$repo       = Split-Path $root -Parent
$appProj    = Join-Path $root "Lotus.Browser.csproj"
$setupProj  = Join-Path $repo "Lotus.Installer\Lotus.Installer.csproj"
$publishDir = Join-Path $root "publish\$Runtime"
$work       = Join-Path $root "installer-build"
$stubDir    = Join-Path $work "stub"
$outDir     = Join-Path $root "installer-output"
$payload    = Join-Path $work "payload.zip"

foreach ($d in @($publishDir, $work)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force -Path $work, $outDir | Out-Null

# 1. Publish Lotus ---------------------------------------------------------
Write-Host "==> Publishing Lotus v$Version ($Configuration, $Runtime)..." -ForegroundColor Cyan
dotnet publish $appProj `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version -p:FileVersion=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Lotus) failed." }

# 2. payload.zip -----------------------------------------------------------
Write-Host "==> Creating payload..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$publishFull = (Resolve-Path $publishDir).Path.TrimEnd('\') + '\'
$zip = [System.IO.Compression.ZipFile]::Open($payload, 'Create')
try {
    Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
        $rel = $_.FullName.Substring($publishFull.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, 'Optimal')
    }
} finally { $zip.Dispose() }

# 3. Installer stub --------------------------------------------------------
Write-Host "==> Building installer stub..." -ForegroundColor Cyan
dotnet publish $setupProj -c Release -p:Version=$Version -o $stubDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Lotus.Installer) failed." }

# 4. Stub + payload --------------------------------------------------------
$setupExe = Join-Path $outDir "Setup-Lotus-$Version.exe"
Copy-Item (Join-Path $stubDir "LotusSetup.exe") $setupExe -Force

$len = (Get-Item $payload).Length
$out = [System.IO.File]::Open($setupExe, 'Append', 'Write')
try {
    $in = [System.IO.File]::OpenRead($payload)
    try { $in.CopyTo($out) } finally { $in.Dispose() }
    $lenBytes = [BitConverter]::GetBytes([int64]$len)
    $out.Write($lenBytes, 0, 8)
    $magic = [System.Text.Encoding]::ASCII.GetBytes("LOTUSPAY")
    $out.Write($magic, 0, 8)
} finally { $out.Dispose() }

$hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash
Write-Host "==> Done: $setupExe" -ForegroundColor Green
Write-Host "    SHA256: $hash"
