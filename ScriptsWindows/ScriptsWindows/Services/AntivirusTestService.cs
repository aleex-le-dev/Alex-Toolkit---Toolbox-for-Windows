using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public enum AvOutcome { Pending, Running, Detected, NotDetected, Skipped, Error }

/// <summary>Résultat d'un test (une ligne de la fenêtre).</summary>
public sealed class AvTest
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public AvOutcome Outcome { get; set; } = AvOutcome.Pending;
    public string Detail { get; set; } = "";
}

/// <summary>Antivirus déclaré à Windows (Centre de sécurité).</summary>
public sealed record AvProduct(string Name, bool Enabled, bool UpToDate);

/// <summary>
/// Teste la réaction de l'antivirus avec des « faux virus » standard et inoffensifs :
///  • fichier de test EICAR (reconnu par tous les antivirus, ne contient aucun code dangereux) écrit sur le disque ;
///  • le même dans une archive ZIP ;
///  • échantillon de test AMSI de Microsoft exécuté dans PowerShell (analyse des scripts).
/// Les chaînes de test ne figurent JAMAIS en clair dans le code ni dans l'exe (sinon l'antivirus mettrait l'outil
/// lui-même en quarantaine) : elles sont stockées à l'envers et reconstituées en mémoire au moment du test.
/// Tout est écrit dans un dossier temporaire supprimé à la fin.
/// </summary>
public static class AntivirusTestService
{
    // Chaînes de test À L'ENVERS (ne pas les remettre à l'endroit dans le code).
    private const string EicarReversed = @"*H+H$!ELIF-TSET-SURIVITNA-DRADNATS-RACIE$}7)CC7)^P(45XZP\4[PA@%P!O5X";
    private const string AmsiReversed = "6831c4841ca0-0478-9334-b168-ec3c27e7 :elpmaS tseT ISMA";
    public const string FolderPrefix = "AlexToolkit_TestAntivirus_";

    /// <summary>Délai laissé à l'antivirus pour réagir après l'écriture d'un fichier.</summary>
    internal static TimeSpan ReactionTimeout { get; set; } = TimeSpan.FromSeconds(20);

    private static string Reverse(string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }

    /// <summary>Fichier de test EICAR (68 octets ASCII), reconstitué en mémoire.</summary>
    internal static byte[] EicarBytes() => Encoding.ASCII.GetBytes(Reverse(EicarReversed));
    internal static string AmsiSample() => Reverse(AmsiReversed);

