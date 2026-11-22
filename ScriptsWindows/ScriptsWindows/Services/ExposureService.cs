using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScriptsWindows.Services;

/// <summary>Une fuite de données connue de Have I Been Pwned (modèle « Breach » de l'API v3).</summary>
public sealed record Breach(string Name, string Title, string Domain, string BreachDate, long PwnCount, string[] DataClasses, bool IsSensitive, bool IsSpamList, bool IsFabricated, bool IsFrench = false)
{
    public DateTime Date => DateTime.TryParse(BreachDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MinValue;
    public string Year => BreachDate.Length >= 4 ? BreachDate[..4] : "?";
    public bool PasswordsLeaked => DataClasses.Contains("Passwords");
    /// <summary>Types de données en français (les plus courants traduits, les autres laissés tels quels).</summary>
    public string DataFr => string.Join(", ", DataClasses.Select(ExposureService.TranslateData));
}

/// <summary>
/// « Mes données exposées » : uniquement des services faits pour vérifier SES PROPRES données, sans jamais montrer
/// les données fuitées elles-mêmes (Have I Been Pwned, Troy Hunt) :
/// - liste publique des fuites (gratuite, sans donnée personnelle envoyée) croisée avec les comptes trouvés ;
/// - fuites d'une adresse e-mail (API payante, clé de l'utilisateur chiffrée par DPAPI) ou page du site ;
/// - mot de passe compromis : k-anonymat, seuls les 5 premiers caractères de l'empreinte SHA-1 partent ;
/// - recherche de son nom sur le web + liens de déréférencement.
/// </summary>
public static class ExposureService
{
    private const string Ua = "AlexToolkit-AccountFinder";
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);   // exigé par l'API (sinon 403)
        return c;
    }

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows");
    public static string BreachesCache { get; set; } = Path.Combine(Dir, "hibp_breaches.json");
    public static string KeyPath { get; set; } = Path.Combine(Dir, "hibp_key.bin");

    private static readonly Dictionary<string, string> DataNames = new()
    {
        ["Email addresses"] = "adresses e-mail", ["Passwords"] = "mots de passe", ["Usernames"] = "pseudos", ["IP addresses"] = "adresses IP",
        ["Names"] = "noms", ["Phone numbers"] = "téléphones", ["Dates of birth"] = "dates de naissance", ["Physical addresses"] = "adresses postales",
        ["Genders"] = "sexe", ["Geographic locations"] = "localisation", ["Credit cards"] = "cartes bancaires", ["Partial credit card data"] = "cartes bancaires (partiel)",
        ["Bank account numbers"] = "comptes bancaires", ["Security questions and answers"] = "questions de sécurité", ["Private messages"] = "messages privés",
        ["Website activity"] = "activité sur le site", ["Purchases"] = "achats", ["Avatars"] = "photos de profil", ["Social media profiles"] = "profils de réseaux sociaux",
        ["Job titles"] = "métiers", ["Employers"] = "employeurs", ["Device information"] = "appareils", ["Browser user agent details"] = "navigateurs",
        ["Government issued IDs"] = "pièces d'identité", ["Spoken languages"] = "langues", ["Auth tokens"] = "jetons de connexion", ["Historical passwords"] = "anciens mots de passe",
        ["Password hints"] = "indices de mot de passe", ["Time zones"] = "fuseaux horaires", ["Salutations"] = "civilités", ["Ages"] = "âges",
        ["Bios"] = "biographies", ["User website URLs"] = "sites web personnels", ["Education levels"] = "niveaux d'études", ["Marital statuses"] = "situations familiales",
        ["Income levels"] = "revenus", ["Nationalities"] = "nationalités", ["Religions"] = "religions", ["Sexual orientations"] = "orientations sexuelles",
        ["Family members' names"] = "noms des proches", ["Driver's licenses"] = "permis de conduire", ["Passport numbers"] = "numéros de passeport",
        ["Instant messenger identities"] = "messageries", ["Mothers maiden names"] = "noms de jeune fille de la mère", ["Chat logs"] = "conversations",
    };
    public static string TranslateData(string d) => DataNames.TryGetValue(d, out var fr) ? fr : d;

    // ─── 1. Liste publique des fuites (cache 1 jour) ───
    public static async Task<List<Breach>> GetBreachesAsync(CancellationToken ct = default)
    {
        string? json = null;
        if (File.Exists(BreachesCache) && DateTime.Now - File.GetLastWriteTime(BreachesCache) < TimeSpan.FromDays(1))
            json = await File.ReadAllTextAsync(BreachesCache, ct);
        if (json == null)
        {
            try
            {
                json = await Http.GetStringAsync("https://haveibeenpwned.com/api/v3/breaches", ct);
                Directory.CreateDirectory(Dir); await File.WriteAllTextAsync(BreachesCache, json, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch when (File.Exists(BreachesCache)) { json = await File.ReadAllTextAsync(BreachesCache, ct); }   // hors ligne : ancienne copie
        }
        return ParseBreaches(json);
    }

    internal static List<Breach> ParseBreaches(string json)
    {
        var list = new List<Breach>();
        using var doc = JsonDocument.Parse(json);
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            string S(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            // Fuite française : domaine .fr ou description qui parle de la France / de Français
            bool fr = S("Domain").EndsWith(".fr", StringComparison.OrdinalIgnoreCase) || S("Domain").Contains(".gouv.", StringComparison.OrdinalIgnoreCase)
                      || System.Text.RegularExpressions.Regex.IsMatch(S("Title") + " " + S("Description"), @"\b(France|French|Français|française)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            list.Add(new Breach(S("Name"), S("Title"), S("Domain"), S("BreachDate"), b.TryGetProperty("PwnCount", out var p) && p.TryGetInt64(out var n) ? n : 0,
                b.TryGetProperty("DataClasses", out var dc) && dc.ValueKind == JsonValueKind.Array ? dc.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>(),
                B("IsSensitive"), B("IsSpamList"), B("IsFabricated"), fr));
        }
        return list;
    }

    private static string Host(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        string h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    /// <summary>
    /// Comptes trouvés dont le site a subi une fuite connue (par domaine, puis domaine parent). Croisement local :
    /// rien n'est envoyé. Fuites « fabriquées » (fausses) et listes de spam écartées.
    /// </summary>
    public static List<(AccountResult Account, Breach Breach)> MatchAccounts(IEnumerable<AccountResult> accounts, IEnumerable<Breach> breaches)
    {
        var byDomain = breaches.Where(b => b.Domain.Length > 0 && !b.IsFabricated && !b.IsSpamList)
            .GroupBy(b => b.Domain.ToLowerInvariant().Replace("www.", "")).ToDictionary(g => g.Key, g => g.ToList());
        var res = new List<(AccountResult, Breach)>();
        foreach (var a in accounts.Where(a => a.Status == AccountStatus.Found).GroupBy(a => a.Site).Select(g => g.First()))
        {
            string h = Host(a.UrlMain.Length > 0 ? a.UrlMain : a.Url);
            if (h.Length == 0) continue;
            var parts = h.Split('.');
            if (!byDomain.TryGetValue(h, out var hits) && !(parts.Length > 2 && byDomain.TryGetValue(string.Join('.', parts[^2..]), out hits))) continue;
            foreach (var b in hits) res.Add((a, b));
        }
        return res.OrderByDescending(x => x.Item2.BreachDate).ToList();   // la plus récente d'abord
    }

    private static string Squash(string s)
    {
        var f = (s ?? "").ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(f.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>Noms courants → nom ou domaine dans la liste des fuites.</summary>
    private static readonly (string Alias, string Target)[] ServiceAliases =
    {
        ("bouygues", "bouyguestelecom"), ("bbox", "bouyguestelecom"), ("laposte", "laposte"), ("poleemploi", "francetravail"),
        ("caf", "caf"), ("secu", "ameli"), ("securitesociale", "ameli"), ("impots", "impots"), ("fnac", "fnac"), ("darty", "darty"),
        ("facebook", "facebook"), ("insta", "instagram"), ("twitter", "twitter"), ("x", "twitter"),
    };

    /// <summary>
    /// Fuites d'un service dont on est (ou a été) client, d'après la liste Have I Been Pwned : correspondance sur le nom,
    /// le titre ou le domaine (sans accents, espaces ni majuscules), la plus récente d'abord. Fausses fuites et spam écartés.
    /// </summary>
    public static List<Breach> SearchService(IEnumerable<Breach> breaches, string query)
    {
        string q = Squash(query);
        if (q.Length < 2) return new();
        string target = ServiceAliases.FirstOrDefault(a => a.Alias == q).Target ?? q;
        var pool = breaches.Where(b => !b.IsFabricated && !b.IsSpamList).ToList();
        static string Root(Breach b) => Squash(b.Domain.Split('.').FirstOrDefault() ?? "");
        // 1. Correspondance exacte (nom, titre ou domaine) : « free » = Free, pas FreeOnes ni Freedom Hosting
        var exact = pool.Where(b => Root(b) == target || Squash(b.Name) == target || Squash(b.Title) == target).ToList();
        if (exact.Count > 0) return exact.OrderByDescending(b => b.Date).ToList();
        // 2. Sinon, un mot entier du titre, ou un début de domaine / titre assez long pour ne pas être ambigu
        return pool.Where(b => System.Text.RegularExpressions.Regex.Split(b.Title, @"[^\p{L}\p{N}]+").Any(w => Squash(w) == target)
                               || (target.Length >= 5 && (Root(b).StartsWith(target) || Squash(b.Title).StartsWith(target))))
                   .OrderByDescending(b => b.Date).ToList();
    }

    /// <summary>
    /// Fuites récentes (moins de <paramref name="months"/> mois, hors fausses fuites, listes de spam et fuites « sensibles »),
    /// les plus récentes d'abord : surtout des comptes liés à un e-mail, que la recherche par pseudo ne trouve pas.
    /// </summary>
    public static List<Breach> Recent(IEnumerable<Breach> breaches, int months, bool frenchOnly) =>
        breaches.Where(b => !b.IsFabricated && !b.IsSpamList && !b.IsSensitive && b.Date >= DateTime.Today.AddMonths(-months) && (!frenchOnly || b.IsFrench))
                .OrderByDescending(b => b.Date).ThenByDescending(b => b.PwnCount).ToList();

    // ─── 2. Fuites d'une adresse e-mail ───
    public static bool IsEmail(string s) => System.Text.RegularExpressions.Regex.IsMatch(s ?? "", @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    /// <summary>Page du site Have I Been Pwned (gratuite, sans clé) pour l'adresse.</summary>
    public static string WebCheckUrl(string email) => "https://haveibeenpwned.com/?email=" + Uri.EscapeDataString(email.Trim());

    /// <summary>Fuites contenant l'adresse (API v3, clé requise). null + message en cas d'erreur.</summary>
    public static Task<(List<Breach>? Breaches, string Error)> CheckEmailAsync(string email, string apiKey, CancellationToken ct = default)
        => IsEmail(email) ? CheckAccountAsync(email.Trim(), apiKey, ct) : Task.FromResult<(List<Breach>?, string)>((null, "adresse e-mail invalide"));

    /// <summary>Fuites contenant le numéro (API v3, clé requise ; Have I Been Pwned ne cherche les numéros que dans la fuite Facebook 2021).</summary>
    public static Task<(List<Breach>? Breaches, string Error)> CheckPhoneAsync(string normalizedFrPhone, string apiKey, CancellationToken ct = default)
        => CheckAccountAsync("33" + normalizedFrPhone[1..], apiKey, ct);

    private static async Task<(List<Breach>? Breaches, string Error)> CheckAccountAsync(string account, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://haveibeenpwned.com/api/v3/breachedaccount/{Uri.EscapeDataString(account)}?truncateResponse=false");
        req.Headers.TryAddWithoutValidation("hibp-api-key", apiKey.Trim());
        try
        {
            using var r = await Http.SendAsync(req, ct);
            return (int)r.StatusCode switch
            {
                200 => (ParseBreaches(await r.Content.ReadAsStringAsync(ct)), ""),
                404 => (new List<Breach>(), ""),
                401 => (null, "clé d'API refusée (vérifiez-la sur haveibeenpwned.com/API/Key)"),
                429 => (null, "trop de demandes : réessayez dans quelques secondes"),
                _ => (null, $"réponse inattendue (HTTP {(int)r.StatusCode})")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, ex is HttpRequestException or TaskCanceledException ? "Have I Been Pwned injoignable" : ex.Message); }
    }

    // Clé d'API : chiffrée pour l'utilisateur Windows (DPAPI), jamais en clair sur le disque
    public static void SaveApiKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, Dpapi(Encoding.UTF8.GetBytes(key.Trim()), protect: true));
    }
    public static string LoadApiKey()
    {
        try { return File.Exists(KeyPath) ? Encoding.UTF8.GetString(Dpapi(File.ReadAllBytes(KeyPath), protect: false)) : ""; }
        catch { return ""; }
    }
    public static void DeleteApiKey() { try { File.Delete(KeyPath); } catch { } }

    // ─── 3. Mot de passe compromis (k-anonymat) ───
    public static string Sha1Hex(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>
    /// Nombre d'apparitions du mot de passe dans les fuites connues (0 = jamais vu). Seuls les 5 premiers caractères
    /// de son empreinte SHA-1 sont envoyés (réponse complétée par du remplissage : « Add-Padding »).
    /// </summary>
    public static async Task<long> PasswordCountAsync(string password, CancellationToken ct = default)
    {
        string hash = Sha1Hex(password);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.pwnedpasswords.com/range/{hash[..5]}");
        req.Headers.TryAddWithoutValidation("Add-Padding", "true");
        using var r = await Http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        return CountInRange(await r.Content.ReadAsStringAsync(ct), hash[5..]);
    }

    internal static long CountInRange(string body, string suffix)
    {
        foreach (var line in body.Split('\n'))
        {
            var p = line.Trim().Split(':');
            if (p.Length == 2 && p[0].Equals(suffix, StringComparison.OrdinalIgnoreCase) && long.TryParse(p[1], out long n)) return n;
        }
        return 0;
    }

    // ─── Sur quels sites mon e-mail est inscrit (sources fiables, pas d'énumération façon holehe) ───

    /// <summary>Pages listant les sites où l'on s'est inscrit avec « Se connecter avec… ».</summary>
    public static readonly (string Label, string Url)[] SocialLoginPages =
    {
        ("Google : applis connectées", "https://myaccount.google.com/connections"),
        ("Microsoft : applis autorisées", "https://account.live.com/consent/Manage"),
        ("Apple : « Se connecter avec Apple »", "https://account.apple.com/account/manage"),
        ("Facebook : applis et sites", "https://www.facebook.com/settings?tab=applications"),
    };

    /// <summary>Requête de recherche des e-mails d'inscription (bienvenue, confirmation…), en français et en anglais.</summary>
    public const string SignupMailQuery =
        "bienvenue OR welcome OR \"confirmez votre adresse\" OR \"confirmer votre adresse\" OR \"vérifiez votre adresse\" OR \"verify your email\" OR \"confirm your email\" OR \"votre compte a été créé\" OR \"account created\" OR \"activate your account\" OR \"activez votre compte\"";

    public static string GmailSearchUrl(string email) =>
        "https://mail.google.com/mail/u/" + Uri.EscapeDataString(email.Trim()) + "/#search/" + Uri.EscapeDataString(SignupMailQuery);

    /// <summary>Profil Gravatar public de l'adresse (API publique, empreinte SHA-256 de l'adresse). null = aucun ; Error renseigné si échec.</summary>
    public static async Task<(string? ProfileUrl, string DisplayName, string Error)> GravatarAsync(string email, CancellationToken ct = default)
    {
        if (!IsEmail(email)) return (null, "", "adresse e-mail invalide");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant()))).ToLowerInvariant();
        try
        {
            using var r = await Http.GetAsync($"https://api.gravatar.com/v3/profiles/{hash}", ct);
            if (r.StatusCode == HttpStatusCode.NotFound) return (null, "", "");
            if (!r.IsSuccessStatusCode) return (null, "", $"Gravatar a répondu HTTP {(int)r.StatusCode}");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            string S(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return (S("profile_url").Length > 0 ? S("profile_url") : $"https://gravatar.com/{hash}", S("display_name"), "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, "", ex is HttpRequestException or TaskCanceledException ? "Gravatar injoignable" : ex.Message); }
    }

    // ─── Mon numéro de téléphone (son propre numéro uniquement : aucune recherche de comptes par numéro) ───

    /// <summary>
    /// Numéro français normalisé (10 chiffres commençant par 0) depuis « 06 12 34 56 78 », « +33 6 12… », « 0033… ».
    /// null si ce n'est pas un numéro français valide.
    /// </summary>
    public static string? NormalizeFrPhone(string input)
    {
        string d = new string((input ?? "").Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());
        if (d.StartsWith("+33")) d = "0" + d[3..];
        else if (d.StartsWith("0033")) d = "0" + d[4..];
        else if (d.StartsWith("33") && d.Length == 11) d = "0" + d[2..];
        if (d.Contains('+')) return null;
        if (d.StartsWith("00") || d.Length != 10 || d[0] != '0' || d[1] == '0') return null;
        return d;
    }

    /// <summary>Façons courantes d'écrire le numéro (annuaires, annonces, profils).</summary>
    public static List<string> PhoneFormats(string normalized)
    {
        string n = normalized, intl = n[1..];
        string Pairs(string s) => string.Join(" ", Enumerable.Range(0, s.Length / 2).Select(i => s.Substring(i * 2, 2)));
        return new List<string>
        {
            Pairs(n), n, string.Join(".", Pairs(n).Split(' ')), string.Join("-", Pairs(n).Split(' ')),
            $"+33 {intl[0]} {Pairs(intl[1..])}", $"+33{intl}", $"0033{intl}"
        }.Distinct().ToList();
    }

    /// <summary>Format international sans espace (+33612345678), celui qu'accepte Have I Been Pwned.</summary>
    public static string PhoneIntl(string normalized) => "+33" + normalized[1..];

    /// <summary>Fuites récentes dont les données comprennent des numéros de téléphone (ex. opérateurs, boutiques).</summary>
    public static List<Breach> RecentWithPhones(IEnumerable<Breach> breaches, int months, bool frenchOnly) =>
        Recent(breaches, months, frenchOnly).Where(b => b.DataClasses.Contains("Phone numbers")).ToList();

    /// <summary>Recherches web du numéro sous tous ses formats (entre guillemets).</summary>
    public static IEnumerable<(string Label, string Url)> PhoneSearches(string normalized)
    {
        var f = PhoneFormats(normalized);
        string q = string.Join(" OR ", f.Take(5).Select(x => $"\"{x}\""));
        yield return ("Google", "https://www.google.com/search?q=" + Uri.EscapeDataString(q));
        yield return ("Bing", "https://www.bing.com/search?q=" + Uri.EscapeDataString(q));
        yield return ("DuckDuckGo", "https://duckduckgo.com/?q=" + Uri.EscapeDataString(string.Join(" OR ", f.Take(3).Select(x => $"\"{x}\""))));
    }

    /// <summary>Comptes qui utilisent le numéro (connexion, récupération).</summary>
    public static readonly (string Label, string Url)[] PhoneAccountPages =
    {
        ("Google : informations personnelles", "https://myaccount.google.com/personal-info"),
        ("Microsoft : méthodes de vérification", "https://account.live.com/proofs/Manage"),
        ("Apple : connexion et sécurité", "https://account.apple.com/account/manage"),
    };

    /// <summary>Services officiels français : démarchage, arnaques par SMS / appel, assistance.</summary>
    public static readonly (string Label, string Url, string Tip)[] PhoneOfficial =
    {
        ("Bloctel (démarchage)", "https://www.bloctel.gouv.fr/", "Liste d'opposition officielle au démarchage téléphonique (gratuit)"),
        ("33700 (signaler un SMS ou un appel)", "https://www.33700.fr/", "Signaler un SMS ou un appel frauduleux (ou transférer le SMS au 33700)"),
        ("Cybermalveillance.gouv.fr", "https://www.cybermalveillance.gouv.fr/", "Assistance officielle en cas d'arnaque ou de piratage"),
    };

    // ─── 4. Son nom sur le web ───
    public static IEnumerable<(string Label, string Url)> NameSearches(string fullName, IEnumerable<string> usernames)
    {
        string q = $"\"{fullName.Trim()}\"";
        yield return ("Google", "https://www.google.com/search?q=" + Uri.EscapeDataString(q));
        yield return ("Bing", "https://www.bing.com/search?q=" + Uri.EscapeDataString(q));
        yield return ("DuckDuckGo", "https://duckduckgo.com/?q=" + Uri.EscapeDataString(q));
        yield return ("Google Images", "https://www.google.com/search?tbm=isch&q=" + Uri.EscapeDataString(q));
        var users = usernames.Take(3).ToList();
        if (users.Count > 0)
            yield return ("Nom + pseudos", "https://www.google.com/search?q=" + Uri.EscapeDataString(q + " (" + string.Join(" OR ", users.Select(u => $"\"{u}\"")) + ")"));
    }

    // ─── Agir : alertes, vérification des mots de passe enregistrés, demandes RGPD ───
    public const string MozillaMonitor = "https://monitor.mozilla.org/";
    public const string GooglePasswordCheckup = "https://passwords.google.com/checkup";
    public const string CnilComplaint = "https://www.cnil.fr/fr/plaintes";
    public const string CnilErasureModel = "https://www.cnil.fr/fr/modele/courrier/supprimer-des-informations-vous-concernant-dun-site-internet";

    /// <summary>Recherche web de l'adresse du délégué à la protection des données (DPO) d'un service.</summary>
    public static string DpoSearchUrl(string service) => "https://duckduckgo.com/?q=" + Uri.EscapeDataString($"{service} délégué à la protection des données contact DPO");

    /// <summary>
    /// Lettre de demande d'accès (RGPD art. 15) et d'effacement (art. 17), avec la fuite si elle est connue.
    /// Délai légal de réponse : un mois (art. 12) ; à défaut, plainte possible auprès de la CNIL.
    /// </summary>
    public static (string Subject, string Body) GdprRequest(string service, Breach? breach, string email)
    {
        string who = IsEmail(email) ? email.Trim() : "[votre adresse e-mail]";
        string context = breach != null
            ? $"Je suis ou j'ai été client(e) de {service}. À la suite de la fuite de données survenue en {breach.Date.ToString("MMMM yyyy", new System.Globalization.CultureInfo("fr-FR"))}, qui a concerné notamment les données suivantes : {breach.DataFr}, je vous demande, en application du Règlement général sur la protection des données (RGPD) :"
            : $"Je suis ou j'ai été client(e) de {service}. En application du Règlement général sur la protection des données (RGPD), je vous demande :";
        string body =
            "Madame, Monsieur,\r\n\r\n" + context + "\r\n\r\n" +
            "1. Article 15 (droit d'accès) : de me communiquer l'ensemble des données personnelles me concernant que vous détenez" +
            (breach != null ? ", en précisant celles qui ont été touchées par cette fuite ;" : " ;") + "\r\n" +
            "2. Article 17 (droit à l'effacement) : de supprimer l'ensemble de ces données ainsi que mon compte, sauf obligation légale de conservation que vous voudrez bien me préciser.\r\n\r\n" +
            $"Mon compte est associé à l'adresse e-mail : {who}.\r\n\r\n" +
            "Conformément à l'article 12 du RGPD, vous disposez d'un délai d'un mois pour répondre à ma demande. À défaut de réponse, je me réserve la possibilité de saisir la Commission nationale de l'informatique et des libertés (CNIL).\r\n\r\n" +
            "Je vous prie d'agréer, Madame, Monsieur, l'expression de mes salutations distinguées.\r\n\r\n[Prénom Nom]\r\n";
        return ($"Demande d'accès et d'effacement de mes données personnelles (RGPD, articles 15 et 17) — {service}", body);
    }

    public static string GdprMailTo(string to, (string Subject, string Body) letter) =>
        $"mailto:{Uri.EscapeDataString(to ?? "").Replace("%40", "@")}?subject={Uri.EscapeDataString(letter.Subject)}&body={Uri.EscapeDataString(letter.Body)}";

    /// <summary>Page d'obtention de la clé d'API (redirige vers le tableau de bord Have I Been Pwned après connexion).</summary>
    public const string ApiKeyPage = "https://haveibeenpwned.com/API/Key";

    /// <summary>Une clé d'API Have I Been Pwned : 32 caractères hexadécimaux (tolère espaces et retours copiés avec).</summary>
    public static string? ParseApiKey(string? text)
    {
        string k = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        return System.Text.RegularExpressions.Regex.IsMatch(k, "^[0-9a-fA-F]{32}$") ? k.ToLowerInvariant() : null;
    }

    public const string GoogleResultsAboutYou = "https://myactivity.google.com/results-about-you";
    public const string CnilDereferencing = "https://www.cnil.fr/fr/le-dereferencement-dun-contenu-dans-un-moteur-de-recherche";

    // ─── « Ce qui a fuité sur moi » : fuites regroupées par type d'information ───
    // Aucune recherche dans les données volées : on part des fuites qui concernent l'utilisateur (son e-mail, son numéro,
    // les services dont il est client, ses comptes trouvés) et de la liste publique des types de données de chaque fuite.

    public const string ServicePublicIdentityTheft = "https://www.service-public.gouv.fr/particuliers/vosdroits/F37944";
    public const string ServicePublicDirectDebit = "https://www.service-public.gouv.fr/particuliers/vosdroits/F20752";
    public const string BanqueFranceSepaFaq = "https://www.banque-france.fr/fr/foire-aux-questions-le-prelevement-sepa";
    public const string BanqueFranceFiles = "https://www.service-public.gouv.fr/particuliers/vosdroits/R62642";
    public const string CyberLeakSheet = "https://www.cybermalveillance.gouv.fr/tous-nos-contenus/fiches-reflexes/que-faire-en-cas-de-fuite-de-donnees-personnelles";
    public const string CyberFakeBanker = "https://www.cybermalveillance.gouv.fr/tous-nos-contenus/fiches-reflexes/fraude-faux-conseiller-bancaire";
    public const string CnilDirectory = "https://www.cnil.fr/fr/lannuaire-telephonique";
    public const string PagesBlanches = "https://www.pagesjaunes.fr/pagesblanches";

    /// <summary>Type d'information personnelle : classes Have I Been Pwned correspondantes, risque, conduite à tenir et liens.</summary>
    public sealed record LeakCategory(string Key, string Title, string[] Classes, string Level, string Risk, string Advice, (string Label, string Url, string Tip)[] Links);

    /// <summary>Une fuite qui concerne l'utilisateur, et par quoi on le sait (« votre e-mail », « client de Free »…).</summary>
    public sealed record LeakSource(Breach Breach, string Via);

    public static readonly LeakCategory[] LeakCategories =
    {
        new("bank", "Coordonnées bancaires (IBAN, carte)", new[] { "Bank account numbers", "Credit cards", "Partial credit card data", "Account balances", "Payment histories" }, "Danger",
            "Un IBAN suffit pour créer un faux mandat de prélèvement à votre nom ; il rend aussi crédibles les appels de « faux conseillers » de votre banque.",
            "Surveillez vos relevés ; contestez tout prélèvement inconnu (13 mois pour un prélèvement non autorisé) ; demandez à votre banque de limiter les prélèvements à une liste de créanciers autorisés (liste blanche) ou de bloquer ceux que vous ne connaissez pas ; ne validez jamais une opération demandée au téléphone, même par « votre banque ». Carte bancaire touchée : faites-la remplacer.",
            new[] { ("Contester un prélèvement (service-public)", ServicePublicDirectDebit, "Délais et démarche officiels"),
                    ("Liste blanche / noire des créanciers (Banque de France)", BanqueFranceSepaFaq, "Ce que votre banque doit vous proposer"),
                    ("Fraude au faux conseiller bancaire", CyberFakeBanker, "Reconnaître l'arnaque et réagir") }),
        new("ids", "Pièces d'identité", new[] { "Government issued IDs", "Passport numbers", "Driver's licenses", "Social security numbers", "National identity numbers" }, "Danger",
            "Une copie ou un numéro de pièce d'identité permet d'ouvrir des comptes, des crédits ou des abonnements à votre nom.",
            "Vérifiez qu'aucun crédit n'a été ouvert à votre nom (fichiers de la Banque de France, gratuit) ; en cas d'usurpation, portez plainte et prévenez vos banques ; renouvelez la pièce si elle est encore valide et que la fuite est récente.",
            new[] { ("Usurpation d'identité : que faire (service-public)", ServicePublicIdentityTheft, "Démarches officielles"),
                    ("Consulter mes fichiers Banque de France (FICP, FCC)", BanqueFranceFiles, "Vérifier qu'aucun crédit ou incident n'est à votre nom") }),
        new("passwords", "Mots de passe et questions de sécurité", new[] { "Passwords", "Historical passwords", "Password hints", "Security questions and answers", "Auth tokens" }, "Danger",
            "Les mots de passe fuités sont essayés automatiquement sur des milliers d'autres sites.",
            "Changez le mot de passe du service touché et partout où vous l'avez réutilisé ; activez la double authentification ; un gestionnaire de mots de passe évite la réutilisation.",
            new[] { ("Vérifier mes mots de passe enregistrés (Google)", GooglePasswordCheckup, "Mots de passe enregistrés dans votre compte Google") }),
        new("address", "Adresse postale", new[] { "Physical addresses", "Geographic locations", "Latitude and longitude pairs" }, "Warning",
            "Votre adresse rend crédibles les faux courriers, faux colis et faux avis de passage, et complète une usurpation d'identité.",
            "Méfiez-vous des courriers et SMS de livraison inattendus ; ne payez jamais de « frais de réexpédition » ; vérifiez ce qui est publié sur vous dans les annuaires (rubrique Adresse).",
            new[] { ("Fuite de données : que faire (Cybermalveillance)", CyberLeakSheet, "Fiche réflexe officielle") }),
        new("birth", "Date de naissance", new[] { "Dates of birth", "Ages" }, "Warning",
            "Associée à votre nom, elle sert à vous identifier auprès de services (banque, opérateur, administration) et à usurper votre identité.",
            "Ne l'utilisez jamais comme mot de passe ou code ; méfiez-vous des appels qui la citent pour gagner votre confiance.",
            Array.Empty<(string, string, string)>()),
        new("phone", "Numéro de téléphone", new[] { "Phone numbers" }, "Warning",
            "Démarchage, SMS frauduleux (colis, amendes, Ameli, CPF), appels de faux conseillers, tentative de vol de ligne.",
            "Ne cliquez sur aucun lien reçu par SMS ; ne donnez jamais un code reçu par SMS ; signalez les SMS au 33700.",
            new[] { ("Signaler un SMS ou un appel (33700)", "https://www.33700.fr/", "Service officiel de signalement") }),
        new("email", "Adresse e-mail", new[] { "Email addresses" }, "Warning",
            "Hameçonnage ciblé qui reprend vos informations pour paraître authentique.",
            "Ne cliquez pas sur les liens des e-mails qui reprennent vos informations ; connectez-vous toujours en tapant vous-même l'adresse du site.",
            Array.Empty<(string, string, string)>()),
        new("name", "Nom et prénom", new[] { "Names", "Salutations" }, "Warning",
            "Combiné aux autres informations de la même fuite, il permet de vous cibler personnellement.",
            "Voyez ce que les moteurs de recherche montrent de vous (rubrique Nom).",
            Array.Empty<(string, string, string)>()),
        new("private", "Informations privées", new[] { "Private messages", "Email messages", "Sexual orientations", "Religions", "Ethnicities", "Income levels", "Health records", "Family members' names", "Marital statuses", "Employers", "Job titles" }, "Warning",
            "Des informations intimes ou professionnelles peuvent servir au chantage ou à des arnaques ciblées.",
            "Ne répondez à aucune menace de chantage et ne payez pas ; conservez les preuves et signalez-le sur Cybermalveillance.",
            new[] { ("Fuite de données : que faire (Cybermalveillance)", CyberLeakSheet, "Fiche réflexe officielle") }),
    };

    /// <summary>Types d'information contenus dans une fuite (dans l'ordre de gravité de LeakCategories).</summary>
    public static List<LeakCategory> CategoriesOf(Breach b) => LeakCategories.Where(c => c.Classes.Any(b.DataClasses.Contains)).ToList();

    /// <summary>Nom + (date de naissance ou adresse) + (banque ou pièce d'identité) dans la même fuite : usurpation d'identité possible.</summary>
    public static bool IdentityTheftRisk(Breach b)
    {
        var k = CategoriesOf(b).Select(c => c.Key).ToHashSet();
        return k.Contains("name") && (k.Contains("birth") || k.Contains("address")) && (k.Contains("bank") || k.Contains("ids"));
    }

    /// <summary>Sources regroupées par fuite (plusieurs « via » fusionnés), les plus récentes d'abord.</summary>
    public static List<(Breach Breach, string Via)> MergeSources(IEnumerable<LeakSource> sources) =>
        sources.GroupBy(s => s.Breach.Name, StringComparer.OrdinalIgnoreCase)
               .Select(g => (g.First().Breach, string.Join(", ", g.Select(s => s.Via).Distinct())))
               .OrderByDescending(x => x.Item1.Date).ToList();

    /// <summary>Pour chaque type d'information, les fuites qui le contiennent (types sans fuite omis, ordre de gravité).</summary>
    public static List<(LeakCategory Category, List<(Breach Breach, string Via)> Sources)> GroupLeaks(IEnumerable<LeakSource> sources)
    {
        var merged = MergeSources(sources);
        return LeakCategories.Select(c => (c, merged.Where(m => c.Classes.Any(m.Breach.DataClasses.Contains)).ToList()))
                             .Where(x => x.Item2.Count > 0).ToList();
    }

    // ─── IBAN (vérifié localement, jamais envoyé ni enregistré) ───

    /// <summary>IBAN sans espaces ni tirets, en majuscules, si sa clé de contrôle (modulo 97) est juste ; sinon null.</summary>
    public static string? NormalizeIban(string input)
    {
        string s = new string((input ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        if (s.Length < 15 || s.Length > 34 || !char.IsLetter(s[0]) || !char.IsLetter(s[1]) || !char.IsDigit(s[2]) || !char.IsDigit(s[3]) || !s.All(char.IsAsciiLetterOrDigit)) return null;
        if (s.StartsWith("FR") && s.Length != 27) return null;
        int mod = 0;
        foreach (char c in s[4..] + s[..4])
            foreach (char d in char.IsDigit(c) ? c.ToString() : (c - 'A' + 10).ToString())
                mod = (mod * 10 + (d - '0')) % 97;
        return mod == 1 ? s : null;
    }

    /// <summary>« FR76 •••• •••• 1234 » : seule forme de l'IBAN affichée ou gardée en mémoire.</summary>
    public static string MaskIban(string iban) => iban.Length < 8 ? "••••" : iban[..4] + " •••• •••• " + iban[^4..];

    private static readonly Dictionary<string, string> IbanCountries = new()
    {
        ["FR"] = "France", ["BE"] = "Belgique", ["LU"] = "Luxembourg", ["CH"] = "Suisse", ["MC"] = "Monaco", ["DE"] = "Allemagne (ex. N26)",
        ["LT"] = "Lituanie (ex. Revolut)", ["ES"] = "Espagne", ["IT"] = "Italie", ["PT"] = "Portugal", ["NL"] = "Pays-Bas", ["IE"] = "Irlande", ["GB"] = "Royaume-Uni",
    };

    // Codes banque français les plus courants (5 chiffres après « FRkk ») ; les banques mutualistes en ont des centaines, non listées.
    private static readonly Dictionary<string, string> FrBanks = new()
    {
        ["30004"] = "BNP Paribas", ["30003"] = "Société Générale", ["30002"] = "LCL", ["20041"] = "La Banque Postale",
        ["40618"] = "BoursoBank", ["30066"] = "CIC", ["30056"] = "HSBC", ["30438"] = "ING",
    };

    public static string IbanCountry(string iban) => IbanCountries.TryGetValue(iban[..2], out var c) ? c : iban[..2];
    public static string? IbanBank(string iban) => iban.StartsWith("FR") && iban.Length == 27 && FrBanks.TryGetValue(iban.Substring(4, 5), out var b) ? b : null;

    // ─── Adresse postale : ce qui est publié (recherches lancées par l'utilisateur lui-même) ───
    public static IEnumerable<(string Label, string Url)> AddressSearches(string address, string city, string fullName)
    {
        static string G(string q) => "https://www.google.com/search?q=" + Uri.EscapeDataString(q);
        address = address.Trim(); city = city.Trim(); fullName = fullName.Trim();
        if (address.Length > 0) yield return ("Mon adresse sur Google", G($"\"{address}\"" + (city.Length > 0 ? $" \"{city}\"" : "")));
        if (fullName.Length > 0 && city.Length > 0) yield return ("Mon nom + ma ville sur Google", G($"\"{fullName}\" \"{city}\""));
        if (fullName.Length > 0 && address.Length > 0) yield return ("Mon nom + mon adresse sur Google", G($"\"{fullName}\" \"{address}\""));
    }

    // ─── DPAPI (CryptProtectData / CryptUnprotectData, portée : utilisateur courant) ───
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    internal static byte[] Dpapi(byte[] data, bool protect)
    {
        var inBlob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var outBlob = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);
            const int UiForbidden = 0x1;
            bool ok = protect ? CryptProtectData(ref inBlob, "AlexToolkit", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                              : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
