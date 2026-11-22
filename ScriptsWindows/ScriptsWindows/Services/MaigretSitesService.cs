using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace ScriptsWindows.Services;

/// <summary>
/// « Recherche étendue » : sites supplémentaires tirés de la base du projet maigret
/// (https://github.com/soxoj/maigret, licence MIT, © soxoj) — uniquement la DÉTECTION de compte, jamais l'extraction
/// des profils (bio, liens, localisation). Sélection : sites actifs, sans protection anti-robots, parmi les 100 000 les
/// plus visités ou étiquetés « fr », absents de la base principale. Modèles de moteurs (Discourse, XenForo, phpBB…)
/// fusionnés avec chaque site. Base téléchargée, cache 7 jours.
/// </summary>
public static class MaigretSitesService
{
    public const string DataUrl = "https://raw.githubusercontent.com/soxoj/maigret/main/maigret/resources/data.json";
    public const string ProjectUrl = "https://github.com/soxoj/maigret";
    public static string CachePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows", "maigret_data.json");
    internal const int MaxRank = 100_000;
    private static readonly string[] AdultTags = { "porn", "adult", "erotic", "nsfw", "dating" };

    public static async Task<List<AccountSite>> LoadAsync(IEnumerable<AccountSite> existing, CancellationToken ct = default)
    {
        string? json = null;
        if (File.Exists(CachePath) && DateTime.Now - File.GetLastWriteTime(CachePath) < TimeSpan.FromDays(7))
            json = await File.ReadAllTextAsync(CachePath, ct);
        if (json == null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
                json = await http.GetStringAsync(DataUrl, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, json, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch when (File.Exists(CachePath)) { json = await File.ReadAllTextAsync(CachePath, ct); }
        }
        return Convert(json, existing);
    }

    private static string Host(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        string h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    private static string[] Strs(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray(),
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => new[] { s },
        _ => Array.Empty<string>()
    };

    internal static List<AccountSite> Convert(string json, IEnumerable<AccountSite> existing)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var engines = root["engines"] as JsonObject ?? new JsonObject();
        var names = existing.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hosts = existing.Select(s => Host(s.UrlMain)).Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<AccountSite>();
        foreach (var (name, node) in root["sites"] as JsonObject ?? new JsonObject())
        {
            if (node is not JsonObject site) continue;
            // Modèle du moteur (Discourse, XenForo…) puis champs propres au site (prioritaires)
            var m = new JsonObject();
            if (site["engine"]?.GetValue<string>() is { } eng && engines[eng]?["site"] is JsonObject tpl)
                foreach (var (k, v) in tpl) m[k] = v?.DeepClone();
            foreach (var (k, v) in site) m[k] = v?.DeepClone();

            string? S(string k) => m[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            bool B(string k) => m[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            var tags = Strs(m["tags"]);
            int rank = m["alexaRank"] is JsonValue r && r.TryGetValue<int>(out var ri) ? ri : int.MaxValue;
            if (B("disabled") || m["protection"] != null) continue;
            if (!(rank <= MaxRank || tags.Contains("fr"))) continue;
            string urlMain = (S("urlMain") ?? "").TrimEnd('/');
            string Fill(string t) => t.Replace("{urlMain}", urlMain).Replace("{urlSubpath}", S("urlSubpath") ?? "").Replace("{username}", "{}");
            string url = Fill(S("url") ?? ""), type = S("checkType") ?? "";
            if (url.Length == 0 || !url.Contains("{}") || type is not ("status_code" or "message" or "response_url") || urlMain.Length == 0) continue;
            if (names.Contains(name) || hosts.Contains(Host(urlMain))) continue;   // déjà dans la base principale
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (m["headers"] is JsonObject ho) foreach (var (k, v) in ho) if (v is JsonValue hv && hv.TryGetValue<string>(out var hs)) headers[k] = hs;
            JsonNode? payload = m["requestPayload"]?.DeepClone();
            if (payload != null) payload = JsonNode.Parse(payload.ToJsonString().Replace("{username}", "{}"));
            list.Add(new AccountSite($"{name}", url, urlMain + "/", S("urlProbe") is { } p ? Fill(p) : null, type, Strs(m["absenceStrs"]), Array.Empty<int>(),
                S("regexCheck"), (S("requestMethod") ?? "").ToUpperInvariant(), payload, headers, tags.Any(t => AdultTags.Contains(t)),
                S("usernameClaimed") ?? "", Strs(m["presenseStrs"]), Extended: true));
            hosts.Add(Host(urlMain)); names.Add(name);
        }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
