using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public enum StartupSource { RunUser, RunMachine, RunMachine32, FolderUser, FolderCommon, StoreApp, LogonTask }

/// <summary>Programme lancé au démarrage de Windows (ou à l'ouverture de session).</summary>
public class StartupEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>Fichier lancé (vide si inconnu : application du Store).</summary>
    public string TargetPath { get; set; } = "";
    public StartupSource Source { get; set; }
    public bool Enabled { get; set; }
    public bool FileMissing { get; set; }
    /// <summary>Réglé par une stratégie de l'entreprise : non modifiable ici.</summary>
    public bool Locked { get; set; }
    /// <summary>Clé interne : nom de la valeur Run, nom du fichier du dossier Démarrage, chemin de la tâche, clé du Store.</summary>
    public string Id { get; set; } = "";

    public string SourceLabel => Source switch
    {
        StartupSource.RunUser => "Registre · ce compte",
        StartupSource.RunMachine => "Registre · tous les comptes",
        StartupSource.RunMachine32 => "Registre · tous les comptes (32 bits)",
        StartupSource.FolderUser => "Dossier Démarrage · ce compte",
        StartupSource.FolderCommon => "Dossier Démarrage · tous les comptes",
        StartupSource.StoreApp => "Application du Store",
        _ => "Tâche planifiée à l'ouverture de session"
    };
    public bool IsMicrosoft => Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Programmes au démarrage, comme l'onglet Démarrage du Gestionnaire des tâches (mêmes sources, même état).