    /// <summary>Archive ZIP (en mémoire) contenant le fichier de test.</summary>
    internal static byte[] EicarZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("eicar.com", CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(EicarBytes());
        }
        return ms.ToArray();
    }

    public static List<AvTest> CreateTests() => new()
    {
        new() { Id = "file", Title = "Protection en temps réel",
                Description = "Écrit le fichier de test EICAR sur le disque : l'antivirus doit le bloquer ou le supprimer aussitôt." },
        new() { Id = "zip", Title = "Analyse des archives",
                Description = "Le même fichier caché dans une archive ZIP : l'antivirus doit regarder à l'intérieur." },
        new() { Id = "amsi", Title = "Analyse des scripts (AMSI)",
                Description = "Lance un script PowerShell contenant l'échantillon de test de Microsoft : il doit être bloqué avant de s'exécuter." },
    };

    // ─── Antivirus installé ──────────────────────────────────────────────────────

    /// <summary>Antivirus déclarés au Centre de sécurité Windows (productState : bits 12-15 = activé, bits 4-7 = signatures à jour).</summary>
    public static async Task<List<AvProduct>> GetProductsAsync(CancellationToken ct = default)
    {
        var list = new List<AvProduct>();
        try
        {
            string json = await PowerShellRunner.RunAsync(@"
$r = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
     ForEach-Object { [pscustomobject]@{ N = [string]$_.displayName; S = [int]$_.productState } }
ConvertTo-Json -InputObject @($r) -Compress", ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                int state = e.GetProperty("S").GetInt32();
                list.Add(new AvProduct(e.GetProperty("N").GetString() ?? "?", ((state >> 12) & 0xF) == 1, ((state >> 4) & 0xF) == 0));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return list;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────────

    /// <summary>Lance les tests l'un après l'autre ; onUpdate est appelé à chaque changement d'état.</summary>
    public static async Task RunAsync(IReadOnlyList<AvTest> tests, Action<AvTest> onUpdate, Action<string> log, CancellationToken ct)
    {
        string folder = Path.Combine(Path.GetTempPath(), FolderPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var t in tests)
            {
                ct.ThrowIfCancellationRequested();
                t.Outcome = AvOutcome.Running; t.Detail = "En cours…"; onUpdate(t);
                try
                {
                    switch (t.Id)
                    {
                        case "file": await FileTestAsync(t, Path.Combine(folder, "eicar.com"), EicarBytes(), ct); break;
                        case "zip":
                            await FileTestAsync(t, Path.Combine(folder, "eicar.zip"), EicarZip(), ct);
                            if (t.Outcome == AvOutcome.NotDetected) await OnDemandZipScanAsync(t, Path.Combine(folder, "eicar_analyse.zip"), ct);
                            break;
                        case "amsi": await AmsiTestAsync(t, ct); break;
                    }
                }
                catch (OperationCanceledException) { t.Outcome = AvOutcome.Skipped; t.Detail = "Arrêté."; onUpdate(t); throw; }
                catch (Exception ex) { t.Outcome = AvOutcome.Error; t.Detail = ex.Message; }
                onUpdate(t);
                log(t.Outcome switch
                {
                    AvOutcome.Detected => $"[OK] {t.Title} : {t.Detail}",
                    AvOutcome.NotDetected => $"[!] {t.Title} : {t.Detail}",
                    _ => $"[i] {t.Title} : {t.Detail}"
                });
            }
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    /// <summary>
    /// Écrit le fichier puis attend la réaction de l'antivirus : écriture refusée (« contient un virus »),
    /// fichier supprimé / mis en quarantaine, ou lecture refusée. Sans réaction dans le délai : non détecté.
    /// </summary>
    private static async Task FileTestAsync(AvTest t, string path, byte[] content, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await File.WriteAllBytesAsync(path, content, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            t.Outcome = AvOutcome.Detected;
            t.Detail = $"Bloqué dès l'écriture ({watch.Elapsed.TotalSeconds:0.0} s).";
            return;
        }

        while (watch.Elapsed < ReactionTimeout)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(300, ct);
            if (!File.Exists(path))
            {
                t.Outcome = AvOutcome.Detected;
                t.Detail = $"Supprimé ou mis en quarantaine en {watch.Elapsed.TotalSeconds:0.0} s.";
                return;
            }
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.ReadByte();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                t.Outcome = AvOutcome.Detected;
                t.Detail = $"Accès bloqué par l'antivirus en {watch.Elapsed.TotalSeconds:0.0} s.";
                return;
            }
        }
        t.Outcome = AvOutcome.NotDetected;
        t.Detail = $"Aucune réaction en {ReactionTimeout.TotalSeconds:0} s : le fichier de test est resté accessible.";
    }

    /// <summary>Archive non détectée à l'écriture : certains antivirus ne regardent dans les ZIP qu'à l'analyse. Essai avec Defender (si présent).</summary>
    private static async Task OnDemandZipScanAsync(AvTest t, string path, CancellationToken ct)
    {
        string mpcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        if (!File.Exists(mpcmd)) return;
        try { await File.WriteAllBytesAsync(path, EicarZip(), ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            t.Outcome = AvOutcome.Detected; t.Detail = "Non détecté au premier essai, bloqué au second (analyse différée)."; return;
        }
        var psi = new ProcessStartInfo(mpcmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode == 2)   // MpCmdRun : 2 = menace trouvée
        {
            t.Outcome = AvOutcome.Detected;
            t.Detail = "Pas bloqué à l'écriture, mais détecté par une analyse de Microsoft Defender (archives analysées à la demande).";
        }
        else t.Detail += " Analyse Defender de l'archive : rien trouvé.";
    }

    /// <summary>
    /// Script contenant l'échantillon AMSI, passé en -EncodedCommand (jamais écrit sur le disque) : un antivirus
    /// branché sur AMSI le bloque avec l'erreur « ScriptContainedMaliciousContent » (identifiant non traduit).
    /// </summary>
    private static async Task AmsiTestAsync(AvTest t, CancellationToken ct)
    {
        string script = $"'{AmsiSample()}' | Out-Null; Write-Output 'NON_BLOQUE'";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }) psi.ArgumentList.Add(a);
        var watch = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        string output = await outTask, error = await errTask;
        if (error.Contains("ScriptContainedMaliciousContent", StringComparison.OrdinalIgnoreCase)
            || error.Contains("malicious", StringComparison.OrdinalIgnoreCase) || error.Contains("malveillant", StringComparison.OrdinalIgnoreCase))
        {
            t.Outcome = AvOutcome.Detected;
            t.Detail = $"Script bloqué avant exécution ({watch.Elapsed.TotalSeconds:0.0} s).";
        }
        else if (output.Contains("NON_BLOQUE"))
        {
            t.Outcome = AvOutcome.NotDetected;
            t.Detail = "Le script s'est exécuté : l'antivirus n'analyse pas les scripts (AMSI) ou cette protection est désactivée.";
        }
        else
        {
            t.Outcome = AvOutcome.Error;
            t.Detail = "Résultat indéterminé : " + (error.Trim().Length > 0 ? error.Trim().Split('\n')[0] : $"code {p.ExitCode}");
        }
    }

    /// <summary>Nom des détections récentes de Microsoft Defender (confirmation, ex. « Virus:DOS/EICAR_Test_File »).</summary>
    public static async Task<List<string>> GetRecentDefenderDetectionsAsync(CancellationToken ct = default)
    {
        try
        {
            string text = await PowerShellRunner.RunAsync(@"
$since = (Get-Date).AddMinutes(-10)
Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -gt $since } |
    ForEach-Object { (Get-MpThreat -ThreatID $_.ThreatID -ErrorAction SilentlyContinue).ThreatName } | Sort-Object -Unique", ct);
            return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(); }
    }

    private static void DeleteFolder(string folder)
    {
        if (!Path.GetFileName(folder).StartsWith(FolderPrefix, StringComparison.Ordinal)) return;   // garde-fou
        for (int i = 0; i < 5 && Directory.Exists(folder); i++)
        {
            try { Directory.Delete(folder, true); }
            catch { Thread.Sleep(400); }
        }
    }
}
