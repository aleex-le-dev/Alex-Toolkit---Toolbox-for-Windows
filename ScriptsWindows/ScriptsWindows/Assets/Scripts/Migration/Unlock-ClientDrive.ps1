<#
.SYNOPSIS
    Alex Toolkit - Débloquer l'accès à un dossier ou à l'ancien disque d'un client (« Vous ne disposez pas des autorisations… »).

.DESCRIPTION
    Typiquement : disque d'un ancien PC branché en USB, D:\Users\Jean inaccessible car ses droits NTFS
    appartiennent à un compte qui n'existe pas sur ce PC. Le script :
      1. reprend la propriété de toute l'arborescence pour le groupe Administrateurs (takeown /R /A) ;
      2. accorde le contrôle total aux Administrateurs ET au compte de la session ouverte (icacls /grant /T /C /Q).
         L'Explorateur tourne sans élévation : les seuls droits « Administrateurs » ne suffiraient pas à l'ouvrir.
    Les droits existants ne sont pas supprimés (ajout seulement).

    Garde-fous : refuse le disque et les dossiers de CE Windows (racine du disque système, Windows, Program Files,
    ProgramData, dossier Users, racine et AppData du profil courant, dossiers système cachés). Un autre profil de ce PC est
    accepté après avertissement.

    Portable : groupe désigné par SID (S-1-5-32-544, nom traduit selon la langue), lettre de réponse de takeown
    adaptée à la langue de Windows (O en français, Y en anglais, J en allemand…).

.PARAMETER TargetPath
    Dossier ou lecteur à débloquer (ex. E:\Users\Client ou E:\). Demandé s'il est absent.
.PARAMETER NoPrompt
    Aucune question (pas de confirmation ni de pause finale).
.PARAMETER DryRun
    Contrôles et comptage seulement : rien n'est modifié, aucune élévation demandée.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Unlock-ClientDrive.ps1 -TargetPath "E:\Users\Jean"
