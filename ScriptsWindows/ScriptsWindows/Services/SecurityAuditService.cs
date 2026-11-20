using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Un point du bilan de sécurité. Ok : true = conforme, false = à corriger, null = information / non applicable.</summary>
public sealed record SecurityCheck(string Id, string Title, bool? Ok, string Detail, string FixLabel = "", bool Important = true);

/// <summary>Groupe d'incidents du journal (même cause), expliqué en français.</summary>
public sealed record IncidentGroup(string Title, string Explanation, int Count, DateTime Last, string Level, string Sample);

/// <summary>Programme et mémoire utilisée (tous ses processus additionnés).</summary>
public sealed record MemoryUser(string Name, int Processes, double MegaBytes);

/// <summary>
/// Bilan de sécurité de Windows : protections de Defender (temps réel, cloud, indésirables, falsifications,
/// comportement, signatures, analyses), pare-feu, SmartScreen, UAC, Windows Update, BitLocker, antivirus tiers.
/// Lectures par valeurs (pas de texte traduit) ; corrections ciblées, vérifiées par une nouvelle lecture.
/// Incidents des 7 derniers jours regroupés par cause et expliqués ; mémoire par programme.
/// </summary>
public static class SecurityAuditService
{
    public static async Task<List<SecurityCheck>> AuditAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$o = [ordered]@{}
# Lectures séparées : Get-MpPreference exige l'administrateur, son échec ne doit pas faire croire Defender absent
try {
    $s = Get-MpComputerStatus -ErrorAction Stop
    $o.Def = $true; $o.RT = [bool]$s.RealTimeProtectionEnabled; $o.AV = [bool]$s.AntivirusEnabled
    $o.BM = [bool]$s.BehaviorMonitorEnabled; $o.Tamper = [bool]$s.IsTamperProtected
    $o.SigAge = [int]$s.AntivirusSignatureAge; $o.Sig = $s.AntivirusSignatureLastUpdated.ToString('dd/MM/yyyy HH:mm')
    $o.QuickAge = [int]$s.QuickScanAge; $o.FullAge = [int]$s.FullScanAge
} catch { $o.Def = $false }
try { $p = Get-MpPreference -ErrorAction Stop; $o.Maps = [int]$p.MAPSReporting; $o.Pua = [int]$p.PUAProtection } catch { $o.Maps = -1; $o.Pua = -1 }
# Repli : Defender déclaré actif au Centre de sécurité
$o.DefSc = [bool](Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
    Where-Object { $_.displayName -match 'Defender' -and ((($_.productState -shr 12) -band 0xF) -eq 1) })
$o.Third = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
    Where-Object { $_.displayName -notmatch 'Defender' } | ForEach-Object { '{0}|{1}' -f $_.displayName, ((($_.productState -shr 12) -band 0xF) -eq 1) })
