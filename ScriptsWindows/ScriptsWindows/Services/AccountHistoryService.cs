using System.IO;
using System.Text.Json;

namespace ScriptsWindows.Services;

/// <summary>Une recherche passée : pseudos, date, comptes trouvés / à vérifier / ajoutés à la main (les absents ne sont pas gardés).</summary>
public sealed class AccountSearchEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; } = DateTime.Now;
    public List<string> Usernames { get; set; } = new();
    public int SitesChecked { get; set; }
    public bool Complete { get; set; }
    public List<AccountResult> Results { get; set; } = new();
}

/// <summary>
/// Historique des recherches de comptes, sur ce PC uniquement (%LocalAppData%\ScriptsWindows\account_history.json),
/// 50 recherches au plus, la plus récente en tête. Effaçable recherche par recherche ou en entier.
/// </summary>
public static class AccountHistoryService
{
    public static string FilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptsWindows", "account_history.json");
    private const int Max = 50;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public static List<AccountSearchEntry> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<AccountSearchEntry>>(File.ReadAllText(FilePath), Opts) ?? new() : new(); }
        catch { return new(); }   // fichier abîmé : historique vide plutôt qu'une erreur
    }

    private static void Write(List<AccountSearchEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list.OrderByDescending(e => e.Date).Take(Max).ToList(), Opts));
        File.Move(tmp, FilePath, true);   // écriture atomique
    }

    /// <summary>Ajoute ou met à jour une recherche (même Id). Seuls les comptes trouvés, à vérifier ou ajoutés sont gardés.</summary>
    public static void Save(AccountSearchEntry e)
    {
        e.Results = e.Results.Where(r => r.Status is AccountStatus.Found or AccountStatus.Uncertain).ToList();
        var list = Load();
        list.RemoveAll(x => x.Id == e.Id);
        list.Add(e);
        Write(list);
    }

    public static void Delete(string id) { var list = Load(); if (list.RemoveAll(x => x.Id == id) > 0) Write(list); }

    public static void Clear() { try { File.Delete(FilePath); } catch { } }
}
