using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScriptsWindows.Services;

public enum AccountStatus { Found, Uncertain, NotFound, Skipped }

/// <summary>Un site de la base Sherlock (data.json).</summary>
public sealed record AccountSite(string Name, string Url, string UrlMain, string? UrlProbe, string ErrorType, string[] ErrorMsg, int[] ErrorCode,
                                 string? RegexCheck, string Method, JsonNode? Payload, Dictionary<string, string> Headers, bool Nsfw, string Claimed = "",
                                 string[]? Presence = null, bool Extended = false);

public sealed record AccountResult(string Site, string Username, string Url, string UrlMain, AccountStatus Status, string Detail);

/// <summary>
/// Retrouve les comptes d'un pseudo sur ~480 sites, avec la base et les règles de détection du projet Sherlock
/// (https://github.com/sherlock-project/sherlock, licence MIT, © Sherlock Project) : « status_code » (page de profil
/// en 2xx), « message » (texte d'erreur absent de la page), « response_url » (pas de redirection). La base est
/// téléchargée à chaque recherche (toujours à jour) et gardée en cache pour fonctionner hors ligne.
/// </summary>
public static class AccountFinderService
{
    public const string DataUrl = "https://raw.githubusercontent.com/sherlock-project/sherlock/master/sherlock_project/resources/data.json";
    public const string ProjectUrl = "https://github.com/sherlock-project/sherlock";

    public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows", "sherlock_data.json");

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";

    // Pages de protection anti-robots (Cloudflare, AWS WAF, PerimeterX) : le résultat n'est pas fiable
    private static readonly string[] WafMarks =
    {
        "<span id=\"challenge-error-text\">", "cf-chl-", "challenge-platform", "AwsWafIntegration.forceRefreshToken", "perimeterxIdentifiers", "_Incapsula_Resource"
    };

    private const int RequestTimeoutSeconds = 10;
    private const string NoAnswer = "pas de réponse en 10 s", Unreachable = "site injoignable";
    private static readonly HttpClient Follow = Create(true), NoFollow = Create(false);

    private static HttpClient Create(bool redirects)
    {
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = redirects, MaxAutomaticRedirections = 8, AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 4, PooledConnectionLifetime = TimeSpan.FromMinutes(2), UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
        // Comme Sherlock : seulement l'identification du navigateur. Surtout pas « Accept-Language: fr » : les sites
        // répondraient en français et les messages d'erreur attendus (en anglais) ne seraient plus reconnus.
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return c;
    }

    /// <summary>
    /// Sites ajoutés par Alex Toolkit (Assets/Data/sites_extra.json, incorporé à l'exe), absents de Sherlock et validés
    /// un par un (pseudo existant = trouvé, pseudos inventés = absents). Ajoutés seulement si Sherlock ne les a pas déjà.
    /// </summary>
    internal static List<AccountSite> ExtraSites()
    {
        try
        {
            using var s = typeof(AccountFinderService).Assembly.GetManifestResourceStream("sites_extra.json");
            if (s == null) return new();
            using var r = new StreamReader(s);
            return Parse(r.ReadToEnd());
        }
        catch { return new(); }
    }

