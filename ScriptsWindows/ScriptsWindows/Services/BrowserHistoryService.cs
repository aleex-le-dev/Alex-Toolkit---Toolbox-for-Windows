using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ScriptsWindows.Services;

/// <summary>
/// Historique de navigation de Chrome, Edge, Brave, Vivaldi, Opera (tous les profils) et Firefox.
/// La base est copiée avant lecture : fonctionne même navigateur ouvert, sans rien modifier.
/// </summary>
public static class BrowserHistoryService
{
    public record HistoryEntry(string Browser, string Profile, string Title, string Url, string Domain, long Visits, DateTime LastVisit);

    public static List<HistoryEntry> Read(int maxPerProfile, CancellationToken ct, Action<string>? onError = null)
    {
        try { SQLitePCL.Batteries_V2.Init(); } catch { }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<HistoryEntry>();

        var chromium = new (string Browser, string UserData)[]
        {
            ("Google Chrome", Path.Combine(local, @"Google\Chrome\User Data")),
            ("Microsoft Edge", Path.Combine(local, @"Microsoft\Edge\User Data")),
            ("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data")),
            ("Vivaldi", Path.Combine(local, @"Vivaldi\User Data")),
        };
        foreach (var (browser, userData) in chromium)
        {
            if (!Directory.Exists(userData)) continue;
            foreach (var dir in SafeDirs(userData))
            {
                string name = Path.GetFileName(dir);
                if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                string db = Path.Combine(dir, "History");
                if (File.Exists(db)) ReadChromium(browser, ProfileLabel(name), db, maxPerProfile, list, ct, onError);
            }
        }

        // Opera : un seul profil, base à la racine
        string opera = Path.Combine(roaming, @"Opera Software\Opera Stable\History");
        if (File.Exists(opera)) ReadChromium("Opera", "Profil principal", opera, maxPerProfile, list, ct, onError);
        string operaGx = Path.Combine(roaming, @"Opera Software\Opera GX Stable\History");
        if (File.Exists(operaGx)) ReadChromium("Opera GX", "Profil principal", operaGx, maxPerProfile, list, ct, onError);

        // Firefox : tous les profils
        string ffProfiles = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ffProfiles))
            foreach (var dir in SafeDirs(ffProfiles))
            {
                string db = Path.Combine(dir, "places.sqlite");
                if (!File.Exists(db)) continue;
                string label = Path.GetFileName(dir);
                int dot = label.IndexOf('.');
                ReadFirefox(dot >= 0 ? label[(dot + 1)..] : label, db, maxPerProfile, list, ct, onError);
            }

        return list.OrderByDescending(e => e.LastVisit).ToList();
    }

    private static string ProfileLabel(string folder) => folder.Equals("Default", StringComparison.OrdinalIgnoreCase) ? "Profil principal" : folder.Replace("Profile ", "Profil ");

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static void ReadChromium(string browser, string profile, string dbPath, int max, List<HistoryEntry> list, CancellationToken ct, Action<string>? onError)
    {
        Query(dbPath, $@"SELECT title, url, visit_count, last_visit_time FROM urls
                         WHERE url IS NOT NULL AND last_visit_time > 0
                           AND url NOT LIKE 'chrome%://%' AND url NOT LIKE 'edge://%' AND url NOT LIKE 'brave://%'
                           AND url NOT LIKE 'vivaldi://%' AND url NOT LIKE 'opera://%' AND url NOT LIKE 'about:%'
                         ORDER BY last_visit_time DESC LIMIT {max};",
            raw => DateTime.FromFileTimeUtc(raw * 10).ToLocalTime(),
            browser, profile, list, ct, onError);
    }

    private static void ReadFirefox(string profile, string dbPath, int max, List<HistoryEntry> list, CancellationToken ct, Action<string>? onError)
    {
        Query(dbPath, $@"SELECT title, url, visit_count, last_visit_date FROM moz_places
                         WHERE url IS NOT NULL AND last_visit_date IS NOT NULL
                           AND url NOT LIKE 'place:%' AND url NOT LIKE 'about:%' AND url NOT LIKE 'moz-extension:%'
                         ORDER BY last_visit_date DESC LIMIT {max};",
            raw => DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000).LocalDateTime,
            "Mozilla Firefox", profile, list, ct, onError);
    }

    private static void Query(string dbPath, string sql, Func<long, DateTime> toDate, string browser, string profile,
                              List<HistoryEntry> list, CancellationToken ct, Action<string>? onError)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"atk_hist_{Guid.NewGuid():N}.db");
        try
        {
            // Copie partagée : le navigateur peut rester ouvert (base verrouillée)
            using (var src = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write))
                src.CopyTo(dst);

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (ct.IsCancellationRequested) break;
                string url = r.IsDBNull(1) ? "" : r.GetString(1);
                if (url.Length == 0) continue;
                string title = r.IsDBNull(0) ? "" : r.GetString(0);
                long visits = r.IsDBNull(2) ? 1 : r.GetInt64(2);
                DateTime when;
                try { when = r.IsDBNull(3) ? default : toDate(r.GetInt64(3)); } catch { when = default; }
                string domain = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.StartsWith("www.") ? u.Host[4..] : u.Host : "";
                list.Add(new HistoryEntry(browser, profile, string.IsNullOrWhiteSpace(title) ? url : title.Trim(), url, domain, visits, when));
            }
        }
        catch (Exception ex) { onError?.Invoke($"[!] {browser} ({profile}) : lecture impossible — {ex.Message}"); }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(temp); } catch { }
        }
    }

    public static void ExportCsv(IEnumerable<HistoryEntry> entries, string path)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Date;Navigateur;Profil;Site;Titre;Adresse;Visites\r\n");
        foreach (var e in entries)
            sb.Append($"{e.LastVisit:dd/MM/yyyy HH:mm};{Q(e.Browser)};{Q(e.Profile)};{Q(e.Domain)};{Q(e.Title)};{Q(e.Url)};{e.Visits}\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM : accents corrects dans Excel
    }
}