$o.Fw = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | ForEach-Object { '{0}|{1}' -f $_.Name, [bool]$_.Enabled })
$sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$o.Lua = [int](Get-ItemProperty $sys -ErrorAction SilentlyContinue).EnableLUA
$o.Consent = [int](Get-ItemProperty $sys -ErrorAction SilentlyContinue).ConsentPromptBehaviorAdmin
$ss = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' -ErrorAction SilentlyContinue).SmartScreenEnabled
$ssPol = (Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' -ErrorAction SilentlyContinue).EnableSmartScreen
$o.SmartScreen = if ($ssPol -eq 0 -or $ss -eq 'Off') { 'off' } else { 'on' }
$last = Get-HotFix -ErrorAction SilentlyContinue | Where-Object InstalledOn | Sort-Object InstalledOn -Descending | Select-Object -First 1
$o.UpdAge = if ($last) { [int]((Get-Date) - $last.InstalledOn).TotalDays } else { -1 }
$bl = Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction SilentlyContinue
$o.Bl = if ($bl) { [string]$bl.ProtectionStatus } else { '' }
$o.Guest = [bool](Get-LocalUser -ErrorAction SilentlyContinue | Where-Object { $_.SID.Value -like '*-501' } | Select-Object -First 1).Enabled
ConvertTo-Json -Compress -InputObject $o", ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var r = doc.RootElement;
        bool B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        int I(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        IEnumerable<string[]> A(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => (x.GetString() ?? "").Split('|')) : Enumerable.Empty<string[]>();

        var list = new List<SecurityCheck>();
        var third = A("Third").Where(a => a.Length == 2).ToList();
        bool thirdOn = third.Any(t => t[1] == "True");
        if (third.Count > 0)
            list.Add(new("third", "Antivirus installé", thirdOn, string.Join(", ", third.Select(t => $"{t[0]} ({(t[1] == "True" ? "activé" : "désactivé")})")) +
                     (thirdOn ? " : il remplace Microsoft Defender, les réglages Defender ci-dessous ne s'appliquent pas." : " : aucun antivirus actif !")));

        if (B("Def") && !thirdOn)
        {
            list.Add(new("rt", "Protection en temps réel", B("RT"), B("RT") ? "Les fichiers sont analysés à l'ouverture et au téléchargement." : "Désactivée : un virus copié ou téléchargé ne serait pas arrêté.", "Activer"));
            int age = I("SigAge");
            list.Add(new("sig", "Signatures antivirus", age is >= 0 and <= 3, age < 0 ? "Âge inconnu." : $"Mises à jour le {S("Sig")} ({(age == 0 ? "aujourd'hui" : $"il y a {age} jour(s)")})." + (age > 3 ? " Trop anciennes." : ""), "Mettre à jour"));
            int maps = I("Maps"), pua = I("Pua");
            list.Add(new("maps", "Protection cloud", maps < 0 ? null : maps > 0, maps < 0 ? "Réglage illisible sans droits administrateur." : maps > 0 ? "Les fichiers suspects inconnus sont vérifiés en ligne en quelques secondes." : "Désactivée : les nouveaux virus sont détectés bien plus tard.", "Activer"));
            list.Add(new("pua", "Blocage des logiciels indésirables", pua < 0 ? null : pua == 1, pua < 0 ? "Réglage illisible sans droits administrateur." : pua == 1 ? "Barres d'outils, faux optimiseurs et installateurs piégés sont bloqués." : "Désactivé : les logiciels publicitaires et faux nettoyeurs passent.", "Activer"));
            list.Add(new("bm", "Surveillance du comportement", B("BM"), B("BM") ? "Un programme qui agit comme un virus est arrêté." : "Désactivée.", "Activer"));
            list.Add(new("tamper", "Protection contre les falsifications", B("Tamper"), B("Tamper") ? "Un virus ne peut pas désactiver Defender." : "Désactivée : un virus pourrait couper l'antivirus. Se réactive dans Sécurité Windows.", "Ouvrir"));
            int q = I("QuickAge");
            list.Add(new("scan", "Dernière analyse", q is >= 0 and <= 14, q < 0 || q > 10000 ? "Aucune analyse enregistrée." : $"Analyse rapide il y a {q} jour(s)" + (I("FullAge") is >= 0 and < 10000 ? $", complète il y a {I("FullAge")} jour(s)." : "."), "Analyser…", Important: false));
        }
        else if (!B("Def") && !thirdOn)
            list.Add(B("DefSc")
                ? new("rt", "Microsoft Defender", true, "Actif selon le Centre de sécurité (détail illisible : relancez en administrateur pour tout vérifier).")
                : new("rt", "Microsoft Defender", false, "Defender ne répond pas et aucun autre antivirus n'est actif : le PC n'est pas protégé.", "Ouvrir"));

        var fw = A("Fw").Where(a => a.Length == 2).ToList();
        var fwOff = fw.Where(f => f[1] != "True").Select(f => f[0] switch { "Domain" => "domaine", "Private" => "privé", "Public" => "public", _ => f[0] }).ToList();
        list.Add(new("fw", "Pare-feu Windows", fw.Count > 0 && fwOff.Count == 0, fw.Count == 0 ? "État illisible." : fwOff.Count == 0 ? "Actif sur les 3 types de réseau." : $"Désactivé pour le réseau {string.Join(", ", fwOff)}.", "Activer"));
        list.Add(new("smartscreen", "SmartScreen", S("SmartScreen") != "off", S("SmartScreen") != "off" ? "Les programmes téléchargés inconnus sont signalés avant de s'ouvrir." : "Désactivé : aucun avertissement avant d'ouvrir un programme téléchargé.", "Ouvrir"));
        bool lua = I("Lua") != 0;
        list.Add(new("uac", "Contrôle de compte (UAC)", lua && I("Consent") != 0, !lua ? "Désactivé : tout programme obtient les droits administrateur sans demander." : I("Consent") == 0 ? "Les programmes deviennent administrateur sans demander confirmation." : "Une confirmation est demandée avant toute modification du système.", "Activer"));
        int upd = I("UpdAge");
        list.Add(new("wu", "Mises à jour de Windows", upd is >= 0 and <= 45, upd < 0 ? "Aucune mise à jour enregistrée." : $"Dernière installation il y a {upd} jour(s)." + (upd > 45 ? " Windows Update semble bloqué." : ""), "Réparer…", Important: upd > 45));
        string bl = S("Bl");
        list.Add(new("bitlocker", "Chiffrement du disque (BitLocker)", bl == "" ? null : bl == "On",
                     bl == "On" ? "Le disque est chiffré : illisible si le PC est volé." : bl == "" ? "Non disponible sur cette édition de Windows (Famille) ou non lisible." : "Non chiffré : un voleur peut lire le disque en le branchant ailleurs.", "", Important: false));
        list.Add(new("guest", "Compte Invité", !B("Guest"), B("Guest") ? "Activé : n'importe qui peut ouvrir une session sans mot de passe." : "Désactivé.", "Désactiver", Important: B("Guest")));
        return list;
    }

    /// <summary>Applique une correction (administrateur). Renvoie le message à afficher ; le bilan doit être relu ensuite.</summary>
    public static async Task<string> FixAsync(string id, CancellationToken ct = default)
    {
        string script = id switch
        {
            "rt" => "Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop",
            "sig" => "Update-MpSignature -ErrorAction Stop",
            "maps" => "Set-MpPreference -MAPSReporting Advanced -SubmitSamplesConsent SendSafeSamples -ErrorAction Stop",
            "pua" => "Set-MpPreference -PUAProtection Enabled -ErrorAction Stop",
            "bm" => "Set-MpPreference -DisableBehaviorMonitoring $false -ErrorAction Stop",
            "fw" => "Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled True -ErrorAction Stop",
            "uac" => @"$k = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Set-ItemProperty $k -Name EnableLUA -Value 1 -Type DWord -ErrorAction Stop
Set-ItemProperty $k -Name ConsentPromptBehaviorAdmin -Value 5 -Type DWord -ErrorAction Stop",
            "guest" => @"Get-LocalUser | Where-Object { $_.SID.Value -like '*-501' } | Disable-LocalUser -ErrorAction Stop",
            _ => ""
        };
        if (script.Length == 0) return "";
        string r = (await PowerShellRunner.RunAsync($"try {{ {script}; 'OK' }} catch {{ 'ERR ' + $_.Exception.Message }}", ct)).Trim();
        if (!r.EndsWith("OK")) return r.StartsWith("ERR ") ? r[4..] : r;
        return id == "uac" ? "UAC réactivé : redémarrez le PC pour l'appliquer." : "";
    }

    /// <summary>Incidents des 7 derniers jours (Système + Application, critiques et erreurs), regroupés par cause connue.</summary>
    public static async Task<List<IncidentGroup>> GetIncidentsAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'System','Application'; Level = 1,2; StartTime = (Get-Date).AddDays(-7) } -MaxEvents 600 -ErrorAction SilentlyContinue
$r = foreach ($e in $ev) {
    $app = ''
    if ($e.ProviderName -eq 'Application Error' -and $e.Properties.Count -gt 0) { $app = [string]$e.Properties[0].Value }
    $m = ($e.Message -split ""`n"")[0]
    if ($m.Length -gt 200) { $m = $m.Substring(0,200) }
    [pscustomobject]@{ P = [string]$e.ProviderName; I = [int]$e.Id; L = [int]$e.Level; T = $e.TimeCreated.ToString('o'); A = $app; M = $m.Trim() }
}
ConvertTo-Json -Compress -InputObject @($r)", ct);
        var raw = new List<(string P, int I, int L, DateTime T, string A, string M)>();
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json))
            foreach (var e in doc.RootElement.EnumerateArray())
                raw.Add((e.GetProperty("P").GetString() ?? "", e.GetProperty("I").GetInt32(), e.GetProperty("L").GetInt32(),
                         DateTime.TryParse(e.GetProperty("T").GetString(), out var t) ? t : DateTime.MinValue, e.GetProperty("A").GetString() ?? "", e.GetProperty("M").GetString() ?? ""));

        var groups = new List<IncidentGroup>();
        foreach (var g in raw.GroupBy(x => Classify(x.P, x.I, x.A)))
        {
            var (title, expl, level) = g.Key;
            var last = g.OrderByDescending(x => x.T).First();
            groups.Add(new IncidentGroup(title, expl, g.Count(), last.T, level, last.M));
        }
        int Rank(string l) => l switch { "Danger" => 0, "Warning" => 1, _ => 2 };
        return groups.OrderBy(g => Rank(g.Level)).ThenByDescending(g => g.Count).ToList();
    }

    /// <summary>Cause connue d'un événement (titre, explication, gravité) ; sinon regroupé par source.</summary>
    public static (string Title, string Explanation, string Level) Classify(string provider, int id, string app) => (provider, id) switch
    {
        ("Microsoft-Windows-Kernel-Power", 41) => ("Arrêts brutaux du PC", "Le PC s'est éteint sans passer par « Arrêter » : coupure de courant, bouton maintenu, plantage ou alimentation défaillante.", "Danger"),
        ("Microsoft-Windows-WER-SystemErrorReporting", 1001) or ("BugCheck", 1001) => ("Écrans bleus", "Windows a planté (écran bleu). L'outil « Écrans bleus et périphériques en erreur » en donne la cause.", "Danger"),
        ("Microsoft-Windows-WHEA-Logger", _) => ("Erreurs matérielles (WHEA)", "Le processeur, la mémoire ou un bus PCIe a signalé une erreur : surchauffe, overclocking, mémoire ou carte défaillante.", "Danger"),
        ("disk", 7) or ("disk", 51) or ("disk", 153) or ("Disk", 7) or ("Disk", 51) or ("Disk", 153) => ("Erreurs de lecture du disque", "Le disque a eu du mal à lire ou écrire : secteurs défectueux ou câble. Sauvegardez vos données et vérifiez le disque (CHKDSK, santé S.M.A.R.T.).", "Danger"),
        ("Ntfs", 55) or ("Ntfs", 98) or ("Microsoft-Windows-Ntfs", 55) or ("Microsoft-Windows-Ntfs", 98) => ("Système de fichiers endommagé", "Windows a trouvé des erreurs sur le disque : lancez « Réparer les erreurs de disque (CHKDSK) ».", "Danger"),
        ("volmgr", 161) or ("volmgr", 46) => ("Vidage mémoire impossible", "Windows n'a pas pu enregistrer les informations d'un plantage (fichier d'échange trop petit ou désactivé).", "Warning"),
        ("Application Error", 1000) => ($"Plantages de {(app.Length > 0 ? app : "logiciels")}", "Ce programme s'est fermé brutalement. S'il se répète : mettez-le à jour ou réinstallez-le.", "Warning"),
        ("Application Hang", 1002) => ("Programmes figés", "Un programme a cessé de répondre et a été fermé.", "Warning"),
        ("Service Control Manager", 7000) or ("Service Control Manager", 7009) or ("Service Control Manager", 7011) => ("Services qui ne démarrent pas", "Un service Windows ou d'un logiciel n'a pas démarré à temps (souvent un logiciel désinstallé à moitié).", "Warning"),
        ("Service Control Manager", 7031) or ("Service Control Manager", 7034) => ("Services arrêtés de façon inattendue", "Un service s'est arrêté brutalement puis a été relancé par Windows.", "Warning"),
        ("Microsoft-Windows-DistributedCOM", 10016) or ("Microsoft-Windows-DistributedCOM", 10010) => ("Avertissements DCOM (sans gravité)", "Bruit habituel de Windows : un composant système demande une autorisation qu'il n'a pas. Aucune action nécessaire.", "Info"),
        ("Microsoft-Windows-WindowsUpdateClient", 20) => ("Échecs de mises à jour", "Une mise à jour n'a pas pu s'installer : voir « Réparer les fichiers système » > Windows Update.", "Warning"),
        _ => ($"{provider} (événement {id})", "Erreur enregistrée par Windows sans explication connue.", "Info")
    };

    /// <summary>Mémoire : total et 15 programmes les plus gourmands (processus d'un même programme additionnés).</summary>
    public static async Task<(double UsedGb, double TotalGb, List<MemoryUser> Top)> GetMemoryAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$os = Get-CimInstance Win32_OperatingSystem
$top = Get-Process | Group-Object ProcessName | ForEach-Object {
    [pscustomobject]@{ N = $_.Name; C = $_.Count; M = [math]::Round((($_.Group | Measure-Object WorkingSet64 -Sum).Sum) / 1MB, 0) }
} | Sort-Object M -Descending | Select-Object -First 15
ConvertTo-Json -Compress -Depth 3 -InputObject ([pscustomobject]@{ T = [double]$os.TotalVisibleMemorySize; F = [double]$os.FreePhysicalMemory; L = @($top) })", ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var r = doc.RootElement;
        double total = r.TryGetProperty("T", out var t) ? t.GetDouble() / 1048576 : 0, free = r.TryGetProperty("F", out var f) ? f.GetDouble() / 1048576 : 0;
        var top = new List<MemoryUser>();
        if (r.TryGetProperty("L", out var l))
            foreach (var p in l.EnumerateArray()) top.Add(new MemoryUser(p.GetProperty("N").GetString() ?? "", p.GetProperty("C").GetInt32(), p.GetProperty("M").GetDouble()));
        return (total - free, total, top);
    }
}
