<#
.SYNOPSIS
    Alex Toolkit - Restauration sur le nouveau PC d'une sauvegarde faite par Backup-ClientPC.ps1.

.DESCRIPTION
    1. Liste les sauvegardes de <Lecteur>:\Backups_AlexDepannage (tous les lecteurs) et en fait choisir une.
    2. Restaure les dossiers utilisateurs (robocopy /XO : un fichier plus récent sur le nouveau PC n'est jamais écrasé).
    3. Restaure les profils Firefox / Thunderbird et les favoris / paramètres Chrome / Edge
       (les fichiers présents sont d'abord mis de côté en « .avant_restauration »).
    4. Réimporte les profils Wi-Fi (netsh wlan add profile).
    5. Réinstalle les logiciels (winget import --ignore-unavailable).
    6. Fait importer les mots de passe CSV puis propose de les supprimer du support (sécurité / RGPD).

    Script 100 % console : aucune fenêtre graphique. En mode non interactif (-NonInteractive ou -NoPrompt),
    les étapes manuelles sont ignorées et notées dans le rapport.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Restore-ClientPC.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File Restore-ClientPC.ps1 -BackupPath "E:\Backups_AlexDepannage\Dupont_2026-11-18"
#>
[CmdletBinding()]
param(
    # Dossier client à restaurer ; sinon, choix dans la liste des sauvegardes trouvées.
    [string]$BackupPath,
    # Profil à restaurer s'il n'est pas celui du compte courant (ex. C:\Users\Dupont).
    [string]$ProfilePath,
    [switch]$NoPrompt,
    [switch]$SkipApps
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$script:Started = Get-Date
$script:TotalSteps = 6
$script:LogFile = $null
$script:Summary = New-Object System.Collections.Generic.List[string]
$script:TotalBytes = [long]0
$script:Warnings = 0
$script:Stamp = Get-Date -Format 'yyyyMMdd_HHmm'

# ─── Affichage et journal ────────────────────────────────────────────────────

function Write-Log([string]$Text) {
    if (-not $script:LogFile) { return }
    Add-Content -LiteralPath $script:LogFile -Value ('{0:HH:mm:ss}  {1}' -f (Get-Date), $Text) -Encoding UTF8
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
    if ($Code -eq 0) { return 'rien à copier (déjà présent)' }
    if ($Code -lt 8) {
        $parts = @()
        if ($Code -band 1) { $parts += 'fichiers copiés' }
        if ($Code -band 2) { $parts += 'fichiers déjà présents conservés' }
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

# Lance robocopy et affiche l'avancement en direct (identique à Backup-ClientPC.ps1) : le journal
# robocopy (/V /BYTES : une ligne par fichier traité, ignorés compris) est lu au fil de l'eau.
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
        if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
        if ($live) { [Console]::Write("`r" + (' ' * $width) + "`r") }
    }
    return [pscustomobject]@{ Code = $proc.ExitCode; Bytes = $doneBytes; Files = $doneFiles }
}

# Mêmes options que la sauvegarde (voir Backup-ClientPC.ps1) ; /XO en restauration : n'écrase jamais
# un fichier plus récent déjà présent sur le nouveau PC.
function Invoke-FastCopy {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Label,
        [string[]]$ExtraArgs = @()
    )
    if (-not (Test-Path -LiteralPath $Source)) { Write-Info "$Label : absent de la sauvegarde"; return }
    $m = Measure-Folder $Source
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $safe = ($Label -replace '[^\w\-]', '_')
    $rcLog = Join-Path $script:RoboLogDir "restauration_$safe.log"
    Remove-Item -LiteralPath $rcLog -Force -ErrorAction SilentlyContinue
    $watch = [Diagnostics.Stopwatch]::StartNew()
    # Deux passes (mesuré sur une clé USB 3.2 : petits fichiers 15/s en /MT:16 contre 9/s en 1 thread ;
    # gros fichiers 3,8 Mo/s en 1 thread contre 3,4 Mo/s en /MT:16, les écritures parallèles se gênant sur la flash).
    $bigFile = 8MB
    $baseArgs = @($Source, $Destination, '/E', '/R:1', '/W:1', '/XJ', '/NP', '/NDL', '/V', '/BYTES') + $ExtraArgs
    $r1 = Invoke-RobocopyProgress -Arguments ($baseArgs + @('/MT:16', "/MAX:$($bigFile - 1)", "/UNILOG:$rcLog")) -LogPath $rcLog -Label $Label `
        -TotalBytes $m.Bytes -TotalFiles $m.Files -MaxSize ($bigFile - 1)
    $offset = [long]0
    try { $offset = (Get-Item -LiteralPath $rcLog).Length } catch { }
    $r2 = Invoke-RobocopyProgress -Arguments ($baseArgs + @("/MIN:$bigFile", "/UNILOG+:$rcLog")) -LogPath $rcLog -Label $Label `
        -TotalBytes $m.Bytes -TotalFiles $m.Files -MinSize $bigFile -BaseBytes $r1.Bytes -BaseFiles $r1.Files -LogOffset $offset
    # Codes robocopy : < 8 = bits de réussite cumulables ; >= 8 = échec (on garde le plus grave).
    if ($r1.Code -ge 8 -or $r2.Code -ge 8) { $code = [Math]::Max($r1.Code, $r2.Code) } else { $code = $r1.Code -bor $r2.Code }
    $watch.Stop()

    $script:TotalBytes += $m.Bytes
    $detail = '{0} - {1} fichiers - {2:mm\:ss} - code robocopy {3} ({4})' -f (Format-Size $m.Bytes), $m.Files, $watch.Elapsed, $code, (Get-RobocopyMeaning $code)
    if ($code -lt 8) { Write-Ok "$Label -> $Destination : $detail" }
    else { Write-Warn "$Label : $detail - détail dans Logs\restauration_$safe.log" }
    $script:Summary.Add(('{0,-22} {1}' -f $Label, $detail))
}

# Le navigateur réécrit ses fichiers en se fermant : il doit être fermé avant la restauration.
function Wait-ProcessClosed([string[]]$Names, [string]$Label) {
    # Profil d'un autre compte ou d'un ancien disque : les navigateurs ouverts ici ne l'utilisent pas.
    if ($script:External) { return $true }
    while ($true) {
        $running = @(Get-Process -Name $Names -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return $true }
        if (-not (Test-Interactive)) { Write-Warn "$Label est ouvert : restauration de $Label ignorée"; return $false }
        $answer = Read-Operator "$Label est ouvert. Fermez-le puis appuyez sur Entrée (F = le fermer de force, I = ignorer $Label)"
        if ($answer -match '^[fF]') { $running | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
        elseif ($answer -match '^[iI]') { Write-Warn "$Label : restauration ignorée par l'opérateur"; return $false }
    }
}

# Met de côté un fichier ou dossier existant (jamais supprimé) avant de le remplacer.
function Backup-Existing([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $aside = "$Path.avant_restauration_$($script:Stamp)"
    try { Rename-Item -LiteralPath $Path -NewName (Split-Path $aside -Leaf) -ErrorAction Stop; Write-Info "Ancien contenu conservé : $aside" }
    catch { Write-Warn "Impossible de mettre de côté $Path ($($_.Exception.Message))" }
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

# Suppression sans corbeille, contenu écrasé avant (limite : sur clé USB / SSD, l'usure répartie peut garder
# une ancienne copie physique ; seul le chiffrement du support protège totalement).
function Remove-Securely([string]$Path) {
    try {
        $len = (Get-Item -LiteralPath $Path -Force).Length
        $buffer = New-Object byte[] ([Math]::Min([Math]::Max($len, 1), 1MB))
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $fs = [System.IO.File]::Open($Path, 'Open', 'Write', 'None')
        try {
            $written = [long]0
            while ($written -lt $len) {
                $rng.GetBytes($buffer)
                $n = [int][Math]::Min($buffer.Length, $len - $written)
                $fs.Write($buffer, 0, $n); $written += $n
            }
            $fs.Flush($true)
        } finally { $fs.Dispose(); $rng.Dispose() }
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        return $true
    } catch {
        try { Remove-Item -LiteralPath $Path -Force -ErrorAction Stop; return $true } catch { return $false }
    }
}

# ─── [1] Choix de la sauvegarde ──────────────────────────────────────────────

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
Write-Host '  ALEX TOOLKIT - RESTAURATION DU PC CLIENT' -ForegroundColor Cyan
Write-Host '  ========================================' -ForegroundColor Cyan

Write-Step 1 'Choix de la sauvegarde'
if (-not $BackupPath) {
    $found = @()
    foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $d.IsReady) { continue }
        $base = Join-Path $d.RootDirectory.FullName 'Backups_AlexDepannage'
        if (Test-Path -LiteralPath $base) {
            $found += @(Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Fichiers') } )
        }
    }
    $found = @($found | Sort-Object LastWriteTime -Descending)
    if ($found.Count -eq 0) { Write-Host '  [ERREUR] Aucune sauvegarde trouvée (dossier Backups_AlexDepannage absent de tous les lecteurs).' -ForegroundColor Red; exit 2 }

    Write-Host ''
    for ($i = 0; $i -lt $found.Count; $i++) {
        Write-Host ('    {0,2}. {1,-40} {2:dd/MM/yyyy HH:mm}   ({3})' -f ($i + 1), $found[$i].Name, $found[$i].LastWriteTime, $found[$i].FullName)
    }
    if ($found.Count -eq 1 -and -not (Test-Interactive)) { $choice = '1' }
    else { $choice = Read-Operator "Numéro de la sauvegarde à restaurer (1-$($found.Count))" }
    $index = 0
    if (-not [int]::TryParse("$choice", [ref]$index) -or $index -lt 1 -or $index -gt $found.Count) {
        Write-Host '  [ERREUR] Choix invalide.' -ForegroundColor Red; exit 2
    }
    $BackupPath = $found[$index - 1].FullName
}
if (-not (Test-Path -LiteralPath (Join-Path $BackupPath 'Fichiers'))) {
    Write-Host "  [ERREUR] $BackupPath n'est pas une sauvegarde Backup-ClientPC (dossier Fichiers absent)." -ForegroundColor Red; exit 2
}

$script:LogFile = Join-Path $BackupPath 'rapport_intervention.log'
$script:RoboLogDir = Join-Path $BackupPath 'Logs'
New-Item -ItemType Directory -Force -Path $script:RoboLogDir | Out-Null
Add-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value @(
    '',
    '################################################################',
    ('RESTAURATION - {0:dd/MM/yyyy HH:mm}' -f $script:Started),
    ('PC cible : {0} - compte : {1} - profil : {2}' -f $env:COMPUTERNAME, $env:USERNAME, $(if ($ProfilePath) { $ProfilePath } else { $env:USERPROFILE })),
    ('Sauvegarde : {0}' -f $BackupPath),
    '################################################################'
)

$origin = ''
try {
    $m = Get-Content -LiteralPath (Join-Path $BackupPath 'sauvegarde_client.json') -Raw -ErrorAction Stop | ConvertFrom-Json
    $origin = " (client « $($m.Client) », PC $($m.Computer), compte $($m.User), le $(([datetime]$m.Date).ToString('dd/MM/yyyy HH:mm')))"
} catch { }
Write-Ok "Sauvegarde choisie : $BackupPath$origin"

if (-not $script:External) { try {
    $owner = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction Stop |
        Select-Object -First 1 | Invoke-CimMethod -MethodName GetOwner -ErrorAction Stop
    if ($owner -and $owner.User -and $owner.User -ne $env:USERNAME) {
        Write-Warn "La session ouverte est celle de « $($owner.User) », mais le script tourne sous « $env:USERNAME » : les fichiers iraient dans le profil de « $env:USERNAME »."
        $answer = Read-Operator 'Continuer quand même ? (o/N)' 'n'
        if ($answer -notmatch '^[oOyY]') { Write-Host '  Restauration annulée : relancez depuis le compte du client.'; exit 3 }
    }
} catch { } }
if ($script:External) { Write-Info "Profil cible : $ProfilePath (autre compte : l'import des mots de passe est ignoré)" }

# ─── [2] Dossiers utilisateurs ───────────────────────────────────────────────

Write-Step 2 'Dossiers utilisateurs'
$targets = [ordered]@{
    'Bureau'          = (Get-ProfileFolder 'Desktop' 'DesktopDirectory')
    'Documents'       = (Get-ProfileFolder 'Documents' 'MyDocuments')
    'Images'          = (Get-ProfileFolder 'Pictures' 'MyPictures')
    'Telechargements' = (Get-ProfileFolder 'Downloads' '' '{374DE290-123F-4565-9164-39C4925E467B}')
    'Videos'          = (Get-ProfileFolder 'Videos' 'MyVideos')
    'Musique'         = (Get-ProfileFolder 'Music' 'MyMusic')
}
$index = 0
foreach ($name in $targets.Keys) {
    $index++
    Write-Host ('  ({0}/{1}) {2}...' -f $index, $targets.Count, $name) -ForegroundColor DarkGray
    Invoke-FastCopy -Source (Join-Path $BackupPath "Fichiers\$name") -Destination $targets[$name] -Label $name -ExtraArgs @('/XO')
}

# ─── [3] Navigateurs et messagerie ───────────────────────────────────────────

Write-Step 3 'Navigateurs et messagerie'
$nav = Join-Path $BackupPath 'Navigateurs'

foreach ($moz in @(
    @{ Name = 'Firefox';     Src = Join-Path $nav 'Mozilla';     Dst = Join-Path $script:Roaming 'Mozilla';     Proc = 'firefox' },
    @{ Name = 'Thunderbird'; Src = Join-Path $nav 'Thunderbird'; Dst = Join-Path $script:Roaming 'Thunderbird'; Proc = 'thunderbird' })) {
    if (-not (Test-Path -LiteralPath $moz.Src)) { Write-Info "$($moz.Name) : absent de la sauvegarde"; continue }
    if (-not (Wait-ProcessClosed @($moz.Proc) $moz.Name)) { continue }
    # Profil complet de l'ancien PC (profiles.ini compris) : le profil vide éventuellement créé sur le nouveau PC est mis de côté.
    Backup-Existing $moz.Dst
    Invoke-FastCopy -Source $moz.Src -Destination $moz.Dst -Label $moz.Name
}

$chromium = @(
    @{ Name = 'Chrome'; UserData = Join-Path $script:Local 'Google\Chrome\User Data';  Proc = 'chrome';  Exe = 'chrome.exe'; Url = 'chrome://password-manager/settings' },
    @{ Name = 'Edge';   UserData = Join-Path $script:Local 'Microsoft\Edge\User Data'; Proc = 'msedge';  Exe = 'msedge.exe'; Url = 'edge://settings/passwords' }
)
foreach ($b in $chromium) {
    $src = Join-Path $nav $b.Name
    if (-not (Test-Path -LiteralPath $src)) { Write-Info "$($b.Name) : absent de la sauvegarde"; continue }
    if (-not (Wait-ProcessClosed @($b.Proc) $b.Name)) { continue }
    $restored = @()
    foreach ($p in @(Get-ChildItem -LiteralPath $src -Directory -ErrorAction SilentlyContinue)) {
        $dest = Join-Path $b.UserData $p.Name
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        foreach ($file in @(Get-ChildItem -LiteralPath $p.FullName -File -ErrorAction SilentlyContinue)) {
            $target = Join-Path $dest $file.Name
            if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination "$target.avant_restauration_$($script:Stamp)" -Force -ErrorAction SilentlyContinue }
            try { Copy-Item -LiteralPath $file.FullName -Destination $target -Force -ErrorAction Stop }
            catch { Write-Warn "$($b.Name) ($($p.Name)) : $($file.Name) non restauré ($($_.Exception.Message))" }
        }
        $restored += $p.Name
    }
    if ($restored.Count -gt 0) {
        Write-Ok "$($b.Name) : favoris et paramètres restaurés (profils : $($restored -join ', '))"
        if (@($restored | Where-Object { $_ -ne 'Default' }).Count -gt 0) {
            Write-Info "$($b.Name) : les profils autres que « Default » apparaîtront après création d'un profil du même nom dans le navigateur (ou importer leurs favoris)."
        }
        if (-not (Get-BrowserExe $b.Exe)) { Write-Warn "$($b.Name) n'est pas installé : ses favoris seront repris à son installation." }
    }
}

# ─── [4] Wi-Fi ───────────────────────────────────────────────────────────────

Write-Step 4 'Réseaux Wi-Fi'
$wifiDir = Join-Path $BackupPath 'Systeme\WiFi'
$wifiFiles = @(Get-ChildItem -LiteralPath $wifiDir -Filter *.xml -ErrorAction SilentlyContinue)
if ($wifiFiles.Count -eq 0) { Write-Info 'Aucun profil Wi-Fi dans la sauvegarde' }
else {
    $ok = 0; $failed = @()
    foreach ($x in $wifiFiles) {
        $out = & netsh.exe wlan add profile filename="$($x.FullName)" user=all 2>&1
        if ($LASTEXITCODE -eq 0) { $ok++ } else { $failed += $x.BaseName }
    }
    Write-Ok "$ok / $($wifiFiles.Count) profil(s) Wi-Fi importé(s)"
    if ($failed.Count -gt 0) { Write-Warn ("Non importés (pas de carte Wi-Fi ou service WLAN arrêté ?) : {0}" -f ($failed -join ', ')) }
}

# ─── [5] Logiciels ───────────────────────────────────────────────────────────

Write-Step 5 'Réinstallation des logiciels'
$appsJson = Join-Path $BackupPath 'Logiciels\apps.json'
if (-not (Test-Path -LiteralPath $appsJson)) { Write-Info 'Pas de apps.json dans la sauvegarde' }
elseif ($SkipApps) { Write-Info 'Réinstallation ignorée (-SkipApps)' }
elseif (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
    Write-Warn 'Winget absent sur ce PC : installez « Programme d''installation d''application » depuis le Microsoft Store puis relancez.'
} else {
    $count = 0
    try { $count = @((Get-Content -LiteralPath $appsJson -Raw | ConvertFrom-Json).Sources | ForEach-Object { $_.Packages }).Count } catch { }
    $answer = Read-Operator "Réinstaller les $count logiciels de l'ancien PC ? Peut être long. (O/n)" 'o'
    if ($answer -match '^[nN]') { Write-Info 'Réinstallation passée par l''opérateur' }
    else {
        Write-Info "Installation silencieuse de $count logiciels (les paquets disparus du dépôt sont ignorés)…"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        # --ignore-unavailable : un paquet retiré du dépôt n'interrompt pas le lot.
        & winget.exe import -i $appsJson --accept-package-agreements --accept-source-agreements --ignore-unavailable --disable-interactivity
        $code = $LASTEXITCODE
        $watch.Stop()
        if ($code -eq 0) { Write-Ok ('Logiciels réinstallés ({0:hh\:mm\:ss})' -f $watch.Elapsed) }
        else { Write-Warn ('winget import terminé avec le code {0} ({1:hh\:mm\:ss}) : certains logiciels ne sont pas installés' -f $code, $watch.Elapsed) }
        $script:Summary.Add(('{0,-22} code winget {1} - {2:hh\:mm\:ss}' -f 'Logiciels', $code, $watch.Elapsed))
    }
    Write-Info 'Logiciels hors Winget (métier, anciens) : voir Logiciels\apps_list.txt et logiciels_registre.txt'
}

# ─── [6] Mots de passe ───────────────────────────────────────────────────────

Write-Step 6 'Mots de passe et nettoyage'
$passwordsDir = Join-Path $BackupPath 'Passwords'
$csv = @(Get-ChildItem -LiteralPath $passwordsDir -Filter *.csv -ErrorAction SilentlyContinue)
if ($csv.Count -eq 0) { Write-Info 'Aucun export de mots de passe dans la sauvegarde' }
elseif ($script:External -or -not (Test-Interactive)) { Write-Warn "$($csv.Count) fichier(s) de mots de passe à importer à la main depuis le compte du client : $passwordsDir" }
else {
    foreach ($b in $chromium) {
        $exe = Get-BrowserExe $b.Exe
        if (-not $exe) { continue }
        $file = $csv | Where-Object { $_.Name -like "$($b.Name)*" } | Select-Object -First 1
        if (-not $file) { $file = $csv | Select-Object -First 1 }
        $answer = Read-Operator "Importer des mots de passe dans $($b.Name) ? (O/n)" 'o'
        if ($answer -match '^[nN]') { continue }
        Start-Process -FilePath $exe -ArgumentList $b.Url
        Write-Host ''
        Write-Host "  ---- IMPORT DANS $($b.Name.ToUpper()) ----" -ForegroundColor Yellow
        Write-Host '  1. Dans la page ouverte : « Importer des mots de passe » (ou « ... > Importer »).'
        Write-Host '  2. Choisissez le fichier :' -NoNewline; Write-Host " $($file.FullName)" -ForegroundColor White
        Read-Operator "Entrée une fois l'import terminé dans $($b.Name)" | Out-Null
        Write-Ok "$($b.Name) : import des mots de passe validé par l'opérateur"
    }

    Write-Host ''
    Write-Host '  Les fichiers CSV contiennent les mots de passe EN CLAIR sur le support externe.' -ForegroundColor Yellow
    $answer = Read-Operator "Supprimer définitivement les $($csv.Count) fichier(s) CSV du support ? (O/n)" 'o'
    if ($answer -match '^[nN]') { Write-Warn "Fichiers de mots de passe conservés en clair : $passwordsDir" }
    else {
        $removed = 0
        foreach ($f in $csv) { if (Remove-Securely $f.FullName) { $removed++ } else { Write-Fail "Impossible de supprimer $($f.FullName)" } }
        if (-not (Get-ChildItem -LiteralPath $passwordsDir -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $passwordsDir -Force -ErrorAction SilentlyContinue }
        Write-Ok "$removed fichier(s) de mots de passe écrasé(s) puis supprimé(s)"
    }
}

if ($wifiFiles.Count -gt 0) {
    $answer = Read-Operator "Supprimer aussi les $($wifiFiles.Count) profil(s) Wi-Fi exportés (clés en clair) du support ? (o/N)" 'n'
    if ($answer -match '^[oOyY]') {
        $removed = 0
        foreach ($x in $wifiFiles) { if (Remove-Securely $x.FullName) { $removed++ } }
        Write-Ok "$removed profil(s) Wi-Fi supprimé(s) du support"
    } else { Write-Info "Clés Wi-Fi conservées dans la sauvegarde : $wifiDir" }
}

# ─── Bilan ───────────────────────────────────────────────────────────────────

$elapsed = (Get-Date) - $script:Started
$report = New-Object System.Collections.Generic.List[string]
$report.Add('')
$report.Add('---- Résumé de la restauration ----')
foreach ($l in $script:Summary) { $report.Add($l) }
$report.Add(('Volume restauré (taille de la sauvegarde) : {0}' -f (Format-Size $script:TotalBytes)))
$report.Add(('Durée totale : {0:hh\:mm\:ss}' -f $elapsed))
$report.Add(('Avertissements : {0}' -f $script:Warnings))
Add-Content -LiteralPath $script:LogFile -Encoding UTF8 -Value $report.ToArray()

Write-Host ''
Write-Host ('  Volume restauré : {0}   Durée : {1:hh\:mm\:ss}   Avertissements : {2}' -f (Format-Size $script:TotalBytes), $elapsed, $script:Warnings) -ForegroundColor Cyan
Write-Host "  Rapport : $script:LogFile"
Write-Host '  À vérifier avec le client : imprimantes (Systeme\system_info.txt), logiciels hors Winget, comptes de messagerie.' -ForegroundColor Yellow
if ($script:Warnings -gt 0) { exit 1 } else { exit 0 }
