using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Service Windows tel qu'affiché par le gestionnaire.</summary>
public sealed class WinServiceInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; set; } = "";
    public string StartMode { get; set; } = "";
    public bool Delayed { get; set; }
    public bool TriggerStart { get; init; }
    public string ExePath { get; init; } = "";
    public string Company { get; set; } = "";
    public bool IsMicrosoft => Company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || ExePath.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) && Company.Length == 0;
    public bool IsCritical => WindowsServicesService.Critical.Contains(Name);
    /// <summary>Automatique mais arrêté (hors démarrage différé ou par déclencheur, normaux à l'arrêt) : à vérifier.</summary>
    public bool LooksBroken => StartMode == "Auto" && State == "Stopped" && !Delayed && !TriggerStart
                               && !WindowsServicesService.NormallyStops.Any(p => Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public string StateLabel => State switch { "Running" => "En cours", "Stopped" => "Arrêté", "Paused" => "En pause", "Start Pending" => "Démarrage…", "Stop Pending" => "Arrêt…", _ => State };
    public string StartLabel => StartMode switch { "Auto" => Delayed ? "Automatique (différé)" : "Automatique", "Manual" => TriggerStart ? "Manuel (déclencheur)" : "Manuel", "Disabled" => "Désactivé", "Boot" or "System" => "Système", _ => StartMode };
}

