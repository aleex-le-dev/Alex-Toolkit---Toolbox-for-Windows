using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Win32;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public enum MigrationKind { Folder, Bookmarks, Wifi, Apps }

/// <summary>Élément sauvegardable du profil (dossier, favoris, Wi-Fi, logiciels).</summary>
public class MigrationItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _sizeText = "Calcul…";
    private bool _isAvailable = true;

    public required string Key { get; init; }
    public required string Label { get; init; }
    public required MigrationKind Kind { get; init; }
    public string SourcePath { get; init; } = "";
    public string Hint { get; init; } = "";
    /// <summary>Taille en octets (dossiers), -1 si inconnue.</summary>
    public long SizeBytes { get; set; } = -1;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }
    public bool IsAvailable { get => _isAvailable; set => Set(ref _isAvailable, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Manifeste écrit à la racine de chaque sauvegarde (sauvegarde.json).</summary>
public class BackupManifest
{
    public string Computer { get; set; } = "";
    public string User { get; set; } = "";
    public DateTime Date { get; set; }
    public List<string> Items { get; set; } = new();
    public string ToolVersion { get; set; } = "";
}

/// <summary>
/// Sauvegarde et migration du profil de l'utilisateur connecté vers un disque externe / une clé USB,
/// puis restauration sur un autre PC. La restauration n'efface jamais rien et n'écrase pas un fichier plus récent.
/// </summary>
public static class ProfileMigrationService
{
    public const string ManifestName = "sauvegarde.json";
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string ChromeUserData => Path.Combine(Local, @"Google\Chrome\User Data");
    private static string EdgeUserData => Path.Combine(Local, @"Microsoft\Edge\User Data");

    /// <summary>Profils d'un navigateur Chromium possédant des favoris (Default, Profile 1, Profile 2…).</summary>
    private static IEnumerable<string> BookmarkProfiles(string userData)
        => Directory.Exists(userData)
            ? Directory.GetDirectories(userData).Where(d => File.Exists(Path.Combine(d, "Bookmarks"))).Select(d => Path.GetFileName(d))
            : Enumerable.Empty<string>();
    private static string FirefoxProfiles => Path.Combine(Roaming, @"Mozilla\Firefox\Profiles");

    /// <summary>Dossier « Téléchargements » réel (peut être déplacé par l'utilisateur).</summary>
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

    /// <summary>Éléments du profil actuel (chemins réels, y compris redirections OneDrive).</summary>
    public static List<MigrationItem> GetItems() => new()
    {
        new() { Key = "Bureau",            Label = "Bureau",            Kind = MigrationKind.Folder, SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) },
        new() { Key = "Documents",         Label = "Documents",         Kind = MigrationKind.Folder, SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
        new() { Key = "Images",            Label = "Images",            Kind = MigrationKind.Folder, SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) },
        new() { Key = "Videos",            Label = "Vidéos",            Kind = MigrationKind.Folder, SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) },
        new() { Key = "Musique",           Label = "Musique",           Kind = MigrationKind.Folder, SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) },
        new() { Key = "Telechargements",   Label = "Téléchargements",   Kind = MigrationKind.Folder, SourcePath = DownloadsFolder(), IsSelected = false },
        new() { Key = "Favoris",           Label = "Favoris des navigateurs", Kind = MigrationKind.Bookmarks, Hint = "Chrome, Edge, Firefox" },
        new() { Key = "WiFi",              Label = "Réseaux Wi-Fi enregistrés", Kind = MigrationKind.Wifi, Hint = "Clés incluses : protégez la sauvegarde" },
        new() { Key = "Applications",      Label = "Liste des logiciels installés", Kind = MigrationKind.Apps, Hint = "Réinstallation automatique via Winget" },
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} Ko",
        _ => $"{bytes} o"
    };

    public static long FolderSize(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return -1;
        long size = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try { foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts)) { try { size += f.Length; } catch { } } } catch { }
        return size;
    }

    /// <summary>Calcule la taille de chaque élément (hors thread UI).</summary>
    public static Task ComputeSizesAsync(IEnumerable<MigrationItem> items) => Task.Run(() =>
    {
        foreach (var item in items)
        {
            string text; bool available = true; long bytes = -1;
            switch (item.Kind)
            {
                case MigrationKind.Folder:
                    bytes = FolderSize(item.SourcePath);
                    available = bytes >= 0;
                    text = available ? FormatSize(bytes) : "Introuvable";
                    break;
                case MigrationKind.Bookmarks:
                    var found = new List<string>();
                    if (BookmarkProfiles(ChromeUserData).Any()) found.Add("Chrome");
                    if (BookmarkProfiles(EdgeUserData).Any()) found.Add("Edge");
                    if (Directory.Exists(FirefoxProfiles)) found.Add("Firefox");
                    available = found.Count > 0;
                    text = available ? string.Join(", ", found) : "Aucun navigateur";
                    break;
                default:
                    text = item.Hint;
                    break;
            }
            System.Windows.Application.Current?.Dispatcher.Invoke(() => { item.SizeBytes = bytes; item.SizeText = text; item.IsAvailable = available; if (!available) item.IsSelected = false; });
        }
    });

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Avancement d'une sauvegarde / restauration (affiché par la fenêtre).</summary>
    /// <param name="ItemTotal">0 pour un élément sans volume (Wi-Fi, logiciels, favoris) : avancement indéterminé.</param>
    public record CopyProgress(string Item, int Index, int Count,
                               long ItemDone, long ItemTotal, int FilesDone, int FilesTotal,
                               long OverallDone, long OverallTotal, double BytesPerSecond);

    /// <summary>
    /// Dossiers techniques régénérables, ignorés par défaut à la sauvegarde : des centaines de milliers de petits
    /// fichiers (très lents sur une clé USB) recréés par « npm install » / Python. Les .git (historique) restent copiés.
    /// </summary>
    public static readonly string[] RegenerableDirs = { "node_modules", "__pycache__" };

    /// <summary>Taille et nombre de fichiers d'un dossier (jonctions ignorées, comme robocopy /XJ ; dossiers exclus par nom, comme /XD).</summary>
    public static (long Bytes, int Files) FolderStats(string path, IReadOnlyCollection<string>? excludeDirs = null)
    {
        long size = 0; int files = 0;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return (0, 0);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(path));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var f in dir.EnumerateFiles()) { try { size += f.Length; files++; } catch { } }
                foreach (var d in dir.EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (excludeDirs != null && excludeDirs.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    stack.Push(d);
                }
            }
            catch { /* dossier inaccessible */ }
        }
        return (size, files);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    /// <summary>Octets écrits par un processus depuis son démarrage (0 si indisponible).</summary>
    private static long ProcessWriteBytes(System.Diagnostics.Process p)
    {
        try { return !p.HasExited && GetProcessIoCounters(p.Handle, out var c) ? (long)c.WriteBytes : 0; }
        catch { return 0; }
    }

    // « \t  Nouveau fichier\t\t  307200\tC:\… » : une ligne par fichier traité (/V : ignorés compris ; /BYTES : taille en octets).
    // Les fichiers en trop de la destination (« *Fichier SUPPL. », « *EXTRA File ») commencent par « * » : non comptés.
    private static readonly System.Text.RegularExpressions.Regex RoboFileLine =
        new(@"^\t([^\t]+)\t+ *(\d+)\t", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Taille à partir de laquelle un fichier est copié par la passe « gros fichiers » (1 seul thread).
    /// Mesuré sur une clé USB 3.2 (SanDisk, NTFS) : gros fichiers 3,8 Mo/s en 1 thread contre 3,4 Mo/s en /MT:16
    /// (plusieurs écritures en parallèle se gênent sur la mémoire flash) ; petits fichiers 15 fichiers/s en /MT:16
    /// contre 9 en 1 thread (le délai par fichier se recouvre). D'où deux passes.
    /// </summary>
    private const long BigFileBytes = 8L << 20;

    /// <summary>
    /// Copie robocopy en deux passes (petits fichiers en /MT:16, puis gros fichiers un par un) avec avancement en
    /// direct : le journal de robocopy (/UNILOG, une ligne par fichier) est lu pendant la copie, complété par les
    /// octets réellement écrits. restore=true : /XO, n'écrase pas un fichier plus récent de la destination.
    /// </summary>
    private static async Task<long> RobocopyAsync(string src, string dst, bool restore, string label, Action<string> log,
                                            Action<long, int, long, int, double>? onProgress, CancellationToken ct,
                                            IReadOnlyCollection<string>? excludeDirs = null)
    {
        var (totalBytes, totalFiles) = await Task.Run(() => FolderStats(src, excludeDirs), ct);
        log($"[i] {label} : {FormatSize(totalBytes)}, {totalFiles} fichiers à traiter");
        if (excludeDirs is { Count: > 0 }) log($"[i] Dossiers ignorés : {string.Join(", ", excludeDirs)}");
        Directory.CreateDirectory(dst);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        long doneBytes = 0; int doneFiles = 0; int nextLog = 10;
        var errors = new List<string>();
        long measured = 0, passBase = 0, passLog = 0, ioStart = 0;
        var history = new Queue<(double t, long bytes)>();
        System.Diagnostics.Process? proc = null;
        int code = 0;

        // Volume réellement écrit. Le journal seul retarde : avec /MT, robocopy n'y inscrit un fichier qu'une fois
        // terminé ET dans l'ordre ; un gros fichier en cours (ISO, vidéo) bloque l'affichage de tous les suivants.
        // On prend donc le maximum du journal et des octets réellement écrits par le processus robocopy
        // (compteurs d'E/S Windows). Pas l'espace libre de la destination : robocopy réserve la taille complète
        // d'un fichier dès qu'il le commence (un ISO de 1,5 Go ferait sauter l'affichage à 100 %).
        void Report(bool final)
        {
            long io = proc == null ? 0 : Math.Max(0, ProcessWriteBytes(proc) - ioStart);
            // Jamais au-delà du total avant la fin, et jamais en recul.
            measured = Math.Max(measured, Math.Min(passBase + Math.Max(passLog, io), totalBytes));

            // Débit sur les 10 dernières secondes : stable, et suit les changements (petits fichiers / gros fichiers).
            double now = watch.Elapsed.TotalSeconds;
            history.Enqueue((now, measured));
            while (history.Count > 2 && now - history.Peek().t > 10) history.Dequeue();
            var first = history.Peek();
            double speed = now - first.t >= 2 ? (measured - first.bytes) / (now - first.t) : 0;

            long shown = final ? Math.Max(measured, totalBytes) : measured;
            onProgress?.Invoke(shown, final ? Math.Max(doneFiles, totalFiles) : doneFiles, totalBytes, totalFiles, speed);
            if (!final && totalBytes > 0 && watch.Elapsed.TotalSeconds >= 5)
            {
                int pct = (int)Math.Min(99, measured * 100 / totalBytes);
                if (pct >= nextLog)
                {
                    log($"[i] {label} : {pct} % ({FormatSize(measured)} / {FormatSize(totalBytes)}, {doneFiles} / {totalFiles} fichiers terminés, {FormatSize((long)speed)}/s)");
                    while (nextLog <= pct) nextLog += 10;
                }
            }
        }

        var passes = new (string[] Args, long Min, long Max)[]
        {
            (new[] { "/MT:16", $"/MAX:{BigFileBytes - 1}" }, 0, BigFileBytes - 1),   // petits fichiers en parallèle
            (new[] { $"/MIN:{BigFileBytes}" }, BigFileBytes, long.MaxValue),          // gros fichiers un par un
        };
        foreach (var pass in passes)
        {
            string rcLog = Path.Combine(Path.GetTempPath(), $"alextoolkit_robocopy_{Guid.NewGuid():N}.log");
            var psi = new System.Diagnostics.ProcessStartInfo("robocopy.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true   // vide avec /UNILOG ; évite toute écriture dans la console de l'appli
            };
            foreach (var a in new[] { src, dst, "/E", "/R:1", "/W:1", "/XJ", "/NDL", "/NP", "/V", "/BYTES", "/NJH",
                                      "/XF", "desktop.ini", "thumbs.db", $"/UNILOG:{rcLog}" })
                psi.ArgumentList.Add(a);
            foreach (var a in pass.Args) psi.ArgumentList.Add(a);
            if (restore) psi.ArgumentList.Add("/XO");
            if (excludeDirs is { Count: > 0 })
            {
                psi.ArgumentList.Add("/XD");
                foreach (var d in excludeDirs) psi.ArgumentList.Add(d);
            }

            passLog = 0;
            var pending = new System.Text.StringBuilder();
            FileStream? fs = null; StreamReader? reader = null;
            var buffer = new char[32768];

            // Une ligne par fichier vu. Les fichiers écartés par la taille (/MIN, /MAX) sont aussi listés
            // (« grand » / « petit », selon la langue) : on les reconnaît à leur taille, pas à leur libellé.
            void ReadNew()
            {
                if (reader == null)
                {
                    if (!File.Exists(rcLog)) return;
                    try
                    {
                        fs = new FileStream(rcLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        reader = new StreamReader(fs, System.Text.Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
                    }
                    catch (IOException) { return; }
                }
                int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    pending.Append(buffer, 0, n);
                    string text = pending.ToString();
                    int last = text.LastIndexOf('\n');
                    if (last < 0) continue;
                    pending.Clear().Append(text, last + 1, text.Length - last - 1);
                    foreach (var raw in text.Substring(0, last).Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        var m = RoboFileLine.Match(line);
                        if (m.Success && !m.Groups[1].Value.TrimStart().StartsWith('*'))
                        {
                            long size = long.Parse(m.Groups[2].Value);
                            if (size < pass.Min || size > pass.Max) continue;   // traité par l'autre passe
                            doneBytes += size; passLog += size;
                            doneFiles++;
                        }
                        else if (line.Contains("ERREUR", StringComparison.OrdinalIgnoreCase) || line.Contains("ERROR", StringComparison.Ordinal))
                        {
                            if (errors.Count < 5) errors.Add(line.Trim());
                        }
                    }
                }
            }

            proc = System.Diagnostics.Process.Start(psi)!;
            _ = proc.StandardOutput.ReadToEndAsync();
            ioStart = ProcessWriteBytes(proc);
            try
            {
                while (!proc.HasExited)
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { proc.Kill(true); } catch { }
                        ct.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(400, CancellationToken.None);
                    ReadNew();
                    Report(false);
                }
                proc.WaitForExit();
                ReadNew();
                int c = proc.ExitCode;
                // Codes robocopy : < 8 = bits de réussite cumulables ; >= 8 = échec (on garde le plus grave).
                code = (c >= 8 || code >= 8) ? Math.Max(code, c) : (code | c);
            }
            finally
            {
                reader?.Dispose(); fs?.Dispose();
                proc.Dispose();
                proc = null;
                try { File.Delete(rcLog); } catch { }
            }
            passBase += passLog; passLog = 0;   // volume exact de la passe (tous ses fichiers sont maintenant dans le journal)
        }
        Report(true);

        string time = watch.Elapsed.ToString(@"mm\:ss");
        if (code < 8)
            log($"[OK] {label} → {dst} ({FormatSize(totalBytes)}, {totalFiles} fichiers, {time})");
        else
        {
            log($"[ERR] Copie incomplète de {label} (code robocopy {code} : fichiers verrouillés ou accès refusé)");
            foreach (var e in errors) log($"      {e}");
        }
        return totalBytes;
    }

    /// <summary>Suivi de l'avancement global (volume de tous les dossiers) pour <see cref="CopyProgress"/>.</summary>
    private sealed class ProgressTracker(int count, long overallTotal, Action<CopyProgress>? sink)
    {
        private long _before;   // volume des éléments terminés
        public int Index { get; private set; }
        public string Item { get; private set; } = "";

        public void Start(string item) { Index++; Item = item; sink?.Invoke(new(item, Index, count, 0, 0, 0, 0, _before, overallTotal, 0)); }

        public void Folder(long done, int files, long itemTotal, int filesTotal, double speed) =>
            sink?.Invoke(new(Item, Index, count, done, itemTotal, files, filesTotal, _before + done, Math.Max(overallTotal, _before + itemTotal), speed));

        public void Done(long bytes) => _before += bytes;
    }

    public static async Task<string> BackupAsync(IEnumerable<MigrationItem> items, string destinationRoot, Action<string> log, CancellationToken ct,
                                                 Action<CopyProgress>? progress = null, bool skipRegenerable = true)
    {
        string user = Environment.UserName;
        string root = Path.Combine(destinationRoot, $"Sauvegarde_{user}_{DateTime.Now:yyyy-MM-dd_HH-mm}");
        Directory.CreateDirectory(root);
        var done = new List<string>();
        var selected = items.Where(i => i.IsSelected && i.IsAvailable).ToList();
        var tracker = new ProgressTracker(selected.Count,
            selected.Where(i => i.Kind == MigrationKind.Folder).Sum(i => Math.Max(0, i.SizeBytes)), progress);

        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            log($"\n[i] ── {item.Label} ({tracker.Index + 1}/{selected.Count}) ──");
            tracker.Start(item.Label);
            switch (item.Kind)
            {
                case MigrationKind.Folder:
                    tracker.Done(await RobocopyAsync(item.SourcePath, Path.Combine(root, "Fichiers", item.Key), false, item.Label, log, tracker.Folder, ct,
                                                     skipRegenerable ? RegenerableDirs : null));
                    break;

                case MigrationKind.Bookmarks:
                    string nav = Path.Combine(root, "Navigateurs");
                    void CopyBrowser(string userData, string name)
                    {
                        foreach (var profile in BookmarkProfiles(userData))
                        {
                            Directory.CreateDirectory(Path.Combine(nav, name, profile));
                            File.Copy(Path.Combine(userData, profile, "Bookmarks"), Path.Combine(nav, name, profile, "Bookmarks"), true);
                            log($"[OK] Favoris {name} ({profile}) sauvegardés");
                        }
                    }
                    CopyBrowser(ChromeUserData, "Chrome");
                    CopyBrowser(EdgeUserData, "Edge");
                    if (Directory.Exists(FirefoxProfiles))
                        foreach (var backups in Directory.GetDirectories(FirefoxProfiles).Select(p => Path.Combine(p, "bookmarkbackups")).Where(Directory.Exists))
                            await RobocopyAsync(backups, Path.Combine(nav, "Firefox", Path.GetFileName(Path.GetDirectoryName(backups)!)), false, "Favoris Firefox", log, null, ct);
                    break;

                case MigrationKind.Wifi:
                    string wifi = Path.Combine(root, "WiFi");
                    Directory.CreateDirectory(wifi);
                    await PowerShellRunner.RunAsync($@"
$root = {Q(wifi)}
# netsh tronque le chemin du fichier exporté (~128 caractères) : sur une destination profonde, deux réseaux
# finissent dans le même fichier tronqué. On exporte donc profil par profil dans un dossier temporaire au chemin court.
$tmp = Join-Path $env:PUBLIC ('ATK' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$names = netsh wlan show profiles | ForEach-Object {{ if ($_ -match '^\s+[^:]+\s:\s(.+)$') {{ $Matches[1].Trim() }} }}
$i = 0
try {{
    foreach ($name in $names) {{
        $i++; $sub = Join-Path $tmp $i
        New-Item -ItemType Directory -Force -Path $sub | Out-Null
        & netsh wlan export profile name=""$name"" key=clear folder=""$sub"" | Out-Null
        $dest = Join-Path $root $i
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Get-ChildItem $sub -Filter *.xml -ErrorAction SilentlyContinue | Move-Item -Destination $dest -Force
    }}
}} finally {{
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue   # contenait des clés en clair
}}
$n = @(Get-ChildItem $root -Recurse -Filter *.xml -ErrorAction SilentlyContinue).Count
Write-Output ""[OK] $n profil(s) Wi-Fi exporté(s) (clés en clair : sauvegarde à protéger)""
", log, ct);
                    break;

                case MigrationKind.Apps:
                    string apps = Path.Combine(root, "Applications");
                    Directory.CreateDirectory(apps);
                    await PowerShellRunner.RunAsync($@"
$dir = {Q(apps)}
& winget export -o (Join-Path $dir 'winget.json') --accept-source-agreements --disable-interactivity 2>$null | Out-Null
$keys = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
$list = Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object {{ $_.DisplayName -and -not $_.SystemComponent }} |
    Sort-Object DisplayName -Unique | ForEach-Object {{ ""{{0}}  {{1}}  {{2}}"" -f $_.DisplayName, $_.DisplayVersion, $_.Publisher }}
$list | Out-File (Join-Path $dir 'logiciels_installes.txt') -Encoding utf8
$w = if (Test-Path (Join-Path $dir 'winget.json')) {{ 'winget.json créé' }} else {{ 'winget indisponible' }}
Write-Output ""[OK] $(@($list).Count) logiciels listés ($w)""
", log, ct);
                    break;
            }
            done.Add(item.Key);
        }

        var manifest = new BackupManifest
        {
            Computer = Environment.MachineName, User = user, Date = DateTime.Now, Items = done,
            ToolVersion = UpdateService.GetCurrentVersion().ToString()
        };
        File.WriteAllText(Path.Combine(root, ManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        log($"[OK] Sauvegarde terminée : {root}");
        return root;
    }

    public static BackupManifest? ReadManifest(string folder)
    {
        try
        {
            string path = Path.Combine(folder, ManifestName);
            return File.Exists(path) ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static async Task RestoreAsync(IEnumerable<MigrationItem> items, string backupRoot, bool reinstallApps, Action<string> log, CancellationToken ct,
                                          Action<CopyProgress>? progress = null)
    {
        // Éléments réellement présents dans la sauvegarde (pour « 2/5 » et le volume total à restaurer)
        var selected = items.Where(i => i.IsSelected && i.Kind switch
        {
            MigrationKind.Folder => Directory.Exists(Path.Combine(backupRoot, "Fichiers", i.Key)),
            MigrationKind.Wifi => Directory.Exists(Path.Combine(backupRoot, "WiFi")),
            MigrationKind.Apps => reinstallApps && File.Exists(Path.Combine(backupRoot, "Applications", "winget.json")),
            _ => true
        }).ToList();
        log("[i] Calcul du volume à restaurer…");
        long overall = await Task.Run(() => selected.Where(i => i.Kind == MigrationKind.Folder)
            .Sum(i => FolderStats(Path.Combine(backupRoot, "Fichiers", i.Key)).Bytes), ct);
        log($"[i] {FormatSize(overall)} de fichiers à restaurer");
        var tracker = new ProgressTracker(selected.Count, overall, progress);

        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            tracker.Start(item.Label);
            switch (item.Kind)
            {
                case MigrationKind.Folder:
                    string src = Path.Combine(backupRoot, "Fichiers", item.Key);
                    log($"\n[i] ── {item.Label} ({tracker.Index}/{selected.Count}) ──");
                    tracker.Done(await RobocopyAsync(src, item.SourcePath, true, item.Label, log, tracker.Folder, ct));
                    break;

                case MigrationKind.Bookmarks:
                    log($"\n[i] ── {item.Label} ({tracker.Index}/{selected.Count}) ──");
                    void RestoreBookmarks(string name, string userData, string process)
                    {
                        string fromRoot = Path.Combine(backupRoot, "Navigateurs", name);
                        if (!Directory.Exists(fromRoot)) return;
                        if (System.Diagnostics.Process.GetProcessesByName(process).Length > 0)
                        {
                            log($"[!] {name} est ouvert : fermez-le puis relancez la restauration des favoris.");
                            return;
                        }
                        foreach (var profileDir in Directory.GetDirectories(fromRoot))
                        {
                            string from = Path.Combine(profileDir, "Bookmarks");
                            if (!File.Exists(from)) continue;
                            string target = Path.Combine(userData, Path.GetFileName(profileDir), "Bookmarks");
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            if (File.Exists(target)) File.Copy(target, target + ".avant_restauration", true);
                            File.Copy(from, target, true);
                            log($"[OK] Favoris {name} ({Path.GetFileName(profileDir)}) restaurés (ancien fichier conservé en .avant_restauration)");
                        }
                    }
                    RestoreBookmarks("Chrome", ChromeUserData, "chrome");
                    RestoreBookmarks("Edge", EdgeUserData, "msedge");
                    if (Directory.Exists(Path.Combine(backupRoot, "Navigateurs", "Firefox")))
                        log("[i] Firefox : copiez une sauvegarde de « Navigateurs\\Firefox » puis, dans Firefox, Bibliothèque > Importation et sauvegarde > Restaurer.");
                    break;

                case MigrationKind.Wifi:
                    string wifi = Path.Combine(backupRoot, "WiFi");
                    if (!Directory.Exists(wifi)) continue;
                    log($"\n[i] ── {item.Label} ──");
                    await PowerShellRunner.RunAsync($@"
$ok = 0
Get-ChildItem {Q(wifi)} -Recurse -Filter *.xml | ForEach-Object {{
    $r = & netsh wlan add profile filename=""$($_.FullName)"" user=all
    if ($LASTEXITCODE -eq 0) {{ $ok++ }}
}}
Write-Output ""[OK] $ok profil(s) Wi-Fi importé(s)""
", log, ct);
                    break;

                case MigrationKind.Apps:
                    string json = Path.Combine(backupRoot, "Applications", "winget.json");
                    if (!reinstallApps || !File.Exists(json)) continue;
                    log($"\n[i] ── Réinstallation des logiciels (peut prendre longtemps) ──");
                    await CommandRunner.RunAsync($"winget import -i \"{json}\" --accept-package-agreements --accept-source-agreements --ignore-unavailable --disable-interactivity", log, ct);
                    break;
            }
        }
        log("[OK] Restauration terminée.");
    }

    // ─── Mode intervention client : scripts console autonomes (Backup-ClientPC / Restore-ClientPC) ───
    // Ils attendent l'opérateur (export des mots de passe, choix de la sauvegarde) : ils tournent dans une
    // vraie console PowerShell, pas dans le journal de l'appli (sans entrée clavier, Read-Host y est impossible).
    public const string ClientBackupFolder = "Backups_AlexDepannage";

    public record DriveChoice(char Letter, string Label, bool IsSystem)
    {
        public override string ToString() => Label;
    }

    /// <summary>Lecteurs utilisables pour une sauvegarde client (disques externes et clés USB en premier).</summary>
    public static List<DriveChoice> GetBackupDrives()
    {
        char system = char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)![0]);
        var list = new List<(DriveChoice Choice, int Order)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Removable or DriveType.Fixed or DriveType.Network)) continue;
                char letter = char.ToUpperInvariant(d.Name[0]);
                bool isSystem = letter == system;
                string kind = d.DriveType switch { DriveType.Removable => "amovible", DriveType.Network => "réseau", _ => isSystem ? "disque de Windows" : "disque" };
                string name = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel + " - ";
                list.Add((new DriveChoice(letter, $"{letter}:  {name}{kind} - {FormatSize(d.AvailableFreeSpace)} libres", isSystem),
                          isSystem ? 2 : d.DriveType == DriveType.Removable ? 0 : 1));
            }
            catch { /* lecteur qui disparaît pendant l'énumération */ }
        }
        return list.OrderBy(x => x.Order).ThenBy(x => x.Choice.Letter).Select(x => x.Choice).ToList();
    }

    /// <summary>Nom de client utilisable comme nom de dossier (même règle que le script).</summary>
    public static string SanitizeClientName(string name)
    {
        var chars = name.Trim().Select(c => "\\/:*?\"<>|".Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        return new string(chars).Trim('.', ' ');
    }

    /// <summary>Installe le script dans %ProgramData%\AlexToolkit puis l'ouvre dans une console PowerShell visible.</summary>
    public static string LaunchClientScript(bool backup, string? clientName = null, char? drive = null)
    {
        string script = PersonalizationService.InstallSharedScript(backup ? "Backup-ClientPC.ps1" : "Restore-ClientPC.ps1");
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,   // nouvelle fenêtre de console (l'appli n'en a pas) ; arguments passés tels quels
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        // -NoExit : la console reste ouverte pour relire le bilan. Les valeurs sont des arguments séparés
        // (jamais insérées dans du code) : un nom comme « Projets d'été » ne casse rien.
        foreach (var a in new[] { "-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-File", script }) psi.ArgumentList.Add(a);
        if (backup && !string.IsNullOrWhiteSpace(clientName)) { psi.ArgumentList.Add("-ClientName"); psi.ArgumentList.Add(clientName); }
        if (backup && drive is char d) { psi.ArgumentList.Add("-DestinationDrive"); psi.ArgumentList.Add(d.ToString()); }
        using var _ = System.Diagnostics.Process.Start(psi);
        return script;
    }

    /// <summary>
    /// Débloque l'accès à un dossier ou à l'ancien disque d'un client (Unlock-ClientDrive.ps1 : takeown + icacls)
    /// dans une console visible (confirmation et bilan ; le script attend Entrée avant de se fermer).
    /// </summary>
    public static string LaunchUnlockScript(string targetPath)
    {
        string script = PersonalizationService.InstallSharedScript("Unlock-ClientDrive.ps1");
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-TargetPath", targetPath }) psi.ArgumentList.Add(a);
        using var _ = System.Diagnostics.Process.Start(psi);
        return script;
    }
}
