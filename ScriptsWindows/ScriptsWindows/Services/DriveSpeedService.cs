using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>
/// Support testable : une clé, un disque ou un SSD physique (toutes ses partitions regroupées), ou un lecteur réseau.
/// Letter = partition où le test écrit (celle qui a le plus d'espace libre) ; Size = taille du support entier.
/// </summary>
public record DriveTarget(char Letter, string VolumeLabel, string FileSystem, long Size, long Free,
                          string Model, string Bus, string Serial, string Kind, bool IsSystem,
                          IReadOnlyList<string>? Partitions = null)
{
    /// <summary>« E:  USB SanDisk 3.2Gen1 — USB, 115 Go, exFAT » ou, avec plusieurs partitions,
    /// « E:  USB SanDisk 3.2Gen1 — USB, 115 Go · 2 partitions : E: exFAT 115 Go, F: FAT 32 Mo ».</summary>
    public string Display => $"{Letter}:  {Name}  —  {Bus}, {DriveSpeedService.FormatSize(Size)}"
        + (Partitions is { Count: > 1 } ? $"  ·  {Partitions.Count} partitions : {string.Join(", ", Partitions)}" : $", {FileSystem}")
        + (IsSystem ? "  (disque de Windows)" : "");
    public string Name => !string.IsNullOrWhiteSpace(Model) ? Model.Trim()
                        : !string.IsNullOrWhiteSpace(VolumeLabel) ? VolumeLabel : Kind;
    /// <summary>Identifie le support d'un test à l'autre, même si sa lettre change.</summary>
    public string Key => !string.IsNullOrWhiteSpace(Serial) ? $"{Model}|{Serial}".Trim() : $"{Name}|{Size / (1L << 30)}";
}

/// <summary>Résultat d'un test (enregistré dans l'historique pour comparer les supports).</summary>
public class DriveSpeedResult
{
    public string DriveKey { get; set; } = "";
    public string DriveName { get; set; } = "";
    public string Letter { get; set; } = "";
    public string Bus { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long Size { get; set; }
    public DateTime Date { get; set; }
    public int TestSizeMB { get; set; }
    /// <summary>Écriture / lecture séquentielles (gros fichiers : vidéos, ISO), en Mo/s.</summary>
    public double SeqWriteMBs { get; set; }
    public double SeqReadMBs { get; set; }
    /// <summary>Accès aléatoires de 4 Ko (système, logiciels lancés depuis le support), en opérations/s.</summary>
    public double RandWriteIops { get; set; }
    public double RandReadIops { get; set; }
    /// <summary>Création de fichiers de 16 Ko (documents, photos, sauvegarde d'un profil), en fichiers/s.</summary>
    public double SmallFilesPerSec { get; set; }
    public bool Partial { get; set; }
    /// <summary>Nom donné par l'utilisateur (« Clé rouge », « Disque Paul ») ; vide = nom du modèle.</summary>
    public string Nickname { get; set; } = "";
    public string Shown => string.IsNullOrWhiteSpace(Nickname) ? DriveName : Nickname;
}

/// <summary>Une étape en cours (affichée par la fenêtre).</summary>
public record DriveSpeedStep(int Index, int Count, string Title, double Fraction, string Live);

/// <summary>
/// Mesure la vitesse réelle d'un lecteur, sans le cache de Windows (FILE_FLAG_NO_BUFFERING + écriture directe) :
/// sinon on mesurerait la mémoire vive. Tout est écrit dans un dossier temporaire du lecteur, supprimé à la fin.
/// Chaque mesure est limitée en durée : une clé lente (4 Mo/s) ne bloque pas le test pendant des minutes.
/// </summary>
public static class DriveSpeedService
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;   // FILE_FLAG_NO_BUFFERING
    private const int BlockSize = 1 << 20;     // séquentiel : blocs de 1 Mo
    private const int SmallIo = 4096;          // aléatoire : 4 Ko
    private const int SmallFileSize = 16 << 10; // petits fichiers : 16 Ko
    public const string TestFolderPrefix = "AlexToolkit_TestVitesse_";

