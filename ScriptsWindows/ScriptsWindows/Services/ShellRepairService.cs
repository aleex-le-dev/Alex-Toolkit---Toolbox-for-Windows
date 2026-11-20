using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Paquet du Microsoft Store et ses compagnons.</summary>
public sealed record StorePackage(string Name, string Version, string Status, bool Installed);

/// <summary>
/// Réparation du Microsoft Store et des icônes, ciblée et vérifiée (l'ancienne méthode réenregistrait TOUTES les
/// applications du PC : plusieurs minutes, des centaines d'erreurs, risque d'abîmer des applications).
/// </summary>
public static class ShellRepairService
{
    /// <summary>Le Store et ce dont il dépend pour installer et mettre à jour les applications.</summary>
    private static readonly string[] StorePackages =
        { "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.Services.Store.Engagement" };

    public static async Task<List<StorePackage>> GetStoreStateAsync(CancellationToken ct = default)
    {
        string names = string.Join(",", StorePackages.Select(n => $"'{n}'"));
        string json = await PowerShellRunner.RunAsync($@"
$r = foreach ($n in @({names})) {{
    # Remis à zéro à chaque tour : une erreur bloquante (-AllUsers sans droits) n'affecte pas $p, qui garderait le paquet précédent
    $p = $null
    try {{ $p = Get-AppxPackage -AllUsers -Name $n -ErrorAction Stop | Sort-Object Version -Descending | Select-Object -First 1 }} catch {{ }}
    # -AllUsers exige l'administrateur : repli sur le compte courant
    if (-not $p) {{ $p = Get-AppxPackage -Name $n -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1 }}
    if ($p) {{ [pscustomobject]@{{ N = $n; V = [string]$p.Version; S = [string]$p.Status; I = $true }} }}
    else {{ [pscustomobject]@{{ N = $n; V = ''; S = ''; I = $false }} }}
}}
ConvertTo-Json -Compress -InputObject @($r)", ct);
        var list = new List<StorePackage>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        foreach (var p in doc.RootElement.EnumerateArray())
            list.Add(new StorePackage(p.GetProperty("N").GetString() ?? "", p.GetProperty("V").GetString() ?? "", p.GetProperty("S").GetString() ?? "", p.GetProperty("I").GetBoolean()));
        return list;
    }

    public static string PackageLabel(string name) => name switch
    {
        "Microsoft.WindowsStore" => "Microsoft Store",
        "Microsoft.StorePurchaseApp" => "Achats du Store",
        "Microsoft.DesktopAppInstaller" => "Programme d'installation d'application (winget)",
        "Microsoft.Services.Store.Engagement" => "Services du Store",
        _ => name
    };

    /// <summary>« Ok » = en bon état ; sinon libellé du problème.</summary>
    public static string StatusLabel(StorePackage p) => !p.Installed ? "Absent" : p.Status is "" or "Ok" ? "OK" : p.Status switch
    {
        "LicenseIssue" => "Problème de licence",
        "Modified" => "Fichiers modifiés",
        "Tampered" => "Fichiers altérés",
        "Disabled" => "Désactivé",
        "NeedsRemediation" => "À réparer",
        "Servicing" => "Mise à jour en cours",
        "DependencyIssue" => "Dépendance manquante",
        "PackageOffline" => "Hors ligne",
        _ => p.Status
    };

    /// <summary>
    /// Réparation du Store en 3 temps : réinitialisation officielle (Reset-AppxPackage = Paramètres > Réinitialiser),
    /// vidage du cache (wsreset, fermé automatiquement), réenregistrement ciblé des paquets du Store pour tous les comptes.
    /// </summary>
    public static async Task RepairStoreAsync(Action<string> step, Action<string> log, CancellationToken ct)
    {
        step("Réinitialisation du Microsoft Store…");
        string reset = (await PowerShellRunner.RunAsync(@"
try { Get-AppxPackage Microsoft.WindowsStore -ErrorAction Stop | Reset-AppxPackage -ErrorAction Stop; 'OK' } catch { 'ERR ' + $_.Exception.Message }", ct)).Trim();
        log(reset.StartsWith("OK") ? "[OK] Microsoft Store réinitialisé." : $"[!] Réinitialisation : {reset.Replace("ERR ", "")}");

        step("Vidage du cache du Store (wsreset)…");
        await RunWsresetAsync(log, ct);

        step("Réenregistrement du Store et de ses composants…");
        string names = string.Join(",", StorePackages.Select(n => $"'{n}'"));
        string reg = await PowerShellRunner.RunAsync($@"
$ok = 0; $ko = 0
foreach ($n in @({names})) {{
    foreach ($p in @(Get-AppxPackage -AllUsers -Name $n -ErrorAction SilentlyContinue)) {{
        $m = Join-Path $p.InstallLocation 'AppxManifest.xml'
        if (-not (Test-Path -LiteralPath $m)) {{ continue }}
        try {{ Add-AppxPackage -DisableDevelopmentMode -Register $m -ErrorAction Stop; $ok++ }} catch {{ $ko++; Write-Output ('ERR ' + $n + ' : ' + $_.Exception.Message.Split([char]10)[0]) }}
    }}
}}
Write-Output ""BILAN $ok $ko""", ct);
        foreach (var line in reg.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ERR "))) log($"[!] {line[4..]}");
        var bilan = reg.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("BILAN "))?.Split(' ');
        if (bilan is { Length: 3 }) log($"[i] Paquets réenregistrés : {bilan[1]}, en échec : {bilan[2]}.");
    }

    /// <summary>wsreset vide le cache puis ouvre le Store : la fenêtre est fermée et le processus arrêté au bout de 2 min max.</summary>
    private static async Task RunWsresetAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsreset.exe")) { UseShellExecute = false, CreateNoWindow = true })!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await p.WaitForExitAsync(timeout.Token); log("[OK] Cache du Store vidé."); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(true); } catch { }
                log("[i] wsreset n'a pas fini en 2 min : arrêté (le cache est en général déjà vidé).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log($"[!] wsreset : {ex.Message}"); }
    }

    /// <summary>Rafraîchissement officiel des icônes (ie4uinit -show) : sans fermer l'Explorateur.</summary>
    public static async Task RefreshIconsAsync(Action<string> log, CancellationToken ct)
    {
        string ie4 = Path.Combine(Environment.SystemDirectory, "ie4uinit.exe");
        if (File.Exists(ie4)) await CommandRunner.RunAsync($"\"{ie4}\" -show", _ => { }, ct);
        log("[OK] Cache des icônes rafraîchi (ie4uinit).");
    }

    /// <summary>
    /// Reconstruction complète : l'Explorateur est fermé, les caches d'icônes et de miniatures supprimés, puis
    /// l'Explorateur relancé (les fenêtres de dossiers se ferment). Renvoie les octets supprimés.
    /// </summary>
    public static async Task<long> RebuildIconCacheAsync(Action<string> log, CancellationToken ct)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(local, @"Microsoft\Windows\Explorer");
        foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
        await Task.Delay(800, ct);
        long freed = 0; int count = 0, locked = 0;
        var files = new List<string>();
        try { files.AddRange(Directory.EnumerateFiles(dir, "iconcache_*.db")); files.AddRange(Directory.EnumerateFiles(dir, "thumbcache_*.db")); } catch { }
        string old = Path.Combine(local, "IconCache.db");
        if (File.Exists(old)) files.Add(old);
        foreach (var f in files)
        {
            try { long len = new FileInfo(f).Length; File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); freed += len; count++; }
            catch { locked++; }
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        log($"[OK] Caches d'icônes et de miniatures reconstruits : {count} fichier(s) supprimé(s)" + (locked > 0 ? $", {locked} encore utilisé(s)" : "") + ". Explorateur relancé.");
        return freed;
    }
}
