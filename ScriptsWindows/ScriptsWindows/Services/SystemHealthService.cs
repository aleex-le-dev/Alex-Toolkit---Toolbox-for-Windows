using System.IO;
using System.Text.RegularExpressions;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public enum ImageHealth { Unknown, Healthy, Repairable, NonRepairable }

/// <summary>Résultat de DISM : état de l'image Windows après l'opération.</summary>
public sealed record DismResult(ImageHealth Health, int ExitCode, string Message);

/// <summary>Résultat de SFC lu dans CBS.log ([SR], en anglais quelle que soit la langue de Windows).</summary>
public sealed record SfcResult(bool Completed, int Repaired, int Unrepaired, IReadOnlyList<string> UnrepairedFiles, string Message);

/// <summary>
/// Intégrité de Windows : DISM (image) et SFC (fichiers système) avec avancement en % et résultats LUS,
/// pas seulement affichés : état de l'image via Repair-WindowsImage (Healthy / Repairable / NonRepairable,
/// indépendant de la langue), fichiers réparés / irréparables via CBS.log, codes d'erreur DISM traduits.
/// </summary>
public static class SystemHealthService
{
    private static readonly Regex Percent = new(@"(\d{1,3})(?:[.,]\d)?\s*%", RegexOptions.Compiled);
    private static string CbsLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\CBS\CBS.log");

    public static string HealthLabel(ImageHealth h) => h switch
    {
        ImageHealth.Healthy => "Image Windows saine",
        ImageHealth.Repairable => "Image Windows endommagée, réparable (DISM /RestoreHealth)",
        ImageHealth.NonRepairable => "Image Windows endommagée et irréparable en ligne (réparation par mise à niveau sur place nécessaire)",
        _ => "État de l'image inconnu"
    };

    /// <summary>Codes de sortie DISM fréquents, en clair.</summary>
    public static string DismCodeMessage(int code) => unchecked((uint)code) switch
    {
        0 => "Opération réussie.",
        0x800F081F => "Fichiers source introuvables (0x800F081F) : vérifiez la connexion Internet et que Windows Update fonctionne, ou utilisez une source d'installation.",
        0x800F0906 => "Téléchargement des fichiers impossible (0x800F0906) : connexion Internet ou Windows Update bloqué.",
        0x800F0907 => "Téléchargement bloqué par une stratégie (0x800F0907) : un WSUS ou une stratégie de groupe empêche l'accès à Windows Update.",
        0x800F0950 => "Installation ou réparation impossible (0x800F0950).",
        0x80070005 => "Accès refusé (0x80070005) : l'outil doit tourner en administrateur.",
        0x800705B9 => "Fichier de manifeste illisible (0x800705B9).",
        0x800F0922 => "Échec du traitement (0x800F0922) : espace insuffisant sur la partition système réservée ou VPN actif.",
        0x8007000E => "Mémoire insuffisante (0x8007000E).",
        0x800704C7 => "Opération annulée (0x800704C7).",
        _ => $"DISM a échoué (code 0x{code:X8})."
    };

    /// <summary>État de l'image tel que Windows l'a enregistré (rapide : CheckHealth).</summary>
    public static async Task<ImageHealth> GetImageHealthAsync(CancellationToken ct = default)
    {
        string s = (await PowerShellRunner.RunAsync("(Repair-WindowsImage -Online -CheckHealth -ErrorAction SilentlyContinue).ImageHealthState", ct)).Trim();
        return s switch
        {
            "Healthy" => ImageHealth.Healthy,
            "Repairable" => ImageHealth.Repairable,
            "NonRepairable" => ImageHealth.NonRepairable,
            _ => ImageHealth.Unknown
        };
    }

