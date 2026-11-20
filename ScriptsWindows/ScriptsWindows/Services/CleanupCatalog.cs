using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public enum CleanupLevel { Recommended, Advanced }

/// <summary>
/// Un élément du Grand nettoyage : sait mesurer l'espace récupérable et nettoyer en renvoyant l'espace réellement libéré.
/// Measure : octets, ou -1 si inconnu à l'avance (DISM), ou -2 pour un élément sans espace disque (cache DNS).
/// </summary>
public sealed class CleanupItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public CleanupLevel Level { get; init; }
    /// <summary>Contrepartie à connaître avant de cocher (affichée en orange).</summary>
    public string Warning { get; init; } = "";
    public required Func<CancellationToken, Task<long>> Measure { get; init; }
    public required Func<Action<string>, CancellationToken, Task<long>> Clean { get; init; }
    public bool DefaultChecked => Level == CleanupLevel.Recommended;
}

/// <summary>
/// Catalogue du Grand nettoyage. Toutes les suppressions de fichiers passent par <see cref="DeleteContents"/> :
/// contenu du dossier seulement (jamais le dossier lui-même), jonctions et liens jamais suivis, fichiers verrouillés
/// ignorés, espace compté fichier par fichier réellement supprimé.
/// </summary>
public static class CleanupCatalog
{
    private const long DnsMarker = -2, Unknown = -1;
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string LocalLow => Path.Combine(Path.GetDirectoryName(Local)!, "LocalLow");
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string SystemDrive => Path.GetPathRoot(WinDir)!;

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        > 0 => $"{Math.Max(1, bytes / 1024)} Ko",
        _ => "0 Mo"
    };

    public static List<CleanupItem> GetItems()
    {
        var items = new List<CleanupItem>
        {
            // ─── Recommandés ───
            TempItem(),
            new()
            {
                Id = "wu", Title = "Cache de Windows Update",
                Description = "Mises à jour déjà téléchargées et installées (SoftwareDistribution\\Download). Le service Windows Update est arrêté pendant le nettoyage puis relancé.",
                Measure = ct => Task.Run(() => SizeOf(WuDownload), ct),
                Clean = async (log, ct) =>
                {
                    await CommandRunner.RunAsync("net stop wuauserv /y", _ => { }, ct);
                    try { return await Task.Run(() => DeleteContents(WuDownload), ct); }
                    finally { await CommandRunner.RunAsync("net start wuauserv", _ => { }, CancellationToken.None); }
                }
            },
            new()
            {
                Id = "delivery", Title = "Cache d'optimisation de livraison",
                Description = "Morceaux de mises à jour partagés avec d'autres PC (Delivery Optimization). Vidé par la commande officielle de Windows.",
                Measure = ct => Task.Run(() => SizeOf(DeliveryCache), ct),
                Clean = async (log, ct) =>
                {
                    long before = SizeOf(DeliveryCache);
                    await PowerShellRunner.RunAsync("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue", ct);
                    return Math.Max(0, before - SizeOf(DeliveryCache));
                }
            },
            Folders("browsers", "Caches des navigateurs et miniatures",
                    "Pages et images en cache de Chrome, Edge, Brave, Vivaldi et Firefox (tous les profils) et miniatures de l'Explorateur. Mots de passe, historique et favoris ne sont pas touchés. Fermez les navigateurs pour tout nettoyer.",
                    BrowserCacheFolders, extraFiles: ThumbnailFiles),
            Folders("wer", "Rapports d'erreurs et de plantage",
                    "Rapports d'erreurs Windows (WER) et vidages laissés par les logiciels qui ont planté (CrashDumps).",
                    () => new[] { Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
                                  Path.Combine(ProgramData, @"Microsoft\Windows\WER\Temp"), Path.Combine(Local, @"Microsoft\Windows\WER"), Path.Combine(Local, "CrashDumps") }),
            Folders("shaders", "Caches graphiques (DirectX, NVIDIA, AMD, Intel)",
                    "Shaders précompilés par la carte graphique. Recréés automatiquement : le premier lancement d'un jeu peut être un peu plus lent.",
                    () => new[] { Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"),
                                  Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\GLCache"),
                                  Path.Combine(LocalLow, @"Intel\ShaderCache") }),
            Folders("logs", "Anciens journaux d'installation de Windows",
                    "Journaux CBS et DISM de plus de 7 jours (Windows\\Logs). Les journaux récents sont gardés pour un éventuel dépannage.",
                    () => new[] { Path.Combine(WinDir, @"Logs\CBS"), Path.Combine(WinDir, @"Logs\DISM") }, olderThanDays: 7),
            new()
            {
                Id = "recycle", Title = "Corbeille",
                Description = "Fichiers supprimés de ce compte sur tous les disques. Ils seront définitivement effacés.",
                Measure = ct => Task.Run(() => RecycleBinFolders().Sum(SizeOf), ct),
                Clean = async (log, ct) =>
                {
                    long before = await Task.Run(() => RecycleBinFolders().Sum(SizeOf), ct);
                    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND : aucune fenêtre
                    int hr = SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
                    if (hr != 0 && before > 0) log($"[!] Corbeille : Windows a renvoyé le code 0x{hr:X8}.");
                    return Math.Max(0, before - await Task.Run(() => RecycleBinFolders().Sum(SizeOf), ct));
                }
            },
            new()
            {
                Id = "dns", Title = "Cache DNS",
                Description = "Adresses de sites mémorisées (ipconfig /flushdns) : utile après un changement de box ou de DNS. Ne libère pas d'espace disque.",
                Measure = _ => Task.FromResult(DnsMarker),
                Clean = async (log, ct) => { await CommandRunner.RunAsync("ipconfig /flushdns", _ => { }, ct); return 0; }
            },

            // ─── Avancés ───
            new()
            {
                Id = "drivers", Title = "Anciennes versions des pilotes", Level = CleanupLevel.Advanced,
                Description = "Versions de pilotes remplacées par une plus récente (carte graphique, son, puce…). La version la plus récente et les pilotes en service sont toujours conservés.",
                Warning = "Il ne sera plus possible de revenir à la version précédente d'un pilote (« Restaurer le pilote »).",
                Measure = async ct => (await DriverStoreService.AnalyzeAsync(ct)).OldVersions.Where(o => !o.Package.InUse).Sum(o => o.Package.Size),
                Clean = async (log, ct) =>
                {
                    var old = (await DriverStoreService.AnalyzeAsync(ct)).OldVersions.Where(o => !o.Package.InUse).ToList();
                    long freed = 0; int ok = 0;
                    foreach (var o in old)
                    {
                        ct.ThrowIfCancellationRequested();
                        var (done, code) = await DriverStoreService.DeleteAsync(o.Package.Inf, ct);
                        if (done) { freed += o.Package.Size; ok++; }
                        else log($"[!] {o.Package.OriginalName} {o.Package.VersionText} ({o.Package.Inf}) conservé : Windows refuse (code {code}).");
                    }
                    log($"[i] Pilotes : {ok} / {old.Count} ancienne(s) version(s) supprimée(s).");
                    return freed;
                }
            },
            new()
            {
                Id = "winsxs", Title = "Composants système remplacés (WinSxS)", Level = CleanupLevel.Advanced,
                Description = "Anciennes versions des composants de Windows conservées après les mises à jour (DISM /StartComponentCleanup /ResetBase). Dure de 3 à 15 minutes.",
                Warning = "Les mises à jour déjà installées ne pourront plus être désinstallées.",
                Measure = _ => Task.FromResult(Unknown),
                Clean = async (log, ct) =>
                {
                    long before = FreeSpace();
                    await CommandRunner.RunAsync("DISM /Online /Cleanup-Image /StartComponentCleanup /ResetBase", line =>
                    {
                        if (line.Contains('%') || line.Contains("Erreur", StringComparison.OrdinalIgnoreCase) || line.Contains("Error", StringComparison.OrdinalIgnoreCase)) log(line.Trim());
                    }, ct);
                    return Math.Max(0, FreeSpace() - before);
                }
            },
            Folders("dumps", "Vidages mémoire des écrans bleus",
                    "Fichiers enregistrés par Windows lors des écrans bleus (MEMORY.DMP, Minidump, LiveKernelReports).",
                    () => new[] { Path.Combine(WinDir, "Minidump"), Path.Combine(WinDir, "LiveKernelReports") },
                    extraFiles: () => new[] { Path.Combine(WinDir, "MEMORY.DMP") }, level: CleanupLevel.Advanced,
                    warning: "Ils servent à trouver la cause d'un écran bleu (outil « Écrans bleus et périphériques en erreur ») : à garder si le PC plante."),
        };

        // Ancienne installation de Windows : proposée seulement si elle existe
        if (PreviousWindowsFolders().Any(Directory.Exists))
        {
            items.Add(new()
            {
                Id = "windowsold", Title = "Ancienne installation de Windows (Windows.old)", Level = CleanupLevel.Advanced,
                Description = "Copie de la version précédente de Windows gardée après une mise à niveau, et fichiers temporaires d'installation. Supprimée par l'outil officiel de Windows (cleanmgr).",
                Warning = "Il ne sera plus possible de revenir à la version précédente de Windows.",
                Measure = ct => Task.Run(() => PreviousWindowsFolders().Sum(SizeOf), ct),
                Clean = async (log, ct) =>
                {
                    long before = FreeSpace();
                    await RunCleanmgrHandlersAsync(new[] { "Previous Installations", "Temporary Setup Files" }, ct);
                    return Math.Max(0, FreeSpace() - before);
                }
            });
        }
        return items;
    }

    // ─── Constructeur d'élément « dossiers » ─────────────────────────────────────

    private static CleanupItem Folders(string id, string title, string description, Func<IEnumerable<string>> folders,
                                       Func<IEnumerable<string>>? extraFiles = null, int olderThanDays = 0,
                                       CleanupLevel level = CleanupLevel.Recommended, string warning = "") => new()
    {
        Id = id, Title = title, Description = description, Level = level, Warning = warning,
        Measure = ct => Task.Run(() =>
            folders().Distinct(StringComparer.OrdinalIgnoreCase).Sum(f => SizeOf(f, olderThanDays))
            + (extraFiles?.Invoke() ?? Array.Empty<string>()).Sum(FileSize), ct),
        Clean = (log, ct) => Task.Run(() =>
        {
            long freed = 0;
            foreach (var f in folders().Distinct(StringComparer.OrdinalIgnoreCase)) { ct.ThrowIfCancellationRequested(); freed += DeleteContents(f, olderThanDays); }
            foreach (var file in extraFiles?.Invoke() ?? Array.Empty<string>()) freed += DeleteFile(file);
            return freed;
        }, ct)
    };

    /// <summary>Temporaires : dossiers « .net » des applications en cours protégés, anciennes extractions d'Alex Toolkit retirées.</summary>
    private static CleanupItem TempItem()
    {
        var b = Folders("temp", "Fichiers temporaires",
            "Fichiers laissés par les logiciels et les installations (%TEMP% et Windows\\Temp). Les fichiers en cours d'utilisation et ceux des applications ouvertes sont ignorés.",
            () => new[] { Path.GetTempPath(), Path.Combine(WinDir, "Temp") });
        return new CleanupItem
        {
            Id = b.Id, Title = b.Title, Description = b.Description, Level = b.Level, Warning = b.Warning, Measure = b.Measure,
            Clean = async (log, ct) => await b.Clean(log, ct) + await Task.Run(DeleteStaleToolkitExtractions, ct)
        };
    }

    // ─── Emplacements ────────────────────────────────────────────────────────────

    private static string WuDownload => Path.Combine(WinDir, @"SoftwareDistribution\Download");
    private static string DeliveryCache => Path.Combine(WinDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");

    private static IEnumerable<string> PreviousWindowsFolders() => new[]
        { Path.Combine(SystemDrive, "Windows.old"), Path.Combine(SystemDrive, "$Windows.~BT"), Path.Combine(SystemDrive, "$Windows.~WS") };

    /// <summary>Caches des navigateurs, tous profils : Cache, Code Cache, GPUCache (Chromium), cache2 / startupCache (Firefox).</summary>
    internal static IEnumerable<string> BrowserCacheFolders()
    {
        var bases = new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data", @"Vivaldi\User Data" };
        foreach (var b in bases.Select(b => Path.Combine(Local, b)).Where(Directory.Exists))
        {
            yield return Path.Combine(b, "ShaderCache");
            yield return Path.Combine(b, "GrShaderCache");
            IEnumerable<string> profiles;
            try { profiles = Directory.EnumerateDirectories(b).Where(p => { var n = Path.GetFileName(p); return n == "Default" || n.StartsWith("Profile ") || n == "Guest Profile"; }).ToList(); }
            catch { continue; }
            foreach (var p in profiles)
                foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" }) yield return Path.Combine(p, c);
        }
        string ff = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff))
        {
            List<string> profiles;
            try { profiles = Directory.EnumerateDirectories(ff).ToList(); } catch { yield break; }
            foreach (var p in profiles) { yield return Path.Combine(p, "cache2"); yield return Path.Combine(p, "startupCache"); }
        }
    }

    private static IEnumerable<string> ThumbnailFiles()
    {
        string dir = Path.Combine(Local, @"Microsoft\Windows\Explorer");
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "thumbcache_*.db").ToList() : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }

    /// <summary>Corbeille de CE compte sur chaque disque fixe ($Recycle.Bin\SID) : c'est ce que SHEmptyRecycleBin vide.</summary>
    private static IEnumerable<string> RecycleBinFolders()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        if (sid.Length == 0) yield break;
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.IsReady && d.DriveType == DriveType.Fixed; } catch { ok = false; }
            if (ok) yield return Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
        }
    }

    // ─── Mesure et suppression sûres ─────────────────────────────────────────────

    private static long FreeSpace() { try { return new DriveInfo(SystemDrive).AvailableFreeSpace; } catch { return 0; } }

    private static long FileSize(string file) { try { return File.Exists(file) ? new FileInfo(file).Length : 0; } catch { return 0; } }

    /// <summary>Taille du contenu d'un dossier (jonctions non suivies), éventuellement des seuls fichiers plus vieux que N jours.</summary>
    internal static long SizeOf(string folder) => SizeOf(folder, 0);

    internal static long SizeOf(string folder, int olderThanDays)
    {
        long size = 0;
        DateTime limit = DateTime.Now.AddDays(-olderThanDays);
        foreach (var f in EnumerateFilesSafe(folder))
            try { if (olderThanDays == 0 || f.LastWriteTime < limit) size += f.Length; } catch { }
        return size;
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafe(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) yield break;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(folder));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileInfo[] files; DirectoryInfo[] dirs;
            try { files = dir.GetFiles(); dirs = dir.GetDirectories(); } catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !IsProtectedDir(d)) stack.Push(d);
        }
    }

    /// <summary>
    /// Dossiers jamais vidés : « .net » = DLL natives extraites par les applications .NET en un seul fichier
    /// (dont Alex Toolkit). Elles ne sont chargées qu'au besoin, donc pas verrouillées : les supprimer pendant que
    /// l'application tourne la fait planter (vcruntime140_cor3.dll introuvable à la fermeture, SQLite indisponible).
    /// </summary>
    internal static bool IsProtectedDir(DirectoryInfo d) => d.Name.Equals(".net", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dossiers d'extraction des anciennes versions d'Alex Toolkit (%TEMP%\.net\AlexToolkit_v…) qui ne tournent plus :
    /// supprimés (ils ne servent plus) ; celui d'une version encore ouverte est conservé.
    /// </summary>
    internal static long DeleteStaleToolkitExtractions()
    {
        long freed = 0;
        string root = Path.Combine(Path.GetTempPath(), ".net");
        if (!Directory.Exists(root)) return 0;
        string current = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        foreach (var d in new DirectoryInfo(root).GetDirectories("AlexToolkit*"))
        {
            if (d.Name.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
            if (Process.GetProcessesByName(d.Name).Length > 0) continue;   // cette version est ouverte
            try
            {
                long size = d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                d.Delete(true);
                freed += size;
            }
            catch { }
        }
        return freed;
    }

    /// <summary>
    /// Vide un dossier (le dossier lui-même est gardé) : fichiers supprimés un par un (verrouillés = ignorés),
    /// puis sous-dossiers devenus vides. Une jonction ou un lien est retiré sans toucher à sa cible.
    /// Renvoie les octets réellement supprimés.
    /// </summary>
    internal static long DeleteContents(string folder, int olderThanDays = 0)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
        if (!IsSafeTarget(folder)) return 0;
        long freed = 0;
        DateTime limit = DateTime.Now.AddDays(-olderThanDays);
        foreach (var f in EnumerateFilesSafe(folder).ToList())
        {
            try
            {
                if (olderThanDays > 0 && f.LastWriteTime >= limit) continue;
                long len = f.Length;
                if ((f.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0) f.Attributes = FileAttributes.Normal;
                f.Delete();
                freed += len;
            }
            catch { /* fichier en cours d'utilisation ou protégé */ }
        }
        // Sous-dossiers vides (du plus profond au moins profond) ; liens supprimés sans suivre leur cible
        try
        {
            var dirs = new List<DirectoryInfo>();
            var stack = new Stack<DirectoryInfo>(); stack.Push(new DirectoryInfo(folder));
            while (stack.Count > 0)
            {
                DirectoryInfo[] subs;
                try { subs = stack.Pop().GetDirectories(); } catch { continue; }
                foreach (var d in subs)
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) { try { d.Delete(false); } catch { } continue; }
                    dirs.Add(d); stack.Push(d);
                }
            }
            foreach (var d in dirs.OrderByDescending(d => d.FullName.Length))
                try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(false); } catch { }
        }
        catch { }
        return freed;
    }

    /// <summary>
    /// Garde-fou : jamais une racine de disque ni un dossier vital (Windows, Program Files, profil, Bureau, Documents,
    /// AppData…), même si une variable d'environnement est mal réglée (TEMP=C:\ viderait le disque). Le dossier
    /// temporaire de l'utilisateur n'est accepté que s'il s'appelle bien « Temp » / « Tmp ».
    /// </summary>
    internal static bool IsSafeTarget(string folder)
    {
        // « C: » sans barre oblique = dossier courant de ce lecteur (n'importe où) : refusé, comme tout chemin relatif.
        if (!Path.IsPathFullyQualified(folder)) return false;
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\'); } catch { return false; }
        if (full.Length <= 3 || Path.GetPathRoot(full)!.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)) return false;
        var vital = new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
        }.Select(Environment.GetFolderPath).Where(p => p.Length > 0).Select(p => p.TrimEnd('\\'));
        if (vital.Any(v => v.Equals(full, StringComparison.OrdinalIgnoreCase))) return false;
        string users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\')) ?? "";
        if (users.Length > 0 && full.Equals(users, StringComparison.OrdinalIgnoreCase)) return false;
        string tmp = Path.GetTempPath().TrimEnd('\\');
        if (full.Equals(tmp, StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(full);
            if (!name.Equals("Temp", StringComparison.OrdinalIgnoreCase) && !name.Equals("Tmp", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static long DeleteFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return 0;
            var fi = new FileInfo(file); long len = fi.Length;
            fi.Attributes = FileAttributes.Normal; fi.Delete();
            return len;
        }
        catch { return 0; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    /// <summary>Lance les gestionnaires officiels de cleanmgr choisis (/sagerun), sans interface de sélection, puis retire le réglage.</summary>
    private static async Task RunCleanmgrHandlersAsync(string[] handlers, CancellationToken ct)
    {
        const string flag = "StateFlags0077";
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\";
        var set = new List<string>();
        try
        {
            foreach (var h in handlers)
            {
                using var k = Registry.LocalMachine.OpenSubKey(root + h, writable: true);
                if (k == null) continue;
                k.SetValue(flag, 2, RegistryValueKind.DWord);
                set.Add(h);
            }
            if (set.Count == 0) return;
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), "/sagerun:77") { UseShellExecute = false })!;
            await p.WaitForExitAsync(ct);
        }
        finally
        {
            foreach (var h in set)
                try { using var k = Registry.LocalMachine.OpenSubKey(root + h, writable: true); k?.DeleteValue(flag, false); } catch { }
        }
    }
}
