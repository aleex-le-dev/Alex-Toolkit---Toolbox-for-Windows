using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Microsoft Defender : état de la protection, mise à jour, analyses et menaces détectées.</summary>
public static class DefenderService
{
    public record DefenderStatus(
        bool Available, bool AntivirusEnabled, bool RealTimeEnabled,
        DateTime? SignaturesUpdated, string SignatureVersion,
        DateTime? LastQuickScan, DateTime? LastFullScan,
        IReadOnlyList<string> OtherAntivirus, string? Error);

    public record Threat(string Id, string Name, int Severity, bool Active, string Resources, DateTime? Detected, int Status)
    {
        public string SeverityText => Severity switch { 5 => "Grave", 4 => "Élevée", 2 => "Moyenne", 1 => "Faible", _ => "Inconnue" };
        public bool IsSevere => Severity >= 4;
        /// <summary>ThreatStatusID : 3 = mise en quarantaine, 4 = supprimée, 6 = autorisée, 2/102… = nettoyée.</summary>
        public string StatusText => Active ? "Active : à traiter" : Status switch
        {
            3 => "En quarantaine", 4 => "Supprimée", 6 => "Autorisée par l'utilisateur", 2 or 102 or 103 or 104 or 105 or 106 => "Nettoyée",
            _ => "Traitée",
        };
    }

    public static async Task<DefenderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        const string script = @"
$st = Get-MpComputerStatus -ErrorAction SilentlyContinue
$av = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
        Where-Object { $_.displayName -notmatch 'Defender' } | ForEach-Object { [string]$_.displayName })
function D($d) { if ($d -and $d.Year -gt 2000) { $d.ToString('o') } else { '' } }
[pscustomobject]@{
  Ok = [bool]$st
  Av = [bool]$st.AntivirusEnabled
  Rt = [bool]$st.RealTimeProtectionEnabled
  SigDate = $(if ($st) { D $st.AntivirusSignatureLastUpdated } else { '' })
  SigVer = [string]$st.AntivirusSignatureVersion
  Quick = $(if ($st) { D $st.QuickScanEndTime } else { '' })
  Full = $(if ($st) { D $st.FullScanEndTime } else { '' })
  Others = $av
} | ConvertTo-Json -Compress";

        string json = await PowerShellRunner.RunAsync(script, ct);
        int start = json.IndexOf('{');
        if (start < 0)
            return new DefenderStatus(false, false, false, null, "", null, null, Array.Empty<string>(),
                PowerShellRunner.LastErrors.FirstOrDefault() ?? "État de Defender illisible.");

        using var doc = JsonDocument.Parse(json[start..]);
        var r = doc.RootElement;
        bool B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        DateTime? D(string n) => DateTime.TryParse(S(n), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
        var others = new List<string>();
        if (r.TryGetProperty("Others", out var o))
        {
            if (o.ValueKind == JsonValueKind.Array) others.AddRange(o.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
            else if (o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } one) others.Add(one);
        }

        return new DefenderStatus(B("Ok"), B("Av"), B("Rt"), D("SigDate"), S("SigVer"), D("Quick"), D("Full"), others,
            B("Ok") ? null : "Microsoft Defender ne répond pas (désactivé, remplacé par un autre antivirus ou service arrêté).");
    }

    /// <summary>Met à jour les signatures. Renvoie null si tout s'est bien passé, sinon le message d'erreur.</summary>
    public static async Task<string?> UpdateSignaturesAsync(Action<string> onOutput, CancellationToken ct = default)
    {
        await PowerShellRunner.RunAsync("Update-MpSignature -ErrorAction Stop; 'OK'", ct);
        return PowerShellRunner.LastErrors.FirstOrDefault();
    }

    /// <summary>Lance une analyse rapide ou complète (bloquant jusqu'à la fin). Renvoie le message d'erreur éventuel.</summary>
    public static async Task<string?> ScanAsync(bool full, CancellationToken ct = default)
    {
        await PowerShellRunner.RunAsync($"Start-MpScan -ScanType {(full ? "FullScan" : "QuickScan")} -ErrorAction Stop; 'OK'", ct);
        return PowerShellRunner.LastErrors.FirstOrDefault();
    }

    public static async Task<List<Threat>> GetThreatsAsync(CancellationToken ct = default)
    {
        const string script = @"
$det = @(Get-MpThreatDetection -ErrorAction SilentlyContinue)
$list = New-Object System.Collections.Generic.List[object]
foreach ($t in @(Get-MpThreat -ErrorAction SilentlyContinue)) {
  $d = $det | Where-Object { $_.ThreatID -eq $t.ThreatID } | Sort-Object InitialDetectionTime -Descending | Select-Object -First 1
  $list.Add([pscustomobject]@{
    Id = [string]$t.ThreatID
    Name = [string]$t.ThreatName
    Severity = [int]$t.SeverityID
    Active = [bool]$t.IsActive
    Resources = (@($t.Resources) | Where-Object { $_ }) -join ' | '
    Detected = $(if ($d -and $d.InitialDetectionTime) { $d.InitialDetectionTime.ToString('o') } else { '' })
    Status = $(if ($d) { [int]$d.ThreatStatusID } else { 0 })
  })
}
ConvertTo-Json -InputObject $list.ToArray() -Compress";

        string json = await PowerShellRunner.RunAsync(script, ct);
        int start = json.IndexOf('[');
        if (start < 0) return new();
        using var doc = JsonDocument.Parse(json[start..]);
        var result = new List<Threat>();
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            string S(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            DateTime? det = DateTime.TryParse(S("Detected"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
            result.Add(new Threat(S("Id"), S("Name"), I("Severity"), t.TryGetProperty("Active", out var a) && a.ValueKind == JsonValueKind.True,
                S("Resources").Replace("file:_", ""), det, I("Status")));
        }
        return result.OrderByDescending(x => x.Active).ThenByDescending(x => x.Detected).ToList();
    }

    /// <summary>Supprime les menaces actives (action par défaut de Defender : quarantaine / suppression).</summary>
    public static async Task<string?> RemoveActiveThreatsAsync(CancellationToken ct = default)
    {
        await PowerShellRunner.RunAsync("Remove-MpThreat -ErrorAction Stop; 'OK'", ct);
        return PowerShellRunner.LastErrors.FirstOrDefault();
    }
}
