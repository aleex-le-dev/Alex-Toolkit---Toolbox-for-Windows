using System.IO;
using System.Text;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Réseaux Wi-Fi mémorisés par Windows et leur mot de passe (netsh, lecture seule).</summary>
public static class WifiProfileService
{
    public record WifiProfile(string Ssid, string? Password, string Authentication, bool IsConnected)
    {
        public bool IsOpen => string.IsNullOrEmpty(Password);
    }

    public static async Task<List<WifiProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        string raw = await CommandRunner.RunAsync("netsh wlan show profiles", ct);
        var names = new List<string>();
        foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            string prefix = line[..idx];
            // « Profil Tous les utilisateurs : X » / « All User Profile : X »
            if (!prefix.Contains("Profil", StringComparison.OrdinalIgnoreCase)) continue;
            string name = line[(idx + 1)..].Trim();
            if (name.Length > 0 && !name.Contains('<') && !names.Contains(name)) names.Add(name);
        }

        string connected = await GetConnectedSsidAsync(ct);

        var results = new WifiProfile[names.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, names.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) =>
            {
                string ssid = names[i];
                string? password = null, auth = "";
                try
                {
                    string details = await CommandRunner.RunAsync($"netsh wlan show profile name=\"{ssid}\" key=clear", token);
                    foreach (var l in details.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int c = l.IndexOf(':');
                        if (c < 0) continue;
                        string key = l[..c].Trim();
                        string value = l[(c + 1)..].Trim();
                        // « Contenu de la clé » / « Key Content » (préfixe : évite les soucis d'accents)
                        if (password == null && (key.StartsWith("Contenu de la cl", StringComparison.OrdinalIgnoreCase)
                                                 || key.StartsWith("Key Content", StringComparison.OrdinalIgnoreCase)))
                            password = value;
                        else if (auth.Length == 0 && key.StartsWith("Authenti", StringComparison.OrdinalIgnoreCase))
                            auth = value;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* profil illisible : affiché sans mot de passe */ }
                results[i] = new WifiProfile(ssid, password, auth, string.Equals(ssid, connected, StringComparison.Ordinal));
            });

        return results.Where(r => r != null)
                      .OrderByDescending(r => r.IsConnected)
                      .ThenBy(r => r.Ssid, StringComparer.CurrentCultureIgnoreCase)
                      .ToList();
    }

    private static async Task<string> GetConnectedSsidAsync(CancellationToken ct)
    {
        try
        {
            string raw = await CommandRunner.RunAsync("netsh wlan show interfaces", ct);
            foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line[..c].Trim().Equals("SSID", StringComparison.OrdinalIgnoreCase))
                    return line[(c + 1)..].Trim();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return "";
    }

    /// <summary>Fichier texte lisible (à conserver en lieu sûr : il contient les mots de passe en clair).</summary>
    public static void ExportText(IEnumerable<WifiProfile> profiles, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===========================================================");
        sb.AppendLine($" RÉSEAUX WI-FI ENREGISTRÉS — {Environment.MachineName}");
        sb.AppendLine($" Exporté le {DateTime.Now:dd/MM/yyyy HH:mm} avec Alex Toolkit");
        sb.AppendLine("===========================================================");
        sb.AppendLine();
        foreach (var p in profiles)
        {
            sb.AppendLine($"Réseau        : {p.Ssid}{(p.IsConnected ? "  (connecté)" : "")}");
            sb.AppendLine($"Sécurité      : {(p.Authentication.Length > 0 ? p.Authentication : "inconnue")}");
            sb.AppendLine($"Mot de passe  : {(p.IsOpen ? "(réseau ouvert ou clé d'entreprise)" : p.Password)}");
            sb.AppendLine("-----------------------------------------------------------");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