/// <summary>
/// Gestionnaire de services : liste complète (Win32_Service + éditeur du fichier lancé), démarrer / arrêter /
/// redémarrer, type de démarrage. Les noms sont validés et passés entre apostrophes doublées (jamais insérés bruts) ;
/// les services vitaux de Windows ne peuvent pas être arrêtés ni désactivés.
/// </summary>
public static class WindowsServicesService
{
    /// <summary>Services dont l'arrêt ou la désactivation rend Windows instable ou inutilisable.</summary>
    public static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "Winmgmt", "PlugPlay", "Power", "ProfSvc", "Schedule",
        "BFE", "MpsSvc", "WinDefend", "CryptSvc", "Dhcp", "Dnscache", "nsi", "BrokerInfrastructure", "CoreMessagingRegistrar",
        "SystemEventsBroker", "TimeBrokerSvc", "StateRepository", "UserManager", "gpsvc", "AudioEndpointBuilder", "Audiosrv",
        "LanmanWorkstation", "TrustedInstaller", "msiserver", "AppXSvc", "camsvc", "SecurityHealthService", "wscsvc", "EFS", "KeyIso", "VaultSvc"
    };

    /// <summary>Services automatiques qui s'arrêtent d'eux-mêmes une fois leur travail fait (pas une panne). Début du nom.</summary>
    public static readonly string[] NormallyStops =
    {
        "TrustedInstaller", "sppsvc", "GoogleUpdater", "gupdate", "edgeupdate", "MicrosoftEdgeElevationService", "MapsBroker",
        "RemoteRegistry", "tiledatamodelsvc", "wuauserv", "UsoSvc", "BITS", "DoSvc", "CDPSvc", "AdobeARMservice", "Brave", "MozillaMaintenance"
    };

    private static readonly Regex ValidName = new(@"^[\w.\-$@ ]{1,256}$", RegexOptions.Compiled);
    private static string Quote(string name) => "'" + name.Replace("'", "''") + "'";

    public static async Task<List<WinServiceInfo>> GetAllAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$trig = @{}
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services' -ErrorAction SilentlyContinue | ForEach-Object {
    if (Test-Path (Join-Path $_.PSPath 'TriggerInfo')) { $trig[$_.PSChildName] = $true }
}
$r = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue | ForEach-Object {
    $d = (Get-ItemProperty ""HKLM:\SYSTEM\CurrentControlSet\Services\$($_.Name)"" -Name DelayedAutostart -ErrorAction SilentlyContinue).DelayedAutostart
    [pscustomobject]@{ N = $_.Name; D = [string]$_.DisplayName; X = [string]$_.Description; S = [string]$_.State; M = [string]$_.StartMode
                       L = ($d -eq 1); T = [bool]$trig[$_.Name]; P = [string]$_.PathName }
}
ConvertTo-Json -Compress -InputObject @($r)", ct);
        var list = new List<WinServiceInfo>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        var companies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            string exe = ExtractExe(s.GetProperty("P").GetString() ?? "");
            var info = new WinServiceInfo
            {
                Name = s.GetProperty("N").GetString() ?? "", DisplayName = s.GetProperty("D").GetString() ?? "", Description = s.GetProperty("X").GetString() ?? "",
                State = s.GetProperty("S").GetString() ?? "", StartMode = s.GetProperty("M").GetString() ?? "", Delayed = s.GetProperty("L").GetBoolean(),
                TriggerStart = s.GetProperty("T").GetBoolean(), ExePath = exe
            };
            if (exe.Length > 0)
            {
                if (!companies.TryGetValue(exe, out var company))
                {
                    try { company = File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim() ?? "" : ""; } catch { company = ""; }
                    companies[exe] = company;
                }
                info.Company = company;
            }
            list.Add(info);
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Exécutable d'une ligne de commande de service (« "C:\…\x.exe" -k … » ou « C:\…\x.exe -k … »).</summary>
    internal static string ExtractExe(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (path.StartsWith('"')) { int e = path.IndexOf('"', 1); return e > 1 ? path[1..e] : path.Trim('"'); }
        int i = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? path[..(i + 4)] : path.Split(' ')[0];
    }

    /// <summary>Action : start | stop | restart. Renvoie le nouvel état ou lève une exception explicite.</summary>
    public static async Task<string> ControlAsync(WinServiceInfo s, string action, CancellationToken ct = default)
    {
        if (!ValidName.IsMatch(s.Name)) throw new ArgumentException("Nom de service invalide.");
        if (action != "start" && s.IsCritical) throw new InvalidOperationException($"« {s.DisplayName} » est vital pour Windows : il ne peut pas être arrêté ici.");
        string cmd = action switch
        {
            "start" => $"Start-Service -Name {Quote(s.Name)} -ErrorAction Stop",
            "stop" => $"Stop-Service -Name {Quote(s.Name)} -Force -ErrorAction Stop",
            "restart" => $"Restart-Service -Name {Quote(s.Name)} -Force -ErrorAction Stop",
            _ => throw new ArgumentException(action)
        };
        string r = (await PowerShellRunner.RunAsync($"try {{ {cmd}; (Get-Service -Name {Quote(s.Name)}).Status.ToString() }} catch {{ 'ERR ' + $_.Exception.Message }}", ct)).Trim();
        if (r.StartsWith("ERR ")) throw new InvalidOperationException(r[4..]);
        s.State = r == "Running" ? "Running" : r == "Stopped" ? "Stopped" : r;
        return s.State;
    }

    /// <summary>Type de démarrage : auto | delayed | manual | disabled.</summary>
    public static async Task SetStartAsync(WinServiceInfo s, string mode, CancellationToken ct = default)
    {
        if (!ValidName.IsMatch(s.Name)) throw new ArgumentException("Nom de service invalide.");
        if ((mode == "disabled" || mode == "manual") && s.IsCritical)
            throw new InvalidOperationException($"« {s.DisplayName} » est vital pour Windows : son démarrage ne peut pas être modifié ici.");
        string start = mode switch { "auto" => "auto", "delayed" => "delayed-auto", "manual" => "demand", "disabled" => "disabled", _ => throw new ArgumentException(mode) };
        // sc.exe : nom passé en argument séparé (ArgumentList), jamais dans une chaîne de commande
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "config", s.Name, "start=", start }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string outp = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException(outp.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"sc.exe code {p.ExitCode}");
        s.StartMode = mode switch { "auto" or "delayed" => "Auto", "manual" => "Manual", _ => "Disabled" };
        s.Delayed = mode == "delayed";
    }
}
