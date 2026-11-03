# bump-version.ps1
# Met à jour la version de l'application dans Directory.Build.props (Source Unique de Vérité)
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$NewVersion
)

$ErrorActionPreference = "Stop"
$NewVersion = $NewVersion.TrimStart('v')
$propsPath = (Resolve-Path "$PSScriptRoot\..\Directory.Build.props").Path

$content = Get-Content $propsPath -Raw
$updated = $content -replace '<Version>[^<]+</Version>', "<Version>$NewVersion</Version>"
$updated = $updated -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$NewVersion.0</AssemblyVersion>"
$updated = $updated -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$NewVersion.0</FileVersion>"
$updated = $updated -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$NewVersion</InformationalVersion>"

Set-Content -Path $propsPath -Value $updated -NoNewline
Write-Host "Version de CassiniFlow mise a jour vers $NewVersion dans Directory.Build.props !" -ForegroundColor Green
