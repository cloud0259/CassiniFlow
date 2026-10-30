# build-installer.ps1
# Script de compilation locale ou CI pour l'installateur Windows Inno Setup
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path "$PSScriptRoot\..\..").Path

Write-Host "=== Compilation de CassiniFlow (Version $Version - $Configuration) ===" -ForegroundColor Cyan

# 1. Publication Desktop autonome win-x64
$stagingDesktop = "$rootDir\staging\win-x64\Desktop"
Write-Host "Publication Desktop -> $stagingDesktop"
dotnet publish "$rootDir\src\CassiniFlow.Desktop\CassiniFlow.Desktop.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $stagingDesktop

# 2. Publication CLI autonome win-x64
$stagingCli = "$rootDir\staging\win-x64\Cli"
Write-Host "Publication CLI -> $stagingCli"
dotnet publish "$rootDir\src\CassiniFlow.Cli\CassiniFlow.Cli.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $stagingCli

# 3. Création de l'archive portable .zip
$artifactsDir = "$rootDir\release-artifacts"
if (-not (Test-Path $artifactsDir)) {
    New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
}

$portableZip = "$artifactsDir\CassiniFlow-win-x64-portable.zip"
Write-Host "Création de l'archive portable : $portableZip" -ForegroundColor Green
$stagingWin = "$rootDir\staging\win-x64"
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
Compress-Archive -Path "$stagingWin\*" -DestinationPath $portableZip -Force

# 4. Compilation de l'installateur Inno Setup si ISCC est disponible
$isccPaths = @(
    "iscc.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)

$iscc = $null
foreach ($p in $isccPaths) {
    if (Get-Command $p -ErrorAction SilentlyContinue) {
        $iscc = $p
        break
    } elseif (Test-Path $p) {
        $iscc = $p
        break
    }
}

if ($iscc) {
    Write-Host "Exécution de Inno Setup ($iscc)..." -ForegroundColor Green
    & $iscc "/DMyAppVersion=$Version" "$PSScriptRoot\CassiniFlow.iss"
    Write-Host "Installateur généré dans $artifactsDir\CassiniFlow-Setup-x64.exe" -ForegroundColor Green
} else {
    Write-Warning "Inno Setup (ISCC.exe) non trouvé. L'archive portable a été créée avec succès. Pour compiler l'installateur .exe, installez Inno Setup 6."
}

Write-Host "Terminé avec succès !" -ForegroundColor Cyan

