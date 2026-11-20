using System.Text;
using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public sealed record RestorePointInfo(int Sequence, DateTime? Date, string Description, string Kind);

/// <summary>État de la protection du système (disque de Windows).</summary>
public sealed record RestoreStatus(bool Protected, long UsedBytes, long MaxBytes, long VolumeBytes, List<RestorePointInfo> Points);

/// <summary>
/// Points de restauration : état réel de la protection, espace occupé, liste typée, création VÉRIFIÉE.
/// Windows refuse en silence un 2e point en moins de 24 h (SystemRestorePointCreationFrequency) : la limite est
/// levée le temps de la création puis remise à sa valeur. La description n'est jamais insérée dans du code
/// (encodée en base64, décodée par le script).
/// </summary>
public static class RestorePointService
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static string KindLabel(int type) => type switch
    {
        0 => "Installation d'un logiciel",
        1 => "Désinstallation d'un logiciel",
        10 => "Installation d'un pilote",
        12 => "Modification des paramètres",
        13 => "Opération annulée",
        _ => "Point de restauration"
    };

    public static async Task<RestoreStatus> GetStatusAsync(CancellationToken ct = default)
    {
        string json = await PowerShellRunner.RunAsync(@"
$sys = $env:SystemDrive
$vol = Get-CimInstance Win32_Volume -Filter ""DriveLetter='$sys'"" -ErrorAction SilentlyContinue
# Volumes protégés : valeur du client « Restauration du système » de SPP (contient l'identifiant des volumes)
$spp = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients' -ErrorAction SilentlyContinue).'{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}'
$guid = if ($vol) { ($vol.DeviceID -replace '^\\\\\?\\Volume', '').TrimEnd('\') } else { '' }
$protected = [bool]($guid -and ($spp -join ' ') -match [regex]::Escape($guid))
$used = 0; $max = 0
$st = Get-CimInstance Win32_ShadowStorage -ErrorAction SilentlyContinue | Where-Object { $_.Volume.DeviceID -eq $vol.DeviceID }
if ($st) { $used = [long]$st.UsedSpace; $max = [long]$st.MaxSpace }
$pts = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue | ForEach-Object {
    $d = try { ([Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime)).ToString('o') } catch { '' }
    [pscustomobject]@{ S = [int]$_.SequenceNumber; D = $d; T = [string]$_.Description; K = [int]$_.RestorePointType }
})
if ($pts.Count -gt 0) { $protected = $true }
ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{ P = $protected; U = $used; M = $max; V = [long]$vol.Capacity; L = $pts })", ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var r = doc.RootElement;
        var list = new List<RestorePointInfo>();
        if (r.TryGetProperty("L", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var p in l.EnumerateArray())
                list.Add(new RestorePointInfo(p.GetProperty("S").GetInt32(),
                    DateTime.TryParse(p.GetProperty("D").GetString(), out var d) ? d : null,
                    p.GetProperty("T").GetString() ?? "", KindLabel(p.GetProperty("K").GetInt32())));
        long L(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        return new RestoreStatus(r.TryGetProperty("P", out var pr) && pr.ValueKind == JsonValueKind.True, L("U"), L("M"), L("V"),
                                 list.OrderByDescending(p => p.Date ?? DateTime.MinValue).ToList());
    }

    /// <summary>Active la protection du disque de Windows et réserve <paramref name="percent"/> % du disque.</summary>
    public static async Task<bool> EnableProtectionAsync(int percent, Action<string> log, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 2, 20);
        string outp = await PowerShellRunner.RunAsync($@"
Enable-ComputerRestore -Drive ""$env:SystemDrive\"" -ErrorAction SilentlyContinue
& vssadmin resize shadowstorage /for=$env:SystemDrive /on=$env:SystemDrive /maxsize={percent}% | Out-Null
Write-Output 'fait'", ct);
        var st = await GetStatusAsync(ct);
        log(st.Protected ? $"[OK] Protection du système activée ({percent} % du disque réservé)." : "❌ [ERREUR] La protection du système n'a pas pu être activée.");
        return st.Protected;
    }

    /// <summary>Crée un point et vérifie qu'il existe. Renvoie (réussi, message).</summary>
    public static async Task<(bool Ok, string Message)> CreateAsync(string description, Action<string> log, CancellationToken ct = default)
    {
        description = string.IsNullOrWhiteSpace(description) ? "Point de restauration Alex Toolkit" : description.Trim();
        if (description.Length > 200) description = description[..200];
        var before = await GetStatusAsync(ct);
        int maxSeq = before.Points.Count == 0 ? 0 : before.Points.Max(p => p.Sequence);
        string result = (await PowerShellRunner.RunAsync($@"
$desc = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{B64(description)}'))
$key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
$old = (Get-ItemProperty $key -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
try {{
    Enable-ComputerRestore -Drive ""$env:SystemDrive\"" -ErrorAction SilentlyContinue
    # Limite « 1 point toutes les 24 h » levée le temps de la création
    Set-ItemProperty $key -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue
    Checkpoint-Computer -Description $desc -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningAction SilentlyContinue
    Write-Output 'OK'
}} catch {{
    Write-Output ('ERR ' + $_.Exception.Message)
}} finally {{
    if ($null -eq $old) {{ Remove-ItemProperty $key -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }}
    else {{ Set-ItemProperty $key -Name SystemRestorePointCreationFrequency -Value $old -Type DWord -ErrorAction SilentlyContinue }}
}}", ct)).Trim();

        var after = await GetStatusAsync(ct);
        var created = after.Points.FirstOrDefault(p => p.Sequence > maxSeq);
        if (created != null)
        {
            string m = $"Point de restauration créé : « {created.Description} » ({created.Date:dd/MM/yyyy HH:mm}).";
            log($"[OK] {m}");
            return (true, m);
        }
        string reason = result.StartsWith("ERR ") ? result[4..]
                      : !after.Protected ? "la protection du système est désactivée sur le disque de Windows"
                      : "Windows n'a pas créé le point (service de clichés instantanés indisponible ou espace réservé insuffisant)";
        log($"❌ [ERREUR] Point de restauration non créé : {reason}");
        return (false, $"Point de restauration non créé : {reason}.");
    }
}
