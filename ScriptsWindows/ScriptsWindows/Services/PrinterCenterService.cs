using System.IO;
using System.Text;
using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Imprimante installée, avec un état traduit et un niveau de gravité (Centre de dépannage des imprimantes).</summary>
public class PrinterInfo
{
    public string Name { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string PortName { get; set; } = "";
    public string RawStatus { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Offline { get; set; }
    public bool Shared { get; set; }
    public bool IsNetwork { get; set; }
    public int Jobs { get; set; }

    public bool IsVirtual => PrinterCenterService.IsVirtualPrinter(Name, PortName);
    public (string Label, string Level, string Advice) State => PrinterCenterService.DescribeState(this);
    public string StatusLabel => State.Label;
    public string StatusBrushKey => State.Level switch { "ok" => "SuccessText", "warn" => "WarningText", "error" => "DangerText", _ => "TextMuted" };
    public string StatusBgKey => State.Level switch { "ok" => "SuccessBg", "warn" => "WarningBg", "error" => "DangerBg", _ => "BgInput" };
    public System.Windows.Media.Brush StatusBrush => ThemeManager.GetBrush(StatusBrushKey);
    public System.Windows.Media.Brush StatusBg => ThemeManager.GetBrush(StatusBgKey);
    public string Advice => State.Advice;
    public string Kind => IsVirtual ? "Imprimante virtuelle" : IsNetwork ? "Réseau" : "Locale (USB)";
    public string Details => $"{Kind} • {DriverName}" + (Jobs > 0 ? $" • {Jobs} document(s) en attente" : "");
    public string Icon => IsVirtual ? "" : IsNetwork ? "" : ""; // Document / Réseau / Imprimante
}

public class PrinterDriverInfo
{
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Version { get; set; } = "";
    public bool InUse { get; set; }
    public bool IsMicrosoft => Manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    public bool CanRemove => !InUse && !IsMicrosoft;
    public string Details => string.Join(" • ", new[] { Manufacturer, string.IsNullOrEmpty(Version) ? null : $"v{Version}", InUse ? "utilisé" : "inutilisé" }.Where(s => !string.IsNullOrEmpty(s)));
}

public class PrintError
{
    public string Date { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// Centre de dépannage des imprimantes : état, files d'attente, spouleur, pilotes, erreurs récentes,
/// et script autonome « Débloquer l'impression » pour le Bureau.
/// Toutes les valeurs saisies sont échappées avant insertion dans un script.
/// </summary>
public static class PrinterCenterService
{
    private static string Q(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

    public static bool IsVirtualPrinter(string name, string port)
        => port.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase) || port.Equals("nul:", StringComparison.OrdinalIgnoreCase)
        || port.StartsWith("SHRFAX", StringComparison.OrdinalIgnoreCase) || port.Contains("OneNote", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PDF", StringComparison.OrdinalIgnoreCase) || name.Contains("XPS", StringComparison.OrdinalIgnoreCase)
        || name.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || name.Equals("Fax", StringComparison.OrdinalIgnoreCase);

    /// <summary>État lisible + gravité + conseil de dépannage.</summary>
    public static (string Label, string Level, string Advice) DescribeState(PrinterInfo p)
    {
        string s = p.RawStatus.ToLowerInvariant();
        if (p.Offline || s.Contains("offline"))
            return ("Hors ligne", "error", "Vérifiez que l'imprimante est allumée et connectée (câble USB ou même réseau Wi-Fi), puis cliquez sur « Remettre en ligne ». Si elle repasse hors ligne, « Réinstaller l'imprimante ».");
        if (s.Contains("paperjam")) return ("Bourrage papier", "error", "Ouvrez l'imprimante, retirez délicatement la feuille coincée, refermez les capots puis relancez l'impression.");
        if (s.Contains("paperout")) return ("Plus de papier", "warn", "Rechargez le bac papier.");
        if (s.Contains("notoner")) return ("Plus d'encre / toner", "error", "Remplacez la cartouche ou le toner.");
        if (s.Contains("tonerlow")) return ("Encre / toner bas", "warn", "Prévoyez une cartouche de rechange.");
        if (s.Contains("dooropen")) return ("Capot ouvert", "warn", "Refermez le capot ou la trappe de l'imprimante.");
        if (s.Contains("paused")) return ("En pause", "warn", "L'impression est suspendue : cliquez sur « Remettre en ligne ».");
        if (s.Contains("error") || s.Contains("userintervention") || s.Contains("paperproblem") || s.Contains("outofmemory"))
            return ("En erreur", "error", "Vérifiez l'écran de l'imprimante. Ensuite : « Vider la file », puis « Débloquer l'impression ». Si l'erreur persiste : « Réinstaller l'imprimante ».");
        if (s.Contains("notavailable") || s.Contains("serverunknown"))
            return ("Inaccessible", "error", "L'ordinateur ne joint pas l'imprimante : vérifiez le réseau ou le partage, puis « Réinstaller l'imprimante ».");
        if (s.Contains("pendingdeletion")) return ("Suppression en cours", "warn", "Redémarrez le service d'impression pour terminer la suppression.");
        if (s.Contains("printing") || s.Contains("busy") || s.Contains("processing") || s.Contains("warmingup") || s.Contains("initializing"))
            return ("Impression en cours", "ok", "L'imprimante traite actuellement des documents.");
        if (p.Jobs > 0)
            return ("Documents en attente", "warn", $"{p.Jobs} document(s) en attente. Si rien ne sort : « Vider la file », puis « Débloquer l'impression ».");
        return ("Prête", "ok", p.IsVirtual ? "Imprimante virtuelle (PDF, XPS, OneNote…) : elle crée un fichier au lieu d'imprimer sur papier."
                                            : "Aucun problème détecté. En cas de doute, imprimez une page de test.");
    }

    public static async Task<List<PrinterInfo>> GetPrintersAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$cims = @(Get-CimInstance Win32_Printer -ErrorAction SilentlyContinue)
$list = New-Object System.Collections.Generic.List[object]
foreach ($p in @(Get-Printer -ErrorAction SilentlyContinue)) {
    $c = $cims | Where-Object { $_.Name -eq $p.Name } | Select-Object -First 1
    $jobs = @(Get-PrintJob -PrinterName $p.Name -ErrorAction SilentlyContinue).Count
    $list.Add([PSCustomObject]@{
        Name = $p.Name; DriverName = ""$($p.DriverName)""; PortName = ""$($p.PortName)""; RawStatus = ""$($p.PrinterStatus)""
        IsDefault = [bool]$c.Default; Offline = [bool]$c.WorkOffline; Shared = [bool]$p.Shared
        IsNetwork = ($p.Type -eq 'Connection') -or ($p.PortName -match '^(\\\\|IP_|WSD|https?:)') -or ($p.PortName -match '^\d+\.\d+\.\d+\.\d+')
        Jobs = $jobs
    })
}
ConvertTo-Json -InputObject $list.ToArray() -Compress
", ct);
        return Deserialize<PrinterInfo>(json)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.IsVirtual).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static async Task<List<PrinterDriverInfo>> GetDriversAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$used = @(Get-Printer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DriverName)
$list = New-Object System.Collections.Generic.List[object]
foreach ($d in @(Get-PrinterDriver -ErrorAction SilentlyContinue)) {
    $v = $d.DriverVersion
    $ver = if ($v) { '{0}.{1}.{2}.{3}' -f (($v -shr 48) -band 0xFFFF), (($v -shr 32) -band 0xFFFF), (($v -shr 16) -band 0xFFFF), ($v -band 0xFFFF) } else { '' }
    $list.Add([PSCustomObject]@{ Name = $d.Name; Manufacturer = ""$($d.Manufacturer)""; Version = $ver; InUse = ($used -contains $d.Name) })
}
ConvertTo-Json -InputObject $list.ToArray() -Compress
", ct);
        return Deserialize<PrinterDriverInfo>(json).GroupBy(d => d.Name).Select(g => g.First())
            .OrderBy(d => d.InUse ? 0 : 1).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static async Task<List<PrintError>> GetRecentErrorsAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-PrintService/Admin'; Level = 1,2,3; StartTime = (Get-Date).AddDays(-7) } -MaxEvents 15 -ErrorAction SilentlyContinue)
$list = @($ev | ForEach-Object { [PSCustomObject]@{ Date = $_.TimeCreated.ToString('dd/MM HH:mm'); Message = (($_.Message -replace '\s+', ' ').Trim()) } })
ConvertTo-Json -InputObject $list -Compress
", ct);
        return Deserialize<PrintError>(json);
    }

    public static async Task<(string Status, string StartType)> GetSpoolerAsync(CancellationToken ct = default)
    {
        string r = (await PowerShellRunner.RunAsync("$s = Get-Service Spooler; \"$($s.Status)|$($s.StartType)\"", ct)).Trim();
        var parts = r.Split('|');
        return parts.Length == 2 ? (parts[0], parts[1]) : ("Unknown", "");
    }

    private static List<T> Deserialize<T>(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return new();
        if (json.StartsWith("{")) json = "[" + json + "]";
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    // ── Actions sur une imprimante ──────────────────────────────────────────

    public static Task PrintTestPage(string name, Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync($@"
$r = Get-CimInstance Win32_Printer | Where-Object {{ $_.Name -eq {Q(name)} }} | Invoke-CimMethod -MethodName PrintTestPage
if ($r.ReturnValue -eq 0) {{ Write-Output '[OK] Page de test envoyée.' }} else {{ Write-Output ""[ERR] Échec de l'envoi de la page de test (code $($r.ReturnValue)).""}}
", log, ct);

    public static Task SetDefault(string name, Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync($@"
# Désactive « Laisser Windows gérer mon imprimante par défaut », sinon Windows changerait le choix tout seul
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows' -Name LegacyDefaultPrinterMode -Value 1 -Type DWord -ErrorAction SilentlyContinue
$r = Get-CimInstance Win32_Printer | Where-Object {{ $_.Name -eq {Q(name)} }} | Invoke-CimMethod -MethodName SetDefaultPrinter
if ($r.ReturnValue -eq 0) {{ Write-Output '[OK] Imprimante définie par défaut.' }} else {{ Write-Output ""[ERR] Impossible de la définir par défaut (code $($r.ReturnValue)).""}}
", log, ct);

    public static Task SetOnline(string name, Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync($@"
$p = Get-CimInstance Win32_Printer | Where-Object {{ $_.Name -eq {Q(name)} }}
if ($p.WorkOffline) {{ Set-CimInstance -InputObject $p -Property @{{ WorkOffline = $false }} }}
$p | Invoke-CimMethod -MethodName Resume | Out-Null
Get-PrintJob -PrinterName {Q(name)} -ErrorAction SilentlyContinue | Where-Object {{ $_.JobStatus -match 'Paused' }} | Resume-PrintJob -ErrorAction SilentlyContinue
Write-Output '[OK] Imprimante remise en ligne et impression reprise.'
", log, ct);

    public static Task ClearQueue(string name, Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync($@"
$jobs = @(Get-PrintJob -PrinterName {Q(name)} -ErrorAction SilentlyContinue)
$jobs | Remove-PrintJob -ErrorAction SilentlyContinue
$left = @(Get-PrintJob -PrinterName {Q(name)} -ErrorAction SilentlyContinue).Count
if ($left -eq 0) {{ Write-Output ""[OK] $($jobs.Count) document(s) supprimé(s) de la file."" }}
else {{ Write-Output ""[!] $left document(s) bloqué(s) : utilisez « Débloquer l'impression ».""}}
", log, ct);

    public static Task RemovePrinter(string name, Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync($"Remove-Printer -Name {Q(name)} -ErrorAction Stop; Write-Output '[OK] Imprimante supprimée.'", log, ct);

    /// <summary>Recrée l'imprimante avec le même pilote et le même port (corrige une configuration corrompue).</summary>
    public static Task ReinstallPrinter(string name, Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync($@"
$p = Get-Printer -Name {Q(name)} -ErrorAction Stop
$driver = $p.DriverName; $port = $p.PortName; $shared = $p.Shared; $share = $p.ShareName
if ($p.Type -eq 'Connection') {{
    Remove-Printer -Name $p.Name -ErrorAction Stop
    Add-Printer -ConnectionName $p.Name -ErrorAction Stop
}} else {{
    Get-PrintJob -PrinterName $p.Name -ErrorAction SilentlyContinue | Remove-PrintJob -ErrorAction SilentlyContinue
    Remove-Printer -Name $p.Name -ErrorAction Stop
    Start-Sleep -Seconds 2
    Add-Printer -Name {Q(name)} -DriverName $driver -PortName $port -ErrorAction Stop
    if ($shared -and $share) {{ Set-Printer -Name {Q(name)} -Shared $true -ShareName $share -ErrorAction SilentlyContinue }}
}}
Write-Output '[OK] Imprimante réinstallée (même pilote, même port).'
", log, ct);

    public static void OpenQueue(string name) => CommandRunner.StartProcess("rundll32.exe", $"printui.dll,PrintUIEntry /o /n \"{name}\"");
    public static void OpenProperties(string name) => CommandRunner.StartProcess("rundll32.exe", $"printui.dll,PrintUIEntry /p /n \"{name}\"");
    public static void OpenAddPrinter() => CommandRunner.StartProcess("ms-settings:printers");

    // ── Service et pilotes ──────────────────────────────────────────────────

    /// <summary>Débloque l'impression : arrêt forcé du spouleur, purge des documents bloqués, démarrage automatique, relance, vérification.</summary>
    public static Task UnblockPrinting(Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync(@"
Write-Output '[i] 1/4 Arrêt du service d''impression…'
Stop-Service Spooler -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
if ((Get-Service Spooler).Status -ne 'Stopped') { Stop-Process -Name spoolsv -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1 }

Write-Output '[i] 2/4 Suppression des documents bloqués…'
$spool = Join-Path $env:SystemRoot 'System32\spool\PRINTERS'
$n = @(Get-ChildItem $spool -File -ErrorAction SilentlyContinue).Count
Remove-Item (Join-Path $spool '*') -Recurse -Force -ErrorAction SilentlyContinue
Write-Output ""    $n fichier(s) de file d'attente supprimé(s)""

Write-Output '[i] 3/4 Démarrage automatique et relance du service…'
Set-Service Spooler -StartupType Automatic -ErrorAction SilentlyContinue
Start-Service Spooler -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Output '[i] 4/4 Vérification…'
if ((Get-Service Spooler).Status -eq 'Running') { Write-Output '[OK] Impression débloquée : relancez votre impression.' }
else { Write-Output '[ERR] Le service d''impression ne redémarre pas : redémarrez le PC.' }
", log, ct);

    public static Task RestartSpooler(Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync("Restart-Service Spooler -Force -ErrorAction Stop; Write-Output '[OK] Service d''impression redémarré.'", log, ct);

    public static Task RemoveDrivers(IEnumerable<string> names, Action<string> log, CancellationToken ct = default)
    {
        string list = string.Join(",", names.Select(Q));
        return PowerShellRunner.RunAsync($@"
$ok = 0
foreach ($n in @({list})) {{
    try {{ Remove-PrinterDriver -Name $n -RemoveFromDriverStore -ErrorAction Stop; $ok++; Write-Output ""[OK] Pilote supprimé : $n"" }}
    catch {{
        try {{ Remove-PrinterDriver -Name $n -ErrorAction Stop; $ok++; Write-Output ""[OK] Pilote supprimé (conservé dans le magasin) : $n"" }}
        catch {{ Write-Output ""[ERR] $n : $($_.Exception.Message)"" }}
    }}
}}
Write-Output ""[i] $ok pilote(s) supprimé(s).""
", log, ct);
    }

    /// <summary>
    /// Supprime un pilote d'imprimante spécifique. S'il est utilisé par des imprimantes,
    /// celles-ci sont d'abord supprimées pour libérer le pilote.
    /// Si purgeFromStore est vrai, le package OEM associé est également désinstallé du DriverStore avec pnputil.
    /// </summary>
    public static Task RemoveSingleDriver(string name, bool purgeFromStore, Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync($@"
$n = {Q(name)}
Write-Output ""[i] Préparation de la suppression du pilote : $n""

# 1. Vérifier si des imprimantes utilisent ce pilote
$using = @(Get-Printer -ErrorAction SilentlyContinue | Where-Object {{ $_.DriverName -eq $n }})
if ($using.Count -gt 0) {{
    Write-Output ""[!] Ce pilote est utilisé par : $(($using.Name) -join ', ')""
    Write-Output '[i] Suppression préalable des imprimantes associées...'
    foreach ($p in $using) {{
        try {{
            Remove-Printer -Name $p.Name -ErrorAction Stop
            Write-Output ""    Imprimante '$($p.Name)' supprimée.""
        }} catch {{
            Write-Output ""    [!] Erreur sur '$($p.Name)' : $($_.Exception.Message)""
        }}
    }}
}}

# 2. Redémarrer rapidement le spouleur pour libérer les dll verrouillées
Stop-Service Spooler -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
Start-Service Spooler -ErrorAction SilentlyContinue

# 3. Supprimer le pilote
try {{
    Remove-PrinterDriver -Name $n -ErrorAction Stop
    Write-Output ""[OK] Pilote '$n' supprimé du sous-système d'impression.""
}} catch {{
    Write-Output ""[!] Remove-PrinterDriver : $($_.Exception.Message)""
    Start-Process rundll32.exe -ArgumentList ""printui.dll,PrintUIEntry /dd /m `""$n`"""" -Wait -NoNewWindow
}}

# 4. Suppression du package OEM dans le DriverStore (pnputil) si demandé
if ({ (purgeFromStore ? "$true" : "$false") }) {{
    Write-Output '[i] Recherche des packages OEM associés dans le DriverStore...'
    $driversList = pnputil /enum-drivers
    $currentInf = ''
    $found = $false
    foreach ($line in ($driversList -split ""\r?\n"")) {{
        if ($line -match 'Nom publi[eé]\s*:\s*(oem\d+\.inf)') {{
            $currentInf = $Matches[1]
        }}
        if ($line -match [regex]::Escape($n)) {{
            if ($currentInf) {{
                Write-Output ""    Suppression du package OEM $currentInf du DriverStore...""
                pnputil /delete-driver $currentInf /uninstall /force
                $found = $true
                $currentInf = ''
            }}
        }}
    }}
    if (-not $found) {{
        Write-Output '[i] Aucun fichier oem.inf spécifique résiduel dans le DriverStore.'
    }}
}}
Write-Output ""[OK] Fin de suppression du pilote '$n'.""
", log, ct);

    /// <summary>
    /// Réinstallation automatique Plug & Play : réactive le service d'installation de périphériques,
    /// lance un scan matériel Windows (pnputil /scan-devices) et réactive la découverte d'imprimantes réseau (WSD/UPnP).
    /// Windows détecte et réinstalle automatiquement les imprimantes branchées ou connectées.
    /// </summary>
    public static Task RescanPlugAndPlay(Action<string> log, CancellationToken ct = default)
        => PowerShellRunner.RunAsync(@"
Write-Output '========================================================'
Write-Output '  DÉTECTION ET RÉINSTALLATION AUTOMATIQUE PLUG & PLAY'
Write-Output '========================================================'

Write-Output '[1/4] Démarrage du service d''installation de périphériques...'
Set-Service DeviceInstall -StartupType Manual -ErrorAction SilentlyContinue
Start-Service DeviceInstall -ErrorAction SilentlyContinue

Write-Output '[2/4] Scan matériel Plug & Play (pnputil /scan-devices)...'
pnputil /scan-devices

Write-Output '[3/4] Activation de la découverte des périphériques réseau (WSD / UPnP)...'
Set-Service SSDPSRV -StartupType Manual -ErrorAction SilentlyContinue
Start-Service SSDPSRV -ErrorAction SilentlyContinue
Set-Service fdPHost -StartupType Manual -ErrorAction SilentlyContinue
Start-Service fdPHost -ErrorAction SilentlyContinue

Write-Output '[4/4] Redémarrage du spouleur d''impression...'
Restart-Service Spooler -Force -ErrorAction SilentlyContinue

Write-Output '[OK] Analyse terminée avec succès !'
Write-Output '[i] Windows réassocie et réinstalle automatiquement vos imprimantes branchées en USB ou détectées sur votre réseau local.'
", log, ct);

    // ── Script autonome pour le Bureau ──────────────────────────────────────

    /// <summary>
    /// Crée sur le Bureau « Debloquer_Impression.bat » : fichier autonome (aucune dépendance), qui se relance en administrateur.
    /// Copiable sur clé USB pour dépanner n'importe quel PC.
    /// </summary>
    public static string CreateDesktopUnblockScript(Action<string>? log = null)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(desktop);
        string path = Path.Combine(desktop, "Debloquer_Impression.bat");

        // ASCII uniquement (pas d'accents) : CMD l'interprète correctement quelle que soit la page de code
        const string bat = @"@echo off
setlocal
title Debloquer l'impression - Alex Toolkit

:: Relance automatique en administrateur si necessaire
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command ""Start-Process -FilePath '%~f0' -Verb RunAs""
    exit /b
)

echo ============================================================
echo    DEBLOCAGE DE L'IMPRESSION - Alex Toolkit
echo ============================================================
echo.
echo [1/4] Arret du service d'impression...
net stop spooler /y >nul 2>&1
timeout /t 2 /nobreak >nul
sc query spooler | find ""STOPPED"" >nul || taskkill /f /im spoolsv.exe >nul 2>&1

echo [2/4] Suppression des documents bloques...
del /f /s /q ""%SystemRoot%\System32\spool\PRINTERS\*.*"" >nul 2>&1

echo [3/4] Demarrage automatique et relance du service...
sc config spooler start= auto >nul 2>&1
net start spooler >nul 2>&1
timeout /t 2 /nobreak >nul

echo [4/4] Verification...
echo.
sc query spooler | find ""RUNNING"" >nul
if %errorlevel% equ 0 (
    echo [OK] Impression debloquee : relancez votre impression.
) else (
    echo [ERREUR] Le service d'impression ne redemarre pas : redemarrez le PC.
)
echo.
echo Cette fenetre se fermera automatiquement...
timeout /t 10 >nul
";
        File.WriteAllText(path, bat.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        log?.Invoke($"[OK] Script créé sur le Bureau : {path}");
        return path;
    }
}
