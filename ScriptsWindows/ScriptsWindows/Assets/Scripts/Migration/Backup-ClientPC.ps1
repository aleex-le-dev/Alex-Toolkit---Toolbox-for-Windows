<#
.SYNOPSIS
    Alex Toolkit - Sauvegarde du PC d'un client avant changement de PC ou formatage.

.DESCRIPTION
    Copie rapide (robocopy multi-thread) vers <Lecteur>:\Backups_AlexDepannage\<Client>_<AAAA-MM-JJ>\ :
      Fichiers\      Bureau, Documents, Images, Téléchargements, Vidéos, Musique
      Navigateurs\   Profils Firefox / Thunderbird complets ; favoris et paramètres Chrome / Edge
      Passwords\     Exports CSV des mots de passe Chrome / Edge (faits par l'opérateur)
      Logiciels\     apps.json (winget export) + apps_list.txt (winget list)
      Systeme\       WiFi\*.xml (clés en clair) + system_info.txt (clé OEM, imprimantes, matériel)
      rapport_intervention.log

    Script 100 % console : aucune fenêtre graphique. Les étapes manuelles (export des mots de passe,
    fermeture d'un navigateur) attendent une validation au clavier ; en mode non interactif
    (-NonInteractive ou -NoPrompt), elles sont ignorées et notées dans le rapport.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Backup-ClientPC.ps1 -ClientName "Dupont" -DestinationDrive E
#>
[CmdletBinding()]
param(
    [string]$ClientName,
    [string]$DestinationDrive,
    # Profil à sauvegarder s'il n'est pas celui du compte courant (ex. D:\Users\Dupont).
    [string]$ProfilePath,
    # N'attend aucune validation (les étapes manuelles sont sautées).
    [switch]$NoPrompt,
    # Ne sauvegarde pas le dossier Téléchargements (souvent volumineux et sans valeur).
    [switch]$SkipDownloads
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$script:Started = Get-Date
$script:TotalSteps = 6
$script:LogFile = $null
$script:PendingLog = New-Object System.Collections.Generic.List[string]
$script:Summary = New-Object System.Collections.Generic.List[string]
$script:TotalBytes = [long]0
$script:Warnings = 0

# ─── Affichage et journal ────────────────────────────────────────────────────

function Write-Log([string]$Text) {
    $line = '{0:HH:mm:ss}  {1}' -f (Get-Date), $Text
    if ($script:LogFile) { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 }
    else { $script:PendingLog.Add($line) }
}

function Write-Step([int]$Number, [string]$Title) {
    Write-Host ''
    Write-Host ("[{0}/{1}] {2}" -f $Number, $script:TotalSteps, $Title) -ForegroundColor Cyan
    Write-Log ("=== [{0}/{1}] {2} ===" -f $Number, $script:TotalSteps, $Title)
}

function Write-Ok([string]$Text)   { Write-Host "  [OK] $Text" -ForegroundColor Green;  Write-Log "[OK] $Text" }
function Write-Info([string]$Text) { Write-Host "  [i] $Text";                          Write-Log "[i] $Text" }
function Write-Warn([string]$Text) { Write-Host "  [!] $Text" -ForegroundColor Yellow; Write-Log "[!] $Text"; $script:Warnings++ }
function Write-Fail([string]$Text) { Write-Host "  [ERREUR] $Text" -ForegroundColor Red; Write-Log "[ERREUR] $Text"; $script:Warnings++ }

function Test-Interactive {
    if ($NoPrompt) { return $false }
    if (-not [Environment]::UserInteractive) { return $false }
    foreach ($a in [Environment]::GetCommandLineArgs()) { if ($a -like '-NonI*') { return $false } }
    return $true
}

# Pose une question à l'opérateur ; en mode non interactif, renvoie la valeur par défaut.
function Read-Operator([string]$Prompt, [string]$Default = '') {
    if (-not (Test-Interactive)) { return $Default }
    Write-Host ''
    return (Read-Host "  >> $Prompt")
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return '{0:N1} Go' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N0} Mo' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N0} Ko' -f ($Bytes / 1KB) }
    return "$Bytes o"
}

# Taille et nombre de fichiers d'un dossier, sans suivre les jonctions.
function Measure-Folder([string]$Path) {
    $result = [pscustomobject]@{ Bytes = [long]0; Files = 0 }
    if (-not (Test-Path -LiteralPath $Path)) { return $result }
    $stack = New-Object System.Collections.Generic.Stack[System.IO.DirectoryInfo]
    $stack.Push((New-Object System.IO.DirectoryInfo $Path))
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        try {
            foreach ($f in $dir.EnumerateFiles()) { $result.Bytes += $f.Length; $result.Files++ }
            foreach ($d in $dir.EnumerateDirectories()) {
                if (-not ($d.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { $stack.Push($d) }
            }
        } catch { }
    }
    return $result
}

function Get-RobocopyMeaning([int]$Code) {
    if ($Code -eq 0) { return 'rien à copier (déjà à jour)' }
    if ($Code -lt 8) {
        $parts = @()
        if ($Code -band 1) { $parts += 'fichiers copiés' }
        if ($Code -band 2) { $parts += 'fichiers en plus dans la destination' }
        if ($Code -band 4) { $parts += 'différences détectées' }
        return ($parts -join ', ')
    }
    if ($Code -band 16) { return 'échec grave (accès refusé à la source ou à la destination)' }
    return 'certains fichiers n''ont pas pu être copiés (verrouillés ou accès refusé)'
}

# Guillemets d'un argument de ligne de commande (chemins avec espaces ; « E:\ » -> « "E:\\" »).
function ConvertTo-CommandArg([string]$Value) {
    if ($Value -and $Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}

# Octets réellement écrits par un processus (compteurs d'E/S Windows). Avec /MT, robocopy n'inscrit un fichier
# dans son journal qu'une fois terminé ET dans l'ordre : pendant un gros fichier (ISO, vidéo), le journal seul
# n'avance plus alors que la copie continue. L'avancement prend le maximum des deux mesures.
# (Pas l'espace libre de la destination : robocopy réserve la taille entière d'un fichier dès qu'il le commence.)
if (-not ('AlexToolkit.IoCounters' -as [type])) {
    try {
        Add-Type -TypeDefinition @'
namespace AlexToolkit {
    using System; using System.Runtime.InteropServices;
    public static class IoCounters {
        [StructLayout(LayoutKind.Sequential)] struct C { public ulong RO, WO, OO, RB, WB, OB; }
        [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out C c);
        public static long WriteBytes(IntPtr h) { C c; return GetProcessIoCounters(h, out c) ? (long)c.WB : 0; }
    }
}
'@
    } catch { }
}
function Get-ProcessWriteBytes($Process) {
    try { if (-not $Process.HasExited -and ('AlexToolkit.IoCounters' -as [type])) { return [AlexToolkit.IoCounters]::WriteBytes($Process.Handle) } } catch { }
    return [long]0
}

# Lance robocopy et affiche l'avancement en direct (pourcentage, volume, fichiers, débit, temps restant).
# robocopy écrit dans son journal une ligne par fichier traité, avec sa taille en octets (/BYTES),
# fichiers ignorés compris (/V) : ce journal est lu au fil de l'eau, sans re-parcourir la destination.
# Les fichiers en trop dans la destination (« *Fichier SUPPL. ») ne comptent pas.
# Le rapport reçoit un point d'étape tous les 10 % pour les copies de plus de 10 s.
function Invoke-RobocopyProgress {
    param([string[]]$Arguments, [string]$LogPath, [string]$Label, [long]$TotalBytes, [int]$TotalFiles,
          # Passe précédente (déjà copiée) : l'avancement continue au lieu de repartir de 0.
          [long]$BaseBytes = 0, [int]$BaseFiles = 0,
          # Fichiers de cette passe (/MIN /MAX) : les autres sont aussi listés dans le journal, on les écarte par leur taille.
          [long]$MinSize = 0, [long]$MaxSize = [long]::MaxValue,
          # Journal en ajout (/UNILOG+) : position où commence cette passe.
          [long]$LogOffset = 0)
    $psi = New-Object System.Diagnostics.ProcessStartInfo 'robocopy.exe'
    $psi.Arguments = (@($Arguments | ForEach-Object { ConvertTo-CommandArg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $ioStart = Get-ProcessWriteBytes $proc
    $measured = [long]0
    $history = New-Object System.Collections.Generic.Queue[object]

    $live = $true
    try { $live = -not [Console]::IsOutputRedirected } catch { }
    $width = 100
    try { $width = [Math]::Max(60, $Host.UI.RawUI.WindowSize.Width - 1) } catch { }
    $doneBytes = [long]0; $doneFiles = 0
    $nextLog = 10; $nextPrint = 10
    $decoder = [System.Text.Encoding]::Unicode.GetDecoder()
    $buffer = New-Object byte[] 65536
    $chars = New-Object char[] 65537
    $pending = ''
    $stream = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastDraw = [TimeSpan]::Zero
    try {
        while ($true) {
            # Lu AVANT la dernière lecture du journal : les dernières lignes écrites sont prises en compte.
            $exited = $proc.HasExited
            if (-not $stream -and (Test-Path -LiteralPath $LogPath)) {
                try { $stream = New-Object System.IO.FileStream($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)); [void]$stream.Seek($LogOffset, [IO.SeekOrigin]::Begin) } catch { }
            }
            if ($stream) {
                while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $c = $decoder.GetChars($buffer, 0, $n, $chars, 0)
                    $lines = ($pending + [string]::new($chars, 0, $c)) -split "`r?`n"
                    $pending = $lines[-1]
                    for ($i = 0; $i -lt $lines.Count - 1; $i++) {
                        if ($lines[$i] -match '^\t([^\t]+)\t+ *(\d+)\t' -and -not $Matches[1].Trim().StartsWith('*')) {
                            $size = [long]$Matches[2]
                            if ($size -ge $MinSize -and $size -le $MaxSize) { $doneBytes += $size; $doneFiles++ }
                        }
                    }
                }
            }

            $elapsed = $watch.Elapsed
            if ($exited -or ($elapsed - $lastDraw).TotalMilliseconds -ge 1000) {
                $lastDraw = $elapsed
                # Volume copié : maximum du journal et des octets écrits ; jamais en recul, jamais au-delà du total.
                $io = [Math]::Max([long]0, [long]((Get-ProcessWriteBytes $proc) - $ioStart))
                $cand = $BaseBytes + [Math]::Max([long]$doneBytes, [long]$io)
                if ($TotalBytes -gt 0) { $cand = [Math]::Min([long]$cand, [long]$TotalBytes) }
                if ($exited) { $cand = [Math]::Max([long]$cand, [long]($BaseBytes + $doneBytes)) }
                $measured = [Math]::Max([long]$measured, [long]$cand)
                # Débit sur les 10 dernières secondes : suit le passage petits fichiers / gros fichiers.
                $now = $elapsed.TotalSeconds
                $history.Enqueue(@($now, $measured))
                while ($history.Count -gt 2 -and ($now - $history.Peek()[0]) -gt 10) { [void]$history.Dequeue() }
                $first = $history.Peek()
                $speed = 0
                if (($now - $first[0]) -ge 2) { $speed = ($measured - $first[1]) / ($now - $first[0]) }
                if ($TotalBytes -gt 0) { $ratio = $measured / $TotalBytes }
                elseif ($TotalFiles -gt 0) { $ratio = ($BaseFiles + $doneFiles) / $TotalFiles }
                else { $ratio = 0 }
                # 1.0 et [double] : sinon PowerShell choisit Min(int, int) et arrondit 0,54 à 1.
                $pct = [int][Math]::Floor(100 * [Math]::Min(1.0, [double]$ratio))
                if (-not $exited -and $pct -ge 100) { $pct = 99 }
                $filled = [int][Math]::Floor($pct / 5)
                $text = '  {0,-18} {1,3} % [{2}{3}] {4} / {5} - {6} / {7} fichiers' -f $Label, $pct, ('#' * $filled), ('-' * (20 - $filled)), (Format-Size $measured), (Format-Size $TotalBytes), ($BaseFiles + $doneFiles), $TotalFiles
                if ($speed -gt 0) {
                    $text += ' - {0}/s' -f (Format-Size ([long]$speed))
                    if (-not $exited -and $TotalBytes -gt $measured) {
                        $left = [TimeSpan]::FromSeconds([Math]::Min(359999, ($TotalBytes - $measured) / $speed))
                        $text += ' - reste {0}' -f $left.ToString('h\:mm\:ss')
                    }
                }
                if ($live) {
                    if ($text.Length -gt $width) { $text = $text.Substring(0, $width) }
                    [Console]::Write("`r" + $text.PadRight($width))
                } elseif ($pct -ge $nextPrint -and -not $exited) {
                    Write-Host $text
                    while ($nextPrint -le $pct) { $nextPrint += 10 }
                }
                if ($pct -ge $nextLog -and $pct -lt 100 -and $elapsed.TotalSeconds -ge 10) {
                    Write-Log ('    {0} : {1} % ({2} / {3}, {4} / {5} fichiers terminés)' -f $Label, $pct, (Format-Size $measured), (Format-Size $TotalBytes), ($BaseFiles + $doneFiles), $TotalFiles)
                    while ($nextLog -le $pct) { $nextLog += 10 }
                }
            }
            if ($exited) { break }
            Start-Sleep -Milliseconds 300
        }
        $proc.WaitForExit()
    } finally {
        if ($stream) { $stream.Dispose() }
        # Ctrl+C : robocopy reçoit aussi l'interruption ; sinon, il est arrêté ici.
        if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
        if ($live) { [Console]::Write("`r" + (' ' * $width) + "`r") }
    }
    return [pscustomobject]@{ Code = $proc.ExitCode; Bytes = $doneBytes; Files = $doneFiles }
}

# Copie robocopy rapide. Les options imposées :
#   /MT:16    petits fichiers (< 8 Mo) en 16 threads, puis gros fichiers un par un (2 passes, voir plus bas)
#   /R:1 /W:1 un fichier verrouillé (cache ouvert, NTUSER.DAT) ne fige pas la copie
#   /XJ       ignore les jonctions (« Application Data »…) : pas de boucle infinie
#   /NP /NDL  pas de pourcentage par fichier ni de liste des dossiers
#   /V /BYTES la liste des fichiers va dans Logs\robocopy_<dossier>.log (rien dans la console) :
#             elle sert à l'avancement en direct (Invoke-RobocopyProgress) et garde la trace de chaque fichier
function Invoke-FastCopy {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Label,
        [string[]]$ExtraArgs = @(),
        # Taille déjà mesurée (étape 1) : évite de re-parcourir la source.
        [long]$ExpectedBytes = -1,
        [int]$ExpectedFiles = -1
    )
    if (-not (Test-Path -LiteralPath $Source)) { Write-Info "$Label : introuvable, ignoré"; return }

    if ($ExpectedBytes -lt 0 -or $ExpectedFiles -lt 0) {
        $src = Measure-Folder $Source
        $ExpectedBytes = $src.Bytes; $ExpectedFiles = $src.Files
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $safe = ($Label -replace '[^\w\-]', '_')
    $rcLog = Join-Path $script:RoboLogDir "robocopy_$safe.log"
    # Journal d'une sauvegarde précédente du même jour : il serait relu avant que robocopy ne le vide.
    Remove-Item -LiteralPath $rcLog -Force -ErrorAction SilentlyContinue
    $watch = [Diagnostics.Stopwatch]::StartNew()
    # Deux passes (mesuré sur une clé USB 3.2 : petits fichiers 15/s en /MT:16 contre 9/s en 1 thread ;
    # gros fichiers 3,8 Mo/s en 1 thread contre 3,4 Mo/s en /MT:16, les écritures parallèles se gênant sur la flash).
    $bigFile = 8MB
    $baseArgs = @($Source, $Destination, '/E', '/R:1', '/W:1', '/XJ', '/NP', '/NDL', '/V', '/BYTES') + $ExtraArgs
    $r1 = Invoke-RobocopyProgress -Arguments ($baseArgs + @('/MT:16', "/MAX:$($bigFile - 1)", "/UNILOG:$rcLog")) -LogPath $rcLog -Label $Label `
        -TotalBytes $ExpectedBytes -TotalFiles $ExpectedFiles -MaxSize ($bigFile - 1)
    $offset = [long]0
    try { $offset = (Get-Item -LiteralPath $rcLog).Length } catch { }
    $r2 = Invoke-RobocopyProgress -Arguments ($baseArgs + @("/MIN:$bigFile", "/UNILOG+:$rcLog")) -LogPath $rcLog -Label $Label `
        -TotalBytes $ExpectedBytes -TotalFiles $ExpectedFiles -MinSize $bigFile -BaseBytes $r1.Bytes -BaseFiles $r1.Files -LogOffset $offset
    # Codes robocopy : < 8 = bits de réussite cumulables ; >= 8 = échec (on garde le plus grave).
    if ($r1.Code -ge 8 -or $r2.Code -ge 8) { $code = [Math]::Max($r1.Code, $r2.Code) } else { $code = $r1.Code -bor $r2.Code }
    $watch.Stop()

    $m = Measure-Folder $Destination
    $script:TotalBytes += $m.Bytes
    $detail = '{0} - {1} fichiers - {2:mm\:ss} - code robocopy {3} ({4})' -f (Format-Size $m.Bytes), $m.Files, $watch.Elapsed, $code, (Get-RobocopyMeaning $code)
    if ($code -lt 8) { Write-Ok "$Label : $detail" }
    else { Write-Warn "$Label : $detail - détail dans Logs\robocopy_$safe.log" }
    $script:Summary.Add(('{0,-22} {1}' -f $Label, $detail))
}

# Attend que les processus indiqués soient fermés (copie cohérente des bases d'un profil).
function Wait-ProcessClosed([string[]]$Names, [string]$Label) {
    # Profil d'un autre compte ou d'un ancien disque : les navigateurs ouverts ici ne l'utilisent pas.
    if ($script:External) { return $true }
    while ($true) {
        $running = @(Get-Process -Name $Names -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return $true }
        if (-not (Test-Interactive)) {
            Write-Warn "$Label est ouvert : copie faite quand même (des fichiers en cours d'utilisation peuvent manquer)"
            return $false
        }
        $answer = Read-Operator "$Label est ouvert. Fermez-le puis appuyez sur Entrée (F = le fermer de force, I = ignorer)"
        if ($answer -match '^[fF]') { $running | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
        elseif ($answer -match '^[iI]') { Write-Warn "$Label laissé ouvert à la demande de l'opérateur"; return $false }
    }
}

function Get-BrowserExe([string]$ExeName) {
    foreach ($root in 'HKLM:', 'HKCU:') {
        try {
            $p = (Get-ItemProperty -LiteralPath "$root\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$ExeName" -ErrorAction Stop).'(default)'
            if ($p -and (Test-Path -LiteralPath $p.Trim('"'))) { return $p.Trim('"') }
        } catch { }
    }
    # Edge n'a pas toujours d'entrée « App Paths » ; Chrome peut être installé pour un seul compte.
    $sub = @{ 'chrome.exe' = 'Google\Chrome\Application'; 'msedge.exe' = 'Microsoft\Edge\Application' }[$ExeName]
    if ($sub) {
        foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
            if (-not $base) { continue }
            $p = Join-Path $base "$sub\$ExeName"
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    return $null
}

# ─── [0] Paramètres ──────────────────────────────────────────────────────────

# ─── Profil source / cible ───────────────────────────────────────────────────
# Par défaut : le profil du compte qui lance le script. -ProfilePath : un autre profil
# (autre compte, ou disque d'un PC en panne branché en USB : D:\Users\Dupont).
$script:External = $false
if ($ProfilePath) {
    $ProfilePath = $ProfilePath.Trim().TrimEnd('\')
    if (-not (Test-Path -LiteralPath $ProfilePath -PathType Container)) {
        Write-Host "  [ERREUR] Profil introuvable : $ProfilePath" -ForegroundColor Red; exit 2
    }
    $ProfilePath = (Resolve-Path -LiteralPath $ProfilePath).Path.TrimEnd('\')
    $script:External = ($ProfilePath -ne $env:USERPROFILE.TrimEnd('\'))
}
if ($script:External) {
    $script:Roaming = Join-Path $ProfilePath 'AppData\Roaming'
    $script:Local = Join-Path $ProfilePath 'AppData\Local'
} else {
    $script:Roaming = $env:APPDATA
    $script:Local = $env:LOCALAPPDATA
}

# Chemin réel d'un dossier personnel (déplacements et redirection OneDrive compris).
function Get-ProfileFolder([string]$Leaf, [string]$Special, [string]$Guid) {
    if ($script:External) {
        $direct = Join-Path $ProfilePath $Leaf
        $oneDrive = Join-Path $ProfilePath "OneDrive\$Leaf"
        if (-not (Test-Path -LiteralPath $direct) -and (Test-Path -LiteralPath $oneDrive)) { return $oneDrive }
        return $direct
    }
    if ($Guid) {
        try {
            $p = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders' -ErrorAction Stop).$Guid
            if ($p) { return [Environment]::ExpandEnvironmentVariables($p) }
        } catch { }
        return (Join-Path $env:USERPROFILE $Leaf)
    }
    return [Environment]::GetFolderPath($Special)
}

Write-Host ''
Write-Host '  ALEX TOOLKIT - SAUVEGARDE DU PC CLIENT' -ForegroundColor Cyan
Write-Host '  ======================================' -ForegroundColor Cyan

if (-not $ClientName) { $ClientName = Read-Operator 'Nom du client' }
if (-not $ClientName) { Write-Fail 'Nom du client manquant (-ClientName).'; exit 2 }
# Nom de dossier valide : caractères interdits remplacés, espaces conservés.
$ClientName = ($ClientName.Trim() -replace '[\\/:*?"<>|]', '_').Trim('. ')
if (-not $ClientName) { Write-Fail 'Nom du client invalide.'; exit 2 }

if (-not $DestinationDrive) {
    Write-Host ''
    Write-Host '  Lecteurs disponibles :'
    Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=2 OR DriveType=3' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host ('    {0}  {1,-20} {2} libres' -f $_.DeviceID, $_.VolumeName, (Format-Size ([long]$_.FreeSpace)))
    }
    $DestinationDrive = Read-Operator 'Lettre du disque externe (ex. E)'
}
$letter = ($DestinationDrive.Trim() -replace '[:\\/\s]', '').ToUpper()
if ($letter -notmatch '^[A-Z]$') { Write-Fail "Lecteur invalide : « $DestinationDrive » (attendu : une lettre, ex. E)."; exit 2 }
$driveRoot = "${letter}:\"
if (-not (Test-Path -LiteralPath $driveRoot)) { Write-Fail "Le lecteur $driveRoot est introuvable."; exit 2 }

$systemLetter = $env:SystemDrive.Substring(0, 1).ToUpper()
if ($letter -eq $systemLetter) {
    $answer = Read-Operator "$driveRoot est le disque de Windows : la sauvegarde serait perdue en cas de formatage. Continuer quand même ? (o/N)" 'n'
    if ($answer -notmatch '^[oOyY]') { Write-Host '  Sauvegarde annulée.'; exit 3 }
}

$backupRoot = Join-Path $driveRoot ("Backups_AlexDepannage\{0}_{1:yyyy-MM-dd}" -f $ClientName, (Get-Date))
$reuse = Test-Path -LiteralPath $backupRoot
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$script:RoboLogDir = Join-Path $backupRoot 'Logs'
New-Item -ItemType Directory -Force -Path $script:RoboLogDir | Out-Null
$script:LogFile = Join-Path $backupRoot 'rapport_intervention.log'

Add-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value @(
    '',
    '################################################################',
    ('SAUVEGARDE - client « {0} » - {1:dd/MM/yyyy HH:mm}' -f $ClientName, $script:Started),
    ('PC : {0} - compte : {1} - profil : {2}' -f $env:COMPUTERNAME, $env:USERNAME, $(if ($ProfilePath) { $ProfilePath } else { $env:USERPROFILE })),
    ('Destination : {0}' -f $backupRoot),
    '################################################################'
)
foreach ($l in $script:PendingLog) { Add-Content -LiteralPath $script:LogFile -Value $l -Encoding UTF8 }

# ─── [1] Préparation ─────────────────────────────────────────────────────────

Write-Step 1 'Préparation'
Write-Info "Dossier de sauvegarde : $backupRoot"
if ($reuse) { Write-Info 'Ce dossier existait déjà (même client, même jour) : la copie le complète sans rien supprimer.' }

# Le Toolkit tourne en administrateur : si l'élévation s'est faite avec un autre compte que celui du client,
# les dossiers « Documents », « Bureau »… seraient ceux de ce compte.
if (-not $script:External) { try {
    $owner = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction Stop |
        Select-Object -First 1 | Invoke-CimMethod -MethodName GetOwner -ErrorAction Stop
    if ($owner -and $owner.User -and $owner.User -ne $env:USERNAME) {
        Write-Warn "La session ouverte est celle de « $($owner.User) », mais le script tourne sous « $env:USERNAME »."
        $answer = Read-Operator "Sauvegarder quand même le profil de « $env:USERNAME » ? (o/N)" 'n'
        if ($answer -notmatch '^[oOyY]') { Write-Host '  Sauvegarde annulée : relancez depuis le compte du client.'; exit 3 }
    }
} catch { } }
if ($script:External) { Write-Info "Profil sauvegardé : $ProfilePath (autre compte ou ancien disque : Wi-Fi, logiciels, licence et mots de passe ignorés)" }

$folders = @(
    @{ Name = 'Bureau';          Path = (Get-ProfileFolder 'Desktop' 'DesktopDirectory') },
    @{ Name = 'Documents';       Path = (Get-ProfileFolder 'Documents' 'MyDocuments') },
    @{ Name = 'Images';          Path = (Get-ProfileFolder 'Pictures' 'MyPictures') },
    @{ Name = 'Telechargements'; Path = (Get-ProfileFolder 'Downloads' '' '{374DE290-123F-4565-9164-39C4925E467B}') },
    @{ Name = 'Videos';          Path = (Get-ProfileFolder 'Videos' 'MyVideos') },
    @{ Name = 'Musique';         Path = (Get-ProfileFolder 'Music' 'MyMusic') }
)
if ($SkipDownloads) { $folders = @($folders | Where-Object { $_.Name -ne 'Telechargements' }) }

$needed = [long]0
foreach ($f in $folders) {
    $m = Measure-Folder $f.Path
    $f.Bytes = $m.Bytes
    $f.Files = $m.Files
    $needed += $m.Bytes
    Write-Info ('{0,-16} {1,10}   {2}' -f $f.Name, (Format-Size $m.Bytes), $f.Path)
    if ($f.Path -like '*OneDrive*') { Write-Warn "$($f.Name) est synchronisé par OneDrive : les fichiers « en ligne uniquement » seront téléchargés pendant la copie." }
}
$free = (Get-PSDrive -Name $letter -ErrorAction SilentlyContinue).Free
Write-Info ('Total des dossiers : {0} - espace libre sur {1} : {2}' -f (Format-Size $needed), $driveRoot, (Format-Size ([long]$free)))
if ($free -and $needed -gt $free) {
    Write-Warn 'Espace libre insuffisant pour tout copier.'
    $answer = Read-Operator 'Continuer quand même ? (o/N)' 'n'
    if ($answer -notmatch '^[oOyY]') { Write-Host '  Sauvegarde annulée.'; exit 4 }
}

# ─── [2] Dossiers utilisateurs ───────────────────────────────────────────────

Write-Step 2 'Dossiers utilisateurs'
# desktop.ini / thumbs.db et fichiers système (/XA:S) exclus : recopiés sur un autre Windows,
# ils fausseraient le nom et l'icône des dossiers spéciaux.
$doneFolders = [long]0
$index = 0
foreach ($f in $folders) {
    $index++
    Write-Host ('  ({0}/{1}) {2} : {3}...' -f $index, $folders.Count, $f.Name, (Format-Size $f.Bytes)) -ForegroundColor DarkGray
    Invoke-FastCopy -Source $f.Path -Destination (Join-Path $backupRoot "Fichiers\$($f.Name)") -Label $f.Name `
        -ExtraArgs @('/XA:S', '/XF', 'desktop.ini', 'thumbs.db') -ExpectedBytes $f.Bytes -ExpectedFiles $f.Files
    $doneFolders += $f.Bytes
    if ($needed -gt 0) {
        Write-Info ('Avancement des dossiers : {0} % ({1} / {2})' -f [int][Math]::Floor(100 * $doneFolders / $needed), (Format-Size $doneFolders), (Format-Size $needed))
    }
}

# ─── [3] Navigateurs et messagerie ───────────────────────────────────────────

Write-Step 3 'Navigateurs, messagerie et mots de passe'
$nav = Join-Path $backupRoot 'Navigateurs'

# Firefox et Thunderbird : profils portables (logins.json + key4.db, courriers locaux) -> copie intégrale.
$mozilla = Join-Path $script:Roaming 'Mozilla'
if (Test-Path -LiteralPath $mozilla) {
    Wait-ProcessClosed @('firefox') 'Firefox' | Out-Null
    Invoke-FastCopy -Source $mozilla -Destination (Join-Path $nav 'Mozilla') -Label 'Firefox (profils)'
} else { Write-Info 'Firefox : non utilisé sur ce PC' }

$thunderbird = Join-Path $script:Roaming 'Thunderbird'
if (Test-Path -LiteralPath $thunderbird) {
    Wait-ProcessClosed @('thunderbird') 'Thunderbird' | Out-Null
    Invoke-FastCopy -Source $thunderbird -Destination (Join-Path $nav 'Thunderbird') -Label 'Thunderbird'
} else { Write-Info 'Thunderbird : non utilisé sur ce PC' }

# Chrome et Edge : les mots de passe sont chiffrés par DPAPI avec la clé de ce compte sur ce PC
# (clé dans « Local State ») : copiés tels quels, ils seraient illisibles ailleurs. On ne copie donc
# que les favoris et les paramètres, et les mots de passe passent par un export CSV.
$chromium = @(
    @{ Name = 'Chrome'; UserData = Join-Path $script:Local 'Google\Chrome\User Data';  Exe = 'chrome.exe'; Url = 'chrome://password-manager/settings' },
    @{ Name = 'Edge';   UserData = Join-Path $script:Local 'Microsoft\Edge\User Data'; Exe = 'msedge.exe'; Url = 'edge://settings/passwords' }
)
$passwordsDir = Join-Path $backupRoot 'Passwords'
foreach ($b in $chromium) {
    if (-not (Test-Path -LiteralPath $b.UserData)) { Write-Info "$($b.Name) : non utilisé sur ce PC"; $b.Used = $false; continue }
    $b.Used = $true
    $profiles = @(Get-ChildItem -LiteralPath $b.UserData -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' })
    $copied = 0
    foreach ($p in $profiles) {
        $dest = Join-Path $nav "$($b.Name)\$($p.Name)"
        foreach ($file in 'Bookmarks', 'Preferences') {
            $src = Join-Path $p.FullName $file
            if (Test-Path -LiteralPath $src) {
                New-Item -ItemType Directory -Force -Path $dest | Out-Null
                try { Copy-Item -LiteralPath $src -Destination $dest -Force -ErrorAction Stop; $copied++ }
                catch { Write-Warn "$($b.Name) ($($p.Name)) : $file non copié ($($_.Exception.Message))" }
            }
        }
    }
    $names = ($profiles | ForEach-Object { $_.Name }) -join ', '
    if ($copied -gt 0) { Write-Ok "$($b.Name) : favoris et paramètres sauvegardés (profils : $names)" }
    else { Write-Info "$($b.Name) : aucun favori trouvé" }
}

# Export manuel des mots de passe (le navigateur demande le code du compte Windows : DPAPI).
foreach ($b in ($chromium | Where-Object { $_.Used })) {
    $exe = Get-BrowserExe $b.Exe
    if ($script:External) { Write-Warn "$($b.Name) : mots de passe non exportables depuis un autre profil (chiffrés pour ce compte : DPAPI)"; continue }
    if (-not $exe) { Write-Info "$($b.Name) : profil présent mais navigateur non installé, export des mots de passe impossible"; continue }
    if (-not (Test-Interactive)) {
        Write-Warn "$($b.Name) : export des mots de passe non fait (mode non interactif)"
        continue
    }
    New-Item -ItemType Directory -Force -Path $passwordsDir | Out-Null
    $target = Join-Path $passwordsDir "$($b.Name)_Passwords.csv"
    $askedAt = Get-Date
    Start-Process -FilePath $exe -ArgumentList $b.Url
    Write-Host ''
    Write-Host "  ---- MOTS DE PASSE $($b.Name.ToUpper()) ----" -ForegroundColor Yellow
    Write-Host "  1. Dans la page ouverte : « Exporter les mots de passe » (ou « ... > Exporter »)."
    Write-Host '  2. Saisissez le code PIN / mot de passe Windows du client si demandé.'
    Write-Host "  3. Enregistrez le fichier ici :" -NoNewline; Write-Host " $target" -ForegroundColor White
    while ($true) {
        $answer = Read-Operator "Entrée une fois l'export enregistré (P = passer ce navigateur)"
        if ($answer -match '^[pP]') { Write-Warn "$($b.Name) : export des mots de passe passé par l'opérateur"; break }
        $csv = @(Get-ChildItem -LiteralPath $passwordsDir -Filter *.csv -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $askedAt.AddSeconds(-2) })
        if ($csv.Count -gt 0) {
            Write-Ok "$($b.Name) : export trouvé ($(($csv | ForEach-Object { $_.Name }) -join ', '))"
            break
        }
        Write-Warn "Aucun fichier .csv récent dans $passwordsDir"
    }
}
if (Test-Path -LiteralPath $passwordsDir) {
    Write-Warn 'Passwords\ contient des mots de passe EN CLAIR : à supprimer après la restauration (le script de restauration le propose).'
}

# ─── [4] Logiciels installés ─────────────────────────────────────────────────

Write-Step 4 'Liste des logiciels installés'
if ($script:External) { Write-Info 'Ignoré : les logiciels sont ceux de ce PC, pas du profil sauvegardé' } else {
$apps = Join-Path $backupRoot 'Logiciels'
New-Item -ItemType Directory -Force -Path $apps | Out-Null
$appsJson = Join-Path $apps 'apps.json'
$appsList = Join-Path $apps 'apps_list.txt'

if (Get-Command winget.exe -ErrorAction SilentlyContinue) {
    # apps.json : réinstallation en lot sur le nouveau PC ; apps_list.txt : trace lisible, y compris
    # les logiciels hors catalogue Winget (métier, anciens) qu'il faudra réinstaller à la main.
    # Les deux commandes sont lancées l'une après l'autre : deux winget simultanés se disputent l'index des sources.
    & winget.exe export -o $appsJson --accept-source-agreements --disable-interactivity 2>&1 | Out-Null
    if (Test-Path -LiteralPath $appsJson) {
        $count = 0
        try { $count = @((Get-Content -LiteralPath $appsJson -Raw | ConvertFrom-Json).Sources | ForEach-Object { $_.Packages }).Count } catch { }
        Write-Ok "apps.json : $count logiciels réinstallables par Winget"
    } else { Write-Warn 'winget export a échoué : pas de apps.json' }

    & winget.exe list --accept-source-agreements --disable-interactivity 2>&1 |
        Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*[-\\|/]\s*$' } |
        Out-File -LiteralPath $appsList -Encoding utf8
    Write-Ok 'apps_list.txt : liste complète (winget list)'
} else {
    Write-Warn 'Winget absent : pas de apps.json, liste construite depuis le registre'
}

# Complément toujours utile : liste du registre (Programmes et fonctionnalités), avec version et éditeur.
$keys = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
$reg = @(Get-ItemProperty $keys -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -and -not $_.SystemComponent -and -not $_.ParentKeyName } |
    Sort-Object DisplayName -Unique |
    ForEach-Object { '{0,-60} {1,-20} {2}' -f $_.DisplayName, $_.DisplayVersion, $_.Publisher })
$reg | Out-File -LiteralPath (Join-Path $apps 'logiciels_registre.txt') -Encoding utf8
Write-Ok "logiciels_registre.txt : $($reg.Count) programmes (Programmes et fonctionnalités)"
}

# ─── [5] Réseau, licence et matériel ─────────────────────────────────────────

Write-Step 5 'Wi-Fi, licence Windows, imprimantes'
if ($script:External) { Write-Info 'Ignoré : Wi-Fi, licence et imprimantes sont ceux de ce PC, pas du profil sauvegardé' } else {
$sys = Join-Path $backupRoot 'Systeme'
$wifiDir = Join-Path $sys 'WiFi'
New-Item -ItemType Directory -Force -Path $wifiDir | Out-Null

# netsh tronque le chemin d'export (~128 caractères) : export dans un dossier temporaire au chemin court, puis déplacement.
$tmp = Join-Path $env:PUBLIC ('ATK' + [guid]::NewGuid().ToString('N').Substring(0, 6))
try {
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    & netsh.exe wlan export profile key=clear folder="$tmp" 2>&1 | Out-Null
    $xml = @(Get-ChildItem -LiteralPath $tmp -Filter *.xml -ErrorAction SilentlyContinue)
    $xml | Move-Item -Destination $wifiDir -Force
    if ($xml.Count -gt 0) { Write-Ok "$($xml.Count) profil(s) Wi-Fi exporté(s) avec leur clé (reconnexion automatique)" }
    else { Write-Info 'Aucun réseau Wi-Fi enregistré (ou pas de carte Wi-Fi)' }
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue   # contenait des clés en clair
}

$info = New-Object System.Collections.Generic.List[string]
$info.Add(('Rapport système - {0} - {1:dd/MM/yyyy HH:mm}' -f $env:COMPUTERNAME, (Get-Date)))
$info.Add(('Client : {0} - compte Windows : {1}' -f $ClientName, $env:USERNAME))
$info.Add('')

$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
$cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
$info.Add('== Windows ==')
if ($os) { $info.Add(('{0} ({1}) - build {2} - installé le {3:dd/MM/yyyy}' -f $os.Caption, $os.OSArchitecture, $os.BuildNumber, $os.InstallDate)) }

$oem = $null
try { $oem = (Get-CimInstance SoftwareLicensingService -ErrorAction Stop).OA3xOriginalProductKey } catch { }
if ($oem) {
    $info.Add("Clé OEM (BIOS) : $oem")
    Write-Ok 'Clé Windows OEM du BIOS relevée'
} else {
    $info.Add('Clé OEM (BIOS) : aucune (licence numérique liée au compte Microsoft, ou clé de détail)')
    Write-Info 'Pas de clé OEM dans le BIOS (licence numérique ou clé de détail)'
}
$info.Add('')
$info.Add('== Matériel ==')
if ($cs) { $info.Add(('{0} {1} - {2:N0} Go de RAM' -f $cs.Manufacturer, $cs.Model, ($cs.TotalPhysicalMemory / 1GB))) }
if ($bios) { $info.Add("N° de série : $($bios.SerialNumber)") }
if ($cpu) { $info.Add("Processeur : $($cpu.Name.Trim())") }
Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue | ForEach-Object {
    $info.Add(('Disque : {0} - {1}' -f $_.Model, (Format-Size ([long]$_.Size))))
}

$info.Add('')
$info.Add('== Imprimantes ==')
$printers = @(Get-CimInstance Win32_Printer -ErrorAction SilentlyContinue)
foreach ($p in $printers) {
    $flag = ''
    if ($p.Default) { $flag = ' [PAR DÉFAUT]' }
    $info.Add(('{0}{1} - pilote : {2} - port : {3}' -f $p.Name, $flag, $p.DriverName, $p.PortName))
}
$real = @($printers | Where-Object { $_.Name -notmatch 'PDF|XPS|OneNote|Fax' }).Count
Write-Ok "$($printers.Count) imprimante(s) listée(s) dont $real physique(s) ou réseau"

$info.Add('')
$info.Add('== Lecteurs réseau ==')
$maps = @(Get-CimInstance Win32_MappedLogicalDisk -ErrorAction SilentlyContinue)
if ($maps.Count -eq 0) { $info.Add('(aucun)') }
foreach ($d in $maps) { $info.Add(('{0}  {1}' -f $d.DeviceID, $d.ProviderName)) }

$info | Out-File -LiteralPath (Join-Path $sys 'system_info.txt') -Encoding utf8
Write-Ok 'system_info.txt écrit'
}

# ─── [6] Bilan ───────────────────────────────────────────────────────────────

Write-Step 6 'Bilan'
$elapsed = (Get-Date) - $script:Started
$manifest = [ordered]@{
    Client = $ClientName; Computer = $env:COMPUTERNAME; User = $env:USERNAME
    Profile = $(if ($ProfilePath) { $ProfilePath } else { $env:USERPROFILE })
    Date = (Get-Date).ToString('s'); Folders = @($folders | ForEach-Object { $_.Name })
    ToolVersion = 'Backup-ClientPC 1.0'
}
$manifest | ConvertTo-Json | Out-File -LiteralPath (Join-Path $backupRoot 'sauvegarde_client.json') -Encoding utf8

$report = New-Object System.Collections.Generic.List[string]
$report.Add('')
$report.Add('---- Résumé des copies ----')
foreach ($l in $script:Summary) { $report.Add($l) }
$report.Add(('Volume total sauvegardé : {0}' -f (Format-Size $script:TotalBytes)))
$report.Add(('Durée totale : {0:hh\:mm\:ss}' -f $elapsed))
$report.Add(('Avertissements : {0}' -f $script:Warnings))
Add-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value $report.ToArray()

Write-Host ''
Write-Host ('  Volume sauvegardé : {0}   Durée : {1:hh\:mm\:ss}   Avertissements : {2}' -f (Format-Size $script:TotalBytes), $elapsed, $script:Warnings) -ForegroundColor Cyan
Write-Host "  Rapport : $script:LogFile"
if ($script:Warnings -gt 0) { Write-Host '  Vérifiez les lignes [!] du rapport avant de formater l''ancien PC.' -ForegroundColor Yellow }
else { Write-Host '  Sauvegarde complète.' -ForegroundColor Green }
$secrets = @()
if (@(Get-ChildItem -LiteralPath (Join-Path $backupRoot 'Systeme\WiFi') -Filter *.xml -ErrorAction SilentlyContinue).Count -gt 0) { $secrets += 'des clés Wi-Fi' }
if (@(Get-ChildItem -LiteralPath $passwordsDir -Filter *.csv -ErrorAction SilentlyContinue).Count -gt 0) { $secrets += 'des mots de passe' }
if ($secrets.Count -gt 0) { Write-Host ('  Le dossier contient {0} en clair : gardez le disque sur vous.' -f ($secrets -join ' et ')) -ForegroundColor Yellow }
if ($script:Warnings -gt 0) { exit 1 } else { exit 0 }
