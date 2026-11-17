<#
  Alex Toolkit - « Ouvrir CMD (Admin) » / « Ouvrir PowerShell (Admin) » du clic droit.
  Ouvre la console en administrateur :
    - dans le dossier cliqué (dossier, lecteur, fond de dossier) ;
    - dans le dossier du fichier cliqué (pratique pour lancer un script) ;
    - sur le Bureau si Windows ne transmet aucun chemin.
  Le chemin est reçu comme argument et n'est jamais réinterprété : les noms avec apostrophe
  (« Projets d'été »), espaces ou caractères spéciaux fonctionnent.
  Installé par Alex Toolkit dans C:\ProgramData\AlexToolkit (modifiable par les administrateurs seulement).
  -DryRun : affiche ce qui serait lancé, sans rien ouvrir (tests).
#>
param(
    [string]$Path,
    [ValidateSet('cmd', 'powershell')][string]$Shell = 'cmd',
    [switch]$DryRun
)

$dir = "$Path".Trim().Trim('"')
if ($dir -match '^[A-Za-z]:$') { $dir += '\' }   # « C:\ » entre guillemets arrive sous la forme C:" (guillemet échappé)
$file = $null
if ($dir -and (Test-Path -LiteralPath $dir -PathType Leaf)) {
    $file = [IO.Path]::GetFileName($dir)
    $dir = [IO.Path]::GetDirectoryName($dir)
}
if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) { $dir = [Environment]::GetFolderPath('Desktop') }

if ($Shell -eq 'cmd') {
    $exe = 'cmd.exe'
    $arguments = @("/s /k pushd `"$dir`"")
    if ($file) { $arguments = @("/s /k pushd `"$dir`" && echo Fichier : `"$file`"") }
} else {
    # Commande transmise encodée : aucun risque avec les apostrophes ou les guillemets du chemin.
    $ps = "Set-Location -LiteralPath '" + $dir.Replace("'", "''") + "'"
    if ($file) { $ps += "; Write-Host ('Fichier : .\' + '" + $file.Replace("'", "''") + "') -ForegroundColor Cyan" }
    $exe = 'powershell.exe'
    $arguments = @('-NoExit', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ps)))
}

if ($DryRun) {
    "DOSSIER : $dir"
    if ($file) { "FICHIER : $file" }
    "LANCE   : $exe $($arguments -join ' ')"
    if ($Shell -eq 'powershell') { "DECODE  : " + [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($arguments[2])) }
    exit 0
}

try { Start-Process -FilePath $exe -ArgumentList $arguments -Verb RunAs } catch { }   # invite UAC refusée : rien à faire
