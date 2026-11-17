using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ScriptsWindows.Services;

/// <summary>
/// Détection des fichiers sensibles d'un profil : noms de fichiers (≈ 200 mots-clés en 8 catégories),
/// extensions de secrets, fichiers de configuration connus, et contenu des fichiers texte
/// (mots de passe en clair, IBAN, cartes bancaires, n° de sécurité sociale, clés privées, clés d'API)
/// avec validation mathématique pour limiter les faux positifs.
/// </summary>
public static class SensitiveDataScanner
{
    public record Finding(string Category, string Path, string Details, string Icon);

    // ── Mots-clés par catégorie (écrits sans accents : les noms de fichiers sont normalisés) ──
    private static readonly (string Category, string Icon, string[] Keywords)[] NameRules =
    {
        ("Mots de passe et accès", "🔑", new[]
        {
            "password", "passwords", "passwd", "pwd", "mot de passe", "mots de passe", "motdepasse", "mdp", "mdps",
            "identifiant", "identifiants", "login", "logins", "credential", "credentials", "secret", "secrets",
            "code acces", "codes acces", "code d acces", "code pin", "pin", "puk", "keepass", "bitwarden", "lastpass",
            "1password", "dashlane", "nordpass", "authenticator", "2fa", "otp", "backup codes", "codes de secours",
            "recovery codes", "code de recuperation", "cle de recuperation", "recovery key", "bitlocker", "wifi", "wi fi",
            "compte google", "compte apple", "compte microsoft", "acces"
        }),
        ("Banque et finances", "💳", new[]
        {
            "iban", "rib", "bic", "swift", "carte bancaire", "cartes bancaires", "cb", "visa", "mastercard", "amex", "cvv",
            "cryptogramme", "banque", "bancaire", "releve", "releves", "releve de compte", "releve bancaire", "virement",
            "pret", "emprunt", "credit immobilier", "offre de pret", "tableau d amortissement", "avis d imposition",
            "avis d impot", "impot", "impots", "declaration de revenus", "revenus", "fiche de paie", "fiches de paie",
            "bulletin de paie", "bulletin de salaire", "bulletins de salaire", "salaire", "payslip", "epargne", "livret a",
            "assurance vie", "pea", "bourse", "patrimoine", "bilan comptable", "comptabilite"
        }),
        ("Pièces d'identité et administratif", "🪪", new[]
        {
            "carte d identite", "carte identite", "cni", "carte nationale", "piece d identite", "pieces d identite",
            "passeport", "passport", "permis de conduire", "permis", "titre de sejour", "carte de sejour", "visa long sejour",
            "acte de naissance", "extrait de naissance", "livret de famille", "acte de mariage", "justificatif de domicile",
            "justificatif domicile", "carte vitale", "securite sociale", "secu", "numero de securite sociale", "nir",
            "attestation de droits", "caf", "france travail", "pole emploi", "avis de situation", "mutuelle",
            "carte grise", "certificat d immatriculation", "ameli", "urssaf", "retraite", "identite"
        }),
        ("Santé", "🩺", new[]
        {
            "dossier medical", "medical", "medicale", "ordonnance", "ordonnances", "analyses", "resultats d analyse",
            "bilan sanguin", "compte rendu medical", "radiologie", "radio", "irm", "certificat medical", "arret de travail",
            "hospitalisation", "vaccination", "carnet de sante", "handicap", "mdph"
        }),
        ("Contrats et juridique", "📜", new[]
        {
            "contrat", "contrats", "bail", "compromis", "acte de vente", "notaire", "testament", "succession", "heritage",
            "jugement", "divorce", "procuration", "statuts", "kbis", "avocat", "plainte", "litige", "rupture conventionnelle",
            "licenciement", "contrat de travail", "avenant", "confidentiel", "confidentielle", "personnel", "prive"
        }),
        ("Licences et clés produit", "🏷️", new[]
        {
            "licence", "licences", "license", "licenses", "serial", "numero de serie", "product key", "cle produit",
            "cle de licence", "cle d activation", "activation", "keygen", "cd key"
        }),
        ("Crypto-monnaies", "🪙", new[]
        {
            "seed", "seed phrase", "phrase de recuperation", "phrase secrete", "mnemonic", "wallet", "portefeuille",
            "bitcoin", "btc", "ethereum", "eth", "metamask", "ledger", "trezor", "crypto", "binance", "coinbase"
        }),
        ("Accès techniques et serveurs", "🖥️", new[]
        {
            "api key", "apikey", "api", "token", "tokens", "access key", "private key", "privatekey", "ssh", "vpn", "rdp",
            "ftp", "sftp", "root", "admin", "administrateur", "connection string", "database", "db", "serveur", "server",
            "nas", "box", "routeur", "router"
        }),
    };

