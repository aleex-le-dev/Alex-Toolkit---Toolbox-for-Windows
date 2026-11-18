<#
.SYNOPSIS
    Alex Toolkit - Grand nettoyage du PC (script du Bureau).

.DESCRIPTION
    Mêmes éléments « recommandés » (sans risque) que le Grand nettoyage de l'application :
    temporaires, cache Windows Update, cache d'optimisation de livraison, caches des navigateurs (tous profils)
    et miniatures, rapports d'erreurs et de plantage, caches graphiques, journaux d'installation de plus de 7 jours,
    corbeille, cache DNS. Les éléments avancés (anciens pilotes, WinSxS, vidages mémoire, Windows.old) ne se font
    que depuis l'application, qui explique leur contrepartie.

    Sécurité : seul le CONTENU des dossiers est supprimé, jonctions et liens jamais suivis, fichiers ouverts ignorés,
    garde-fou contre une variable TEMP mal réglée (jamais une racine de disque ni un dossier vital).

.PARAMETER DryRun
    Mesure seulement ce qui serait libéré, sans rien supprimer ni demander les droits administrateur (tests).
.PARAMETER NoPause
    Ne pas attendre Entrée à la fin.
#>
param([switch]$DryRun, [switch]$NoPause)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# Élévation (sauf mesure seule)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $DryRun) {
    try { Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") -Verb RunAs } catch { }
    exit
}

try { $host.UI.RawUI.WindowTitle = 'Grand nettoyage du PC - Alex Toolkit' } catch { }

# ─── Outils ──────────────────────────────────────────────────────────────────

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return '{0:N1} Go' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N0} Mo' -f ($Bytes / 1MB) }
    if ($Bytes -gt 0) { return '{0:N0} Ko' -f [Math]::Max(1, $Bytes / 1KB) }
    return '0 Mo'
}