    /// <summary>DISM /Online /Cleanup-Image /CheckHealth | /ScanHealth | /RestoreHealth avec avancement, puis état de l'image.</summary>
    public static async Task<DismResult> RunDismAsync(string mode, Action<double> progress, Action<string> log, CancellationToken ct)
    {
        if (mode is not ("CheckHealth" or "ScanHealth" or "RestoreHealth")) throw new ArgumentException(mode);
        int code = await CommandRunner.RunAsync($"DISM /Online /Cleanup-Image /{mode}", line =>
        {
            var m = Percent.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double p)) progress(Math.Min(100, p));
            else if (line.Trim().Length > 0 && !line.Contains('=')) log(line.Trim());
        }, ct);
        var health = await GetImageHealthAsync(ct);
        string msg = code == 0 ? HealthLabel(health) : DismCodeMessage(code);
        return new DismResult(health, code, msg);
    }

    /// <summary>
    /// SFC /scannow avec avancement ; résultat lu dans la partie de CBS.log écrite pendant l'analyse
    /// (lignes [SR] : « Repairing corrupted file », « Cannot repair member file »).
    /// </summary>
    public static async Task<SfcResult> RunSfcAsync(Action<double> progress, Action<string> log, CancellationToken ct)
    {
        long start = 0;
        try { start = new FileInfo(CbsLog).Length; } catch { }
        var lastLines = new List<string>();
        int code = await CommandRunner.RunAsync("sfc /scannow", line =>
        {
            string l = line.Replace("\0", "").Trim();
            if (l.Length == 0) return;
            var m = Percent.Match(l);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double p)) progress(Math.Min(100, p));
            else { lastLines.Add(l); if (lastLines.Count > 8) lastLines.RemoveAt(0); }
        }, ct);

        var (repaired, unrepaired, files, readOk) = await Task.Run(() => ReadSfcResults(start), ct);
        string tail = string.Join(" ", lastLines);
        bool completed = readOk || tail.Length > 0;
        string msg;
        if (!readOk)
            msg = tail.Length > 0 ? tail : $"SFC terminé (code {code}) ; journal CBS illisible.";
        else if (unrepaired > 0)
            msg = $"{unrepaired} fichier(s) système endommagé(s) n'ont pas pu être réparés" + (repaired > 0 ? $", {repaired} réparé(s)" : "") + " : lancez DISM /RestoreHealth puis SFC à nouveau.";
        else if (repaired > 0)
            msg = $"{repaired} fichier(s) système endommagé(s) ont été réparés. Redémarrez le PC.";
        else if (tail.Contains("ne peut pas", StringComparison.OrdinalIgnoreCase) || tail.Contains("could not perform", StringComparison.OrdinalIgnoreCase))
            msg = "SFC n'a pas pu effectuer l'analyse : " + tail;
        else
            msg = "Aucune violation d'intégrité : les fichiers système sont intacts.";
        return new SfcResult(completed, repaired, unrepaired, files, msg);
    }

    /// <summary>Lit les lignes [SR] ajoutées à CBS.log depuis <paramref name="start"/> (fichier ouvert en partage).</summary>
    internal static (int Repaired, int Unrepaired, List<string> Files, bool Ok) ReadSfcResults(long start)
    {
        try
        {
            using var fs = new FileStream(CbsLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (start > fs.Length) start = 0;   // journal tourné pendant l'analyse
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return ParseSrLines(sr.ReadToEnd());
        }
        catch { return (0, 0, new List<string>(), false); }
    }

    internal static (int Repaired, int Unrepaired, List<string> Files, bool Ok) ParseSrLines(string text)
    {
        var repaired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unrepaired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool sawSr = false;
        foreach (var raw in text.Split('\n'))
        {
            if (!raw.Contains("[SR]")) continue;
            sawSr = true;
            string line = raw.Trim();
            string file = Regex.Match(line, @"""([^""]+)""").Groups[1].Value;
            if (file.Length == 0) file = Regex.Match(line, @"\\\?\?\\(\S+)").Groups[1].Value;
            if (line.Contains("Cannot repair member file", StringComparison.OrdinalIgnoreCase)) unrepaired.Add(file.Length > 0 ? file : line);
            else if (line.Contains("Repairing corrupted file", StringComparison.OrdinalIgnoreCase) || line.Contains("Repaired file", StringComparison.OrdinalIgnoreCase))
                repaired.Add(file.Length > 0 ? file : line);
        }
        foreach (var f in unrepaired) repaired.Remove(f);
        return (repaired.Count, unrepaired.Count, unrepaired.Take(15).ToList(), sawSr);
    }
}
