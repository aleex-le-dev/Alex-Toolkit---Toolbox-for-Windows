using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ScriptsWindows.Models;
using ScriptsWindows.Services;

namespace ScriptsWindows.Core;

/// <summary>
/// Répertoire central de tous les outils disponibles dans l'application.
/// Structuré en 12 catégories actives (totalisant 43 outils).
/// Conforme à la directive UI/UX Alex Toolkit.
/// </summary>
public static class ToolRegistry
{
    private static readonly List<ScriptTool> _allTools = new();

    static ToolRegistry()
    {
        RegisterDiagnosticTools();       // Catégorie 1 : Diagnostic Matériel et Intégrité
        RegisterHardwareTools();         // Catégorie 2 : Matériel et Périphériques
        RegisterRepairTools();           // Catégorie 3 : Réparation Intégrale
        RegisterCleaningTools();         // Catégorie 4 : Nettoyage et Optimisation Espace
        RegisterSecretsTools();          // Catégorie 5 : Récupération d'Identifiants et Secrets
        RegisterNetworkTools();          // Catégorie 6 : Réseau et Configuration
        RegisterDiskTools();             // Catégorie 7 : Disque Dur et Stockage
        RegisterAppInstallationTools();  // Catégorie 8 : Installation d'Applications
        RegisterUpdatesTools();          // Catégorie 9 : Mises à Jour et Inventaire Logiciel
        RegisterSecurityTools();         // Catégorie 10 : Surveillance, Sécurité et Événements
        RegisterPersonalizationTools();  // Catégorie 11 : Personnalisation et Système
        RegisterBootTools();             // Catégorie 12 : Démarrage & Récupération
    }

    public static IReadOnlyList<ScriptTool> AllTools => _allTools;