    /// <summary>Historique (modifiable pour le banc de test, qui ne doit pas toucher à celui de l'utilisateur).</summary>
    internal static string HistoryFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows", "drive_speeds.json");

    /// <summary>Durées maximales de chaque mesure (secondes).</summary>
    internal static double SeqSeconds { get; set; } = 25;
    internal static double RandSeconds { get; set; } = 8;
    internal static double SmallFilesSeconds { get; set; } = 10;

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} To",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        _ => $"{bytes / 1024.0:0} Ko"
    };

    public static string FormatSpeed(double mbs) => mbs >= 100 ? $"{mbs:N0} Mo/s" : mbs >= 10 ? $"{mbs:0.0} Mo/s" : $"{mbs:0.00} Mo/s";

    // ─── Lecteurs ────────────────────────────────────────────────────────────────

    /// <summary>Lecteurs inscriptibles, clés et disques amovibles en premier, disque de Windows en dernier.</summary>
    public static async Task<List<DriveTarget>> GetDrivesAsync(CancellationToken ct = default)
    {
        // Disque physique de chaque lettre (Storage, PS 5.1) : numéro, modèle, bus, n° de série, taille.
        var info = new Dictionary<char, (int Disk, string Model, string Bus, string Serial, long DiskSize)>();
        try
        {
            string json = await PowerShellRunner.RunAsync(@"
$r = foreach ($p in Get-Partition -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter }) {
    $d = $p | Get-Disk -ErrorAction SilentlyContinue
    if ($d) { [pscustomobject]@{ L = [string]$p.DriveLetter; N = [int]$d.Number; M = [string]$d.FriendlyName; B = [string]$d.BusType; S = ([string]$d.SerialNumber).Trim(); Z = [long]$d.Size } }
}
ConvertTo-Json -InputObject @($r) -Compress", ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string l = e.GetProperty("L").GetString() ?? "";
                if (l.Length > 0)
                    info[char.ToUpperInvariant(l[0])] = (e.GetProperty("N").GetInt32(), e.GetProperty("M").GetString() ?? "",
                        e.GetProperty("B").GetString() ?? "", e.GetProperty("S").GetString() ?? "", e.GetProperty("Z").GetInt64());
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* sans détails : chaque lettre est proposée seule */ }

        char sys = char.ToUpperInvariant((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0]);
        var volumes = new List<(DriveInfo D, char Letter)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                    volumes.Add((d, char.ToUpperInvariant(d.Name[0])));
            }
            catch { /* lecteur retiré pendant l'énumération */ }
        }

        // Une ligne par support physique : ses partitions (ex. clé Ventoy : exFAT + petite FAT de 32 Mo, ou C: et D:
        // d'un même SSD) ont la même vitesse. Le test écrit sur celle qui a le plus d'espace libre.
        var list = new List<DriveTarget>();
        foreach (var group in volumes.GroupBy(v => info.TryGetValue(v.Letter, out var i) ? $"disk{i.Disk}" : $"vol{v.Letter}"))
        {
            try
            {
                var parts = group.OrderByDescending(v => v.D.AvailableFreeSpace).ToList();
                var (d, letter) = parts[0];
                bool known = info.TryGetValue(letter, out var i);
                string kind = d.DriveType switch { DriveType.Removable => "Clé USB", DriveType.Network => "Lecteur réseau", _ => "Disque" };
                string bus = i.Bus switch
                {
                    "USB" => "USB", "NVMe" => "NVMe", "SATA" => "SATA", "SD" or "MMC" => "Carte SD", "RAID" => "RAID",
                    "" or null => d.DriveType == DriveType.Network ? "Réseau" : "?", _ => i.Bus
                };
                var partitions = parts.OrderBy(p => p.Letter)
                    .Select(p => $"{p.Letter}: {p.D.DriveFormat} {FormatSize(p.D.TotalSize)}").ToList();
                list.Add(new DriveTarget(letter, d.VolumeLabel, d.DriveFormat, known && i.DiskSize > 0 ? i.DiskSize : d.TotalSize,
                                         d.AvailableFreeSpace, i.Model ?? "", bus, i.Serial ?? "", kind,
                                         parts.Any(p => p.Letter == sys), partitions));
            }
            catch { }
        }
        return list.OrderBy(t => t.IsSystem).ThenBy(t => t.Bus == "USB" || t.Kind == "Clé USB" ? 0 : 1).ThenBy(t => t.Letter).ToList();
    }

    // ─── Mesure ──────────────────────────────────────────────────────────────────

    /// <summary>Teste le lecteur (dossier temporaire à sa racine).</summary>
    public static Task<DriveSpeedResult> RunAsync(DriveTarget drive, int testSizeMB, Action<DriveSpeedStep> progress, CancellationToken ct)
        => RunInFolderAsync($"{drive.Letter}:\\", drive, testSizeMB, progress, ct);

    /// <summary>Teste dans un dossier donné (le banc de test l'utilise sur un dossier temporaire).</summary>
    internal static Task<DriveSpeedResult> RunInFolderAsync(string parent, DriveTarget drive, int testSizeMB,
                                                            Action<DriveSpeedStep> progress, CancellationToken ct) => Task.Run(() =>
    {
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(parent))!).AvailableFreeSpace;
        long size = (long)testSizeMB << 20;
        if (free < size + (64L << 20))
            throw new IOException($"Espace libre insuffisant : {FormatSize(free)} disponibles, {FormatSize(size + (64L << 20))} nécessaires. Choisissez un test plus petit.");

        string folder = Path.Combine(parent, TestFolderPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "test.bin");
        var result = new DriveSpeedResult
        {
            DriveKey = drive.Key, DriveName = drive.Name, Letter = $"{drive.Letter}:", Bus = drive.Bus,
            FileSystem = drive.FileSystem, Size = drive.Size, Date = DateTime.Now, TestSizeMB = testSizeMB
        };
        const int steps = 5;
        // Tout le test dans un seul bloc : le dossier de test est supprimé même en cas d'arrêt ou d'erreur.
        try
        {
        // Tampon aligné sur 4 Ko (obligatoire sans cache, NO_BUFFERING) : tableau épinglé dont on prend une tranche
        // alignée. Données aléatoires : incompressibles.
        var raw = GC.AllocateUninitializedArray<byte>(BlockSize + 4096, pinned: true);
        int align = (int)((4096 - Marshal.UnsafeAddrOfPinnedArrayElement(raw, 0).ToInt64() % 4096) % 4096);
        var block = raw.AsMemory(align, BlockSize);
        Random.Shared.NextBytes(block.Span);

        // 1. Écriture séquentielle
        long written = 0;
        using (var h = OpenUnbuffered(file, FileMode.Create, FileAccess.Write))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (written < size && sw.Elapsed.TotalSeconds < SeqSeconds)
            {
                ct.ThrowIfCancellationRequested();
                RandomAccess.Write(h, block.Span, written);
                written += BlockSize;
                progress(new(1, steps, "Écriture séquentielle (gros fichiers)", Math.Max((double)written / size, sw.Elapsed.TotalSeconds / SeqSeconds),
                             FormatSpeed(written / (double)(1 << 20) / sw.Elapsed.TotalSeconds)));
            }
            result.SeqWriteMBs = written / (double)(1 << 20) / sw.Elapsed.TotalSeconds;
            if (written < size) result.Partial = true;
        }

        // 2. Lecture séquentielle (sans cache : relit vraiment le support)
        using (var h = OpenUnbuffered(file, FileMode.Open, FileAccess.Read))
        {
            long read = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (read < written && sw.Elapsed.TotalSeconds < SeqSeconds)
            {
                ct.ThrowIfCancellationRequested();
                int n = RandomAccess.Read(h, block.Span, read);
                if (n <= 0) break;
                read += n;
                progress(new(2, steps, "Lecture séquentielle (gros fichiers)", Math.Max((double)read / written, sw.Elapsed.TotalSeconds / SeqSeconds),
                             FormatSpeed(read / (double)(1 << 20) / sw.Elapsed.TotalSeconds)));
            }
            result.SeqReadMBs = read / (double)(1 << 20) / sw.Elapsed.TotalSeconds;
        }

        // 3 et 4. Accès aléatoires de 4 Ko dans le fichier écrit
        long blocks4k = Math.Max(1, written / SmallIo);
        var small = block[..SmallIo];
        result.RandWriteIops = RandomIops(file, FileAccess.Write, blocks4k, small, 3, steps, "Écriture aléatoire 4 Ko", progress, ct);
        result.RandReadIops = RandomIops(file, FileAccess.Read, blocks4k, small, 4, steps, "Lecture aléatoire 4 Ko", progress, ct);

        {
            // 5. Petits fichiers de 16 Ko, écrits directement sur le support (comme une sauvegarde de documents)
            string dir = Path.Combine(folder, "petits");
            Directory.CreateDirectory(dir);
            var data = new byte[SmallFileSize];
            Random.Shared.NextBytes(data);
            int count = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < SmallFilesSeconds && count < 5000)
            {
                ct.ThrowIfCancellationRequested();
                using (var fs = new FileStream(Path.Combine(dir, $"f{count}.dat"), FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(data);
                    fs.Flush(true);
                }
                count++;
                if (count % 5 == 0)
                    progress(new(5, steps, "Création de petits fichiers (documents, photos)", sw.Elapsed.TotalSeconds / SmallFilesSeconds,
                                 $"{count / sw.Elapsed.TotalSeconds:0} fichiers/s"));
            }
            result.SmallFilesPerSec = count / sw.Elapsed.TotalSeconds;
        }
        }
        finally
        {
            progress(new(steps, steps, "Suppression des fichiers de test…", 1, ""));
            DeleteTestFolder(folder);
        }
        return result;
    }, ct);

    private static SafeFileHandle OpenUnbuffered(string path, FileMode mode, FileAccess access)
    {
        try { return File.OpenHandle(path, mode, access, FileShare.None, NoBuffering | FileOptions.WriteThrough); }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            // Lecteur réseau ou système de fichiers qui refuse l'accès sans cache : écriture directe seulement.
            return File.OpenHandle(path, mode, access, FileShare.None, FileOptions.WriteThrough);
        }
    }

    private static double RandomIops(string file, FileAccess access, long blocks, Memory<byte> buffer, int index, int steps,
                                     string title, Action<DriveSpeedStep> progress, CancellationToken ct)
    {
        using var h = OpenUnbuffered(file, FileMode.Open, access);
        var rnd = new Random(index);
        long ops = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < RandSeconds)
        {
            ct.ThrowIfCancellationRequested();
            long offset = rnd.NextInt64(blocks) * SmallIo;
            if (access == FileAccess.Write) RandomAccess.Write(h, buffer.Span, offset);
            else RandomAccess.Read(h, buffer.Span, offset);
            ops++;
            if (ops % 16 == 0)
                progress(new(index, steps, title, sw.Elapsed.TotalSeconds / RandSeconds, $"{ops / sw.Elapsed.TotalSeconds:0} opérations/s"));
        }
        return ops / sw.Elapsed.TotalSeconds;
    }

    /// <summary>Supprime le dossier de test (plusieurs essais : un antivirus peut bloquer un fichier un instant).</summary>
    private static void DeleteTestFolder(string folder)
    {
        if (!Path.GetFileName(folder).StartsWith(TestFolderPrefix, StringComparison.Ordinal)) return;   // garde-fou
        for (int i = 0; i < 5 && Directory.Exists(folder); i++)
        {
            try { Directory.Delete(folder, true); }
            catch { Thread.Sleep(300); }
        }
    }

    // ─── Historique ──────────────────────────────────────────────────────────────

    public static List<DriveSpeedResult> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFile))
                return JsonSerializer.Deserialize<List<DriveSpeedResult>>(File.ReadAllText(HistoryFile)) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>Ajoute un résultat ; il reprend le nom déjà donné à ce support.</summary>
    public static List<DriveSpeedResult> AddToHistory(DriveSpeedResult result)
    {
        var history = LoadHistory();
        result.Nickname = history.FirstOrDefault(h => h.DriveKey == result.DriveKey && h.Nickname.Length > 0)?.Nickname ?? "";
        history.Add(result);
        SaveHistory(history);
        return LoadHistory();
    }

    /// <summary>Renomme un support dans tous ses tests (nom vide = nom du modèle).</summary>
    public static void Rename(string driveKey, string nickname)
    {
        var history = LoadHistory();
        foreach (var h in history.Where(h => h.DriveKey == driveKey)) h.Nickname = nickname.Trim();
        SaveHistory(history);
    }

    /// <summary>Supprime tous les tests d'un support.</summary>
    public static void Forget(string driveKey)
    {
        var history = LoadHistory();
        history.RemoveAll(h => h.DriveKey == driveKey);
        SaveHistory(history);
    }

    /// <summary>Dernier test de chaque support, du plus rapide au plus lent en écriture (tableau comparatif).</summary>
    public static List<(DriveSpeedResult Last, int Count)> Compare(List<DriveSpeedResult> history)
        => history.GroupBy(h => h.DriveKey)
                  .Select(g => (Last: g.OrderByDescending(h => h.Date).First(), Count: g.Count()))
                  .OrderByDescending(x => x.Last.SeqWriteMBs).ToList();

    public static void SaveHistory(List<DriveSpeedResult> history)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            // 10 derniers tests PAR support : une vieille clé testée une seule fois n'est jamais oubliée.
            var kept = history.GroupBy(h => h.DriveKey).SelectMany(g => g.OrderByDescending(h => h.Date).Take(10))
                              .OrderByDescending(h => h.Date).ToList();
            File.WriteAllText(HistoryFile, JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ─── Interprétation ──────────────────────────────────────────────────────────

    /// <summary>Verdict d'après l'écriture séquentielle (le plus parlant pour une sauvegarde) : titre + jeton de couleur.</summary>
    public static (string Title, string Detail, string BrushKey) Verdict(DriveSpeedResult r) => r.SeqWriteMBs switch
    {
        < 10 => ("Très lent", "Clé d'entrée de gamme ou branchée en USB 2 : à éviter pour les sauvegardes.", "DangerText"),
        < 40 => ("Lent", "Convient aux petits transferts ; une sauvegarde complète prendra des heures.", "WarningText"),
        < 150 => ("Correct", "Bonne clé USB 3 ou disque dur : sauvegardes en un temps raisonnable.", "AccentText"),
        < 600 => ("Rapide", "SSD externe ou disque SATA : idéal pour les sauvegardes et les transferts.", "SuccessText"),
        _ => ("Très rapide", "SSD NVMe : le maximum actuel.", "SuccessText")
    };

    /// <summary>Durées estimées de copies types vers ce support.</summary>
    public static IEnumerable<(string Label, string Duration)> Estimates(DriveSpeedResult r)
    {
        string D(double seconds) => double.IsInfinity(seconds) || double.IsNaN(seconds) ? "—"
            : seconds < 60 ? $"{seconds:0} s"
            : seconds < 3600 ? $"{seconds / 60:0} min"
            : $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00}";
        yield return ("10 Go de vidéos ou d'ISO", D(10 * 1024 / Math.Max(0.01, r.SeqWriteMBs)));
        yield return ("10 000 documents ou photos légères", D(10000 / Math.Max(0.01, r.SmallFilesPerSec)));
        yield return ("Profil complet de 50 Go", D(45 * 1024 / Math.Max(0.01, r.SeqWriteMBs) + 30000 / Math.Max(0.01, r.SmallFilesPerSec)));
    }
}
