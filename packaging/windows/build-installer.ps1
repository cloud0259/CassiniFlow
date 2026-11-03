# build-installer.ps1
# Script de compilation locale ou CI pour l'installateur Windows Inno Setup
param(
    [string]$Version,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$rootDir = (Resolve-Path "$PSScriptRoot\..\..").Path

# Si aucune version n'est spécifiée, extraction automatique depuis Directory.Build.props (source unique)
if (-not $Version) {
    $propsPath = "$rootDir\Directory.Build.props"
    if (Test-Path $propsPath) {
        [xml]$props = Get-Content $propsPath
        $Version = $props.Project.PropertyGroup.Version
    }
    if (-not $Version) { $Version = "1.1.0" }
}

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

# Copie de l'icône dans les assets du staging pour l'archive portable
$stagingAssets = "$stagingDesktop\Assets"
if (-not (Test-Path $stagingAssets)) { New-Item -ItemType Directory -Path $stagingAssets -Force | Out-Null }
Copy-Item "$rootDir\src\CassiniFlow.Desktop\Assets\cassiniflow.ico" -Destination "$stagingAssets\cassiniflow.ico" -Force

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
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 7\ISCC.exe"
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

if (-not $iscc) {
    # Recherche dans le registre Windows (installations personnalisées ou via winget)
    $regPaths = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    $regEntry = Get-ItemProperty $regPaths -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "*Inno Setup*" -and $_.InstallLocation } |
        Select-Object -First 1
    if ($regEntry -and (Test-Path "$($regEntry.InstallLocation)\ISCC.exe")) {
        $iscc = "$($regEntry.InstallLocation)\ISCC.exe"
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