    public static IEnumerable<ScriptTool> GetByCategory(string category)
        => _allTools.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase) && !t.IsHidden);

    public static IEnumerable<ScriptTool> GetFavorites()
    {
        var favIds = FavoritesManager.Default.Favorites;
        return _allTools.Where(t => favIds.Contains(t.Id));
    }

    private static readonly Dictionary<string, string> CommandMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // ─── Diagnostic (4) ───
        ["diag_center"] = "AlexToolkit.exe (Centre de diagnostic unifié : carte mère, BIOS, CPU, RAM, disques S.M.A.R.T., batterie, réseau)",
        ["diag_dxdiag"] = "dxdiag.exe",
        ["diag_startup_manager"] = "AlexToolkit.exe (Gestionnaire des programmes au démarrage de Windows)",
        ["nov_win11_ai_audit"] = "AlexToolkit.exe (Audit d'éligibilité Windows 11 24H2, TPM 2.0 et NPU)",
        ["diag_intervention"] = "AlexToolkit.exe (Checkup : Checkpoint-Computer, nettoyage, Update-MpSignature + Start-MpScan, DISM/SFC, winget upgrade --all → rapport HTML)",
        ["diag_crash_drivers"] = "Get-WinEvent (BugCheck 1001, Kernel-Power 41) + Win32_PnPEntity + pnputil.exe /scan-devices",

        // ─── Matériel (4) ───
        ["hw_winsat"] = "winsat.exe formal -restart clean + lecture du DataStore WinSAT (XML) → jauge, graphiques, comparatifs, historique",
        ["hw_powercfg_energy"] = "AlexToolkit.exe (interface : BatteryDialog — powercfg /batteryreport /xml + /energy /xml)",
        ["hw_power_perf"] = "AlexToolkit.exe (Interface de gestion des modes d'alimentation : Normal, Performances élevées, Performances ultimes)",
        ["nov_gaming_optimizer"] = "AlexToolkit.exe (Optimisation de latence, HAGS et Game Mode)",

        // ─── Réparation (7) ───
        ["repair_system_integrity"] = "AlexToolkit.exe (Centre d'intégrité Windows : DISM Scan/Restore et SFC)",
        ["repair_chkdsk"] = "chkdsk.exe (Interface interactive de réparation de disque NTFS)",
        ["repair_services_manager"] = "AlexToolkit.exe (Gestionnaire des services Windows : Spouleur, Windows Update...)",
        ["repair_spooler"] = "AlexToolkit.exe (Centre de dépannage des imprimantes : Get-Printer, Get-PrintJob, Get-PrinterDriver, Win32_Printer, spouleur) + Debloquer_Impression.bat",
        ["repair_apps_shell"] = "AlexToolkit.exe (Réparation Store UWP, wsreset et cache d'icônes Explorer)",
        ["repair_restore_point"] = "AlexToolkit.exe (Points de restauration système Windows)",
        ["repair_export_drivers"] = "AlexToolkit.exe (interface : DriverBackupDialog — pnputil /export-driver, /add-driver)",

        // ─── Nettoyage (1) ───
        ["clean_system_unified"] = "AlexToolkit.exe (Interface interactive : Cleanmgr, temporaires, corbeille, caches et WinSxS)",

        // ─── Secrets et Clés (4) ───
        ["sec_wifi_show"] = "AlexToolkit.exe (interface : WifiPasswordsDialog — netsh wlan show profile key=clear)",
        ["sec_audit_sensitive"] = "AlexToolkit.exe (interface : SensitiveDocsDialog — noms, scans, contenu des fichiers, .ssh, .aws, .env)",
        ["sec_win_key"] = "AlexToolkit.exe (interface : WindowsKeyDialog — SoftwareLicensingService + DigitalProductId)",
        ["sec_browser_profiles"] = "AlexToolkit.exe (interface : BrowserHistoryDialog — SQLite Chrome, Edge, Brave, Vivaldi, Opera, Firefox)",

        // ─── Réseau — 3 hubs à onglets (3) ───
        ["net_hub_internet"] = "AlexToolkit.exe (onglets : Centre réseau + Benchmark DNS + Grand Reset)",
        ["net_hub_wifi"] = "AlexToolkit.exe (onglets : Wi-Fi voisins + bilan des déconnexions)",
        ["net_hub_devices"] = "AlexToolkit.exe (onglets : appareils LAN + connexions TCP actives)",

        // ─── Disque (4) ───
        ["disk_mgmt_gui"] = "diskmgmt.msc",
        ["disk_diskpart"] = "diskpart.exe",
        ["disk_defrag_gui"] = "dfrgui.exe",
        ["disk_bitlocker"] = "manage-bde.exe -status",
        ["disk_profile_migration"] = "robocopy.exe /E /XJ /MT:8 + netsh wlan export/add profile + winget export/import",

        // ─── Applications (2) ───
        ["app_installer_center"] = "AlexToolkit.exe (Installation d'applications unifiée : Chrome, VLC, 7-Zip, Sumatra PDF, PowerShell 7, Office, ArtCraft)",
        ["app_debloat"] = "Get-AppxPackage + Remove-AppxPackage [-AllUsers] + Remove-AppxProvisionedPackage -Online",

        // ─── Mises à jour (1) ───
        ["upd_winget_manager"] = "winget.exe upgrade (Gestionnaire de paquets interactif Winget)",

        // ─── Sécurité (4) ───
        ["sec_center"] = "AlexToolkit.exe (Centre de surveillance et sécurité unifié Defender, BSOD et RAM)",
        ["nov_privacy_shield"] = "AlexToolkit.exe (Bouclier de confidentialité et blocage télémétrie Windows)",
        ["sec_defender_quickscan"] = "AlexToolkit.exe (interface : DefenderScanDialog — Get-MpComputerStatus, Start-MpScan, Get-MpThreat)",
        ["sec_perfmon_rel"] = "perfmon.exe /rel",
        ["sec_malware_cleanup"] = "AlexToolkit.exe (hosts, proxy, Get-ScheduledTask, clés Run + Start-MpScan / Start-MpWDOScan, winget AdwCleaner / Malwarebytes)",
        ["sec_user_accounts"] = "AlexToolkit.exe (Comptes locaux : Get/New/Set/Remove-LocalUser, groupes par SID)",

        // ─── Personnalisation (3) ───
        ["tweak_godmode"] = "explorer.exe \"shell:::{ED7BA470-8E54-465E-825C-99712043E01C}\"",
        ["tweak_power_shortcuts"] = "AlexToolkit.exe (Interface de création des boutons Arrêter et Redémarrer sur le Bureau)",
        ["tweak_context_menu_items"] = "AlexToolkit.exe (Interface de personnalisation des éléments du menu contextuel : CMD Bureau & Dossiers)",

        // ─── Démarrage & Récupération (1) ───
        ["boot_center"] = "AlexToolkit.exe (Console unifiée de démarrage et récupération : sans échec, WinRE, BIOS/UEFI, BCD)",

        // ─── Réseau — Audit Web (1) ───
        ["nov_website_analyzer"] = "AlexToolkit.exe (Centre d'Audit & Analyseur de Site Web : Vue d'ensemble, 21 Contrôles, Speedtest, SEO, Mots-Clés, SSL & Réseau)",
    };


    public static string GetCommand(string toolId)
    {
        if (CommandMap.TryGetValue(toolId ?? "", out var cmd))
            return cmd;
        return "powershell.exe";
    }

    /// <summary>Indique si l'outil possède une commande déclarée dans <see cref="CommandMap"/>.</summary>
    public static bool HasCommand(string toolId) => CommandMap.ContainsKey(toolId ?? "");

    /// <summary>Outils visibles dans l'interface (hors outils masqués).</summary>
    public static IEnumerable<ScriptTool> VisibleTools => _allTools.Where(t => !t.IsHidden);

    /// <summary>Mots vides ignorés : on peut taper des phrases naturelles (« réparer mon internet »).</summary>
    private static readonly HashSet<string> SearchStopWords = new(StringComparer.Ordinal)
    {
        "le","la","les","un","une","des","du","de","d","l","au","aux","en","dans","sur","sous",
        "pour","avec","sans","par","mon","ma","mes","ton","ta","tes","son","sa","ses","nos","vos",
        "notre","votre","leur","leurs","ce","cet","cette","et","ou","que","qui","quoi","est",
        "je","tu","il","elle","on","nous","vous","ils","me","te","se","ne","pas","plus","the","my"
    };

    /// <summary>Minuscule + suppression des accents (é→e) pour une recherche tolérante.</summary>
    private static string NormalizeSearch(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Champ normalisé où chaque mot est précédé d'une espace (recherche par début de mot).</summary>
    private static string SearchField(string? s)
    {
        var n = NormalizeSearch(s);
        var sb = new StringBuilder(n.Length + 1).Append(' ');
        foreach (var ch in n) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString();
    }

    private static readonly char[] SearchSeparators =
        { ' ', '\t', '\n', '\r', ',', ';', '.', ':', '!', '?', '\'', '’', '"', '(', ')', '[', ']', '/', '\\', '-', '_', '&' };

    /// <summary>
    /// Recherche multi-mots tolérante aux accents : « reparer internet » trouve les outils
    /// contenant TOUS les mots (prioritaires), puis les correspondances partielles, classées par pertinence.
    /// </summary>
    public static IEnumerable<ScriptTool> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<ScriptTool>();

        var tokens = NormalizeSearch(query)
            .Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && !SearchStopWords.Contains(w))
            .Distinct()
            .ToList();

        // Requête trop courte ou uniquement des mots vides : on retombe sur la requête brute.
        if (tokens.Count == 0)
        {
            var raw = NormalizeSearch(query).Trim();
            if (raw.Length == 0) return Enumerable.Empty<ScriptTool>();
            tokens = new List<string> { raw };
        }

        var scored = new List<(ScriptTool Tool, int Score, int Hits)>();
        foreach (var t in _allTools)
        {
            if (t.IsHidden) continue;

            string nName = SearchField(t.Name);
            string nTags = SearchField(string.Join(" ", t.Tags));
            string nSub  = SearchField(t.SubHeading);
            string nCat  = SearchField(t.Category);
            string nDesc = SearchField(t.Description);
            string nFeat = SearchField(string.Join(" ", t.Features));

            int score = 0, hits = 0;
            foreach (var raw in tokens)
            {
                // Mots courts : début de mot uniquement (« cle » → « cles », pas « cycles ») ;
                // mots longs : n'importe où (« virus » → « antivirus »).
                var tok = raw.Length < 5 ? " " + raw : raw;
                int w = 0;
                if (nName.Contains(tok)) w += 5;
                if (nTags.Contains(tok)) w += 4;
                if (nSub.Contains(tok))  w += 2;
                if (nCat.Contains(tok))  w += 2;
                if (nDesc.Contains(tok)) w += 1;
                if (nFeat.Contains(tok)) w += 1;
                if (w > 0) { hits++; score += w; }
            }

            if (hits == 0) continue;
            if (hits == tokens.Count) score += 100;   // bonus : tous les mots trouvés
            scored.Add((t, score, hits));
        }

        if (scored.Count == 0) return Enumerable.Empty<ScriptTool>();

        // On ne garde que les meilleures correspondances : si des outils contiennent TOUS les mots,
        // seuls eux sont affichés ; sinon, ceux qui en contiennent le plus.
        int bestHits = scored.Max(x => x.Hits);
        return scored
            .Where(x => x.Hits == bestHits)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => NormalizeSearch(x.Tool.Name))
            .Select(x => x.Tool);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 1 : 🔍 DIAGNOSTIC MATÉRIEL ET INTÉGRITÉ
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterDiagnosticTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "diag_center",
            Name = "Bilan de santé complet du PC",
            Description = "Vérifie d'un coup d'œil l'état de votre processeur, mémoire, disques durs, batterie et connexion pour s'assurer que tout fonctionne bien.",
            Features = new[]
            {
                "Processeur (CPU), mémoire vive (RAM) et charge",
                "Santé des disques durs & alertes S.M.A.R.T.",
                "Autonomie de la batterie & état du réseau"
            },
            Category = "DIAGNOSTIC",
            SubHeading = "Bilan général de l'ordinateur",
            Icon = "🔍",
            Action = (log, ct) => DiagnosticService.OpenDiagnosticCenterDialog(log, ct),
            Tags = new[] { "bilan", "santé", "diagnostic", "ordinateur", "processeur", "mémoire", "disques", "smart", "batterie", "réseau", "tout-en-un" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "nov_win11_ai_audit",
            Name = "Audit d'éligibilité Windows 11 24H2 et NPU IA",
            Description = "Vérifie les prérequis matériels avancés (POPCNT, SSE4.2, TPM 2.0, Secure Boot, RAM et présence d'un processeur neuronal NPU Copilot+).",
            Features = new[]
            {
                "Instructions CPU requises (POPCNT, SSE4.2)",
                "Sécurité matérielle : puce TPM 2.0 & Secure Boot",
                "Détection du processeur neuronal (NPU Copilot+)"
            },
            Category = "DIAGNOSTIC",
            SubHeading = "Bilan général de l'ordinateur",
            Icon = "🔍",
            Action = (log, ct) => InnovationService.OpenWin11AiAuditDialog(log, ct),
            Tags = new[] { "windows 11", "24h2", "tpm", "secure boot", "popcnt", "ia", "npu", "copilot", "éligibilité", "audit" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "diag_dxdiag",
            Name = "Vérifier l'écran, les jeux et le son (DxDiag)",
            Description = "Teste le bon fonctionnement de votre carte graphique, de votre affichage et de vos haut-parleurs en cas de problème vidéo ou audio.",
            Features = new[]
            {
                "Diagnostic de la carte graphique (DirectX / GPU)",
                "Vérification des écrans, pilotes et rafraîchissement",
                "Test des périphériques audio et haut-parleurs"
            },
            Category = "DIAGNOSTIC",
            SubHeading = "Vérifications rapides",
            Icon = "🎮",
            Action = (log, ct) => HardwareService.LaunchDxdiag(log, ct),
            Tags = new[] { "écran", "affichage", "jeux", "carte graphique", "gpu", "son", "audio", "dxdiag" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "diag_startup_manager",
            Name = "Accélérer le démarrage de Windows",
            Description = "Affiche la liste des logiciels qui se lancent au démarrage et vous permet de désactiver ceux qui ralentissent inutilement votre PC.",
            Features = new[]
            {
                "Inventaire des applications lancées au boot",
                "Mesure de l'impact sur le temps de démarrage",
                "Activation / désactivation en 1 clic sans risque"
            },
            Category = "DIAGNOSTIC",
            SubHeading = "Vérifications rapides",
            Icon = "🚀",
            Action = (log, ct) => DiagnosticService.OpenStartupManagerDialog(log, ct),
            Tags = new[] { "accélérer", "démarrage", "ralentissement", "programmes", "lancement", "vitesse", "boot", "optimiser" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "diag_intervention",
            Name = "Intervention technicien et rapport client",
            Description = "Checkup complet en un clic (restauration, nettoyage, antivirus, DISM/SFC, mises à jour) puis rapport client avec l'état du PC avant / après.",
            Features = new[]
            {
                "Opérations de maintenance au choix, enchaînées",
                "Comparatif avant / après et durée de chaque étape",
                "Rapport HTML imprimable avec signatures"
            },
            Category = "DIAGNOSTIC",
            SubHeading = "Intervention technicien",
            Icon = "🧰",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.InterventionDialog(log, ct)),
            Tags = new[] { "intervention", "rapport", "client", "checkup", "maintenance", "facture", "technicien", "bilan", "pdf", "révision" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "diag_crash_drivers",
            Name = "Écrans bleus et périphériques en erreur",
            Description = "Analyse les écrans bleus et arrêts brutaux des 90 derniers jours, liste les périphériques en erreur et explique la cause probable de chaque panne.",
            Features = new[]
            {
                "Code d'arrêt traduit avec la cause probable",
                "Périphériques en erreur (codes 10, 28, 43…)",
                "Réinstallation d'un pilote en 1 clic"
            },
            Category = "DIAGNOSTIC",
            SubHeading = "Pannes et pilotes",
            Icon = "💥",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.CrashDriverDialog(log, ct)),
            Tags = new[] { "écran bleu", "bsod", "plantage", "crash", "minidump", "pilote", "driver", "périphérique", "erreur", "code 43", "redémarre seul" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 2 : 🖥️ PERFORMANCES ET ÉNERGIE
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterHardwareTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "hw_power_perf",
            Name = "Changer la vitesse du PC (Normal / Rapide)",
            Description = "Bascule facile : économisez de l'énergie en mode normal ou libérez toute la puissance du processeur pour les jeux et logiciels lourds.",
            Features = new[]
            {
                "Bascule instantanée : Économie ou Performance",
                "Optimisation de la fréquence d'horloge du CPU",
                "Idéal pour prolonger la batterie ou booster les jeux"
            },
            Category = "MATERIEL",
            SubHeading = "Gestion de l'alimentation et vitesse",
            Icon = "⚡",
            Action = (log, ct) => HardwareService.OpenPowerPlanDialog(log, ct),
            Tags = new[] { "vitesse", "puissance", "mode", "alimentation", "performances", "normal", "équilibré", "rapide", "batterie", "jeux" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "nov_gaming_optimizer",
            Name = "Optimiseur de latence et Mode Gaming Ultime",
            Description = "Configure la planification GPU matérielle accélérée (HAGS), désactive l'algorithme de Nagle pour réduire le ping et alloue 100% du CPU aux jeux.",
            Features = new[]
            {
                "Planification GPU matérielle accélérée (HAGS)",
                "Désactivation algorithme de Nagle (réduit le ping)",
                "Priorité CPU maximale allouée aux jeux vidéo"
            },
            Category = "MATERIEL",
            SubHeading = "Gestion de l'alimentation et vitesse",
            Icon = "⚡",
            Action = (log, ct) => InnovationService.OpenGamingOptimizerDialog(log, ct),
            Tags = new[] { "gaming", "jeu", "latence", "ping", "nagle", "fps", "hags", "boost", "vitesse", "réactivité" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "hw_winsat",
            Name = "Tester la puissance globale de mon PC",
            Description = "Note officielle Windows avec jauge et graphiques : processeur, mémoire, graphismes et disque, comparés à des PC types ou à une évaluation précédente.",
            Features = new[]
            {
                "Note globale, point faible et conseil d'amélioration",
                "Mesures réelles : Mo/s, Go/s, images/s",
                "Comparatif avant / après et historique"
            },
            Category = "MATERIEL",
            SubHeading = "Banc d'essai et performances",
            Icon = "⚙️",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.BenchmarkDialog(log, ct)),
            Tags = new[] { "tester", "note", "puissance", "vitesse", "benchmark", "score", "processeur", "graphique" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "hw_powercfg_energy",
            Name = "Détecter ce qui vide la batterie",
            Description = "Mesure l'usure de la batterie (capacité, cycles, autonomie) et liste en clair ce qui consomme de l'énergie : pilotes, programmes, réglages.",
            Features = new[]
            {
                "Santé de la batterie en % et évolution de sa capacité",
                "Diagnostic Windows de 60 s lu et trié par gravité",
                "Fonctionne aussi sur PC fixe (consommation)"
            },
            Category = "MATERIEL",
            SubHeading = "Banc d'essai et performances",
            Icon = "🔋",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.BatteryDialog(log, ct)),
            Tags = new[] { "batterie", "autonomie", "énergie", "consommation", "économiser", "rapport", "portable" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 3 : 🔧 RÉPARATION INTÉGRALE
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterRepairTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "repair_system_integrity",
            Name = "Réparer les fichiers système endommagés (SFC & DISM)",
            Description = "Scanne les fichiers internes de Windows et remplace automatiquement ceux qui sont cassés pour stopper les bugs, ralentissements et plantages.",
            Features = new[]
            {
                "Scan approfondi de tous les fichiers protégés (SFC)",
                "Restauration de l'image Windows Update (DISM)",
                "Résolution automatique des corruptions et bugs"
            },
            Category = "REPARATION",
            SubHeading = "Réparation du système et des disques",
            Icon = "🩺",
            Action = (log, ct) => RepairService.OpenSystemIntegrityDialog(log, ct),
            Tags = new[] { "réparer", "fichiers", "système", "corruption", "sfc", "dism", "scannow", "bugs", "plantage", "restaurer", "image" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "repair_chkdsk",
            Name = "Réparer les erreurs de disque dur (CHKDSK)",
            Description = "Vérifie la santé de votre disque dur ou SSD, détecte les erreurs d'écriture et répare les fichiers qui refusent de s'ouvrir.",
            Features = new[]
            {
                "Analyse des métadonnées système de fichiers NTFS",
                "Détection et isolation des secteurs défectueux",
                "Récupération automatique des dossiers illisibles"
            },
            Category = "REPARATION",
            SubHeading = "Réparation du système et des disques",
            Icon = "🔍",
            Action = (log, ct) => RepairService.OpenChkdskDialog(log, ct),
            Tags = new[] { "disque", "réparer", "chkdsk", "erreurs", "secteurs", "bloqué", "ntfs", "dossiers", "intégrité" }
        });

        // Imprimantes : une seule carte (le Centre contient le déblocage en 1 clic).
        // Id historique « repair_spooler » conservé : les favoris existants continuent de fonctionner.
        _allTools.Add(new ScriptTool
        {
            Id = "repair_spooler",
            Name = "Dépannage des imprimantes",
            Description = "Impression bloquée, imprimante hors ligne ou en erreur ? État réel de chaque imprimante avec le conseil adapté, déblocage en 1 clic, pilotes et réinstallation.",
            Features = new[]
            {
                "Déblocage de l'impression en 1 clic",
                "État réel : hors ligne, bourrage, erreur…",
                "Pilotes, réinstallation, page de test"
            },
            Category = "REPARATION",
            SubHeading = "Dépannage de l'imprimante et des services",
            Icon = "🖨️",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.PrinterCenterDialog(log, ct)),
            DesktopAction = log => PrinterCenterService.CreateDesktopUnblockScript(log),
            DesktopHint = "Le fichier « Debloquer_Impression.bat » débloque l'impression en un double-clic (il demande les droits administrateur, vide la file et relance le service). Fichier autonome : copiez-le sur une clé USB pour dépanner n'importe quel PC.",
            Tags = new[] { "imprimante", "impression", "bloquée", "coincée", "file", "spooler", "spouleur", "vider", "relancer", "hors ligne", "pilote", "driver",
                           "page de test", "file d'attente", "réinstaller", "par défaut", "scanner", "supprimer", "pnp" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "repair_services_manager",
            Name = "Gérer et redémarrer les services Windows",
            Description = "Pilotez facilement les programmes d'arrière-plan de Windows : redémarrez Windows Update, le son ou arrêtez les tâches bloquées.",
            Features = new[]
            {
                "Supervision des services système d'arrière-plan",
                "Redémarrage en 1 clic de Windows Update, Audio, etc.",
                "Arrêt immédiat des services figés ou en surcharge"
            },
            Category = "REPARATION",
            SubHeading = "Dépannage de l'imprimante et des services",
            Icon = "⚙️",
            Action = (log, ct) => RepairService.OpenServicesManagerDialog(log, ct),
            Tags = new[] { "services", "arrière-plan", "redémarrer", "windows update", "spooler", "bloqué", "arrêter", "processus" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "repair_apps_shell",
            Name = "Réparer le Microsoft Store et les icônes blanches",
            Description = "Répare la boutique Microsoft Store si elle refuse de s'ouvrir et reconstruit les images de vos raccourcis si vos icônes s'affichent en blanc.",
            Features = new[]
            {
                "Réinitialisation complète du Microsoft Store (wsreset)",
                "Purge et reconstruction du cache IconCache.db",
                "Restauration de l'affichage normal dans l'Explorateur"
            },
            Category = "REPARATION",
            SubHeading = "Boutique d'applications et affichage",
            Icon = "🏬",
            Action = (log, ct) => RepairService.OpenAppsShellRepairDialog(log, ct),
            Tags = new[] { "store", "boutique", "applications", "icônes", "blanches", "miniatures", "wsreset", "explorer", "affichage" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "repair_restore_point",
            Name = "Créer ou restaurer un point de retour",
            Description = "Créez une sauvegarde de secours pour pouvoir revenir en arrière à tout moment en cas de bug ou de mauvaise manipulation.",
            Features = new[]
            {
                "Création d'un point de sauvegarde immédiat",
                "Restauration facile en cas de fausse manipulation",
                "Accès direct à l'assistant officiel Windows (rstrui)"
            },
            Category = "REPARATION",
            SubHeading = "Sauvegardes et points de retour",
            Icon = "🔄",
            Action = (log, ct) => RepairService.OpenRestorePointDialog(log, ct),
            Tags = new[] { "sauvegarde", "restauration", "point de retour", "sécurité", "revenir", "annuler", "secours", "rstrui" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "repair_export_drivers",
            Name = "Sauvegarder les pilotes de mes équipements",
            Description = "Copie tous les pilotes de vos composants (carte graphique, Wi-Fi, son) dans un dossier pour pouvoir les réinstaller facilement.",
            Features = new[]
            {
                "Liste des pilotes des fabricants, sauvegarde au choix",
                "Un dossier par pilote, copiable sur clé USB",
                "Réinstallation en un clic après un formatage"
            },
            Category = "REPARATION",
            SubHeading = "Sauvegardes et points de retour",
            Icon = "💾",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.DriverBackupDialog(log, ct)),
            Tags = new[] { "pilotes", "drivers", "sauvegarder", "dossier", "export", "backup", "carte graphique", "wifi" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 4 : 🧹 NETTOYAGE ET ESPACE DISQUE
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterCleaningTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "clean_system_unified",
            Name = "Grand nettoyage du PC et des fichiers inutiles",
            Description = "Supprime les fichiers temporaires, vide la corbeille, nettoie les traces de navigation et libère plusieurs gigaoctets d'espace disque en toute sécurité.",
            Features = new[]
            {
                "Fichiers temporaires utilisateur, système et caches",
                "Vidage automatique de la Corbeille de tous les disques",
                "Nettoyage du magasin de composants Windows WinSxS"
            },
            Category = "NETTOYAGE",
            SubHeading = "Nettoyage système et disque",
            Icon = "🧹",
            Action = (log, ct) => CleaningService.OpenSystemCleanupDialog(log, ct),
            DesktopAction = log => CleaningService.CreateDesktopCleanupScript(log),
            DesktopHint = "Double-cliquez dessus à tout moment pour lancer le nettoyage complet avec le détail de l'espace libéré.",
            Tags = new[] { "nettoyer", "libérer", "espace", "disque", "temporaires", "corbeille", "cache", "navigateurs", "cleanmgr", "accélérer" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 5 : 🔑 MOTS DE PASSE ET CLÉS DE LICENCE
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterSecretsTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "sec_wifi_show",
            Name = "Retrouver mes mots de passe Wi-Fi enregistrés",
            Description = "Retrouve le mot de passe de votre box Wi-Fi et de tous les réseaux auxquels le PC s'est déjà connecté.",
            Features = new[]
            {
                "Tous les réseaux mémorisés, réseau actuel en tête",
                "Mots de passe masqués, affichables un par un",
                "Copie en 1 clic et export dans un fichier texte"
            },
            Category = "SECRETS",
            SubHeading = "Mots de passe et Wi-Fi",
            Icon = "📶",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.WifiPasswordsDialog(log, ct)),
            Tags = new[] { "wifi", "mot de passe", "box", "clé", "clair", "retrouver", "code", "sans fil" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "sec_browser_profiles",
            Name = "Consulter l'historique de mes navigateurs web",
            Description = "Affiche les pages visitées sur Chrome, Edge, Brave, Vivaldi, Opera et Firefox (tous profils), sans ouvrir les navigateurs.",
            Features = new[]
            {
                "Recherche, filtres par navigateur et par période",
                "Sites les plus visités en un coup d'œil",
                "Export Excel (CSV) de l'historique affiché"
            },
            Category = "SECRETS",
            SubHeading = "Mots de passe et Wi-Fi",
            Icon = "🌐",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.BrowserHistoryDialog(log, ct)),
            Tags = new[] { "historique", "sites", "web", "pages", "chrome", "edge", "firefox", "brave", "navigation" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "sec_win_key",
            Name = "Retrouver ma clé d'activation Windows originale",
            Description = "Lit la clé de licence officielle gravée dans la carte mère de votre PC, indispensable en cas de réinstallation complète de Windows.",
            Features = new[]
            {
                "Clé gravée dans le BIOS / UEFI et clé installée",
                "État d'activation, édition et type de licence",
                "Copie en 1 clic et export dans un fichier texte"
            },
            Category = "SECRETS",
            SubHeading = "Licences et documents confidentiels",
            Icon = "🔑",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.WindowsKeyDialog(log, ct)),
            Tags = new[] { "clé", "licence", "windows", "activation", "produit", "carte mère", "oem", "bios", "uefi" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "sec_audit_sensitive",
            Name = "Rechercher mes documents et codes confidentiels",
            Description = "Repère dans vos dossiers les mots de passe en clair, IBAN, cartes bancaires, pièces d'identité, clés privées et jetons d'API, classés par gravité.",
            Features = new[]
            {
                "Analyse des noms, des scans et du contenu des fichiers",
                "Classement : secrets en clair, documents, accès techniques",
                "Copie vers une clé USB, rapport HTML ou mise à la corbeille"
            },
            Category = "SECRETS",
            SubHeading = "Licences et documents confidentiels",
            Icon = "🔍",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.SensitiveDocsDialog(log, ct)),
            Tags = new[] { "recherche", "documents", "secrets", "confidentiels", "mots de passe", "ssh", "tokens", "sécurité" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 6 : 🌐 INTERNET ET RÉSEAU
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterNetworkTools()
    {
        // ─── HUB 1 : Centre Internet & Connexion (diagnostic + DNS, benchmark DNS, grand reset) ───
        _allTools.Add(new ScriptTool
        {
            Id = "net_hub_internet",
            Name = "Centre Internet & Connexion",
            Description = "Tout pour votre connexion dans une fenêtre à onglets : diagnostic et changement de DNS, comparatif de vitesse des serveurs DNS, et réparation complète (Grand Reset) en cas de panne.",
            Features = new[]
            {
                "Diagnostic connexion, IP, et changement de DNS (Cloudflare, Google, Quad9)",
                "Benchmark de latence DNS (ping réel) et recommandation du plus rapide",
                "Réparation Internet en 7 étapes (DNS, Winsock, TCP/IP…) + test final"
            },
            Category = "RESEAU",
            SubHeading = "Connexion Internet & Wi-Fi",
            Icon = "🌐",
            Action = (log, ct) => DialogHelper.ShowToolAsync(
                "Centre Internet & Connexion",
                ("Diagnostic & DNS", new Views.Controls.NetworkCenterDialog(log, ct)),
                ("Benchmark DNS", new Views.Controls.DnsBenchmarkDialog(log, ct)),
                ("Réparer (Grand Reset)", new Views.Controls.NetworkResetDialog(log, ct))),
            Tags = new[] { "internet", "connexion", "réseau", "dns", "accélérer", "sites", "cloudflare", "google", "quad9", "benchmark", "latence", "ping", "panne", "réparer", "coupure", "reset", "réinitialiser", "bloqué", "dhcp", "winsock", "tcp" }
        });

        // ─── HUB 2 : Wi-Fi (réseaux voisins + bilan des déconnexions) ───
        _allTools.Add(new ScriptTool
        {
            Id = "net_hub_wifi",
            Name = "Wi-Fi : voisins & déconnexions",
            Description = "Deux outils Wi-Fi réunis : repérez les box et canaux autour de vous pour choisir le meilleur, et consultez l'historique visuel de vos coupures de connexion sans fil.",
            Features = new[]
            {
                "Réseaux Wi-Fi voisins avec jauges de signal et choix du meilleur canal",
                "Bilan graphique des déconnexions sur 3 jours (rapport officiel Windows)",
                "Diagnostic des pertes de signal, erreurs de pilote ou de veille"
            },
            Category = "RESEAU",
            SubHeading = "Connexion Internet & Wi-Fi",
            Icon = "📶",
            Action = (log, ct) => DialogHelper.ShowToolAsync(
                "Wi-Fi : voisins & déconnexions",
                ("Réseaux voisins", new Views.Controls.WifiScannerDialog(log, ct)),
                ("Déconnexions (rapport)", new Views.Controls.WifiReportDialog(log, ct))),
            Tags = new[] { "wifi", "box", "voisins", "puissance", "signal", "jauges", "canaux", "scan", "sans fil", "déconnexions", "coupures", "stabilité", "graphique", "historique", "rapport" }
        });

        // ─── HUB 3 : Appareils & Connexions (LAN + flux TCP) ───
        _allTools.Add(new ScriptTool
        {
            Id = "net_hub_devices",
            Name = "Appareils & Connexions",
            Description = "Voyez qui et quoi est connecté : la liste des appareils branchés sur votre box (PC, téléphones, TV, imprimantes) et les applications de votre PC qui communiquent avec Internet.",
            Features = new[]
            {
                "Scan des appareils du réseau local (IP et MAC)",
                "Moniteur temps réel des applications connectées à Internet (TCP)",
                "Repérage facile des programmes suspects ou espions"
            },
            Category = "RESEAU",
            SubHeading = "Appareils & Analyse Web",
            Icon = "🖥️",
            Action = (log, ct) => DialogHelper.ShowToolAsync(
                "Appareils & Connexions",
                ("Appareils sur la Box (LAN)", new Views.Controls.LanScannerDialog(log, ct)),
                ("Applis connectées (TCP)", new Views.Controls.TcpConnectionsDialog(log, ct))),
            Tags = new[] { "box", "appareils", "qui est connecté", "téléphones", "ip", "mac", "réseau", "lan", "découverte", "routeur", "applications", "connexions", "internet", "flux", "espion", "ports", "processus", "surveiller", "tcp" }
        });

        // ─── Analyseur de site web (audit, Speedtest, SEO) ───
        _allTools.Add(new ScriptTool
        {
            Id = "nov_website_analyzer",
            Name = "Centre d'Audit & Analyseur de Site Web",
            Description = "Interface unifiée tout-en-un : entrez un domaine pour obtenir une synthèse globale (note A+ à D), la checklist des 21 contrôles pré-lancement, un Speedtest complet (DNS, TLS, TTFB, salves HTTP), l'analyse SEO (aperçu Google SERP, structure H1-H6), l'extracteur de mots-clés (1/2/3 mots + score SEO) et l'inspecteur SSL/Headers.",
            Features = new[]
            {
                "Tableau de bord global (Note A+ à D, 4 piliers, plan d'action prioritaire) & Checklist 21 points",
                "Speedtest Web (Waterfall DNS/TLS/TTFB, débit, 5 salves HTTP, simulation Fibre/4G/3G)",
                "Studio SEO & Mots-Clés (Aperçu Google SERP, Open Graph, H1-H6, densité lexicale, SSL & Headers)"
            },
            Category = "RESEAU",
            SubHeading = "Appareils & Analyse Web",
            Icon = "🌐",
            Action = (log, ct) => InnovationService.OpenWebsiteAnalyzerDialog(log, ct, initialTab: 0),
            Tags = new[] { "site", "web", "url", "analyseur", "audit", "speedtest", "vitesse", "seo", "serp", "mots-clés", "keywords", "densité", "h1", "rgpd", "cgu", "https", "ssl", "dns", "ttfb", "cookies", "open graph", "favicon", "sitemap", "robots.txt", "alt", "responsive", "404", "liens" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 7 : 💽 DISQUES ET STOCKAGE
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterDiskTools()
    {
        // Sous-titre interne : Organisation et gestion des disques
        _allTools.Add(new ScriptTool
        {
            Id = "disk_mgmt_gui",
            Name = "Gestionnaire de disques et clés USB",
            Description = "Outil visuel officiel de Windows pour voir vos disques durs, créer des espaces de stockage ou formater vos clés USB.",
            Features = new[]
            {
                "Visualisation graphique des volumes et partitions",
                "Création, réduction ou extension de volumes",
                "Attribution et modification des lettres de lecteurs"
            },
            Category = "DISQUE",
            SubHeading = "Organisation et gestion des disques",
            Icon = "💽",
            Action = (log, ct) => DiskService.LaunchDiskManagement(log, ct),
            Tags = new[] { "disques", "stockage", "clés usb", "partitions", "espaces", "formater", "gestionnaire", "diskmgmt" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "disk_diskpart",
            Name = "Nettoyage avancé de disque (Diskpart)",
            Description = "Invite de commandes avancée pour formater, effacer ou réparer un disque dur ou une clé USB récalcitrante.",
            Features = new[]
            {
                "Console système pour disques ou clés USB bloqués",
                "Nettoyage complet (clean), conversion GPT / MBR",
                "Déblocage des partitions protégées en écriture"
            },
            Category = "DISQUE",
            SubHeading = "Organisation et gestion des disques",
            Icon = "💻",
            Action = (log, ct) => DiskService.LaunchDiskpart(log, ct),
            Tags = new[] { "diskpart", "formater", "effacer", "avancé", "partitions", "clé usb", "cmd", "réparer" }
        });

        // Sous-titre interne : Santé et sécurité de vos fichiers
        _allTools.Add(new ScriptTool
        {
            Id = "disk_defrag_gui",
            Name = "Optimiser et accélérer les disques",
            Description = "Donne un coup de boost à vos disques : accélère les disques durs classiques et rafraîchit la mémoire des SSD récents.",
            Features = new[]
            {
                "Défragmentation ciblée des disques magnétiques",
                "Envoi de la commande TRIM pour rafraîchir les SSD",
                "Analyse et planification de l'état de fragmentation"
            },
            Category = "DISQUE",
            SubHeading = "Santé et sécurité de vos fichiers",
            Icon = "⚡",
            Action = (log, ct) => DiskService.LaunchDefragGui(log, ct),
            Tags = new[] { "optimiser", "accélérer", "défragmenter", "ssd", "trim", "disque dur", "vitesse", "boost" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "disk_bitlocker",
            Name = "Protéger et verrouiller un disque (BitLocker)",
            Description = "Verrouille l'accès à vos disques avec un mot de passe sécurisé pour que personne d'autre ne puisse lire vos données personnelles.",
            Features = new[]
            {
                "Statut de chiffrement en direct de chaque lecteur",
                "Verrouillage par mot de passe ou puce TPM",
                "Sauvegarde de la clé de récupération de secours"
            },
            Category = "DISQUE",
            SubHeading = "Santé et sécurité de vos fichiers",
            Icon = "🔒",
            Action = (log, ct) => DiskService.ManageBitLocker(log, ct),
            Tags = new[] { "verrouiller", "protéger", "bitlocker", "mot de passe", "chiffrement", "sécurité", "dossiers", "intrus" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "disk_profile_migration",
            Name = "Sauvegarder et transférer un profil utilisateur",
            Description = "Copie les fichiers personnels, favoris, réseaux Wi-Fi et la liste des logiciels vers une clé USB ou un disque, puis restaure le tout sur un nouveau PC.",
            Features = new[]
            {
                "Bureau, Documents, Images, Vidéos, Musique…",
                "Favoris Chrome / Edge / Firefox et profils Wi-Fi",
                "Réinstallation des logiciels via Winget"
            },
            Category = "DISQUE",
            SubHeading = "Sauvegarde et migration",
            Icon = "📦",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.ProfileMigrationDialog(log, ct)),
            Tags = new[] { "sauvegarde", "backup", "migration", "transfert", "nouveau pc", "clé usb", "profil", "documents", "favoris", "wifi", "restaurer" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 8 : 📦 INSTALLER VOS LOGICIELS
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterAppInstallationTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "app_installer_center",
            Name = "Installer vos applications indispensables",
            Description = "Installez ou mettez à jour facilement vos logiciels du quotidien (Google Chrome, VLC, 7-Zip, Sumatra PDF, PowerShell, Office, ArtCraft Launcher) en cochant simplement ceux que vous voulez.",
            Features = new[]
            {
                "Navigateurs et utilitaires essentiels (Chrome, VLC, 7-Zip, Sumatra PDF)",
                "Bureautique et console moderne (Office, PowerShell 7)",
                "Suite créative tout-en-un (ArtCraft Launcher & 13 applications libres)"
            },
            Category = "APPLICATIONS",
            SubHeading = "Installation et déploiement rapide",
            Icon = "📦",
            Action = (log, ct) => AppService.OpenAppInstallerDialog(log, ct),
            Tags = new[] { "installer", "applications", "logiciels", "programmes", "chrome", "vlc", "7zip", "office", "sumatra", "artcraft", "craft", "facile", "1-clic" }
        });


        _allTools.Add(new ScriptTool
        {
            Id = "app_debloat",
            Name = "Supprimer les applications préinstallées",
            Description = "Retire les jeux, publicités et applications promotionnelles livrés avec Windows ou le fabricant (Candy Crush, TikTok, essais…). Les composants essentiels ne sont jamais proposés.",
            Features = new[]
            {
                "Bloatware connu pré-sélectionné",
                "Option : tous les comptes, sans retour",
                "Réinstallable depuis le Microsoft Store"
            },
            Category = "APPLICATIONS",
            SubHeading = "Nettoyage des applications",
            Icon = "🧽",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.DebloatDialog(log, ct)),
            Tags = new[] { "bloatware", "debloat", "désinstaller", "préinstallé", "candy crush", "publicité", "applications inutiles", "appx", "nettoyer", "fabricant" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 9 : 🔄 MISES À JOUR DES PROGRAMMES
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterUpdatesTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "upd_winget_manager",
            Name = "Mettre à jour tous vos programmes",
            Description = "Recherche automatiquement les nouvelles versions de vos applications installées et met tout à jour en un seul clic.",
            Features = new[]
            {
                "Détection des mises à jour disponibles (Winget)",
                "Mise à jour sélective ou globale en 1 clic",
                "Téléchargement et installation silencieuse"
            },
            Category = "MISES_A_JOUR",
            SubHeading = "Gestionnaire de mises à jour",
            Icon = "🔄",
            Action = (log, ct) => AppService.OpenWingetManagerDialog(log, ct),
            Tags = new[] { "mises à jour", "programmes", "logiciels", "nouvelle version", "upgrade", "sécurité", "tout mettre à jour" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 10 : 🛡️ SÉCURITÉ ET ANTIVIRUS
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterSecurityTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "sec_center",
            Name = "Centre de protection antivirus et surveillance",
            Description = "Tableau de bord : vérifie si l'antivirus Windows est actif, surveille les plantages récents et identifie les logiciels qui saturent votre mémoire.",
            Features = new[]
            {
                "Statut de l'antivirus et du pare-feu Defender",
                "Surveillance des derniers plantages système (BSOD)",
                "Analyse des applications qui saturent la RAM"
            },
            Category = "SECURITE",
            SubHeading = "Protection globale de votre ordinateur",
            Icon = "🛡️",
            Action = (log, ct) => SecurityService.OpenSecurityCenterDialog(log, ct),
            Tags = new[] { "sécurité", "antivirus", "protection", "defender", "plantages", "ralentissement", "mémoire", "surveillance" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "nov_privacy_shield",
            Name = "Bouclier de confidentialité et anti-télémétrie",
            Description = "Désactive la télémétrie agressive de Microsoft, l'identifiant publicitaire ciblé, le suivi des frappes clavier et les suggestions de pub dans Démarrer.",
            Features = new[]
            {
                "Désactivation télémétrie et traçage Windows",
                "Blocage de l'identifiant pub et des suggestions",
                "Protection des frappes clavier et diagnostics"
            },
            Category = "SECURITE",
            SubHeading = "Confidentialité et vie privée",
            Icon = "🛡️",
            Action = (log, ct) => InnovationService.OpenPrivacyShieldDialog(log, ct),
            Tags = new[] { "confidentialité", "vie privée", "télémétrie", "diagtrack", "mouchard", "publicité", "sécurité", "tracking", "espion" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "sec_defender_quickscan",
            Name = "Analyse antivirus rapide en 1 clic",
            Description = "État de Microsoft Defender, mise à jour des signatures, analyse rapide ou complète et liste des menaces détectées.",
            Features = new[]
            {
                "Protection, signatures et dernière analyse en un coup d'œil",
                "Analyse rapide ou complète avec chronomètre",
                "Menaces détectées et neutralisation en 1 clic"
            },
            Category = "SECURITE",
            SubHeading = "Vérifications express",
            Icon = "⚡",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.DefenderScanDialog(log, ct)),
            Tags = new[] { "antivirus", "scan", "rapide", "analyse", "virus", "menaces", "defender", "sécurité" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "sec_perfmon_rel",
            Name = "Historique des plantages et bugs du PC",
            Description = "Affiche une frise chronologique officielle Windows avec une note de stabilité montrant les jours où des logiciels ou pilotes ont planté.",
            Features = new[]
            {
                "Frise chronologique de stabilité Windows (note 1-10)",
                "Détail des plantages matériels, pilotes ou logiciels",
                "Idéal pour repérer l'origine d'un bug récurrent"
            },
            Category = "SECURITE",
            SubHeading = "Vérifications express",
            Icon = "📉",
            Action = (log, ct) => DiagnosticService.LaunchReliabilityMonitor(log, ct),
            Tags = new[] { "plantages", "bugs", "historique", "stabilité", "fiabilité", "crashs", "problèmes", "perfmon" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "sec_user_accounts",
            Name = "Gérer les comptes utilisateurs",
            Description = "Liste les comptes locaux du PC et permet de changer un mot de passe, donner ou retirer les droits administrateur, activer, créer ou supprimer un compte.",
            Features = new[]
            {
                "Réinitialisation ou suppression de mot de passe",
                "Droits administrateur, activation / désactivation",
                "Création et suppression de comptes locaux"
            },
            Category = "SECURITE",
            SubHeading = "Comptes et accès",
            Icon = "👤",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.UserAccountsDialog(log, ct)),
            Tags = new[] { "compte", "utilisateur", "mot de passe", "oublié", "administrateur", "admin", "session", "créer", "supprimer", "user" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "sec_malware_cleanup",
            Name = "Désinfection et détournements",
            Description = "Détecte et corrige les détournements typiques des virus et adwares : fichier hosts, proxy, tâches planifiées et démarrages suspects. Analyses Defender et outils spécialisés.",
            Features = new[]
            {
                "Fichier hosts, proxy WinINET / WinHTTP",
                "Tâches et démarrages masqués ou encodés",
                "Analyse hors ligne, AdwCleaner, Malwarebytes"
            },
            Category = "SECURITE",
            SubHeading = "Désinfection",
            Icon = "🦠",
            Action = (log, ct) => DialogHelper.ShowDialogAsync(() => new Views.Controls.MalwareCleanupDialog(log, ct)),
            Tags = new[] { "virus", "malware", "adware", "désinfection", "hosts", "proxy", "pirate", "publicité", "pop-up", "adwcleaner", "malwarebytes", "rootkit" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 11 : 🎨 PERSONNALISATION DE WINDOWS
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterPersonalizationTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "tweak_godmode",
            Name = "Activer le dossier Tous les Réglages (God Mode)",
            Description = "Crée sur votre Bureau un dossier pratique regroupant plus de 200 raccourcis directs vers tous les réglages et options de Windows.",
            Features = new[]
            {
                "Création directe du dossier spécial sur le Bureau",
                "Plus de 200 réglages Windows classés par thème",
                "Accès rapide sans fouiller dans les paramètres"
            },
            Category = "PERSONNALISATION",
            SubHeading = "Options et réglages système",
            Icon = "👑",
            Action = (log, ct) => PersonalizationService.CreateGodMode(log, ct),
            Tags = new[] { "god mode", "réglages", "options", "panneau de configuration", "raccourci", "pratique", "bureau" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "tweak_power_shortcuts",
            Name = "Boutons Éteindre et Redémarrer sur le Bureau",
            Description = "Place sur votre Bureau des boutons élégants avec leurs icônes officielles pour éteindre ou redémarrer votre ordinateur en un seul clic.",
            Features = new[]
            {
                "Bouton Arrêter le PC avec l'icône rouge officielle Power (shell32.dll, 27)",
                "Bouton Redémarrer le PC avec l'icône officielle de redémarrage (shell32.dll, 238)",
                "Gestionnaire interactif avec vérification en temps réel du Bureau"
            },
            Category = "PERSONNALISATION",
            SubHeading = "Raccourcis et ergonomie du Bureau",
            Icon = "⚡",
            Action = (log, ct) => PersonalizationService.OpenPowerShortcutsDialog(log, ct),
            Tags = new[] { "éteindre", "arrêter", "redémarrer", "bureau", "bouton", "raccourci", "icône", "power", "shutdown", "reboot", "personnalisation" }
        });

        _allTools.Add(new ScriptTool
        {
            Id = "tweak_context_menu_items",
            Name = "Menu contextuel",
            Description = "Centre complet de personnalisation du clic droit : basculez entre le menu classique Windows 10 et le menu moderne Windows 11, ajoutez les options pour ouvrir CMD ou PowerShell en Administrateur dans le dossier actif, et « Forcer la suppression » pour supprimer les fichiers et dossiers qui refusent de partir.",
            Features = new[]
            {
                "Bascule en 1 clic : Grand menu classique Windows 10 ou moderne Windows 11",
                "Ajout de « Ouvrir CMD (Admin) » et « Ouvrir PowerShell (Admin) » au clic droit",
                "« Forcer la suppression » : programmes et services qui bloquent fermés, autorisations ignorées",
                "Prise en compte immédiate avec rafraîchissement de l'Explorateur Windows"
            },
            Category = "PERSONNALISATION",
            SubHeading = "Menu contextuel & Clic droit",
            Icon = "🖱️",
            Action = (log, ct) => PersonalizationService.OpenContextMenuItemsDialog(log, ct),
            Tags = new[] { "menu contextuel", "clic droit", "windows 10", "windows 11", "classique", "cmd", "powershell", "admin", "invite de commandes", "dossier", "images", "bureau", "terminal", "personnaliser", "forcer", "suppression", "supprimer", "effacer", "bloqué", "récalcitrant", "impossible à supprimer", "fichier utilisé", "accès refusé", "autorisation", "propriétaire" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 12 : ⚡ DÉMARRAGE & RÉCUPÉRATION
    // ─────────────────────────────────────────────────────────────────────────
    private static void RegisterBootTools()
    {
        _allTools.Add(new ScriptTool
        {
            Id = "boot_center",
            Name = "Centre de démarrage et récupération",
            Description = "Interface unifiée : redémarrez en mode sans échec (minimal ou réseau), accédez au WinRE, basculez vers le BIOS/UEFI ou réparez le menu de démarrage cassé (BCD).",
            Features = new[]
            {
                "Redémarrage direct en Mode sans échec (Safe Mode)",
                "Accès en 1 clic au dépannage Windows (WinRE)",
                "Bascule immédiate vers le BIOS/UEFI et BCD"
            },
            Category = "DEMARRAGE",
            SubHeading = "Console interactive de démarrage",
            Icon = "⚡",
            Action = (log, ct) => BootService.OpenBootRecoveryDialog(log, ct),
            Tags = new[] { "démarrage", "récupération", "sans échec", "winre", "bios", "uefi", "bcd", "boot", "réparation", "redémarrage" }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CATÉGORIE 13 : ✨ NOUVEAUTÉS & INNOVATIONS
    // ─────────────────────────────────────────────────────────────────────────
}