    // Mots courts ou ambigus : ne correspondent qu'à un mot entier (« rib » ≠ « attribut »)
    private static readonly HashSet<string> WholeWordOnly = new(StringComparer.Ordinal)
    {
        "pin", "puk", "cb", "rib", "bic", "mdp", "mdps", "cni", "nir", "caf", "otp", "2fa", "cvv", "pwd", "ssh", "vpn", "rdp",
        "ftp", "sftp", "pea", "btc", "eth", "irm", "db", "nas", "box", "api", "root", "visa", "amex", "secu", "radio", "seed",
        "permis", "acces", "prive", "personnel", "revenus", "admin", "server", "serveur", "routeur", "router", "ledger", "crypto",
        "wallet", "token", "tokens", "login", "logins", "secret", "secrets", "retraite", "identite", "activation", "serial"
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".doc", ".docx", ".odt", ".rtf", ".pdf", ".xls", ".xlsx", ".xlsm", ".ods", ".csv", ".md", ".json", ".xml",
        ".ini", ".cfg", ".conf", ".yml", ".yaml", ".one", ".msg", ".eml", ".pages", ".numbers", ".ppt", ".pptx", ".odp"
    };

    // Scans de documents (carte d'identité, RIB, ordonnance…)
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tif", ".tiff", ".bmp", ".webp"
    };

    // Fichiers secrets par nature, quel que soit leur nom
    private static readonly Dictionary<string, (string Category, string Icon)> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".kdbx"] = ("Coffre-fort de mots de passe", "🔒"), [".kdb"] = ("Coffre-fort de mots de passe", "🔒"),
        [".psafe3"] = ("Coffre-fort de mots de passe", "🔒"), [".1pif"] = ("Coffre-fort de mots de passe", "🔒"),
        [".opvault"] = ("Coffre-fort de mots de passe", "🔒"), [".agilekeychain"] = ("Coffre-fort de mots de passe", "🔒"),
        [".pem"] = ("Clé / certificat cryptographique", "🔐"), [".key"] = ("Clé / certificat cryptographique", "🔐"),
        [".pfx"] = ("Clé / certificat cryptographique", "🔐"), [".p12"] = ("Clé / certificat cryptographique", "🔐"),
        [".ppk"] = ("Clé / certificat cryptographique", "🔐"), [".asc"] = ("Clé / certificat cryptographique", "🔐"),
        [".gpg"] = ("Clé / certificat cryptographique", "🔐"), [".pgp"] = ("Clé / certificat cryptographique", "🔐"),
        [".jks"] = ("Clé / certificat cryptographique", "🔐"), [".keystore"] = ("Clé / certificat cryptographique", "🔐"),
        [".ovpn"] = ("Accès techniques et serveurs", "🖥️"), [".rdp"] = ("Accès techniques et serveurs", "🖥️"),
        [".wallet"] = ("Crypto-monnaies", "🪙"),
    };

    private static readonly HashSet<string> SecretFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallet.dat", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "credentials", ".git-credentials", ".netrc", "_netrc",
        ".pgpass", ".pypirc", ".npmrc", "sitemanager.xml", "recentservers.xml", "winscp.ini", "filezilla.xml", "keychain.json"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".env", ".ps1", ".bat", ".cmd",
        ".sh", ".sql", ".properties", ".config", ".toml", ".rtf", ".log"
    };

    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "bin", "obj", "packages", "$recycle.bin", "appdata", "venv", ".venv", "env",
        "__pycache__", ".cache", ".gradle", ".m2", ".nuget", ".vs", ".idea", "site-packages", "dist", "build", "target"
    };

    // ── Motifs recherchés dans le contenu des fichiers texte ──
    private static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex RxPassword = new(@"(?i)\b(mot\s*de\s*passe|mdp|password|passwd|pwd|pass)\s*[:=]\s*[""']?([^\s""',;]{4,})", RegexOptions.Compiled, RxTimeout);
    private static readonly Regex RxIban = new(@"\b([A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){2,7}(?:\s?[A-Z0-9]{1,4})?)\b", RegexOptions.Compiled, RxTimeout);
    private static readonly Regex RxCard = new(@"\b(?:\d[ -]?){12,18}\d\b", RegexOptions.Compiled, RxTimeout);
    private static readonly Regex RxNir = new(@"\b([12])\s?(\d{2})\s?(0[1-9]|1[0-2])\s?(\d{2}|2[AB])\s?(\d{3})\s?(\d{3})\s?(\d{2})\b", RegexOptions.Compiled, RxTimeout);
    private static readonly Regex RxPrivateKey = new(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY(?: BLOCK)?-----", RegexOptions.Compiled, RxTimeout);
    private static readonly (string Label, Regex Rx)[] ApiKeys =
    {
        ("Clé AWS", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled, RxTimeout)),
        ("Clé Google", new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled, RxTimeout)),
        ("Jeton GitHub", new Regex(@"\b(?:ghp|gho|ghs|ghu|github_pat)_[A-Za-z0-9_]{30,}\b", RegexOptions.Compiled, RxTimeout)),
        ("Clé Stripe", new Regex(@"\b(?:sk|rk)_live_[0-9a-zA-Z]{20,}\b", RegexOptions.Compiled, RxTimeout)),
        ("Jeton Slack", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled, RxTimeout)),
        ("Clé « sk- » (IA, paiement…)", new Regex(@"\bsk-(?:proj-|ant-)?[A-Za-z0-9_\-]{32,}\b", RegexOptions.Compiled, RxTimeout)),
        ("Jeton JWT", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled, RxTimeout)),
    };

    private const long MaxContentBytes = 2 * 1024 * 1024;
    private const int MaxDepth = 6;

    /// <summary>Normalise un nom : minuscules, sans accents, séparateurs → espaces, CamelCase séparé.</summary>
    public static string Normalize(string name)
    {
        name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return " " + Regex.Replace(sb.ToString(), @"\s+", " ").Trim() + " ";
    }

    /// <summary>Catégorie du nom de fichier (première règle qui correspond) et mot-clé déclencheur.</summary>
    public static (string Category, string Icon, string Keyword)? MatchName(string fileNameWithoutExtension)
    {
        string n = Normalize(fileNameWithoutExtension);
        foreach (var (cat, icon, words) in NameRules)
            foreach (var w in words)
            {
                bool hit = WholeWordOnly.Contains(w) || w.Contains(' ')
                    ? n.Contains(" " + w + " ", StringComparison.Ordinal)
                    : n.Contains(w, StringComparison.Ordinal);
                if (hit) return (cat, icon, w);
            }
        return null;
    }

    /// <param name="progress">Messages d'étape (dossier racine analysé).</param>
    /// <param name="onDirectory">Appelé pour chaque dossier parcouru (suivi détaillé ; appelé hors thread UI).</param>
    public static List<Finding> Scan(Action<string>? progress, CancellationToken ct, Action<string>? onDirectory = null)
    {
        _onDirectory = onDirectory;
        try { return ScanCore(progress, ct); }
        finally { _onDirectory = null; }
    }

    [ThreadStatic] private static Action<string>? _onDirectory;

    private static List<Finding> ScanCore(Action<string>? progress, CancellationToken ct)
    {
        var findings = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        void Add(string category, string path, string details, string icon)
        {
            if (seen.Add(category + "|" + path)) findings.Add(new Finding(category, path, details, icon));
        }
        static string Info(FileInfo fi) => $"{Math.Round(fi.Length / 1024.0, 1)} Ko, modifié le {fi.LastWriteTime:dd/MM/yyyy HH:mm}";

        // 1. Clés SSH et configurations d'outils (identifiants stockés en clair)
        string ssh = Path.Combine(profile, ".ssh");
        if (Directory.Exists(ssh))
            foreach (var f in SafeFiles(ssh))
            {
                var fi = new FileInfo(f);
                bool priv = fi.Name.StartsWith("id_", StringComparison.OrdinalIgnoreCase) && !fi.Name.EndsWith(".pub", StringComparison.OrdinalIgnoreCase);
                Add(priv ? "Clé privée SSH (critique)" : "Configuration / clé SSH", fi.FullName, Info(fi), priv ? "🔴" : "🔑");
            }

        var knownConfigs = new (string Path, string Label)[]
        {
            (Path.Combine(profile, @".aws\credentials"), "Identifiants AWS"),
            (Path.Combine(profile, @".azure\accessTokens.json"), "Jetons Azure"),
            (Path.Combine(profile, @".azure\msal_token_cache.json"), "Jetons Azure"),
            (Path.Combine(profile, @".kube\config"), "Accès Kubernetes"),
            (Path.Combine(profile, @".docker\config.json"), "Identifiants Docker"),
            (Path.Combine(profile, @".config\gh\hosts.yml"), "Jeton GitHub CLI"),
            (Path.Combine(profile, ".git-credentials"), "Identifiants Git en clair"),
            (Path.Combine(profile, ".netrc"), "Identifiants réseau en clair"),
            (Path.Combine(profile, "_netrc"), "Identifiants réseau en clair"),
            (Path.Combine(profile, ".npmrc"), "Jeton npm"),
            (Path.Combine(profile, ".pypirc"), "Identifiants PyPI"),
            (Path.Combine(profile, ".pgpass"), "Mots de passe PostgreSQL"),
            (Path.Combine(roaming, @"FileZilla\sitemanager.xml"), "Mots de passe FTP FileZilla (encodés, lisibles)"),
            (Path.Combine(roaming, @"FileZilla\recentservers.xml"), "Mots de passe FTP FileZilla (encodés, lisibles)"),
            (Path.Combine(roaming, "WinSCP.ini"), "Sessions WinSCP"),
        };
        foreach (var (p, label) in knownConfigs)
            if (File.Exists(p)) Add($"Accès techniques : {label}", p, Info(new FileInfo(p)), "☁️");
        if (Directory.Exists(Path.Combine(profile, ".gnupg"))) Add("Clé / certificat cryptographique", Path.Combine(profile, ".gnupg"), "Trousseau de clés GnuPG", "🔐");

        // 2. Dossiers de l'utilisateur (OneDrive et redirections comprises, sans doublon)
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            DownloadsFolder(),
        };
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } od) roots.Add(od);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            progress?.Invoke($"[i] Analyse de {root}…");
            ScanDir(root, 0, visited, Add, ct);
        }

        // Fichiers posés directement à la racine du profil
        foreach (var f in SafeFiles(profile)) CheckFile(new FileInfo(f), Add, ct);

        return findings;
    }

    private static void ScanDir(string dir, int depth, HashSet<string> visited, Action<string, string, string, string> add, CancellationToken ct)
    {
        if (depth > MaxDepth || ct.IsCancellationRequested || !visited.Add(dir)) return;
        string name = Path.GetFileName(dir);
        if (depth > 0 && (SkippedDirs.Contains(name) || (name.StartsWith('.') && name is not ".ssh" and not ".aws"))) return;
        _onDirectory?.Invoke(dir);

        foreach (var f in SafeFiles(dir))
        {
            if (ct.IsCancellationRequested) return;
            CheckFile(new FileInfo(f), add, ct);
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                try { if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
                ScanDir(sub, depth + 1, visited, add, ct);
            }
        }
        catch { /* dossier inaccessible */ }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); }
    }

    private static void CheckFile(FileInfo fi, Action<string, string, string, string> add, CancellationToken ct)
    {
        string ext = fi.Extension;
        string info;
        try { info = $"{Math.Round(fi.Length / 1024.0, 1)} Ko, modifié le {fi.LastWriteTime:dd/MM/yyyy HH:mm}"; } catch { return; }

        // a. Fichiers secrets par nature
        if (fi.Name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            add("Secrets applicatifs (.env)", fi.FullName, info, "⚙️");
        else if (SecretFileNames.Contains(fi.Name))
            add("Accès techniques et serveurs", fi.FullName, $"Fichier d'identifiants « {fi.Name} » • {info}", "🖥️");
        else if (SecretExtensions.TryGetValue(ext, out var se))
            add(se.Category, fi.FullName, info, se.Icon);

        // b. Nom de fichier évocateur (documents et scans)
        bool isDoc = DocExtensions.Contains(ext), isImage = ImageExtensions.Contains(ext);
        if ((isDoc || isImage) && MatchName(Path.GetFileNameWithoutExtension(fi.Name)) is { } m)
            add(m.Category, fi.FullName, $"Nom contenant « {m.Keyword} »{(isImage ? " (image / scan)" : "")} • {info}", m.Icon);

        // c. Contenu des fichiers texte
        if (TextExtensions.Contains(ext) || fi.Name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            foreach (var (label, detail) in ScanContent(fi, ct))
                add($"Contenu : {label}", fi.FullName, $"{detail} • {info}", "📄");
    }

    /// <summary>Recherche dans le contenu d'un fichier texte ; les valeurs trouvées sont masquées dans le rapport.</summary>
    public static IEnumerable<(string Label, string Detail)> ScanContent(FileInfo fi, CancellationToken ct)
    {
        string text;
        try
        {
            if (fi.Length == 0 || fi.Length > MaxContentBytes || ct.IsCancellationRequested) yield break;
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = sr.ReadToEnd();
        }
        catch { yield break; }
        if (text.IndexOf('\0') >= 0) yield break; // fichier binaire

        var results = new List<(string, string)>();
        try
        {
            var pw = RxPassword.Match(text);
            if (pw.Success) results.Add(("mot de passe en clair", $"« {pw.Groups[1].Value} : {Mask(pw.Groups[2].Value)} » ligne {Line(text, pw.Index)}"));

            foreach (Match iban in RxIban.Matches(text))
                if (IsValidIban(iban.Value)) { results.Add(("IBAN", $"{Mask(iban.Value.Replace(" ", ""))} ligne {Line(text, iban.Index)}")); break; }

            foreach (Match card in RxCard.Matches(text))
            {
                string digits = new(card.Value.Where(char.IsDigit).ToArray());
                if (digits.Length is >= 13 and <= 19 && IsLikelyCard(digits) && Luhn(digits))
                { results.Add(("numéro de carte bancaire", $"{Mask(digits)} ligne {Line(text, card.Index)}")); break; }
            }

            foreach (Match nir in RxNir.Matches(text))
                if (IsValidNir(nir)) { results.Add(("n° de sécurité sociale", $"{Mask(new string(nir.Value.Where(char.IsLetterOrDigit).ToArray()))} ligne {Line(text, nir.Index)}")); break; }

            var pk = RxPrivateKey.Match(text);
            if (pk.Success) results.Add(("clé privée", $"bloc « PRIVATE KEY » ligne {Line(text, pk.Index)}"));

            foreach (var (label, rx) in ApiKeys)
            {
                var k = rx.Match(text);
                if (k.Success) results.Add(($"clé d'API ({label})", $"{Mask(k.Value)} ligne {Line(text, k.Index)}"));
            }
        }
        catch (RegexMatchTimeoutException) { }

        foreach (var r in results) yield return r;
    }

    /// <summary>
    /// Valeurs sensibles d'un texte, <b>en clair</b> (pour l'extraction demandée par l'utilisateur sur ses propres fichiers).
    /// Contrairement à <see cref="ScanContent"/>, rien n'est masqué.
    /// </summary>
    public static List<(string Label, string Value, int Line)> ExtractFromText(string text)
    {
        var results = new List<(string, string, int)>();
        if (string.IsNullOrEmpty(text) || text.IndexOf('\0') >= 0) return results;
        try
        {
            foreach (Match pw in RxPassword.Matches(text))
                results.Add(($"Mot de passe ({pw.Groups[1].Value.Trim()})", pw.Groups[2].Value, Line(text, pw.Index)));

            foreach (Match iban in RxIban.Matches(text))
                if (IsValidIban(iban.Value)) results.Add(("IBAN", iban.Value.Trim(), Line(text, iban.Index)));

            foreach (Match card in RxCard.Matches(text))
            {
                string digits = new(card.Value.Where(char.IsDigit).ToArray());
                if (digits.Length is >= 13 and <= 19 && IsLikelyCard(digits) && Luhn(digits))
                    results.Add(("Carte bancaire", digits, Line(text, card.Index)));
            }

            foreach (Match nir in RxNir.Matches(text))
                if (IsValidNir(nir)) results.Add(("N° de sécurité sociale", new string(nir.Value.Where(char.IsLetterOrDigit).ToArray()), Line(text, nir.Index)));

            var pk = RxPrivateKey.Match(text);
            if (pk.Success) results.Add(("Clé privée", "(bloc PRIVATE KEY — contenu complet dans le fichier d'origine)", Line(text, pk.Index)));

            foreach (var (label, rx) in ApiKeys)
                foreach (Match k in rx.Matches(text))
                    results.Add(($"Clé d'API ({label})", k.Value, Line(text, k.Index)));
        }
        catch (RegexMatchTimeoutException) { }

        // Dédoublonnage (une même valeur peut être captée par plusieurs motifs)
        return results.GroupBy(r => r.Item1 + "|" + r.Item2).Select(g => g.First()).OrderBy(r => r.Item3).ToList();
    }

    /// <summary>Lit un fichier comme texte s'il est lisible (pas binaire, taille raisonnable).</summary>
    private static bool TryReadText(string path, out string text)
    {
        text = "";
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length == 0 || fi.Length > MaxContentBytes) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string content = sr.ReadToEnd();
            if (content.IndexOf('\0') >= 0) return false;
            text = content;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Rassemble, dans un seul fichier texte, les valeurs sensibles <b>en clair</b> extraites des fichiers trouvés,
    /// pour que l'utilisateur les consulte sans ouvrir chaque fichier. Les fichiers non lisibles automatiquement
    /// (documents, scans, coffres-forts) sont listés comme « à ouvrir manuellement ».
    /// </summary>
    public static (string Text, int Extracted, int Manual) BuildExtraction(IReadOnlyList<Finding> items, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===========================================================");
        sb.AppendLine($" EXTRACTION DES INFORMATIONS CONFIDENTIELLES — {Environment.MachineName}");
        sb.AppendLine($" {DateTime.Now:dd/MM/yyyy HH:mm} — Alex Toolkit");
        sb.AppendLine(" ATTENTION : ce fichier contient des mots de passe et des clés EN CLAIR.");
        sb.AppendLine(" Conservez-le en lieu sûr et supprimez-le dès qu'il n'est plus utile.");
        sb.AppendLine("===========================================================");
        sb.AppendLine();

        int extracted = 0, manual = 0;
        // Un fichier par bloc (dédoublonné), rangé du plus grave au moins grave
        var byFile = items
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Level: g.Min(i => GetLevel(i.Category))))
            .OrderBy(f => f.Level).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (path, level) in byFile)
        {
            if (ct.IsCancellationRequested) break;
            sb.AppendLine($"[{LevelFolderName(level)[4..].ToUpperInvariant()}]");
            sb.AppendLine(path);

            if (!File.Exists(path))
            {
                sb.AppendLine(Directory.Exists(path) ? "   → Dossier (à ouvrir manuellement)." : "   → Fichier introuvable.");
            }
            else if (TryReadText(path, out string text))
            {
                var hits = ExtractFromText(text);
                if (hits.Count > 0)
                {
                    foreach (var (label, value, line) in hits)
                        sb.AppendLine($"   • {label} (ligne {line}) : {value}");
                    extracted++;
                }
                else
                {
                    sb.AppendLine("   → Texte sans secret reconnu automatiquement — à ouvrir pour vérifier.");
                    manual++;
                }
            }
            else
            {
                sb.AppendLine("   → Document, scan ou fichier protégé — à ouvrir manuellement.");
                manual++;
            }
            sb.AppendLine();
        }

        sb.AppendLine("===========================================================");
        sb.AppendLine($" {extracted} fichier(s) avec des valeurs extraites, {manual} à ouvrir manuellement.");
        return (sb.ToString(), extracted, manual);
    }

    // ─── Classement et actions (fenêtre « Documents et codes confidentiels ») ───

    public enum Level { Critical, Personal, Technical }

    /// <summary>
    /// Gravité d'un résultat : secret lisible en clair (critique), document personnel (identité, banque, santé…)
    /// ou accès technique (clés, certificats, configurations).
    /// </summary>
    public static Level GetLevel(string category)
    {
        if (category.StartsWith("Contenu :", StringComparison.Ordinal)
            || category.StartsWith("Clé privée", StringComparison.Ordinal)
            || category.StartsWith("Secrets applicatifs", StringComparison.Ordinal)
            || category.StartsWith("Accès techniques :", StringComparison.Ordinal))
            return Level.Critical;
        if (category.StartsWith("Accès techniques", StringComparison.Ordinal)
            || category.StartsWith("Clé / certificat", StringComparison.Ordinal)
            || category.StartsWith("Configuration / clé SSH", StringComparison.Ordinal)
            || category.StartsWith("Coffre-fort", StringComparison.Ordinal))
            return Level.Technical;
        return Level.Personal;
    }

    /// <summary>Dossier de niveau (numéroté pour rester trié du plus grave au moins grave dans l'Explorateur).</summary>
    public static string LevelFolderName(Level level) => level switch
    {
        Level.Critical => "1 - Secrets en clair",
        Level.Personal => "2 - Documents personnels",
        _ => "3 - Accès techniques",
    };

    /// <summary>
    /// Copie les fichiers dans <paramref name="targetDir"/>, rangés par niveau puis par catégorie
    /// (« 1 - Secrets en clair\Mot de passe en clair\… »). Un fichier repéré plusieurs fois n'est copié
    /// qu'une fois, dans son niveau le plus grave ; les noms en double sont renumérotés.
    /// </summary>
    public static (int Copied, List<string> Errors) CopyFindings(IEnumerable<Finding> items, string targetDir, CancellationToken ct)
    {
        int copied = 0;
        var errors = new List<string>();
        var invalid = Path.GetInvalidFileNameChars();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OrderBy(i => GetLevel(i.Category)))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (!File.Exists(item.Path) || !done.Add(item.Path)) continue; // dossier (ex. .gnupg) ou déjà copié
                string label = item.Category.StartsWith("Contenu : ", StringComparison.Ordinal) ? item.Category["Contenu : ".Length..] : item.Category;
                // « Clé / certificat » → « Clé - certificat » (caractères interdits dans un nom de dossier)
                string cat = Regex.Replace(new string(label.Select(c => invalid.Contains(c) ? '-' : c).ToArray()), @"\s+", " ").Trim();
                if (cat.Length > 0) cat = char.ToUpper(cat[0]) + cat[1..];
                string catDir = Path.Combine(targetDir, LevelFolderName(GetLevel(item.Category)), cat.Length > 0 ? cat : "Autres");
                Directory.CreateDirectory(catDir);

                string name = Path.GetFileNameWithoutExtension(item.Path), ext = Path.GetExtension(item.Path);
                string dest = Path.Combine(catDir, name + ext);
                for (int i = 1; File.Exists(dest); i++) dest = Path.Combine(catDir, $"{name}_{i}{ext}");

                using (var src = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
                    src.CopyTo(dst);
                copied++;
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(item.Path)} : {ex.Message}"); }
        }
        return (copied, errors);
    }

    /// <summary>Envoie un fichier dans la Corbeille (récupérable), sans boîte de confirmation Windows.</summary>
    public static bool MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x0003,                    // FO_DELETE
            pFrom = path + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>Rapport HTML imprimable (valeurs déjà masquées par l'analyse : aucun secret n'y figure en clair).</summary>
    public static string BuildHtmlReport(IReadOnlyList<Finding> items)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        int crit = items.Count(i => GetLevel(i.Category) == Level.Critical);
        int pers = items.Count(i => GetLevel(i.Category) == Level.Personal);
        int tech = items.Count - crit - pers;

        sb.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><title>Documents et codes confidentiels</title><style>")
          .Append("body{font-family:'Segoe UI',sans-serif;margin:32px;color:#1f2328}h1{font-size:22px;margin:0}")
          .Append(".sub{color:#59636e;margin:4px 0 20px}.kpi{display:flex;gap:12px;margin-bottom:24px}")
          .Append(".k{border:1px solid #d1d9e0;border-radius:8px;padding:10px 16px;min-width:140px}.k b{display:block;font-size:22px}")
          .Append("h2{font-size:15px;margin:22px 0 6px}table{border-collapse:collapse;width:100%;font-size:12px}")
          .Append("td,th{border-bottom:1px solid #e5e9ed;padding:6px 8px;text-align:left;vertical-align:top}th{color:#59636e;font-weight:600}")
          .Append(".p{font-family:Consolas,monospace;word-break:break-all}.c{color:#cf222e}.w{color:#9a6700}.t{color:#0969da}")
          .Append("@media print{body{margin:12mm}}</style></head><body>")
          .Append("<h1>Documents et codes confidentiels</h1>")
          .Append($"<div class=\"sub\">{E(Environment.MachineName)} — {E(Environment.UserName)} — {DateTime.Now:dd/MM/yyyy HH:mm} — Alex Toolkit. Les valeurs trouvées sont masquées.</div>")
          .Append("<div class=\"kpi\">")
          .Append($"<div class=\"k\"><b>{items.Count}</b>éléments</div>")
          .Append($"<div class=\"k c\"><b>{crit}</b>secrets en clair</div>")
          .Append($"<div class=\"k w\"><b>{pers}</b>documents personnels</div>")
          .Append($"<div class=\"k t\"><b>{tech}</b>accès techniques</div></div>");

        foreach (var g in items.GroupBy(i => i.Category).OrderBy(g => GetLevel(g.Key)).ThenBy(g => g.Key))
        {
            string cls = GetLevel(g.Key) switch { Level.Critical => "c", Level.Personal => "w", _ => "t" };
            sb.Append($"<h2 class=\"{cls}\">{E(g.Key)} ({g.Count()})</h2><table><tr><th>Fichier</th><th>Détail</th></tr>");
            foreach (var f in g.OrderBy(f => f.Path))
                sb.Append($"<tr><td class=\"p\">{E(f.Path)}</td><td>{E(f.Details)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static int Line(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    /// <summary>« FR7630006000011234567890189 » → « FR76•••••••0189 » : le rapport ne recopie jamais un secret.</summary>
    private static string Mask(string v)
        => v.Length <= 6 ? new string('•', v.Length) : v[..Math.Min(4, v.Length / 3)] + new string('•', 6) + v[^Math.Min(4, v.Length / 3)..];

    public static bool IsValidIban(string raw)
    {
        string s = raw.Replace(" ", "").ToUpperInvariant();
        if (s.Length < 15 || s.Length > 34) return false;
        string moved = s[4..] + s[..4];
        var sb = new StringBuilder();
        foreach (char c in moved)
        {
            if (char.IsDigit(c)) sb.Append(c);
            else if (c is >= 'A' and <= 'Z') sb.Append(c - 'A' + 10);
            else return false;
        }
        return BigInteger.Parse(sb.ToString()) % 97 == 1;
    }

    public static bool Luhn(string digits)
    {
        int sum = 0; bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>Préfixes des réseaux de cartes (Visa, Mastercard, Amex, CB…) : évite les suites de chiffres quelconques.</summary>
    private static bool IsLikelyCard(string d)
        => d.Distinct().Count() > 3 && (d[0] == '4' || (d[0] == '5' && d[1] is >= '1' and <= '5') || (d[0] == '2' && d[1] is >= '2' and <= '7')
           || (d[0] == '3' && d[1] is '4' or '7') || d.StartsWith("6011") || d.StartsWith("65"));

    /// <summary>Clé de contrôle du NIR (97 − n mod 97), Corse 2A/2B comprise.</summary>
    public static bool IsValidNir(Match m)
    {
        string dept = m.Groups[4].Value;
        string body = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value
                    + (dept == "2A" ? "19" : dept == "2B" ? "18" : dept) + m.Groups[5].Value + m.Groups[6].Value;
        if (!long.TryParse(body, out long n) || !int.TryParse(m.Groups[7].Value, out int key)) return false;
        return 97 - (n % 97) == key;
    }

    private static string DownloadsFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p && p.Length > 0)
                return Environment.ExpandEnvironmentVariables(p);
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