    private static List<AccountSite> WithExtras(List<AccountSite> sites)
    {
        var names = sites.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hosts = sites.Select(x => Uri.TryCreate(x.UrlMain, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        sites.AddRange(ExtraSites().Where(x => !names.Contains(x.Name) && !(Uri.TryCreate(x.UrlMain, UriKind.Absolute, out var u) && hosts.Contains(u.Host.Replace("www.", "")))));
        return sites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Base des sites (Sherlock + ajouts d'Alex Toolkit) : téléchargée (puis mise en cache), sinon le cache.</summary>
    public static async Task<(List<AccountSite> Sites, string Source)> LoadSitesAsync(CancellationToken ct = default)
    {
        var (sites, source) = await LoadSherlockAsync(ct);
        return (WithExtras(sites), source);
    }

    private static async Task<(List<AccountSite> Sites, string Source)> LoadSherlockAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            string json = await Follow.GetStringAsync(DataUrl, cts.Token);
            var sites = Parse(json);
            if (sites.Count > 100)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); await File.WriteAllTextAsync(CachePath, json, ct); } catch { }
                return (sites, "à jour (téléchargée maintenant)");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        if (File.Exists(CachePath))
            return (Parse(await File.ReadAllTextAsync(CachePath, ct)), $"copie du {File.GetLastWriteTime(CachePath):dd/MM/yyyy} (hors ligne)");
        throw new InvalidOperationException("Base des sites indisponible : pas de connexion Internet et aucune copie locale.");
    }

    internal static List<AccountSite> Parse(string json)
    {
        var list = new List<AccountSite>();
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("data.json invalide");
        foreach (var (name, node) in root)
        {
            if (name.StartsWith('$') || node is not JsonObject o) continue;
            string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            string url = S("url") ?? "", type = S("errorType") ?? "";
            if (url.Length == 0 || type is not ("status_code" or "message" or "response_url")) continue;
            string[] msgs = o["errorMsg"] switch
            {
                JsonArray a => a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray(),
                JsonValue v when v.TryGetValue<string>(out var s) => new[] { s },
                _ => Array.Empty<string>()
            };
            int[] codes = o["errorCode"] switch
            {
                JsonArray a => a.Select(x => x?.GetValue<int>() ?? 0).ToArray(),
                JsonValue v when v.TryGetValue<int>(out var i) => new[] { i },
                _ => Array.Empty<int>()
            };
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (o["headers"] is JsonObject ho)
                foreach (var (k, v) in ho) if (v is JsonValue hv && hv.TryGetValue<string>(out var s)) headers[k] = s;
            list.Add(new AccountSite(name, url, S("urlMain") ?? url, S("urlProbe"), type, msgs, codes, S("regexCheck"),
                (S("request_method") ?? "").ToUpperInvariant(), o["request_payload"]?.DeepClone(), headers,
                o["isNSFW"] is JsonValue n && n.TryGetValue<bool>(out var b) && b, S("username_claimed") ?? ""));
        }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Pseudos saisis (séparés par espaces, virgules, retours) : lettres, chiffres, « . », « _ », « - » ; 1 à 60 caractères.</summary>
    public static (List<string> Valid, List<string> Rejected) ParseUsernames(string input)
    {
        var valid = new List<string>(); var rejected = new List<string>();
        foreach (var raw in (input ?? "").Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string u = raw.Trim().TrimStart('@');
            if (u.Length is >= 1 and <= 60 && u.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            { if (!valid.Contains(u, StringComparer.OrdinalIgnoreCase)) valid.Add(u); }
            else rejected.Add(raw);
        }
        return (valid, rejected);
    }

    private static JsonNode? FillPayload(JsonNode? n, string user) => n switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, FillPayload(kv.Value, user)))),
        JsonArray a => new JsonArray(a.Select(x => FillPayload(x, user)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(s.Replace("{}", user)),
        _ => n.DeepClone()
    };

    /// <summary>Verdict à partir de la réponse (règles de Sherlock, plus prudentes sur les blocages : 403 / 429 / 5xx = incertain).</summary>
    internal static (AccountStatus Status, string Detail) Evaluate(AccountSite s, int status, string body)
    {
        if (body.Length > 0 && WafMarks.Any(m => body.Contains(m, StringComparison.Ordinal)))
            return (AccountStatus.Uncertain, "site protégé contre les robots : à vérifier à la main");
        bool ok = status is >= 200 and < 300;
        bool blocked = status is 403 or 429 or 401 or >= 500;
        switch (s.ErrorType)
        {
            case "status_code":
                if (s.ErrorCode.Contains(status)) return (AccountStatus.NotFound, "");
                if (ok) return (AccountStatus.Found, "");
                return blocked ? (AccountStatus.Uncertain, $"le site a refusé la vérification (HTTP {status})") : (AccountStatus.NotFound, "");
            case "message":
                if (s.ErrorMsg.Any(m => body.Contains(m, StringComparison.Ordinal))) return (AccountStatus.NotFound, "");
                // maigret : texte qui doit être présent sur une page de profil (sinon pas de compte)
                if (ok && s.Presence is { Length: > 0 } pres && !pres.Any(m => body.Contains(m, StringComparison.Ordinal))) return (AccountStatus.NotFound, "");
                if (ok) return (AccountStatus.Found, "");
                return blocked ? (AccountStatus.Uncertain, $"le site a refusé la vérification (HTTP {status})") : (AccountStatus.NotFound, "");
            default: // response_url : redirection = pas de compte
                if (ok) return (AccountStatus.Found, "");
                return blocked ? (AccountStatus.Uncertain, $"le site a refusé la vérification (HTTP {status})") : (AccountStatus.NotFound, "");
        }
    }

    /// <summary>Vérifie un pseudo sur un site.</summary>
    /// <summary>
    /// Sites jamais interrogés automatiquement : les voleurs de mots de passe (LummaStealer, Vidar…) lisent des profils Steam
    /// et des canaux Telegram pour trouver leur serveur ; Microsoft Defender surveille donc les programmes non signés qui les
    /// consultent et a supprimé Alex Toolkit pendant une recherche (Behavior:Win32/LummaStealer.F1, 21 et 22/11).
    /// Ces sites restent proposés « à vérifier vous-même » avec leur lien, sans aucune requête.
    /// </summary>
    internal static readonly string[] ManualOnlyHosts = { "steamcommunity.com", "steampowered.com", "t.me", "telegram.me", "telegram.org", "telegra.ph" };
    internal const string ManualOnlyDetail = "à vérifier vous-même : pas de vérification automatique sur ce site (l'antivirus la prend pour un logiciel espion)";

    internal static bool IsManualOnly(AccountSite s) =>
        new[] { s.Url, s.UrlProbe ?? "", s.UrlMain }.Any(u =>
            Uri.TryCreate(u.Replace("{}", "x"), UriKind.Absolute, out var uri) &&
            ManualOnlyHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)));

    public static async Task<AccountResult> CheckAsync(AccountSite s, string user, CancellationToken ct)
    {
        string profile = s.Url.Replace("{}", user);
        if (IsManualOnly(s)) return new(s.Name, user, profile, s.UrlMain, AccountStatus.Uncertain, ManualOnlyDetail);
        if (s.RegexCheck != null)
        {
            try { if (!Regex.IsMatch(user, s.RegexCheck, RegexOptions.None, TimeSpan.FromSeconds(1))) return new(s.Name, user, profile, s.UrlMain, AccountStatus.Skipped, "pseudo impossible sur ce site"); }
            catch { }
        }
        string probe = (s.UrlProbe ?? s.Url).Replace("{}", Uri.EscapeDataString(user));
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new(s.Name, user, profile, s.UrlMain, AccountStatus.Skipped, "adresse invalide");
        try
        {
            // Comme Sherlock : HEAD pour les codes HTTP (GET essayé le 21/11 : moins fiable sur les sites populaires, 32 contre 35)
            var method = s.Method switch { "POST" => HttpMethod.Post, "PUT" => HttpMethod.Put, "HEAD" => HttpMethod.Head, "GET" => HttpMethod.Get,
                                           _ => s.ErrorType == "status_code" && s.Payload == null ? HttpMethod.Head : HttpMethod.Get };
            using var req = new HttpRequestMessage(method, uri);
            if (s.Payload != null) req.Content = new StringContent(FillPayload(s.Payload, user)!.ToJsonString(), Encoding.UTF8, "application/json");
            ApplyHeaders(req, s.Headers);
            var client = s.ErrorType == "response_url" ? NoFollow : Follow;
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)resp.StatusCode;
            // HEAD refusé par certains sites : nouvel essai en GET
            if (method == HttpMethod.Head && code is 405 or 501)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                ApplyHeaders(get, s.Headers);
                using var r2 = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
                code = (int)r2.StatusCode;
            }
            string body = method == HttpMethod.Head ? "" : await ReadLimitedAsync(resp, ct);
            var (st, detail) = Evaluate(s, code, body);
            return new(s.Name, user, profile, s.UrlMain, st, detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            string why = ex is TaskCanceledException ? NoAnswer : ex is HttpRequestException ? Unreachable : ex.GetType().Name;
            return new(s.Name, user, profile, s.UrlMain, AccountStatus.Uncertain, why);
        }
    }

    /// <summary>En-têtes propres au site ; ceux du contenu (Content-Type…) vont sur le corps de la requête (sinon exception).</summary>
    private static void ApplyHeaders(HttpRequestMessage req, Dictionary<string, string> headers)
    {
        foreach (var (k, v) in headers)
        {
            if (k.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                if (req.Content == null) continue;
                req.Content.Headers.Remove(k);
                req.Content.Headers.TryAddWithoutValidation(k, v);
            }
            else { req.Headers.Remove(k); req.Headers.TryAddWithoutValidation(k, v); }
        }
    }

    /// <summary>Lit au plus 2 Mo de la page (le message d'erreur est toujours au début).</summary>
    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var st = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[2 * 1024 * 1024]; int total = 0, n;
        while (total < buf.Length && (n = await st.ReadAsync(buf.AsMemory(total, buf.Length - total), ct)) > 0) total += n;
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    /// <summary>
    /// Vérifie tous les pseudos sur tous les sites, 24 requêtes à la fois, avec deux contrôles par site :
    /// - négatif : un pseudo aléatoire (inexistant) ; s'il est « trouvé », le site répond pareil pour tout le monde →
    ///   les comptes « trouvés » de ce site passent en « à vérifier » (faux positifs) ;
    /// - positif : le pseudo existant connu de la base (username_claimed) ; s'il est « absent », la détection de ce site
    ///   est en panne → les « absents » de ce site passent en « à vérifier » (évite de croire qu'on n'y a pas de compte).
    /// Chaque résultat est remonté dès qu'il est sûr.
    /// </summary>
    public static async Task SearchAsync(IReadOnlyList<string> users, IReadOnlyList<AccountSite> sites, Action<AccountResult> onResult, CancellationToken ct)
    {
        // 8 sites traités à la fois (donc au plus 8 requêtes simultanées) ; pour chaque site, ses requêtes se suivent et
        // son résultat s'affiche dès qu'il est complet → progression régulière (avant : contrôles en file derrière
        // toutes les vérifications, rien ne s'affichait avant la fin).
        string control = "zq" + Guid.NewGuid().ToString("N")[..12];
        var cache = LoadControls();
        var fresh = new System.Collections.Concurrent.ConcurrentDictionary<string, SiteControl>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Parallel.ForEachAsync(sites, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (s, token) =>
            {
                var first = await CheckAsync(s, users[0], token);
                // Site injoignable ou muet : inutile d'attendre le délai pour chaque pseudo et chaque contrôle
                if (first.Status == AccountStatus.Uncertain && first.Detail is Unreachable or NoAnswer or ManualOnlyDetail)
                {
                    foreach (var u in users) onResult(u == users[0] ? first : first with { Username = u, Url = s.Url.Replace("{}", u) });
                    return;
                }
                bool foundForAnyone, detectionBroken;
                if (cache.TryGetValue(s.Name, out var c) && DateTime.Now - c.Checked < ControlLifetime)
                    (foundForAnyone, detectionBroken) = (c.FoundForAnyone, c.DetectionBroken);   // contrôles récents : pas de requête
                else
                {
                    var ctrl = await CheckAsync(s, control, token);
                    bool hasKnown = s.Claimed.Length > 0 && !users.Contains(s.Claimed, StringComparer.OrdinalIgnoreCase);
                    var known = hasKnown ? await CheckAsync(s, s.Claimed, token) : null;
                    foundForAnyone = ctrl.Status == AccountStatus.Found;
                    detectionBroken = known?.Status == AccountStatus.NotFound;
                    // Mémorisé seulement si les deux contrôles ont donné une réponse nette (pas un blocage passager)
                    if (ctrl.Status is AccountStatus.Found or AccountStatus.NotFound && (known == null || known.Status is AccountStatus.Found or AccountStatus.NotFound))
                        fresh[s.Name] = new SiteControl(s.Name, DateTime.Now, foundForAnyone, detectionBroken);
                }
                onResult(Adjust(first, foundForAnyone, detectionBroken, s.Extended));
                foreach (var u in users.Skip(1))
                    onResult(Adjust(await CheckAsync(s, u, token), foundForAnyone, detectionBroken, s.Extended));
            });
        }
        finally { if (!fresh.IsEmpty) SaveControls(cache, fresh.Values); }   // gardé aussi si la recherche est arrêtée
    }

    // ─── Contrôles par site mémorisés 7 jours ───
    public sealed record SiteControl(string Name, DateTime Checked, bool FoundForAnyone, bool DetectionBroken);
    internal static int Parallelism = 8;
    internal static readonly TimeSpan ControlLifetime = TimeSpan.FromDays(7);
    public static string ControlsPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows", "site_controls.json");

    internal static Dictionary<string, SiteControl> LoadControls()
    {
        try
        {
            if (!File.Exists(ControlsPath)) return new(StringComparer.OrdinalIgnoreCase);
            var list = JsonSerializer.Deserialize<List<SiteControl>>(File.ReadAllText(ControlsPath)) ?? new();
            return list.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Checked).First(), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveControls(Dictionary<string, SiteControl> old, IEnumerable<SiteControl> fresh)
    {
        try
        {
            foreach (var c in fresh) old[c.Name] = c;
            var keep = old.Values.Where(c => DateTime.Now - c.Checked < ControlLifetime).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(ControlsPath)!);
            string tmp = ControlsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(keep));
            File.Move(tmp, ControlsPath, true);
        }
        catch { }
    }

    /// <summary>Applique les contrôles du site au résultat d'un pseudo (recherche étendue : site non fiable = résultat écarté, pas de bruit).</summary>
    internal static AccountResult Adjust(AccountResult r, bool foundForAnyone, bool detectionBroken, bool extended)
    {
        if (extended && (foundForAnyone || detectionBroken) && r.Status is AccountStatus.Found or AccountStatus.NotFound)
            return r with { Status = AccountStatus.Skipped, Detail = "détection non fiable sur ce site (recherche étendue)" };
        return Adjust(r, foundForAnyone, detectionBroken);
    }

    /// <summary>Applique les contrôles du site au résultat d'un pseudo.</summary>
    internal static AccountResult Adjust(AccountResult r, bool foundForAnyone, bool detectionBroken)
    {
        if (foundForAnyone && r.Status == AccountStatus.Found)
            return r with { Status = AccountStatus.Uncertain, Detail = "ce site répond comme si n'importe quel pseudo existait : à vérifier à la main" };
        if (detectionBroken && r.Status == AccountStatus.NotFound)
            return r with { Status = AccountStatus.Uncertain, Detail = "la détection de ce site ne marche plus (un compte connu n'est pas trouvé) : à vérifier à la main" };
        return r;
    }

    /// <summary>Rapport HTML des comptes trouvés et à vérifier.</summary>
    public static string BuildHtml(IEnumerable<AccountResult> results, string source, Func<AccountResult, DeletionInfo?>? deletion = null, Func<AccountResult, string>? note = null)
    {
        deletion ??= _ => null;
        note ??= r => r.Detail;
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder("<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><title>Comptes en ligne</title>" +
            "<style>body{font-family:Segoe UI,sans-serif;margin:32px;color:#1f2937}table{border-collapse:collapse;width:100%}td,th{padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:left}" +
            "h2{margin-top:28px}.m{color:#6b7280;font-size:13px}</style><h1>Comptes en ligne</h1>");
        sb.Append($"<p class=\"m\">Recherche du {DateTime.Now:dd/MM/yyyy HH:mm} — sites : <a href=\"{ProjectUrl}\">Sherlock</a> ({E(source)}) ; " +
                  $"suppression : <a href=\"{JustDeleteMeIndex.ProjectUrl}\">JustDeleteMe</a> (licences MIT).</p>");
        foreach (var (st, title) in new[] { (AccountStatus.Found, "Comptes trouvés"), (AccountStatus.Uncertain, "À vérifier à la main") })
        {
            var rows = results.Where(r => r.Status == st).OrderBy(r => r.Username).ThenBy(r => r.Site).ToList();
            sb.Append($"<h2>{title} ({rows.Count})</h2><table><tr><th>Site</th><th>Pseudo</th><th>Profil</th><th>Suppression</th><th>Suivi, fuites, remarque</th></tr>");
            foreach (var r in rows)
            {
                var d = deletion(r);
                string del = d == null ? $"<a href=\"{E(JustDeleteMeIndex.SearchUrl(r.Site))}\">rechercher</a>"
                    : (d.Url.Length > 0 ? $"<a href=\"{E(d.Url)}\">{E(d.DifficultyLabel)}</a>" : E(d.DifficultyLabel)) + (d.Email.Length > 0 ? $" — {E(d.Email)}" : "");
                sb.Append($"<tr><td>{E(r.Site)}</td><td>{E(r.Username)}</td><td><a href=\"{E(r.Url)}\">{E(r.Url)}</a></td><td>{del}</td><td>{E(note(r))}</td></tr>");
            }
            sb.Append("</table>");
        }
        return sb.Append("</html>").ToString();
    }
}
