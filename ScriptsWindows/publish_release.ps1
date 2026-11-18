# Publie l'exe compilé comme Release GitHub (tag vX.Y.Z) à partir de la version du .csproj.
# Lancé par publish_release.bat à la racine du projet.
# 'Continue' et non 'Stop' : sous PowerShell 5.1, avec 'Stop', le moindre message d'un programme sur
# stderr (gh « You are not logged into any GitHub hosts », gh auth login, gh release view d'une Release
# absente) devient une erreur fatale. Le résultat de chaque commande est vérifié par $LASTEXITCODE.
$ErrorActionPreference = 'Continue'
$root   = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $PSScriptRoot 'ScriptsWindows\ScriptsWindows.csproj'
Set-Location $root

function Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "`n[ERREUR] $msg" -ForegroundColor Red; exit 1 }

# 1. Version et exe à publier
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "Version introuvable dans $csproj" }
$tag = "v$version"
$exe = Join-Path $root "AlexToolkit_v$version.exe"
if (-not (Test-Path $exe)) { Fail "Fichier introuvable : AlexToolkit_v$version.exe`n         Lancez d'abord build.bat." }
$sizeMo = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Version : $tag   Fichier : AlexToolkit_v$version.exe ($sizeMo Mo)"

# 2. GitHub CLI : installation si absent
Step "Vérification de GitHub CLI (gh)"
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) {
    $candidate = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
    if (Test-Path $candidate) { $gh = $candidate }
}
if (-not $gh) {
    Write-Host "GitHub CLI n'est pas installé. Installation via winget..."
    winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements
    $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
    if (-not (Test-Path $gh)) { Fail "Installation de GitHub CLI impossible. Installez-le manuellement : winget install GitHub.cli" }
}

# 3. Connexion au compte GitHub (une seule fois, ouvre le navigateur)
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Step "Connexion à GitHub (une seule fois)"
    # gh n'existe qu'en anglais. Lancé sans console (entrées/sorties redirigées), il ne pose aucune
    # question (« Authenticate Git…? », « Press Enter… ») : il donne le code et l'adresse, puis attend la
    # validation. Ses messages sont lus ici et réaffichés en français.
    $psi = New-Object System.Diagnostics.ProcessStartInfo $gh
    $psi.Arguments = 'auth login --hostname github.com --git-protocol https --web'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $login = [System.Diagnostics.Process]::Start($psi)
    $login.StandardInput.Close()
    $outTask = $login.StandardOutput.ReadToEndAsync()
    $ghLines = New-Object System.Collections.Generic.List[string]
    $codeShown = $false
    while ($null -ne ($line = $login.StandardError.ReadLine())) {
        $ghLines.Add($line)
        if (-not $codeShown -and $line -match '\b([A-Z0-9]{4}-[A-Z0-9]{4})\b') {
            $code = $Matches[1]
            $codeShown = $true
            try { Set-Clipboard -Value $code } catch { }
            Write-Host ""
            Write-Host "  Votre code de connexion :  $code" -ForegroundColor Green
            Write-Host "  (déjà copié : faites simplement Ctrl+V dans la page)" -ForegroundColor DarkGray
            Write-Host ""
            Write-Host "  1. Dans la page GitHub qui s'ouvre (https://github.com/login/device),"
            Write-Host "     collez le code puis cliquez sur « Continue »."
            Write-Host "  2. Cliquez ensuite sur « Authorize GitHub CLI »."
            Write-Host "  3. Revenez ici : la publication continue toute seule (le code expire après 15 minutes)."
            Write-Host ""
            Write-Host "  En attente de votre validation sur GitHub..." -ForegroundColor Yellow
            try { Start-Process 'https://github.com/login/device' } catch { }
        } elseif ($line -match 'Logged in as (\S+)') {
            Write-Host "  [OK] Connecté à GitHub avec le compte $($Matches[1])." -ForegroundColor Green
        }
    }
    $login.WaitForExit()
    [void]$outTask.Wait(2000)
    if ($login.ExitCode -ne 0) {
        Write-Host "`n  Message de GitHub CLI (en anglais) :" -ForegroundColor DarkGray
        $ghLines | Select-Object -Last 3 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        Fail "Connexion à GitHub annulée, refusée ou expirée. Relancez publish_release.bat."
    }
    # Équivalent de la question « Authenticate Git with your GitHub credentials? » : git push utilisera ce compte.
    & $gh auth setup-git --hostname github.com *> $null
    if ($LASTEXITCODE -eq 0) { Write-Host "  [OK] git utilisera ce compte pour envoyer le code sur GitHub." -ForegroundColor Green }
}

# 4. Le code doit être poussé : la Release pointe sur origin/main
Step "Vérification de la synchronisation avec GitHub"
git fetch -q origin
$dirty = git status --porcelain --untracked-files=no
if ($dirty) { Write-Host "[ATTENTION] Modifications non commitées (elles ne seront PAS dans la Release) :" -ForegroundColor Yellow; $dirty | ForEach-Object { Write-Host "   $_" } }
$ahead = [int](git rev-list --count origin/main..main)
if ($ahead -gt 0) {
    Write-Host "$ahead commit(s) local(aux) non poussé(s) : envoi sur GitHub..."
    git push -q origin main
    if ($LASTEXITCODE -ne 0) { Fail "git push a échoué." }
}

# 5. Release déjà existante ?
& $gh release view $tag *> $null
$exists = ($LASTEXITCODE -eq 0)

# 6. Notes de version : première section du CHANGELOG
$notesFile = Join-Path $env:TEMP "alextoolkit_notes_$version.md"
$lines = Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding UTF8
$start = ($lines | Select-String -Pattern '^## ' | Select-Object -First 1).LineNumber
$notes = @()
if ($start) {
    foreach ($l in $lines[$start..($lines.Count - 1)]) { if ($l -match '^## ') { break }; $notes += $l }
}
if (-not ($notes -join '').Trim()) { $notes = @("Alex Toolkit $tag") }
$notes += '', "---", "Téléchargez **AlexToolkit_v$version.exe** ci-dessous : un seul fichier, aucune installation."
[IO.File]::WriteAllLines($notesFile, $notes, [Text.UTF8Encoding]::new($false))

# 7. Confirmation
Write-Host ""
if ($exists) {
    Write-Host "La Release $tag existe déjà sur GitHub." -ForegroundColor Yellow
    $answer = Read-Host "Remplacer son exe par celui-ci ? (O/N)"
} else {
    Write-Host "Prêt à publier la Release $tag (publique, proposée aux utilisateurs comme mise à jour)." -ForegroundColor Yellow
    $answer = Read-Host "Publier maintenant ? (O/N)"
}
if ($answer -notmatch '^[oOyY]') { Write-Host "Annulé. Rien n'a été publié."; exit 0 }

# 8. Publication
if ($exists) {
    Step "Remplacement de l'exe de la Release $tag"
    & $gh release upload $tag $exe --clobber
} else {
    Step "Création de la Release $tag"
    & $gh release create $tag $exe --title "Alex Toolkit $tag" --notes-file $notesFile --target main --latest
}
if ($LASTEXITCODE -ne 0) { Fail "La publication a échoué." }

$url = & $gh release view $tag --json url --jq .url
Write-Host "`n[SUCCES] Release $tag publiée : $url" -ForegroundColor Green