/// Désactiver ne supprime RIEN : comme le Gestionnaire des tâches, on écrit l'état dans les clés
/// « StartupApproved » (1er octet pair = activé, impair = désactivé), réactivable à tout moment ici ou dans
/// le Gestionnaire des tâches. Applications du Store : valeur State de leur StartupTask. Tâches planifiées
/// (hors Microsoft) lancées à l'ouverture de session ou au démarrage : schtasks /ENABLE | /DISABLE.
/// </summary>
public static class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string StoreTasksPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    // Racines et dossiers remplaçables par le banc de test (jamais le vrai démarrage pendant un test).
    internal static Func<bool, RegistryKey> Root { get; set; } = machine => machine ? Registry.LocalMachine : Registry.CurrentUser;
    internal static string UserFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    internal static string CommonFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
    internal static bool IncludeTasks { get; set; } = true;

    private static readonly Regex HashSuffix = new(@"_[0-9A-F]{16,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Tous les programmes au démarrage, triés : activés d'abord, puis par nom.</summary>
    public static async Task<List<StartupEntry>> GetEntriesAsync(CancellationToken ct = default)
    {
        var list = await Task.Run(() =>
        {
            var l = new List<StartupEntry>();
            ReadRun(l, false, RunPath, StartupSource.RunUser, "Run");
            ReadRun(l, true, RunPath, StartupSource.RunMachine, "Run");
            ReadRun(l, true, Run32Path, StartupSource.RunMachine32, "Run32");
            ReadFolder(l, UserFolder, false, StartupSource.FolderUser);
            ReadFolder(l, CommonFolder, true, StartupSource.FolderCommon);
            ReadStoreApps(l);
            return l;
        }, ct);
        if (IncludeTasks) list.AddRange(await ReadLogonTasksAsync(ct));
        return list.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ─── Lecture ─────────────────────────────────────────────────────────────────

    private static void ReadRun(List<StartupEntry> list, bool machine, string path, StartupSource source, string approvedName)
    {
        using var key = Root(machine).OpenSubKey(path);
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
            var e = new StartupEntry { Id = name, Name = HashSuffix.Replace(name, ""), Command = cmd, Source = source,
                                       Enabled = IsApproved(machine, approvedName, name) };
            Describe(e, ExtractPath(cmd));
            list.Add(e);
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, bool machine, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string target = file, cmd = file;
            if (fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var (t, args) = ResolveShortcut(file);
                if (t.Length > 0) { target = t; cmd = string.IsNullOrWhiteSpace(args) ? $"\"{t}\"" : $"\"{t}\" {args}"; }
            }
            var e = new StartupEntry { Id = fileName, Name = Path.GetFileNameWithoutExtension(file), Command = cmd, Source = source,
                                       Enabled = IsApproved(machine, "StartupFolder", fileName) };
            Describe(e, target);
            list.Add(e);
        }
    }

    /// <summary>Applications du Store déclarant une « StartupTask » (Spotify, Teams, WhatsApp…).
    /// State : 0 désactivée, 1 désactivée par l'utilisateur, 2 activée, 3 désactivée par stratégie, 4 activée par stratégie.</summary>
    private static void ReadStoreApps(List<StartupEntry> list)
    {
        using var root = Root(false).OpenSubKey(StoreTasksPath);
        if (root == null) return;
        foreach (var pkg in root.GetSubKeyNames())
        {
            using var pk = root.OpenSubKey(pkg);
            if (pk == null) continue;
            foreach (var task in pk.GetSubKeyNames())
            {
                using var tk = pk.OpenSubKey(task);
                if (tk?.GetValue("State") is not int state) continue;
                // « SpotifyAB.SpotifyMusic_zpdnekdrzrea0 » → « SpotifyMusic », éditeur « SpotifyAB »
                string family = pkg.Split('_')[0];
                int dot = family.IndexOf('.');
                list.Add(new StartupEntry
                {
                    Id = $"{pkg}\\{task}", Name = dot > 0 ? family[(dot + 1)..] : family, Publisher = dot > 0 ? family[..dot] : "",
                    Command = pkg, Source = StartupSource.StoreApp, Enabled = state is 2 or 4, Locked = state is 3 or 4
                });
            }
        }
    }

    /// <summary>Tâches planifiées hors Microsoft lancées à l'ouverture de session ou au démarrage (mises à jour Google, Adobe…).</summary>
    private static async Task<List<StartupEntry>> ReadLogonTasksAsync(CancellationToken ct)
    {
        var list = new List<StartupEntry>();
        try
        {
            string json = await PowerShellRunner.RunAsync(@"
$r = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
    $_.TaskPath -notlike '\Microsoft\*' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' })
} | ForEach-Object {
    $a = @($_.Actions | Where-Object { $_.Execute }) | Select-Object -First 1
    [pscustomobject]@{ P = [string]$_.TaskPath; N = [string]$_.TaskName; S = [string]$_.State; E = [string]$a.Execute; A = [string]$a.Arguments; D = [string]$_.Description }
}
ConvertTo-Json -InputObject @($r) -Compress", ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                string exe = Environment.ExpandEnvironmentVariables((t.GetProperty("E").GetString() ?? "").Trim('"'));
                string args = t.GetProperty("A").GetString() ?? "";
                var e = new StartupEntry
                {
                    Id = (t.GetProperty("P").GetString() ?? "\\") + t.GetProperty("N").GetString(),
                    Name = t.GetProperty("N").GetString() ?? "", Source = StartupSource.LogonTask,
                    Command = exe.Length == 0 ? "(action non exécutable)" : $"\"{exe}\" {args}".Trim(),
                    Enabled = !string.Equals(t.GetProperty("S").GetString(), "Disabled", StringComparison.OrdinalIgnoreCase)
                };
                Describe(e, exe);
                if (e.Description.Length == 0) e.Description = t.GetProperty("D").GetString() ?? "";
                list.Add(e);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Planificateur indisponible : les autres sources suffisent */ }
        return list;
    }

    /// <summary>État dans StartupApproved : absent = activé ; 1er octet impair = désactivé.</summary>
    private static bool IsApproved(bool machine, string approvedName, string valueName)
    {
        using var k = Root(machine).OpenSubKey(ApprovedPath + approvedName);
        return k?.GetValue(valueName) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
    }

    /// <summary>Fichier lancé par une ligne de commande : « "C:\a b\x.exe" -arg », « C:\x.exe /s », « rundll32 x.dll,Fn ».</summary>
    internal static string ExtractPath(string command)
    {
        string cmd = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        string rest;
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            path = end > 1 ? cmd[1..end] : cmd.Trim('"');
            rest = end > 1 ? cmd[(end + 1)..].Trim() : "";
        }
        else
        {
            int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
            rest = exe > 0 ? cmd[(exe + 4)..].Trim() : "";
        }
        // rundll32 : le vrai programme est la DLL passée en argument
        if (Path.GetFileName(path).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) && rest.Length > 0)
        {
            string dll = rest.Trim('"').Split(',')[0].Trim().Trim('"');
            if (dll.Length > 0) path = dll;
        }
        // « ctfmon.exe » sans dossier : dossier système
        if (path.Length > 0 && !Path.IsPathRooted(path))
        {
            string sys = Path.Combine(Environment.SystemDirectory, path);
            if (File.Exists(sys)) path = sys;
        }
        return path;
    }

    private static void Describe(StartupEntry e, string path)
    {
        e.TargetPath = path;
        e.FileMissing = path.Length > 0 && Path.IsPathRooted(path) && !File.Exists(path);
        if (e.FileMissing || path.Length == 0 || !File.Exists(path)) return;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            e.Publisher = v.CompanyName?.Trim() ?? "";
            e.Description = v.FileDescription?.Trim() ?? "";
        }
        catch { }
    }

    /// <summary>Cible et arguments d'un raccourci .lnk (WScript.Shell, appelé par réflexion : pas de dépendance COM).</summary>
    private static (string Target, string Args) ResolveShortcut(string lnk)
    {
        object? shell = null, sc = null;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return ("", "");
            shell = Activator.CreateInstance(t);
            sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var st = sc!.GetType();
            string target = st.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
            string args = st.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string ?? "";
            return (target, args);
        }
        catch { return ("", ""); }
        finally
        {
            if (sc != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    // ─── Activation / désactivation (réversible) ─────────────────────────────────

    /// <summary>Active ou désactive au démarrage, sans rien supprimer. Renvoie l'état relu après écriture.</summary>
    public static async Task<bool> SetEnabledAsync(StartupEntry e, bool enabled, CancellationToken ct = default)
    {
        if (e.Locked) throw new InvalidOperationException("Ce programme est imposé par une stratégie de l'entreprise.");
        switch (e.Source)
        {
            case StartupSource.RunUser: WriteApproved(false, "Run", e.Id, enabled); return IsApproved(false, "Run", e.Id);
            case StartupSource.RunMachine: WriteApproved(true, "Run", e.Id, enabled); return IsApproved(true, "Run", e.Id);
            case StartupSource.RunMachine32: WriteApproved(true, "Run32", e.Id, enabled); return IsApproved(true, "Run32", e.Id);
            case StartupSource.FolderUser: WriteApproved(false, "StartupFolder", e.Id, enabled); return IsApproved(false, "StartupFolder", e.Id);
            case StartupSource.FolderCommon: WriteApproved(true, "StartupFolder", e.Id, enabled); return IsApproved(true, "StartupFolder", e.Id);
            case StartupSource.StoreApp:
                using (var k = Root(false).OpenSubKey(StoreTasksPath + "\\" + e.Id, writable: true)
                               ?? throw new InvalidOperationException("Application introuvable (désinstallée ?)."))
                {
                    k.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);   // 1 = désactivée par l'utilisateur
                    return k.GetValue("State") is int s && s is 2 or 4;
                }
            default:
                // Nom de tâche passé en argument (ArgumentList) : jamais inséré dans une commande
                var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "/Change", "/TN", e.Id, enabled ? "/ENABLE" : "/DISABLE" }) psi.ArgumentList.Add(a);
                using (var p = Process.Start(psi)!)
                {
                    string err = await p.StandardError.ReadToEndAsync(ct);
                    await p.StandardOutput.ReadToEndAsync(ct);
                    await p.WaitForExitAsync(ct);
                    if (p.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"schtasks a échoué (code {p.ExitCode})." : err.Trim());
                }
                return enabled;
        }
    }

    /// <summary>Même format que le Gestionnaire des tâches : 12 octets, 02 = activé, 03 = désactivé + date (FILETIME).</summary>
    private static void WriteApproved(bool machine, string approvedName, string valueName, bool enabled)
    {
        using var k = Root(machine).CreateSubKey(ApprovedPath + approvedName, writable: true);
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        k.SetValue(valueName, data, RegistryValueKind.Binary);
    }

    /// <summary>Ouvre l'Explorateur sur le fichier lancé.</summary>
    public static bool OpenLocation(StartupEntry e)
    {
        if (e.TargetPath.Length == 0 || !File.Exists(e.TargetPath)) return false;
        // Explorer veut « /select,"chemin" » (guillemets autour du chemin seul) ; un chemin Windows ne contient jamais de guillemet.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{e.TargetPath}\"") { UseShellExecute = false });
        return true;
    }
}
