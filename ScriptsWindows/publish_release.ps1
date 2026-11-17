# Publie l'exe compilé comme Release GitHub (tag vX.Y.Z) à partir de la version du .csproj.
# Lancé par publish_release.bat à la racine du projet.
$ErrorActionPreference = 'Stop'
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
    Step "Connexion à GitHub (une seule fois) : suivez les instructions, le navigateur va s'ouvrir"
    & $gh auth login --hostname github.com --git-protocol https --web
    if ($LASTEXITCODE -ne 0) { Fail "Connexion à GitHub annulée ou échouée." }
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
