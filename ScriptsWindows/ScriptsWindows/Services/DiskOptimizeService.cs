using System.Text.Json;
using System.Text.RegularExpressions;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public sealed record OptimizeVolume(char Letter, string Label, string FileSystem, long Size, string Media, string Bus, DateTime? LastRun)
{
    public bool IsSsd => Media == "SSD";
    public string MediaLabel => Media switch { "SSD" => "SSD", "HDD" => "Disque dur", _ => "Type inconnu" };
    public string Action => IsSsd ? "TRIM (informe le SSD des blocs libres)" : Media == "HDD" ? "Défragmentation" : "Optimisation adaptée";
}

/// <summary>
/// Optimisation des disques : type de support (Get-PhysicalDisk), dernière optimisation (journal Microsoft-Windows-Defrag
/// 258, lettre lue dans le message), tâche planifiée ScheduledDefrag, optimisation adaptée au support
/// (defrag /O : TRIM sur SSD, défragmentation sur disque dur) avec avancement (/U).
/// </summary>
public static class DiskOptimizeService
{
    private static readonly Regex Percent = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    public static async Task<(List<OptimizeVolume> Volumes, bool? ScheduleOn)> GetStateAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$last = @{}
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Microsoft-Windows-Defrag'; Id = 258 } -MaxEvents 200 -ErrorAction SilentlyContinue |
    ForEach-Object { if ($_.Message -match '\(([A-Z]):\)') { $l = $Matches[1]; if (-not $last.ContainsKey($l)) { $last[$l] = $_.TimeCreated.ToString('o') } } }
$vols = foreach ($v in Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' -and $_.FileSystem -in 'NTFS','ReFS' }) {
    $pd = $null
    try { $pd = Get-Partition -DriveLetter $v.DriveLetter -ErrorAction Stop | Get-Disk -ErrorAction Stop | Get-PhysicalDisk -ErrorAction Stop | Select-Object -First 1 } catch { }
    [pscustomobject]@{ L = [string]$v.DriveLetter; N = [string]$v.FileSystemLabel; F = [string]$v.FileSystem; S = [long]$v.Size
                       M = [string]$pd.MediaType; B = [string]$pd.BusType; R = [string]$last[[string]$v.DriveLetter] }
}
$t = Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' -TaskName 'ScheduledDefrag' -ErrorAction SilentlyContinue
ConvertTo-Json -Compress -Depth 3 -InputObject ([pscustomobject]@{ V = @($vols); T = if ($t) { [string]$t.State } else { '' } })", ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var list = new List<OptimizeVolume>();
        if (doc.RootElement.TryGetProperty("V", out var v))
            foreach (var x in v.EnumerateArray())
            {
                string l = x.GetProperty("L").GetString() ?? "";
                if (l.Length == 0) continue;
                list.Add(new OptimizeVolume(l[0], x.GetProperty("N").GetString() ?? "", x.GetProperty("F").GetString() ?? "", x.GetProperty("S").GetInt64(),
                    x.GetProperty("M").GetString() ?? "", x.GetProperty("B").GetString() ?? "",
                    DateTime.TryParse(x.GetProperty("R").GetString(), out var d) ? d : null));
            }
        string task = doc.RootElement.TryGetProperty("T", out var t) ? t.GetString() ?? "" : "";
        return (list.OrderBy(x => x.Letter).ToList(), task == "" ? null : task != "Disabled");
    }

    /// <summary>Optimise le volume selon son support (defrag /O), avancement en %. Renvoie le code de defrag (0 = OK).</summary>
    public static Task<int> OptimizeAsync(char letter, Action<double> progress, Action<string> log, CancellationToken ct)
        => CommandRunner.RunAsync($"defrag {letter}: /O /U /V", line =>
        {
            var m = Percent.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double p)) progress(Math.Min(100, p));
            else if (line.Trim().Length > 0) log(line.Trim());
        }, ct);

    /// <summary>Active ou désactive l'optimisation automatique hebdomadaire de Windows.</summary>
    public static async Task<bool> SetScheduleAsync(bool on, CancellationToken ct = default)
    {
        string verb = on ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        string r = await PowerShellRunner.RunAsync($"try {{ {verb} -TaskPath '\\Microsoft\\Windows\\Defrag\\' -TaskName 'ScheduledDefrag' -ErrorAction Stop | Out-Null; 'OK' }} catch {{ 'ERR' }}", ct);
        return r.Trim() == "OK";
    }

    public static string Size(long b) => b >= 1L << 40 ? $"{b / (double)(1L << 40):0.0} To" : $"{b / (double)(1L << 30):0} Go";
}