# Garde-fou : chemin complet, jamais une racine ni un dossier vital ; le Temp de l'utilisateur doit s'appeler Temp / Tmp.
function Test-SafeTarget([string]$Folder) {
    if ([string]::IsNullOrWhiteSpace($Folder)) { return $false }
    if (-not [System.IO.Path]::IsPathRooted($Folder) -or $Folder -match '^[A-Za-z]:$') { return $false }
    try { $full = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\') } catch { return $false }
    if ($full.Length -le 3) { return $false }
    $vital = @('Windows', 'System', 'SystemX86', 'ProgramFiles', 'ProgramFilesX86', 'UserProfile', 'Desktop', 'DesktopDirectory',
               'MyDocuments', 'MyPictures', 'MyVideos', 'MyMusic', 'ApplicationData', 'LocalApplicationData', 'CommonApplicationData') |
             ForEach-Object { [Environment]::GetFolderPath($_).TrimEnd('\') } | Where-Object { $_ }
    foreach ($v in $vital) { if ($v -ieq $full) { return $false } }
    $users = Split-Path ([Environment]::GetFolderPath('UserProfile').TrimEnd('\')) -Parent
    if ($users -and $users -ieq $full) { return $false }
    $tmp = [System.IO.Path]::GetTempPath().TrimEnd('\')
    if ($full -ieq $tmp -and (Split-Path $full -Leaf) -notin @('Temp', 'Tmp')) { return $false }
    return $true
}

# Fichiers d'un dossier sans suivre les jonctions
function Get-FilesSafe([string]$Folder) {
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { return }
    $stack = New-Object System.Collections.Generic.Stack[System.IO.DirectoryInfo]
    $stack.Push((New-Object System.IO.DirectoryInfo $Folder))
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        try { $files = $dir.GetFiles(); $dirs = $dir.GetDirectories() } catch { continue }
        foreach ($f in $files) { $f }
        foreach ($d in $dirs) { if (-not ($d.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { $stack.Push($d) } }
    }
}

function Get-Size([string[]]$Folders, [int]$OlderThanDays = 0) {
    $limit = (Get-Date).AddDays(-$OlderThanDays)
    [long]$size = 0
    foreach ($fo in $Folders | Select-Object -Unique) {
        foreach ($f in Get-FilesSafe $fo) { if ($OlderThanDays -eq 0 -or $f.LastWriteTime -lt $limit) { $size += $f.Length } }
    }
    return $size
}

# Vide un dossier (gardé) : fichiers un par un (ouverts = ignorés), puis sous-dossiers vides ; liens retirés sans suivre leur cible.
function Remove-Contents([string]$Folder, [int]$OlderThanDays = 0) {
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { return [long]0 }
    if (-not (Test-SafeTarget $Folder)) { Write-Host "   [!] Ignoré par sécurité : $Folder" -ForegroundColor Yellow; return [long]0 }
    $limit = (Get-Date).AddDays(-$OlderThanDays)
    [long]$freed = 0
    foreach ($f in @(Get-FilesSafe $Folder)) {
        if ($OlderThanDays -gt 0 -and $f.LastWriteTime -ge $limit) { continue }
        try {
            $len = $f.Length
            if ($f.Attributes -band ([System.IO.FileAttributes]::ReadOnly -bor [System.IO.FileAttributes]::Hidden -bor [System.IO.FileAttributes]::System)) {
                $f.Attributes = [System.IO.FileAttributes]::Normal
            }
            $f.Delete(); $freed += $len
        } catch { }
    }
    $all = New-Object System.Collections.Generic.List[System.IO.DirectoryInfo]
    $stack = New-Object System.Collections.Generic.Stack[System.IO.DirectoryInfo]
    $stack.Push((New-Object System.IO.DirectoryInfo $Folder))
    while ($stack.Count -gt 0) {
        try { $subs = $stack.Pop().GetDirectories() } catch { continue }
        foreach ($d in $subs) {
            if ($d.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { try { $d.Delete($false) } catch { }; continue }
            $all.Add($d); $stack.Push($d)
        }
    }
    foreach ($d in ($all | Sort-Object { $_.FullName.Length } -Descending)) {
        try { if (-not ($d.EnumerateFileSystemInfos() | Select-Object -First 1)) { $d.Delete($false) } } catch { }
    }
    return $freed
}

# ─── Emplacements (identiques à l'application) ───────────────────────────────

$local = [Environment]::GetFolderPath('LocalApplicationData')
$localLow = Join-Path (Split-Path $local -Parent) 'LocalLow'
$programData = [Environment]::GetFolderPath('CommonApplicationData')
$win = [Environment]::GetFolderPath('Windows')

function Get-BrowserCacheFolders {
    foreach ($b in @('Google\Chrome\User Data', 'Microsoft\Edge\User Data', 'BraveSoftware\Brave-Browser\User Data', 'Vivaldi\User Data')) {
        $base = Join-Path $local $b
        if (-not (Test-Path -LiteralPath $base)) { continue }
        Join-Path $base 'ShaderCache'; Join-Path $base 'GrShaderCache'
        Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' -or $_.Name -eq 'Guest Profile' } |
            ForEach-Object { foreach ($c in 'Cache', 'Code Cache', 'GPUCache') { Join-Path $_.FullName $c } }
    }
    $ff = Join-Path $local 'Mozilla\Firefox\Profiles'
    if (Test-Path -LiteralPath $ff) {
        Get-ChildItem -LiteralPath $ff -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'cache2'; Join-Path $_.FullName 'startupCache' }
    }
}

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$recycle = @([System.IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and $_.DriveType -eq 'Fixed' } | ForEach-Object { Join-Path $_.RootDirectory.FullName "`$Recycle.Bin\$sid" })

$items = @(
    @{ Title = 'Fichiers temporaires'; Detail = 'Fichiers laissés par les logiciels et les installations (%TEMP%, Windows\Temp).'
       Folders = @([System.IO.Path]::GetTempPath(), (Join-Path $win 'Temp')) },
    @{ Title = 'Cache de Windows Update'; Detail = 'Mises à jour déjà téléchargées et installées. Le service est arrêté puis relancé.'
       Folders = @(Join-Path $win 'SoftwareDistribution\Download'); Kind = 'wu' },
    @{ Title = "Cache d'optimisation de livraison"; Detail = 'Morceaux de mises à jour partagés entre PC (commande officielle de Windows).'
       Folders = @(Join-Path $win 'ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache'); Kind = 'delivery' },
    @{ Title = 'Caches des navigateurs et miniatures'; Detail = 'Chrome, Edge, Brave, Vivaldi, Firefox (tous profils) : cache seulement, ni mots de passe ni historique.'
       Folders = @(Get-BrowserCacheFolders); Kind = 'browsers' },
    @{ Title = "Rapports d'erreurs et de plantage"; Detail = 'Rapports Windows (WER) et vidages des logiciels qui ont planté.'
       Folders = @((Join-Path $programData 'Microsoft\Windows\WER\ReportArchive'), (Join-Path $programData 'Microsoft\Windows\WER\ReportQueue'),
                   (Join-Path $programData 'Microsoft\Windows\WER\Temp'), (Join-Path $local 'Microsoft\Windows\WER'), (Join-Path $local 'CrashDumps')) },
    @{ Title = 'Caches graphiques'; Detail = 'Shaders DirectX, NVIDIA, AMD, Intel (recréés automatiquement).'
       Folders = @((Join-Path $local 'D3DSCache'), (Join-Path $local 'NVIDIA\DXCache'), (Join-Path $local 'NVIDIA\GLCache'), (Join-Path $local 'AMD\DxCache'),
                   (Join-Path $local 'AMD\DxcCache'), (Join-Path $local 'AMD\GLCache'), (Join-Path $localLow 'Intel\ShaderCache')) },
    @{ Title = "Anciens journaux d'installation"; Detail = 'Journaux CBS et DISM de plus de 7 jours.'
       Folders = @((Join-Path $win 'Logs\CBS'), (Join-Path $win 'Logs\DISM')); OlderThan = 7 },
    @{ Title = 'Corbeille'; Detail = 'Fichiers supprimés de ce compte sur tous les disques (définitif).'; Folders = $recycle; Kind = 'recycle' },
    @{ Title = 'Cache DNS'; Detail = 'Adresses de sites mémorisées (ne libère pas d''espace disque).'; Folders = @(); Kind = 'dns' }
)

# ─── Nettoyage ───────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '  GRAND NETTOYAGE DU PC - Alex Toolkit' -ForegroundColor Cyan
Write-Host ("  {0}  |  {1}  |  {2:dd/MM/yyyy HH:mm}" -f $env:COMPUTERNAME, $env:USERNAME, (Get-Date)) -ForegroundColor DarkGray
if ($DryRun) { Write-Host '  MODE MESURE : rien ne sera supprimé.' -ForegroundColor Yellow }

$watch = [Diagnostics.Stopwatch]::StartNew()
$results = New-Object System.Collections.Generic.List[object]
[long]$total = 0
$n = 0
foreach ($it in $items) {
    $n++
    Write-Host ''
    Write-Host ("[{0}/{1}] {2}" -f $n, $items.Count, $it.Title) -ForegroundColor Cyan
    Write-Host "      $($it.Detail)" -ForegroundColor DarkGray
    $days = 0; if ($it.OlderThan) { $days = $it.OlderThan }
    [long]$freed = 0
    if ($DryRun) {
        $freed = Get-Size $it.Folders $days
    } else {
        switch ([string]$it.Kind) {
            'wu' {
                Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
                foreach ($f in $it.Folders) { $freed += Remove-Contents $f }
                Start-Service -Name wuauserv -ErrorAction SilentlyContinue
            }
            'delivery' {
                $before = Get-Size $it.Folders
                Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue
                $freed = [Math]::Max(0, $before - (Get-Size $it.Folders))
            }
            'browsers' {
                foreach ($f in $it.Folders) { $freed += Remove-Contents $f }
                $thumbs = Join-Path $local 'Microsoft\Windows\Explorer'
                Get-ChildItem -LiteralPath $thumbs -Filter 'thumbcache_*.db' -Force -ErrorAction SilentlyContinue |
                    ForEach-Object { try { $l = $_.Length; Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; $freed += $l } catch { } }
            }
            'recycle' {
                $before = Get-Size $it.Folders
                Clear-RecycleBin -Force -ErrorAction SilentlyContinue
                $freed = [Math]::Max(0, $before - (Get-Size $it.Folders))
            }
            'dns' { ipconfig /flushdns | Out-Null }
            default { foreach ($f in $it.Folders) { $freed += Remove-Contents $f $days } }
        }
    }
    $total += $freed
    if ($it.Kind -eq 'dns') { $label = if ($DryRun) { 'serait vidé' } else { 'vidé' } }
    else { $label = '{0} {1}' -f (Format-Size $freed), $(if ($DryRun) { 'à libérer' } else { 'libérés' }) }
    Write-Host "      [OK] $label" -ForegroundColor Green
    $results.Add([pscustomobject]@{ Element = $it.Title; Resultat = $label })
}
$watch.Stop()

Write-Host ''
Write-Host '  ───────────────────────────── BILAN ─────────────────────────────' -ForegroundColor Green
foreach ($r in $results) { Write-Host ("   {0,-40} {1}" -f $r.Element, $r.Resultat) }
Write-Host ''
Write-Host ("   TOTAL {0} : {1}   (en {2:N0} s)" -f $(if ($DryRun) { 'À LIBÉRER' } else { 'LIBÉRÉ' }), (Format-Size $total), $watch.Elapsed.TotalSeconds) -ForegroundColor Yellow
Write-Host ''
Write-Host '   Anciennes versions des pilotes, WinSxS, vidages mémoire et Windows.old :' -ForegroundColor DarkGray
Write-Host '   depuis Alex Toolkit > Nettoyage > Grand nettoyage (éléments avancés).' -ForegroundColor DarkGray
if (-not $NoPause) { Write-Host ''; Read-Host '   Appuyez sur Entrée pour fermer' | Out-Null }
