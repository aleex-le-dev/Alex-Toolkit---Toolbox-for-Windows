using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>
/// Magasin de pilotes (DriverStore) : Windows conserve chaque version installée d'un pilote de fabricant.
/// Repère les anciennes versions remplacées par une plus récente du même pilote et les supprime
/// (<c>pnputil /delete-driver</c> sans <c>/force</c> : Windows refuse de supprimer un pilote en service).
/// </summary>
public static class DriverStoreService
{
    public sealed record DriverPackage(string Inf, string OriginalName, string Provider, string ClassName, string ClassGuid,
        Version? Version, string VersionText, DateTime? Date, string? Folder = null, long Size = 0, bool InUse = false);

    /// <summary>Ancienne version <see cref="Package"/>, remplacée par <see cref="Newest"/> (toujours conservée).</summary>
    public sealed record OldDriver(DriverPackage Package, DriverPackage Newest);

    public sealed record Analysis(int PackageCount, List<OldDriver> OldVersions);

    private static readonly string Repository = Path.Combine(Environment.SystemDirectory, @"DriverStore\FileRepository");
    private static readonly string Pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
    private static readonly Regex OemInf = new(@"^oem\d+\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DriverVerLine = new(@"^\s*DriverVer\s*=\s*[^,]*,\s*([\d\.]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    static DriverStoreService()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    /// <summary>Inventaire complet + anciennes versions (plus grosses d'abord, celles en service à la fin).</summary>
    public static async Task<Analysis> AnalyzeAsync(CancellationToken ct = default)
    {
        var raw = await EnumerateAsync(ct);
        var inUse = await GetInUseInfsAsync(ct);

        var packages = await Task.Run(() => raw.Select(p =>
        {
            string? folder = ResolveFolder(p);
            return p with { Folder = folder, Size = FolderSize(folder), InUse = inUse.Contains(p.Inf) };
        }).ToList(), ct);

        var old = new List<OldDriver>();
        foreach (var group in packages.GroupBy(p => (p.OriginalName.ToLowerInvariant(), p.Provider.Trim().ToLowerInvariant(), p.ClassGuid.ToLowerInvariant())))
        {
            if (group.Count() < 2) continue;
            var ordered = group.OrderByDescending(p => p.Version ?? new Version(0, 0))
                               .ThenByDescending(p => p.Date ?? DateTime.MinValue).ToList();
            foreach (var p in ordered.Skip(1)) old.Add(new OldDriver(p, ordered[0]));
        }

        return new Analysis(packages.Count,
            old.OrderBy(o => o.Package.InUse).ThenByDescending(o => o.Package.Size).ThenBy(o => o.Package.OriginalName).ToList());
    }

    /// <summary>
    /// Supprime un paquet du magasin (sans /force). Réussi si pnputil renvoie 0 / 3010 et que l'INF a disparu.
    /// </summary>
    public static async Task<(bool Ok, int ExitCode)> DeleteAsync(string inf, CancellationToken ct = default)
    {
        if (!OemInf.IsMatch(inf)) throw new ArgumentException($"Nom de pilote invalide : {inf}");
        var (code, _) = await RunPnputilAsync($"/delete-driver {inf}", ct);
        bool gone = !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf));
        return ((code == 0 || code == 3010) && gone, code);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} Mo",
        >= 1L << 10 => $"{bytes / 1024.0:N0} Ko",
        _ => $"{bytes} o",
    };

    // ── Inventaire ─────────────────────────────────────────────────────────

    private static async Task<List<DriverPackage>> EnumerateAsync(CancellationToken ct)
    {
        // Windows 11 récent : sortie XML (indépendante de la langue)
        var (code, xml) = await RunPnputilAsync("/enum-drivers /format xml", ct);
        int start = xml.IndexOf("<PnpUtil", StringComparison.Ordinal);
        if (code == 0 && start >= 0)
        {
            try { return ParseXml(xml[start..]); } catch { }
        }

        // Windows plus ancien : sortie texte traduite, lue par position des champs (même ordre dans toutes les langues)
        var (code2, text) = await RunPnputilAsync("/enum-drivers", ct);
        if (code2 != 0) throw new InvalidOperationException($"Inventaire des pilotes impossible (pnputil, code {code2}).");
        var list = ParseText(text);
        if (list.Count == 0) throw new InvalidOperationException("Cette version de Windows ne fournit pas le nom d'origine des pilotes : analyse impossible.");
        return list;
    }

    private static List<DriverPackage> ParseXml(string xml)
    {
        var list = new List<DriverPackage>();
        foreach (var d in XDocument.Parse(xml).Root!.Elements("Driver"))
        {
            string inf = (string?)d.Attribute("DriverName") ?? "";
            if (!OemInf.IsMatch(inf)) continue;
            var (version, versionText, date) = ParseDriverVersion((string?)d.Element("DriverVersion") ?? "");
            list.Add(new DriverPackage(inf.ToLowerInvariant(), (string?)d.Element("OriginalName") ?? "", (string?)d.Element("ProviderName") ?? "",
                (string?)d.Element("ClassName") ?? "", (string?)d.Element("ClassGuid") ?? "", version, versionText, date));
        }
        return list;
    }

    /// <summary>Blocs séparés par une ligne vide ; champs : nom publié, nom d'origine, fournisseur, classe, GUID, version.</summary>
    private static List<DriverPackage> ParseText(string text)
    {
        var list = new List<DriverPackage>();
        foreach (var block in Regex.Split(text.Replace("\r", ""), @"\n\s*\n"))
        {
            var values = block.Split('\n').Where(l => l.Contains(':'))
                              .Select(l => l[(l.IndexOf(':') + 1)..].Trim().Trim(' ').Trim()).ToList();
            if (values.Count < 6 || !OemInf.IsMatch(values[0]) || !values[1].EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) continue;
            var (version, versionText, date) = ParseDriverVersion(values[5]);
            list.Add(new DriverPackage(values[0].ToLowerInvariant(), values[1], values[2], values[3], values[4], version, versionText, date));
        }
        return list;
    }

    /// <summary>« 10/09/2026 7.2620.2.3 » → version 7.2620.2.3, date du 10/09/2026.</summary>
    private static (Version?, string, DateTime?) ParseDriverVersion(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, "", null);
        string versionText = parts[^1];
        Version.TryParse(versionText, out var version);
        DateTime? date = null;
        if (parts.Length > 1 &&
            (DateTime.TryParse(parts[0], CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) ||
             DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out d)))
            date = d;
        return (version, versionText, date);
    }

    private static async Task<HashSet<string>> GetInUseInfsAsync(CancellationToken ct)
    {
        const string script = @"
Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
  Where-Object { $_.InfName -like 'oem*.inf' } | ForEach-Object { $_.InfName.ToLowerInvariant() } | Sort-Object -Unique";
        string output = await PowerShellRunner.RunAsync(script, ct);
        return new HashSet<string>(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .Where(s => OemInf.IsMatch(s)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Dossier du paquet dans FileRepository : registre DriverDatabase, sinon dossier dont l'INF porte la même version.</summary>
    private static string? ResolveFolder(DriverPackage p)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\DriverDatabase\DriverInfFiles\{p.Inf}");
            if (key?.GetValue("") is string[] { Length: > 0 } names)
            {
                string folder = Path.Combine(Repository, names[0]);
                if (Directory.Exists(folder)) return folder;
            }
        }
        catch { }

        if (p.OriginalName.Length == 0 || p.Version == null) return null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Repository, p.OriginalName + "_*"))
            {
                string inf = Path.Combine(dir, p.OriginalName);
                if (!File.Exists(inf)) continue;
                string content;
                using (var reader = new StreamReader(inf, Encoding.Default, detectEncodingFromByteOrderMarks: true)) content = reader.ReadToEnd();
                var m = DriverVerLine.Match(content);
                if (m.Success && Version.TryParse(m.Groups[1].Value, out var v) && v == p.Version) return dir;
            }
        }
        catch { }
        return null;
    }

    private static long FolderSize(string? folder)
    {
        if (folder == null) return 0;
        try { return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }

    // ── pnputil ────────────────────────────────────────────────────────────

    /// <summary>Lance pnputil et décode sa sortie (UTF-8 si valide, sinon page de code OEM de la console).</summary>
    private static async Task<(int ExitCode, string Output)> RunPnputilAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Pnputil, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("pnputil introuvable.");
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
        using var ms = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var err = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, err);
        await proc.WaitForExitAsync(ct);

        byte[] bytes = ms.ToArray();
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes); }
        return (proc.ExitCode, text);
    }
}
