using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Licence Windows : clé gravée dans le BIOS / UEFI, clé installée, clé de secours, édition et état d'activation.</summary>
public static class LicenseService
{
    public record LicenseInfo(
        string Edition, string Version, string Build,
        string? OemKey, string? OemDescription,
        string? InstalledKey, string? BackupKey, string? PartialKey,
        int LicenseStatus, string Channel, string Description)
    {
        public string StatusText => LicenseStatus switch
        {
            1 => "Windows est activé",
            0 => "Windows n'est pas activé",
            2 => "Période de grâce (installation récente)",
            3 => "Période de grâce supplémentaire",
            4 => "Licence non authentique (période de grâce)",
            5 => "Notification : activation requise",
            6 => "Période de grâce étendue",
            _ => "État d'activation inconnu",
        };

        public bool IsActivated => LicenseStatus == 1;

        /// <summary>Type de licence lisible (OEM, boîte, entreprise, numérique…).</summary>
        public string ChannelText
        {
            get
            {
                string c = (Channel + " " + Description).ToUpperInvariant();
                if (c.Contains("VOLUME") || c.Contains("KMS") || c.Contains("MAK")) return "Licence en volume (entreprise)";
                if (c.Contains("OEM")) return "OEM (fournie avec le PC)";
                if (c.Contains("RETAIL")) return "Commerce (boîte ou téléchargement)";
                return Channel.Length > 0 ? Channel : "Inconnu";
            }
        }

        /// <summary>Clé installée générique (licence numérique liée au matériel / compte Microsoft).</summary>
        public bool InstalledKeyIsGeneric => InstalledKey != null && GenericKeys.Contains(InstalledKey);
    }

    // Clés génériques publiques de Microsoft (installation / licence numérique) : ne prouvent aucun droit
    private static readonly HashSet<string> GenericKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "VK7JG-NPHTM-C97JM-9MPGT-3V66T", "YTMG3-N6DKC-DKB77-7M9GH-8HVX7", "BT79Q-G7N6G-PGBYW-4YWX6-6F4BT",
        "YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY", "84NGF-MHBT6-FXBX8-QWJK7-DRR8H", "NPPR9-FWDCX-D2C8J-H872K-2YT43",
        "TX9XD-98N7V-6WMQ6-BX7FG-H8Q99", "3KHY7-WNT83-DGQKR-F7HPR-844BM", "7HNRX-D7KGG-3K4RQ-4WPJ4-YTDFH",
        "PVMJN-6DFY6-9CCP6-7BKTT-D3WVR", "W269N-WFGWX-YVC9B-4J6C9-T83GX", "MH37W-N47XK-V7XM9-C7227-GCQG9",
        "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2", "2WH4N-8QGBV-H22JP-CT43Q-MDWWJ", "DXG7C-N36C4-C4HTG-X4T3X-2YV77",
    };

    public static async Task<LicenseInfo> GetAsync(CancellationToken ct = default)
    {
        const string script = @"
$s = Get-CimInstance SoftwareLicensingService -ErrorAction SilentlyContinue
$p = Get-CimInstance SoftwareLicensingProduct -Filter ""ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL"" -ErrorAction SilentlyContinue | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
$bk = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform' -ErrorAction SilentlyContinue).BackupProductKeyDefault
[pscustomobject]@{
  Oem = [string]$s.OA3xOriginalProductKey
  OemDesc = [string]$s.OA3xOriginalProductKeyDescription
  Backup = [string]$bk
  Partial = [string]$p.PartialProductKey
  Status = $(if ($p) { [int]$p.LicenseStatus } else { -1 })
  Channel = [string]$p.ProductKeyChannel
  Desc = [string]$p.Description
  Edition = [string]$os.Caption
  Version = [string]$cv.DisplayVersion
  Build = ""$($cv.CurrentBuild).$($cv.UBR)""
} | ConvertTo-Json -Compress";

        string json = await PowerShellRunner.RunAsync(script, ct);
        int start = json.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Lecture de la licence impossible.");
        using var doc = JsonDocument.Parse(json[start..]);
        var r = doc.RootElement;
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string? N(string n) => S(n) is { Length: > 0 } x ? x.Trim() : null;
        int status = r.TryGetProperty("Status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : -1;

        return new LicenseInfo(
            Edition: S("Edition").Replace("Microsoft ", ""), Version: S("Version"), Build: S("Build"),
            OemKey: N("Oem"), OemDescription: N("OemDesc"),
            InstalledKey: DecodeInstalledKey(), BackupKey: N("Backup"), PartialKey: N("Partial"),
            LicenseStatus: status, Channel: S("Channel"), Description: S("Desc"));
    }

    /// <summary>Clé installée, décodée depuis DigitalProductId (algorithme Windows 8 et suivants).</summary>
    public static string? DecodeInstalledKey()
    {
        try
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                      .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (hk?.GetValue("DigitalProductId") is not byte[] id || id.Length < 67) return null;
            return DecodeProductKey(id);
        }
        catch { return null; }
    }

    public static string DecodeProductKey(byte[] digitalProductId)
    {
        const string digits = "BCDFGHJKMPQRTVWXY2346789";
        const int offset = 52;
        var id = (byte[])digitalProductId.Clone();
        int isWin8 = (id[66] / 6) & 1;
        id[66] = (byte)((id[66] & 0xF7) | ((isWin8 & 2) * 4));

        var key = new StringBuilder();
        int last = 0;
        for (int i = 24; i >= 0; i--)
        {
            int current = 0;
            for (int j = 14; j >= 0; j--)
            {
                current = current * 256 + id[j + offset];
                id[j + offset] = (byte)(current / 24);
                current %= 24;
            }
            key.Insert(0, digits[current]);
            last = current;
        }

        string k = key.ToString();
        if (isWin8 == 1)
        {
            string part1 = k.Substring(1, last);
            k = part1 + "N" + k[(last + 1)..];
        }
        return string.Join("-", Enumerable.Range(0, 5).Select(g => k.Substring(g * 5, 5)));
    }

    public static void ExportText(LicenseInfo info, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("===========================================================");
        sb.AppendLine($" LICENCE WINDOWS — {Environment.MachineName}");
        sb.AppendLine($" Exporté le {DateTime.Now:dd/MM/yyyy HH:mm} avec Alex Toolkit");
        sb.AppendLine("===========================================================");
        sb.AppendLine();
        sb.AppendLine($"Édition            : {info.Edition} {info.Version} (build {info.Build})");
        sb.AppendLine($"Activation         : {info.StatusText}");
        sb.AppendLine($"Type de licence    : {info.ChannelText}");
        sb.AppendLine($"Clé BIOS / UEFI    : {info.OemKey ?? "aucune (pas de clé gravée dans la carte mère)"}");
        sb.AppendLine($"Clé installée      : {info.InstalledKey ?? "illisible"}{(info.InstalledKeyIsGeneric ? "  (clé générique : licence numérique)" : "")}");
        if (info.BackupKey != null) sb.AppendLine($"Clé de secours     : {info.BackupKey}");
        if (info.PartialKey != null) sb.AppendLine($"Fin de clé active  : •••••-•••••-•••••-•••••-{info.PartialKey}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
