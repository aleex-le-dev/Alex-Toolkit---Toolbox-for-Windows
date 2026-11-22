using System.IO;
using System.Text.Json;

namespace ScriptsWindows.Services;

/// <summary>Où en est la suppression d'un compte.</summary>
public enum TrackState { ToDelete, Requested, Deleted, Keep }

/// <summary>Suivi d'un compte (clé : site + pseudo). Dates : changement d'état, dernière revérification.</summary>
public sealed class TrackEntry
{
    public string Site { get; set; } = "";
    public string Username { get; set; } = "";
    public string Url { get; set; } = "";
    public TrackState State { get; set; }
    public DateTime Changed { get; set; } = DateTime.Now;
    public DateTime? Rechecked { get; set; }
    /// <summary>Résultat de la dernière revérification : true = le compte répond encore, false = disparu.</summary>
    public bool? StillOnline { get; set; }
}

/// <summary>Statistiques d'un site au fil des recherches (fiabilité de sa détection).</summary>
public sealed class SiteStat
{
    public string Name { get; set; } = "";
    public int Checks { get; set; }
    public int Blocked { get; set; }        // refus, anti-robots, injoignable, délai dépassé
    public int Unreliable { get; set; }     // contrôles : trouve tout le monde ou ne trouve plus un compte connu
    public DateTime Last { get; set; } = DateTime.Now;
    /// <summary>Part des vérifications qui ont donné une réponse exploitable (0 à 1) ; 1 si jamais vérifié.</summary>
    public double Reliability => Checks == 0 ? 1 : Math.Max(0, (double)(Checks - Blocked - Unreliable) / Checks);
    public string Label => Checks < 2 ? "" : Reliability >= 0.8 ? "site fiable" : Reliability >= 0.4 ? "site parfois bloqué" : $"site qui bloque souvent ({Blocked + Unreliable} fois sur {Checks})";
}

/// <summary>
/// Suivi de la suppression des comptes et fiabilité des sites, enregistrés sur ce PC uniquement
/// (%LocalAppData%\ScriptsWindows\account_tracking.json et site_stats.json). Écritures atomiques ; fichier abîmé = vide.
/// </summary>
public static class AccountTrackingService
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows");
    public static string TrackingPath { get; set; } = Path.Combine(Dir, "account_tracking.json");
    public static string StatsPath { get; set; } = Path.Combine(Dir, "site_stats.json");
    private static readonly object Lock = new();

    public static string Key(string site, string user) => (site.Trim() + "|" + user.Trim()).ToLowerInvariant();

    private static Dictionary<string, T> Read<T>(string path, Func<T, string> key)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var list = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new();
            var d = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in list) d[key(x)] = x;
            return d;
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void Write<T>(string path, IEnumerable<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList()));
        File.Move(tmp, path, true);
    }

    // ─── Suivi des suppressions ───
    public static Dictionary<string, TrackEntry> LoadTracking() { lock (Lock) return Read<TrackEntry>(TrackingPath, t => Key(t.Site, t.Username)); }

    /// <summary>État d'un compte (à supprimer par défaut).</summary>
    public static TrackState StateOf(Dictionary<string, TrackEntry> all, AccountResult r) =>
        all.TryGetValue(Key(r.Site, r.Username), out var t) ? t.State : TrackState.ToDelete;

    public static TrackEntry SetState(AccountResult r, TrackState state)
    {
        lock (Lock)
        {
            var all = Read<TrackEntry>(TrackingPath, t => Key(t.Site, t.Username));
            if (!all.TryGetValue(Key(r.Site, r.Username), out var t)) all[Key(r.Site, r.Username)] = t = new TrackEntry { Site = r.Site, Username = r.Username };
            t.Url = r.Url;
            if (t.State != state) { t.State = state; t.Changed = DateTime.Now; t.StillOnline = null; }
            Write(TrackingPath, all.Values);
            return t;
        }
    }

    public static void SaveRecheck(AccountResult r, bool stillOnline)
    {
        lock (Lock)
        {
            var all = Read<TrackEntry>(TrackingPath, t => Key(t.Site, t.Username));
            if (!all.TryGetValue(Key(r.Site, r.Username), out var t)) return;
            t.Rechecked = DateTime.Now; t.StillOnline = stillOnline;
            if (!stillOnline && t.State == TrackState.Requested) { t.State = TrackState.Deleted; t.Changed = DateTime.Now; }   // demande aboutie
            Write(TrackingPath, all.Values);
        }
    }

    public static string StateLabel(TrackState s) => s switch
    {
        TrackState.Requested => "Demande envoyée", TrackState.Deleted => "Supprimé", TrackState.Keep => "À garder", _ => "À supprimer"
    };

    // ─── Fiabilité des sites ───
    public static Dictionary<string, SiteStat> LoadStats() { lock (Lock) return Read<SiteStat>(StatsPath, s => s.Name); }

    /// <summary>Enregistre le bilan d'une recherche : par site, bloqué ou non, détection non fiable ou non.</summary>
    public static void RecordSearch(IEnumerable<(string Site, bool Blocked, bool Unreliable)> outcomes)
    {
        lock (Lock)
        {
            var all = Read<SiteStat>(StatsPath, s => s.Name);
            foreach (var (site, blocked, unreliable) in outcomes)
            {
                if (!all.TryGetValue(site, out var s)) all[site] = s = new SiteStat { Name = site };
                s.Checks++; if (blocked) s.Blocked++; else if (unreliable) s.Unreliable++;
                s.Last = DateTime.Now;
                if (s.Checks > 20) { s.Blocked = s.Blocked * 20 / s.Checks; s.Unreliable = s.Unreliable * 20 / s.Checks; s.Checks = 20; }   // fenêtre glissante
            }
            Write(StatsPath, all.Values);
        }
    }
}
