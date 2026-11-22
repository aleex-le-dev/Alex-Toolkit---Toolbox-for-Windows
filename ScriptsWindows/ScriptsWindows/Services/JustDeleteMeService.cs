using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ScriptsWindows.Services;

/// <summary>Comment supprimer un compte sur un site (base JustDeleteMe).</summary>
public sealed record DeletionInfo(string Name, string Url, string Difficulty, string Notes, string Email, string EmailSubject, string EmailBody, string[] Domains)
{
    /// <summary>Adresse du site (1er domaine connu), pour un compte ajouté à la main.</summary>
    public string MainUrl => Domains.Length > 0 ? $"https://{Domains[0]}/" : Url;

    public string DifficultyLabel => Difficulty switch
    {
        "easy" => "Suppression facile", "medium" => "Suppression moyenne", "hard" => "Suppression difficile",
        "limited" => "Suppression partielle", "impossible" => "Suppression impossible", _ => "Difficulté inconnue"
    };
    /// <summary>Jeton de couleur du thème.</summary>
    public string Level => Difficulty switch { "easy" => "Success", "medium" => "Warning", "limited" => "Warning", "hard" => "Danger", "impossible" => "Danger", _ => "TextMuted" };
    public int Rank => Difficulty switch { "easy" => 0, "medium" => 1, "limited" => 2, "hard" => 3, "impossible" => 4, _ => 5 };

    /// <summary>Lien mailto: pré-rempli (sites qui ne suppriment que sur demande par e-mail).</summary>
    public string MailTo(string username) => Email.Length == 0 ? "" :
        $"mailto:{Email}?subject={Uri.EscapeDataString(EmailSubject.Length > 0 ? EmailSubject : "Demande de suppression de compte")}" +
        $"&body={Uri.EscapeDataString((EmailBody.Length > 0 ? EmailBody : "Bonjour,\n\nJe demande la suppression définitive de mon compte et de mes données personnelles (RGPD, article 17).") + $"\n\nNom d'utilisateur : {username}")}";
}

/// <summary>
/// Liens et difficulté de suppression des comptes, d'après la base du projet JustDeleteMe
/// (https://github.com/jdm-contrib/jdm, licence MIT, © jdm-contrib) : téléchargée à chaque recherche, cache hors ligne.
/// Correspondance avec un site par nom de domaine (puis domaine parent, puis nom).
/// </summary>
public sealed class JustDeleteMeIndex
{
    public const string DataUrl = "https://raw.githubusercontent.com/jdm-contrib/jdm/master/_data/sites.json";
    public const string ProjectUrl = "https://github.com/jdm-contrib/jdm";
    public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows", "jdm_sites.json");

    private readonly Dictionary<string, DeletionInfo> _byDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeletionInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DeletionInfo> _all = new();
    public int Count { get; private set; }

    /// <summary>
    /// Recherche d'un site par nom ou domaine (sans accents ni majuscules) : nom exact, puis début du nom ou du domaine, puis contenu.
    /// Pour les comptes que la recherche par pseudo ne peut pas trouver (Facebook, Amazon, Leboncoin…).
    /// </summary>
    /// <summary>
    /// Noms courants d'un service dont le compte porte un autre nom dans la base (un compte Xbox est un compte Microsoft…).
    /// Clé = mot tapé (sans accents), valeur = nom exact dans JustDeleteMe.
    /// </summary>
    private static readonly (string Alias, string Name)[] Aliases =
    {
        ("xbox", "Microsoft Account"), ("xbox live", "Microsoft Account"), ("game pass", "Microsoft Account"), ("hotmail", "Microsoft Account"),
        ("outlook", "Microsoft Account"), ("live", "Microsoft Account"), ("msn", "Microsoft Account"), ("onedrive", "Microsoft Account"),
        ("playstation", "PlayStation Network"), ("psn", "PlayStation Network"), ("ps4", "PlayStation Network"), ("ps5", "PlayStation Network"),
        ("sony", "Sony"),
    };

    public List<DeletionInfo> Search(string query, int max = 20)
    {
        string q = Norm(query);
        if (q.Length < 2) return new();
        var aliased = Aliases.Where(a => a.Alias == q || (q.Length >= 3 && a.Alias.StartsWith(q)))
            .Select(a => _byName.TryGetValue(a.Name, out var i) ? i : null).OfType<DeletionInfo>();
        return aliased.Concat(SearchByName(q)).Distinct().Take(max).ToList();
    }

    private IEnumerable<DeletionInfo> SearchByName(string q)
    {
        return _all.Select(i => (i, n: Norm(i.Name), score:
                    Norm(i.Name) == q ? 0 : Norm(i.Name).StartsWith(q) || i.Domains.Any(x => x.StartsWith(q, StringComparison.OrdinalIgnoreCase)) ? 1 :
                    Norm(i.Name).Contains(q) || i.Domains.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase)) ? 2 : 9))
            .Where(x => x.score < 9).OrderBy(x => x.score).ThenBy(x => x.n.Length).ThenBy(x => x.n)
            .Select(x => x.i);
    }

    private static string Norm(string s)
    {
        var f = (s ?? "").Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(f.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    public static async Task<JustDeleteMeIndex> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            string json = await http.GetStringAsync(DataUrl, ct);
            var idx = Parse(json);
            if (idx.Count > 500)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); await File.WriteAllTextAsync(CachePath, json, ct); } catch { }
                return idx;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return File.Exists(CachePath) ? Parse(await File.ReadAllTextAsync(CachePath, ct)) : new JustDeleteMeIndex();
    }

    internal static JustDeleteMeIndex Parse(string json)
    {
        var idx = new JustDeleteMeIndex();
        using var doc = JsonDocument.Parse(json);
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            string S(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string name = S("name"), url = S("url_fr").Length > 0 ? S("url_fr") : S("url");
            if (name.Length == 0) continue;
            var domains = s.TryGetProperty("domains", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).Select(StripWww).ToArray() : Array.Empty<string>();
            var info = new DeletionInfo(name, url, S("difficulty"), S("notes_fr").Length > 0 ? S("notes_fr") : S("notes"), S("email"), S("email_subject"), S("email_body"), domains);
            idx._byName.TryAdd(name, info);
            foreach (var x in domains) idx._byDomain.TryAdd(x, info);
            idx._all.Add(info);
            idx.Count++;
        }
        return idx;
    }

    private static string StripWww(string h) => h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;

    /// <summary>Infos de suppression pour un site (adresse principale + nom), ou null.</summary>
    public DeletionInfo? Find(string siteUrl, string siteName)
    {
        if (Uri.TryCreate(siteUrl, UriKind.Absolute, out var u))
        {
            string host = StripWww(u.Host);
            if (_byDomain.TryGetValue(host, out var i)) return i;
            var parts = host.Split('.');
            if (parts.Length > 2 && _byDomain.TryGetValue(string.Join('.', parts[^2..]), out i)) return i;
        }
        return _byName.TryGetValue(siteName, out var n) ? n : null;
    }

    /// <summary>Recherche web « comment supprimer mon compte » pour un site absent de la base.</summary>
    public static string SearchUrl(string siteName) => "https://duckduckgo.com/?q=" + Uri.EscapeDataString($"supprimer mon compte {siteName}");
}
