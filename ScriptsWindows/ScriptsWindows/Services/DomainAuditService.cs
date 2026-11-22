using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScriptsWindows.Services;

public enum DomainLevel { Ok, Warn, Fail, Info }

public sealed record DomainCheck(string Title, DomainLevel Level, string Verdict, string Detail, string Advice);

public sealed record Subdomain(string Name, bool Active, string Address, bool Sensitive);

/// <summary>
/// Audit d'un domaine (à faire sur SON domaine ou celui d'un client qui l'a demandé) : sous-domaines visibles publiquement
/// dans les journaux de certificats (crt.sh), puis protection des e-mails contre l'usurpation (SPF, DMARC, DKIM, MX).
/// Lectures DNS par DNS-over-HTTPS (Cloudflare, JSON) : .NET ne sait pas lire les enregistrements TXT.
/// Aucune collecte d'adresses e-mail ni de noms de personnes.
/// </summary>
public static class DomainAuditService
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AlexToolkit-DomainAudit");
        return c;
    }

    private static readonly Regex SensitiveSub = new(@"(^|[.-])(dev|test|tests|staging|stage|preprod|pre-prod|recette|qa|uat|demo|beta|old|ancien|backup|bak|admin|administration|intranet|internal|vpn|ftp|sql|db|phpmyadmin|jenkins|gitlab|git|grafana|kibana|webmail)([.-]|\d|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Domaine enregistrable à partir d'une adresse ou d'un nom (www. retiré).</summary>
    public static string? NormalizeDomain(string input)
    {
        string s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || !u.Host.Contains('.')) return null;
        string h = u.Host.ToLowerInvariant().TrimEnd('.');
        if (h.StartsWith("www.")) h = h[4..];
        return Regex.IsMatch(h, @"^[a-z0-9.-]+\.[a-z]{2,}$") && !IPAddress.TryParse(h, out _) ? h : null;
    }

    // ─── DNS (DoH JSON) ───
    internal static async Task<List<string>> DohAsync(string name, string type, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type={type}");
        req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
        using var r = await Http.SendAsync(req, ct);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("Answer", out var ans))
            foreach (var a in ans.EnumerateArray())
            {
                int t = a.GetProperty("type").GetInt32();
                if ((type == "TXT" && t == 16) || (type == "MX" && t == 15) || (type == "A" && t == 1) || (type == "AAAA" && t == 28) || (type == "CNAME" && t == 5))
                {
                    string data = a.GetProperty("data").GetString() ?? "";
                    if (type == "TXT") data = string.Concat(Regex.Matches(data, "\"((?:[^\"\\\\]|\\\\.)*)\"").Select(m => m.Groups[1].Value)) is { Length: > 0 } joined ? joined : data.Trim('"');
                    list.Add(data);
                }
            }
        return list;
    }

    // ─── Protection des e-mails ───
    public static async Task<List<DomainCheck>> EmailSecurityAsync(string domain, CancellationToken ct = default)
    {
        var res = new List<DomainCheck>();
        // « 0 . » = MX nul (RFC 7505) : le domaine déclare ne recevoir aucun e-mail
        var mx = (await DohAsync(domain, "MX", ct)).Where(m => m.Split(' ').Last().Trim().TrimEnd('.').Length > 0).ToList();
        var txt = await DohAsync(domain, "TXT", ct);
        var dmarc = (await DohAsync("_dmarc." + domain, "TXT", ct)).Where(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
        var spf = txt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();

        // MX
        var mxHosts = mx.Select(m => m.Split(' ').Last().TrimEnd('.').ToLowerInvariant()).ToList();
        string provider = mxHosts.Any(h => h.EndsWith("google.com") || h.EndsWith("googlemail.com")) ? "Google Workspace"
            : mxHosts.Any(h => h.EndsWith("outlook.com")) ? "Microsoft 365" : mxHosts.Any(h => h.Contains("ovh")) ? "OVH"
            : mxHosts.Any(h => h.Contains("ionos") || h.Contains("1and1")) ? "IONOS" : mxHosts.Any(h => h.Contains("gandi")) ? "Gandi"
            : mxHosts.Any(h => h.Contains("o2switch")) ? "o2switch" : mxHosts.Any(h => h.Contains("zoho")) ? "Zoho" : "";
        res.Add(mx.Count == 0
            ? new("Serveurs de messagerie (MX)", DomainLevel.Info, "Aucun", "Ce domaine ne reçoit pas d'e-mails.", "S'il n'envoie pas non plus d'e-mails, publiez « v=spf1 -all » et un DMARC « p=reject » pour empêcher toute usurpation.")
            : new("Serveurs de messagerie (MX)", DomainLevel.Info, provider.Length > 0 ? provider : $"{mx.Count} serveur(s)", string.Join(", ", mxHosts.Take(5)), ""));

        // SPF
        if (spf.Count == 0)
            res.Add(new("SPF (serveurs autorisés à envoyer)", DomainLevel.Fail, "Absent", "Aucun enregistrement SPF : n'importe quel serveur peut envoyer des e-mails au nom de ce domaine.",
                mx.Count == 0 ? "Publier « v=spf1 -all »." : "Publier un enregistrement SPF listant les serveurs d'envoi (ex. « v=spf1 include:_spf.google.com ~all »)."));
        else if (spf.Count > 1)
            res.Add(new("SPF (serveurs autorisés à envoyer)", DomainLevel.Fail, "Plusieurs enregistrements", string.Join(" | ", spf), "Un seul enregistrement SPF est autorisé : les fusionner, sinon le SPF est ignoré."));
        else
        {
            string s = spf[0];
            var (lvl, verdict, advice) = Regex.Match(s, @"([+~?-]?)all\b", RegexOptions.IgnoreCase) is { Success: true } m
                ? m.Groups[1].Value switch
                {
                    "-" => (DomainLevel.Ok, "Strict (-all)", ""),
                    "~" => (DomainLevel.Ok, "Présent (~all)", "Après vérification des envois, passer à « -all » pour un rejet strict."),
                    "?" => (DomainLevel.Warn, "Neutre (?all)", "« ?all » ne protège pas : utiliser « ~all » ou « -all »."),
                    _ => (DomainLevel.Fail, "Ouvert à tous (+all)", "« +all » autorise le monde entier à envoyer en votre nom : remplacer par « -all » ou « ~all »."),
                }
                : Regex.IsMatch(s, @"\bredirect=", RegexOptions.IgnoreCase)
                    ? (DomainLevel.Ok, "Délégué (redirect)", "")   // la règle finale est dans l'enregistrement désigné
                    : (DomainLevel.Warn, "Sans règle finale", "Terminer l'enregistrement par « ~all » ou « -all ».");
            int lookups = Regex.Matches(s, @"\b(include|a|mx|ptr|exists|redirect)[:=]?", RegexOptions.IgnoreCase).Count;
            if (lookups > 10) { lvl = DomainLevel.Warn; advice = $"{lookups} consultations DNS (limite : 10) : certains serveurs rejetteront le SPF. " + advice; }
            res.Add(new("SPF (serveurs autorisés à envoyer)", lvl, verdict, s, advice));
        }

        // DMARC
        if (dmarc.Count == 0)
            res.Add(new("DMARC (que faire des e-mails usurpés)", DomainLevel.Fail, "Absent", "Aucune politique DMARC : les e-mails usurpant ce domaine ne sont ni bloqués ni signalés.",
                "Publier sur « _dmarc." + domain + " » : « v=DMARC1; p=none; rua=mailto:dmarc@" + domain + " », surveiller quelques semaines, puis passer à « p=quarantine » ou « p=reject »."));
        else
        {
            string d = dmarc[0];
            string p = Regex.Match(d, @"\bp\s*=\s*(\w+)", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            bool rua = Regex.IsMatch(d, @"\brua\s*=", RegexOptions.IgnoreCase);
            res.Add(p switch
            {
                "reject" => new("DMARC (que faire des e-mails usurpés)", DomainLevel.Ok, "Rejet (p=reject)", d, rua ? "" : "Ajouter « rua=mailto:… » pour recevoir les rapports."),
                "quarantine" => new("DMARC (que faire des e-mails usurpés)", DomainLevel.Ok, "Quarantaine (p=quarantine)", d, "Quand tout est conforme, passer à « p=reject »."),
                "none" => new("DMARC (que faire des e-mails usurpés)", DomainLevel.Warn, "Surveillance seule (p=none)", d, "« p=none » ne bloque rien : passer à « p=quarantine » puis « p=reject » après lecture des rapports."),
                _ => new("DMARC (que faire des e-mails usurpés)", DomainLevel.Warn, "Politique invalide", d, "Indiquer « p=none », « p=quarantine » ou « p=reject »."),
            });
        }

        // DKIM : sélecteurs courants (un sélecteur inconnu peut exister ailleurs)
        static bool IsDkimKey(string t) => Regex.IsMatch(t, @"(^|;)\s*p=[A-Za-z0-9+/=]{40,}") && (t.Contains("v=DKIM1", StringComparison.OrdinalIgnoreCase) || t.Contains("k=", StringComparison.OrdinalIgnoreCase) || t.TrimStart().StartsWith("p="));
        // Domaine qui répond à n'importe quel nom (joker DNS) : un sélecteur inventé ne doit rien renvoyer
        bool wildcard = (await DohAsync($"zq{Guid.NewGuid():N}"[..14] + $"._domainkey.{domain}", "TXT", ct)).Any(IsDkimKey);
        var found = new List<string>();
        if (!wildcard)
            foreach (var sel in new[] { "google", "selector1", "selector2", "default", "k1", "k2", "mail", "dkim", "s1", "s2", "mailjet", "mandrill", "sendgrid", "ovh", "protonmail" })
                if ((await DohAsync($"{sel}._domainkey.{domain}", "TXT", ct)).Any(IsDkimKey)) found.Add(sel);
        if (wildcard)
            res.Add(new("DKIM (signature des e-mails)", DomainLevel.Info, "Non vérifiable", "Le domaine répond à tous les noms de sélecteur (joker DNS) : impossible de distinguer une vraie clé.", ""));
        else res.Add(found.Count > 0
            ? new("DKIM (signature des e-mails)", DomainLevel.Ok, $"{found.Count} clé(s) trouvée(s)", "Sélecteurs : " + string.Join(", ", found), "")
            : new("DKIM (signature des e-mails)", mx.Count == 0 ? DomainLevel.Info : DomainLevel.Warn, "Non trouvé", "Aucune clé parmi les sélecteurs courants (elle peut exister sous un autre nom).",
                  mx.Count == 0 ? "" : "Activer la signature DKIM chez l'hébergeur de messagerie (Google, Microsoft, OVH…)."));
        return res;
    }

    // ─── Sous-domaines (journaux de certificats publics) ───
    public static async Task<(List<Subdomain> Items, string Error)> SubdomainsAsync(string domain, CancellationToken ct = default)
    {
        List<string> names;
        try
        {
            string json = await Http.GetStringAsync($"https://crt.sh/?q={Uri.EscapeDataString("%." + domain)}&output=json", ct);
            names = ParseCrtSh(json, domain);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (new(), ex is TaskCanceledException ? "crt.sh n'a pas répondu à temps (service souvent surchargé) : réessayez plus tard" : "crt.sh indisponible : " + ex.Message); }
        var result = new System.Collections.Concurrent.ConcurrentBag<Subdomain>();
        await Parallel.ForEachAsync(names.Take(150), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (n, token) =>
        {
            string addr = "";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(TimeSpan.FromSeconds(5));
                var ips = await Dns.GetHostAddressesAsync(n, cts.Token);
                addr = ips.FirstOrDefault()?.ToString() ?? "";
            }
            catch { }
            result.Add(new Subdomain(n, addr.Length > 0, addr, SensitiveSub.IsMatch(n[..^(domain.Length)])));
        });
        return (result.OrderByDescending(s => s.Sensitive && s.Active).ThenByDescending(s => s.Active).ThenBy(s => s.Name).ToList(), names.Count > 150 ? $"{names.Count} noms trouvés, 150 vérifiés" : "");
    }

    internal static List<string> ParseCrtSh(string json, string domain)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.TryGetProperty("name_value", out var nv))
                foreach (var raw in (nv.GetString() ?? "").Split('\n'))
                {
                    string n = raw.Trim().ToLowerInvariant().TrimStart('*', '.');
                    if (n.Length > domain.Length && n.EndsWith("." + domain) && !n.Contains('@') && Regex.IsMatch(n, @"^[a-z0-9.-]+$")) set.Add(n);
                }
        return set.OrderBy(n => n).ToList();
    }
}
