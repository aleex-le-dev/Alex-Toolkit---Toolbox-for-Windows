using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public sealed record CheckVolume(char Letter, string Label, string FileSystem, long Size, long Free, string Health, bool Dirty, bool IsSystem)
{
    public string Display => $"{Letter}:  {(Label.Length > 0 ? Label : "Disque local")}  —  {FileSystem}, {Format(Free)} libres sur {Format(Size)}" + (IsSystem ? "  (disque de Windows)" : "");
    internal static string Format(long b) => b >= 1L << 40 ? $"{b / (double)(1L << 40):0.0} To" : b >= 1L << 30 ? $"{b / (double)(1L << 30):0} Go" : $"{b / (double)(1L << 20):0} Mo";
    /// <summary>NTFS / ReFS : analyse en ligne (Repair-Volume) ; FAT : chkdsk en lecture seule.</summary>
    public bool SupportsOnlineScan => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
}

public enum ScanVerdict { NoErrors, Errors, Unknown }

/// <summary>
/// Vérification et réparation des disques, sans dépendre de la langue de Windows :
/// état via Win32_Volume.DirtyBitSet et Get-Volume, analyse en ligne via Repair-Volume -Scan (NTFS/ReFS, sans
/// redémarrage), réparation Repair-Volume -SpotFix / chkdsk /f /r (immédiate sur un disque de données, programmée
/// au redémarrage sur le disque de Windows), annulation réelle d'une vérification programmée (chkntfs /x),
/// dernier résultat de CHKDSK lu dans le journal d'événements.
/// </summary>
public static class DiskCheckService
{
    private static readonly Regex Percent = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    public static async Task<List<CheckVolume>> GetVolumesAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$sys = $env:SystemDrive.Substring(0,1)
$r = Get-CimInstance Win32_Volume -Filter 'DriveType=3 OR DriveType=2' -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object {
    $l = $_.DriveLetter.Substring(0,1)
    $h = (Get-Volume -DriveLetter $l -ErrorAction SilentlyContinue).HealthStatus
    [pscustomobject]@{ L = $l; N = [string]$_.Label; F = [string]$_.FileSystem; S = [long]$_.Capacity; R = [long]$_.FreeSpace
                       H = [string]$h; D = [bool]$_.DirtyBitSet; Y = ($l -eq $sys) }
}
ConvertTo-Json -Compress -InputObject @($r)", ct);
        var list = new List<CheckVolume>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            string l = v.GetProperty("L").GetString() ?? "";
            if (l.Length == 0) continue;
            list.Add(new CheckVolume(char.ToUpperInvariant(l[0]), v.GetProperty("N").GetString() ?? "", v.GetProperty("F").GetString() ?? "",
                v.GetProperty("S").GetInt64(), v.GetProperty("R").GetInt64(), v.GetProperty("H").GetString() ?? "", v.GetProperty("D").GetBoolean(), v.GetProperty("Y").GetBoolean()));
        }
        return list.OrderBy(v => v.IsSystem ? 0 : 1).ThenBy(v => v.Letter).ToList();
    }

    public static string HealthLabel(CheckVolume v) => v.Dirty ? "Marqué comme endommagé par Windows : réparation nécessaire"
        : v.Health switch { "Healthy" => "Sain selon Windows", "Warning" => "Avertissement : analyse recommandée", "Unhealthy" => "Défectueux : réparation nécessaire", _ => "État non communiqué" };

    /// <summary>Analyse sans modification ni redémarrage. NTFS/ReFS : Repair-Volume -Scan ; sinon chkdsk en lecture seule (code 0 = sain).</summary>
    public static async Task<(ScanVerdict Verdict, string Message)> ScanAsync(CheckVolume v, Action<double> progress, Action<string> log, CancellationToken ct)
    {
        if (v.SupportsOnlineScan)
        {
            string r = (await PowerShellRunner.RunAsync($"(Repair-Volume -DriveLetter {v.Letter} -Scan -ErrorAction Stop) 2>&1 | ForEach-Object {{ [string]$_ }}", ct)).Trim();
            if (r.Contains("NoErrorsFound")) return (ScanVerdict.NoErrors, "Aucune erreur trouvée sur le système de fichiers.");
            if (r.Contains("SpotFixNeeded") || r.Contains("ScanNeeded") || r.Contains("FullRepairNeeded"))
                return (ScanVerdict.Errors, "Des erreurs ont été trouvées : utilisez « Réparer »" + (v.IsSystem ? " (au prochain redémarrage)." : "."));
            log($"[i] Repair-Volume : {r}");
        }
        int code = await CommandRunner.RunAsync($"chkdsk {v.Letter}:", line =>
        {
            var m = Percent.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double p)) progress(p);
        }, ct);
        return code switch
        {
            0 => (ScanVerdict.NoErrors, "Aucune erreur trouvée."),
            2 or 3 => (ScanVerdict.Errors, "Des erreurs ont été trouvées : utilisez « Réparer »."),
            _ => (ScanVerdict.Unknown, $"Analyse terminée (code {code}).")
        };
    }

    /// <summary>
    /// Réparation. Disque de données : immédiate (le disque est brièvement démonté). Disque de Windows : programmée au
    /// prochain redémarrage. full = recherche des secteurs défectueux (/r, long).
    /// </summary>
    public static async Task<string> RepairAsync(CheckVolume v, bool full, Action<double> progress, Action<string> log, CancellationToken ct)
    {
        if (!full && v.SupportsOnlineScan && !v.IsSystem)
        {
            string r = (await PowerShellRunner.RunAsync($"(Repair-Volume -DriveLetter {v.Letter} -SpotFix -ErrorAction Stop) 2>&1 | ForEach-Object {{ [string]$_ }}", ct)).Trim();
            if (r.Contains("NoErrorsFound")) return "Réparation terminée : plus aucune erreur.";
            log($"[i] Repair-Volume -SpotFix : {r} → réparation complète avec chkdsk /f.");
        }
        string args = full ? "/f /r /x" : "/f /x";
        if (v.IsSystem)
        {
            // Disque en cours d'utilisation : chkdsk demande « programmer au prochain redémarrage ? » → réponse traduite
            string yes = YesLetter();
            await CommandRunner.RunAsync($"echo {yes}| chkdsk {v.Letter}: {args}", _ => { }, ct);
            bool scheduled = (await CommandRunner.RunAsync($"chkntfs {v.Letter}:", ct)).Contains("chkdsk", StringComparison.OrdinalIgnoreCase)
                             || (await IsScheduledAsync(v.Letter, ct));
            return scheduled ? $"Vérification programmée : elle se fera au prochain redémarrage ({(full ? "longue : 30 min à plusieurs heures" : "quelques minutes")})."
                             : "Windows n'a pas confirmé la programmation : relancez l'outil en administrateur.";
        }
        int code = await CommandRunner.RunAsync($"echo {YesLetter()}| chkdsk {v.Letter}: {args}", line =>
        {
            var m = Percent.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double p)) progress(p);
        }, ct);
        return code switch
        {
            0 => "Réparation terminée : aucune erreur.",
            1 => "Réparation terminée : les erreurs ont été corrigées.",
            _ => $"Réparation terminée avec des erreurs restantes (code {code}) : le disque est peut-être en fin de vie, sauvegardez-le."
        };
    }

    /// <summary>Une vérification est-elle programmée au démarrage (BootExecute contient « autocheck … /k » ou le volume est marqué) ?</summary>
    public static async Task<bool> IsScheduledAsync(char letter, CancellationToken ct = default)
    {
        string r = await PowerShellRunner.RunAsync($@"
$v = Get-CimInstance Win32_Volume -Filter ""DriveLetter='{letter}:'"" -ErrorAction SilentlyContinue
$be = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name BootExecute -ErrorAction SilentlyContinue).BootExecute -join ' '
[bool]($v.DirtyBitSet -or $be -match '/r\s|\\\?\?\\{letter}:')", ct);
        return r.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Annule une vérification programmée sur ce disque (chkntfs /x).</summary>
    public static async Task<bool> CancelScheduledAsync(char letter, CancellationToken ct = default)
    {
        await CommandRunner.RunAsync($"chkntfs /x {letter}:", _ => { }, ct);
        return true;
    }

    /// <summary>Dernier résultat de CHKDSK (au démarrage : Wininit 1001 ; en ligne : Chkdsk 26226).</summary>
    public static async Task<string> LastResultAsync(CancellationToken ct = default)
    {
        string r = await PowerShellRunner.RunAsync(@"
$e = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='Microsoft-Windows-Wininit'; Id=1001 } -MaxEvents 1 -ErrorAction SilentlyContinue) +
     @(Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='Chkdsk'; Id=26226,26212,26214 } -MaxEvents 1 -ErrorAction SilentlyContinue) |
     Where-Object { $_ } | Sort-Object TimeCreated -Descending | Select-Object -First 1
if ($e) {
    $m = ($e.Message -split ""`n"" | Where-Object { $_ -match 'Windows a|Windows has|aucun probl|found no problems|corrig|fixed|secteur|sector' } | Select-Object -First 2) -join ' '
    '{0:dd/MM/yyyy HH:mm}|{1}' -f $e.TimeCreated, ($m -replace '\s+', ' ').Trim()
}", ct);
        r = r.Trim();
        if (r.Length == 0) return "Aucune vérification enregistrée récemment.";
        var parts = r.Split('|', 2);
        return $"Dernière vérification : {parts[0]}" + (parts.Length > 1 && parts[1].Length > 0 ? $" — {parts[1]}" : "");
    }

    /// <summary>Lettre « Oui » attendue par chkdsk selon la langue de Windows (O en français).</summary>
    internal static string YesLetter() => System.Globalization.CultureInfo.InstalledUICulture.TwoLetterISOLanguageName switch
    {
        "fr" => "O", "de" or "nl" or "sv" or "da" or "nb" or "no" => "J", "es" or "it" or "pt" => "S", "pl" => "T", "fi" => "K", "tr" => "E", _ => "Y"
    };
}