#>
[CmdletBinding()]
param(
    [string]$TargetPath,
    [switch]$NoPrompt,
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$AdminsSid = 'S-1-5-32-544'

# ─── Outils ──────────────────────────────────────────────────────────────────

function Test-IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Interactive {
    if ($NoPrompt) { return $false }
    foreach ($a in [Environment]::GetCommandLineArgs()) { if ($a -like '-NonI*') { return $false } }
    return [Environment]::UserInteractive
}

function Exit-Script([int]$Code) {
    if (Test-Interactive) { Write-Host ''; Read-Host '  Appuyez sur Entrée pour fermer' | Out-Null }
    exit $Code
}

# Chemin reçu de l'Explorateur : « "%V " » (espace finale pour qu'un « E:\ » ne termine pas par \") → nettoyé ici.
function Get-CleanPath([string]$Path) {
    $p = ([string]$Path).Trim().Trim('"').Trim()
    if ($p -match '^[A-Za-z]:$') { $p += '\' }
    return $p
}

# Argument de ligne de commande sûr (guillemets ; un « \ » final est doublé pour ne pas échapper le guillemet).
function ConvertTo-Arg([string]$Value) { return '"' + ($Value -replace '(\\+)$', '$1$1') + '"' }

# Lettre « Oui » attendue par takeown /D selon la langue de Windows.
function Get-TakeownYes {
    switch ([Globalization.CultureInfo]::InstalledUICulture.TwoLetterISOLanguageName) {
        'fr' { 'O' } 'de' { 'J' } 'nl' { 'J' } 'sv' { 'J' } 'da' { 'J' } 'nb' { 'J' } 'no' { 'J' }
        'es' { 'S' } 'it' { 'S' } 'pt' { 'S' } 'pl' { 'T' } 'fi' { 'K' } 'tr' { 'E' }
        default { 'Y' }
    }
}

# SID du compte de la session ouverte (propriétaire d'explorer.exe), sinon du compte courant.
function Get-SessionUserSid {
    try {
        $p = Get-CimInstance Win32_Process -Filter "name='explorer.exe'" -ErrorAction Stop | Select-Object -First 1
        if ($p) { $r = Invoke-CimMethod -InputObject $p -MethodName GetOwnerSid -ErrorAction Stop; if ($r.Sid) { return [string]$r.Sid } }
    } catch { }
    return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}

# Raison de refuser une cible (chaîne vide = acceptée). Une cible sous un dossier protégé est aussi refusée.
function Get-ProtectionReason([string]$Path) {
    $Path = Get-CleanPath $Path   # « C: » = racine du lecteur (sinon : dossier courant de ce lecteur)
    if ([string]::IsNullOrWhiteSpace($Path)) { return 'aucun dossier indiqué' }
    if (-not [System.IO.Path]::IsPathRooted($Path)) { return 'le chemin doit être complet (ex. E:\Users\Jean)' }
    try { $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') } catch { return 'chemin invalide' }
    if (-not (Test-Path -LiteralPath $Path)) { return 'dossier introuvable' }

    $sysDrive = ([Environment]::GetFolderPath('Windows')).Substring(0, 2)
    if ($full -ieq $sysDrive) { return "c'est le disque de ce Windows ($sysDrive) : toute l'arborescence de Windows changerait de propriétaire" }
    $protected = @(
        [Environment]::GetFolderPath('Windows'),
        [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86'),
        [Environment]::GetFolderPath('CommonApplicationData'),
        (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData'),
        (Join-Path $sysDrive 'System Volume Information'),
        (Join-Path $sysDrive '$Recycle.Bin'),
        (Join-Path $sysDrive 'Recovery'),
        (Join-Path $sysDrive 'Boot'),
        (Join-Path $sysDrive 'Users\Default'),
        (Join-Path $sysDrive 'Users\Public'),
        (Join-Path $sysDrive 'Users\All Users')
    ) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') }
    foreach ($p in $protected) {
        if ($full -ieq $p -or $full.StartsWith($p + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return "dossier de ce Windows protégé ($p) : en changer le propriétaire peut casser Windows ou ce compte"
        }
    }
    # Le dossier Users de ce PC (tous les profils d'un coup) et tout dossier qui CONTIENT un dossier protégé
    $users = Split-Path ([Environment]::GetFolderPath('UserProfile')) -Parent
    if ($full -ieq $users.TrimEnd('\')) { return "dossier de tous les profils de ce PC ($users)" }
    $me = [Environment]::GetFolderPath('UserProfile').TrimEnd('\')
    if ($full -ieq $me) { return "profil du compte en cours d'utilisation ($me) : ses sous-dossiers (hors AppData) peuvent être débloqués un par un" }
    foreach ($p in $protected) { if ($p.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) { return "contient un dossier de ce Windows ($p)" } }
    return ''
}

# Avertissement (accepté mais à confirmer)
function Get-Warning([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $users = (Split-Path ([Environment]::GetFolderPath('UserProfile')) -Parent).TrimEnd('\')
    if ($full.StartsWith($users + '\', [StringComparison]::OrdinalIgnoreCase)) {
        return "C'est le profil d'un autre compte de CE PC : ses autorisations vont changer (le compte continuera de fonctionner)."
    }
    if ($full.Length -le 3 -and (Test-Path -LiteralPath (Join-Path $full 'Windows\System32'))) {
        return "Ce disque contient un ancien Windows : tout le disque va changer de propriétaire (sans effet sur ce PC). Pour aller plus vite, choisissez plutôt le dossier Users\<client>."
    }
    return ''
}

# Exécute un outil Windows, compte ses lignes (une par élément traité) et affiche l'avancement sur une ligne.
function Invoke-Counted([string]$Exe, [string[]]$Arguments, [string]$Label) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $Exe
    $psi.Arguments = ($Arguments | ForEach-Object { if ($_ -match '[\s"]' -or $_ -eq '') { ConvertTo-Arg $_ } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    try {
        $oem = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
        $psi.StandardOutputEncoding = $oem; $psi.StandardErrorEncoding = $oem
    } catch { }
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $lines = 0; $errors = New-Object System.Collections.Generic.List[string]; $last = ''
    $watch = [Diagnostics.Stopwatch]::StartNew(); $nextDraw = 0
    while ($null -ne ($line = $p.StandardOutput.ReadLine())) {
        if ($line.Trim().Length -eq 0) { continue }
        $lines++; $last = $line
        if ($line -match '^\s*(ERREUR|ERROR|ERRO|FEHLER)') { if ($errors.Count -lt 10) { $errors.Add($line.Trim()) } }
        if ($watch.ElapsedMilliseconds -ge $nextDraw) {
            [Console]::Write("`r  $Label : {0:N0} élément(s)…   " -f $lines)
            $nextDraw = $watch.ElapsedMilliseconds + 400
        }
    }
    $p.WaitForExit()
    [Console]::Write("`r" + (' ' * 70) + "`r")
    $stderr = $errTask.Result
    # takeown / icacls écrivent leurs erreurs sur la sortie d'erreur : comptées et gardées (10 premières)
    $errCount = $errors.Count
    foreach ($l in ($stderr -split "`r?`n")) {
        # Lignes vides ignorées ; ligne indentée = suite du message précédent (pas une nouvelle erreur)
        if (($l.Trim().Length -eq 0) -or (($l -match '^\s+\S') -and ($errCount -gt 0))) { continue }
        $errCount++
        if ($errors.Count -lt 10) { $errors.Add($l.Trim()) }
    }
    return [pscustomobject]@{ Code = $p.ExitCode; Lines = $lines; Errors = $errors; ErrorCount = $errCount; Last = $last; StdErr = $stderr; Seconds = $watch.Elapsed.TotalSeconds }
}

# « Traitement réussi de 1 234 fichiers ; échec du traitement de 5 fichiers » (icacls, toutes langues : 2 nombres)
function Get-IcaclsCounts([string]$Summary) {
    $n = [regex]::Matches(($Summary -replace '[\u00A0\u202F ](?=\d{3}\b)', ''), '\d+') | ForEach-Object { [long]$_.Value }
    if (@($n).Count -ge 2) { return @{ Ok = @($n)[0]; Failed = @($n)[1] } }
    return @{ Ok = -1; Failed = -1 }
}

# Nombre d'éléments (sans suivre les jonctions ; dossiers inaccessibles comptés une fois)
function Measure-Items([string]$Path) {
    $count = 0; $denied = 0
    $stack = New-Object System.Collections.Generic.Stack[string]; $stack.Push($Path)
    while ($stack.Count -gt 0) {
        $d = $stack.Pop()
        try {
            foreach ($e in [System.IO.Directory]::EnumerateFileSystemEntries($d)) {
                $count++
                try { $attr = [System.IO.File]::GetAttributes($e) } catch { continue }
                if (($attr -band [IO.FileAttributes]::Directory) -and -not ($attr -band [IO.FileAttributes]::ReparsePoint)) { $stack.Push($e) }
            }
        } catch { $denied++ }
    }
    return [pscustomobject]@{ Items = $count; Denied = $denied }
}

# ─── Déroulement ─────────────────────────────────────────────────────────────

$TargetPath = Get-CleanPath $TargetPath
Write-Host ''
Write-Host '  DÉBLOQUER L''ACCÈS À UN DOSSIER - Alex Toolkit' -ForegroundColor Cyan
if ($DryRun) { Write-Host '  MODE CONTRÔLE : rien ne sera modifié.' -ForegroundColor Yellow }
if (-not $TargetPath) {
    if (-not (Test-Interactive)) { Write-Host '  [ERREUR] Indiquez le dossier avec -TargetPath.' -ForegroundColor Red; exit 2 }
    $TargetPath = Get-CleanPath (Read-Host '  Dossier ou disque à débloquer (ex. E:\Users\Jean)')
}

$reason = Get-ProtectionReason $TargetPath
if ($reason) {
    Write-Host "  [REFUSÉ] $TargetPath : $reason." -ForegroundColor Red
    Exit-Script 2
}

# Élévation (le script est relancé en administrateur avec le même dossier)
if (-not $DryRun -and -not (Test-IsAdmin)) {
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (ConvertTo-Arg $PSCommandPath), '-TargetPath', (ConvertTo-Arg $TargetPath))
    if ($NoPrompt) { $args2 += '-NoPrompt' }
    try { Start-Process powershell.exe -ArgumentList ($args2 -join ' ') -Verb RunAs | Out-Null }
    catch { Write-Host '  [ANNULÉ] Les droits administrateur sont nécessaires.' -ForegroundColor Yellow; Exit-Script 3 }
    exit 0
}

Write-Host "  Dossier : $TargetPath"
$warning = Get-Warning $TargetPath
if ($warning) { Write-Host "  [!] $warning" -ForegroundColor Yellow }
Write-Host '  Comptage des éléments…' -ForegroundColor DarkGray
$m = Measure-Items $TargetPath
Write-Host ("  Éléments visibles : {0:N0}" -f $m.Items) -NoNewline
if ($m.Denied -gt 0) { Write-Host ("   ·   dossiers inaccessibles : {0:N0} (normal : c'est ce que le script va débloquer)" -f $m.Denied) -ForegroundColor DarkGray } else { Write-Host '' }

$userSid = Get-SessionUserSid
try { $userName = (New-Object Security.Principal.SecurityIdentifier $userSid).Translate([Security.Principal.NTAccount]).Value } catch { $userName = $userSid }
Write-Host "  Accès donné à : Administrateurs + $userName"

if ($DryRun) {
    Write-Host ''
    Write-Host ("  [CONTRÔLE] Commandes prévues : takeown /F … /R /A /D {0}   puis   icacls … /grant *{1}:(OI)(CI)F *{2}:(OI)(CI)F /T /C /Q" -f (Get-TakeownYes), $AdminsSid, $userSid) -ForegroundColor DarkGray
    exit 0
}

if (Test-Interactive) {
    $answer = Read-Host '  Reprendre la propriété et donner le contrôle total ? (O/n)'
    if ($answer -match '^[nN]') { Write-Host '  Annulé : rien n''a été modifié.'; Exit-Script 3 }
}

# 1. Propriété
Write-Host ''
Write-Host '[1/2] Reprise de la propriété (takeown)…' -ForegroundColor Cyan
$yes = Get-TakeownYes
$own = Invoke-Counted "$env:SystemRoot\System32\takeown.exe" @('/F', $TargetPath, '/R', '/A', '/D', $yes) 'Propriété reprise'
if ($own.Lines -le 1 -and $yes -ne 'Y' -and ($own.StdErr + $own.Last) -match '/D') {
    # Lettre de réponse refusée (langue inattendue) : nouvel essai avec Y
    $own = Invoke-Counted "$env:SystemRoot\System32\takeown.exe" @('/F', $TargetPath, '/R', '/A', '/D', 'Y') 'Propriété reprise'
}
$ownOk = [Math]::Max(0, $own.Lines - $own.Errors.Count)
if ($ownOk -eq 0 -and $own.Code -ne 0) {
    Write-Host "  [ERREUR] takeown n'a rien repris (code $($own.Code)) :" -ForegroundColor Red
} else {
    $col = 'Green'; if ($own.ErrorCount -gt 0) { $col = 'Yellow' }
    Write-Host ("  [OK] {0:N0} élément(s) repris par le groupe Administrateurs, {1:N0} erreur(s), en {2:N0} s" -f $ownOk, $own.ErrorCount, $own.Seconds) -ForegroundColor $col
}
foreach ($e in $own.Errors) { Write-Host "  [!] $e" -ForegroundColor Yellow }

# 2. Droits
Write-Host ''
Write-Host '[2/2] Contrôle total pour les Administrateurs et la session ouverte (icacls)…' -ForegroundColor Cyan
$grant = Invoke-Counted "$env:SystemRoot\System32\icacls.exe" @($TargetPath, '/grant', "*${AdminsSid}:(OI)(CI)F", "*${userSid}:(OI)(CI)F", '/T', '/C', '/Q') 'Droits accordés'
$counts = Get-IcaclsCounts $grant.Last
if ($counts.Ok -ge 0) {
    $color = 'Green'; if ($counts.Failed -gt 0) { $color = 'Yellow' }
    Write-Host ("  [OK] {0:N0} élément(s) mis à jour, {1:N0} échec(s), en {2:N0} s" -f $counts.Ok, $counts.Failed, $grant.Seconds) -ForegroundColor $color
} else { Write-Host "  [i] $($grant.Last)" }
foreach ($e in $grant.Errors) { Write-Host "  [!] $e" -ForegroundColor Yellow }

# Bilan
$after = Measure-Items $TargetPath
Write-Host ''
if ($after.Denied -eq 0) {
    Write-Host "  DOSSIER DÉBLOQUÉ : $TargetPath" -ForegroundColor Green
    Write-Host '  Vous pouvez l''ouvrir dans l''Explorateur ou lancer la sauvegarde (Alex Toolkit > Intervention client).' -ForegroundColor Green
    $code = 0
} else {
    Write-Host ("  DÉBLOCAGE PARTIEL : {0:N0} dossier(s) restent inaccessibles (fichiers système ou disque en erreur)." -f $after.Denied) -ForegroundColor Yellow
    Write-Host '  Le reste est accessible ; si le disque est abîmé, une vérification (CHKDSK) peut aider.' -ForegroundColor Yellow
    $code = 1
}
Exit-Script $code
