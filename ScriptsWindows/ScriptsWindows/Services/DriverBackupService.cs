using System.IO;
using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>
/// Pilotes tiers (fabricants, hors Microsoft) : inventaire, sauvegarde sélective ou complète (pnputil)
/// et réinstallation depuis une sauvegarde — typiquement avant / après une réinstallation de Windows.
/// </summary>
public static class DriverBackupService
{
    public record DriverInfo(string Inf, string Devices, string DeviceClass, string Provider, string Version, string Date);

    public static async Task<List<DriverInfo>> GetThirdPartyDriversAsync(CancellationToken ct = default)
    {
        const string script = @"
$list = New-Object System.Collections.Generic.List[object]
Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
  Where-Object { $_.InfName -like 'oem*.inf' } |
  Group-Object InfName | ForEach-Object {
    $f = $_.Group[0]
    $list.Add([pscustomobject]@{
      Inf = [string]$f.InfName
      Devices = (@($_.Group | ForEach-Object { $_.DeviceName } | Where-Object { $_ } | Select-Object -Unique) -join ', ')
      Class = [string]$f.DeviceClass
      Provider = [string]$f.DriverProviderName
      Version = [string]$f.DriverVersion
      Date = $(if ($f.DriverDate) { $f.DriverDate.ToString('dd/MM/yyyy') } else { '' })
    })
  }
ConvertTo-Json -InputObject $list.ToArray() -Compress";

        string json = await PowerShellRunner.RunAsync(script, ct);
        int start = json.IndexOf('[');
        if (start < 0) return new();
        using var doc = JsonDocument.Parse(json[start..]);
        var list = new List<DriverInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            list.Add(new DriverInfo(S("Inf"), S("Devices"), ClassLabel(S("Class")), S("Provider"), S("Version"), S("Date")));
        }
        return list.OrderBy(d => d.DeviceClass).ThenBy(d => d.Devices).ToList();
    }

    /// <summary>Nom lisible de la classe de périphérique.</summary>
    public static string ClassLabel(string cls) => cls.ToUpperInvariant() switch
    {
        "DISPLAY" => "Carte graphique",
        "NET" => "Réseau",
        "MEDIA" => "Son et vidéo",
        "AUDIOPROCESSINGOBJECT" => "Traitement audio",
        "COMPUTEACCELERATOR" => "Accélérateur IA (NPU)",
        "AUDIOENDPOINT" => "Sortie audio",
        "BLUETOOTH" => "Bluetooth",
        "SYSTEM" => "Système",
        "USB" => "USB",
        "HIDCLASS" => "Clavier, souris, tactile",
        "MOUSE" => "Souris",
        "KEYBOARD" => "Clavier",
        "PRINTER" or "PRINTQUEUE" => "Imprimante",
        "CAMERA" or "IMAGE" => "Caméra / scanner",
        "BIOMETRIC" => "Biométrie",
        "SCSIADAPTER" or "HDC" => "Stockage",
        "FIRMWARE" => "Micrologiciel",
        "SOFTWARECOMPONENT" => "Composant logiciel",
        "EXTENSION" => "Extension de pilote",
        "SECURITYDEVICES" => "Sécurité (TPM)",
        "SENSOR" => "Capteurs",
        "PROCESSOR" => "Processeur",
        "BATTERY" => "Batterie",
        "MONITOR" => "Écran",
        "" => "Autre",
        _ => cls,
    };

    /// <summary>
    /// Exporte chaque pilote dans son propre sous-dossier (« Classe - Fabricant - oemN »).
    /// <paramref name="progress"/> reçoit (index, total, pilote). Renvoie (réussis, erreurs).
    /// </summary>
    public static async Task<(int Ok, List<string> Errors)> ExportAsync(IReadOnlyList<DriverInfo> drivers, string targetDir,
        Action<int, int, DriverInfo>? progress, Action<string> onOutput, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var invalid = Path.GetInvalidFileNameChars();
        string Clean(string s) => new string(s.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        int ok = 0;
        var errors = new List<string>();

        for (int i = 0; i < drivers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var d = drivers[i];
            progress?.Invoke(i, drivers.Count, d);
            string folder = Path.Combine(targetDir, Clean($"{d.DeviceClass} - {(d.Provider.Length > 0 ? d.Provider : "Inconnu")} - {Path.GetFileNameWithoutExtension(d.Inf)}"));
            Directory.CreateDirectory(folder);
            int code = await CommandRunner.RunAndGetExitCodeAsync($"pnputil /export-driver {d.Inf} \"{folder}\"", ct);
            if (code == 0 && Directory.EnumerateFileSystemEntries(folder).Any()) { ok++; onOutput($"[OK] Pilote sauvegardé : {d.Devices} ({d.Inf})"); }
            else { errors.Add($"{d.Devices} ({d.Inf}) : code {code}"); onOutput($"[!] Échec de la sauvegarde : {d.Devices} ({d.Inf}), code {code}"); }
        }
        progress?.Invoke(drivers.Count, drivers.Count, drivers.Count > 0 ? drivers[^1] : new DriverInfo("", "", "", "", "", ""));

        File.WriteAllText(Path.Combine(targetDir, "LISEZ-MOI.txt"),
            $"Sauvegarde des pilotes de {Environment.MachineName} — {DateTime.Now:dd/MM/yyyy HH:mm} (Alex Toolkit)\r\n\r\n" +
            "Pour les réinstaller : Alex Toolkit > Sauvegarder les pilotes > « Réinstaller depuis une sauvegarde »,\r\n" +
            "ou en administrateur : pnputil /add-driver \"<ce dossier>\\*.inf\" /subdirs /install\r\n\r\n" +
            string.Join("\r\n", drivers.Select(d => $"{d.DeviceClass} | {d.Devices} | {d.Provider} | {d.Version} | {d.Inf}")),
            new System.Text.UTF8Encoding(true));

        return (ok, errors);
    }

    /// <summary>Installe tous les pilotes (.inf) d'un dossier de sauvegarde et de ses sous-dossiers.</summary>
    public static async Task<(int ExitCode, string Output)> ImportAsync(string sourceDir, Action<string> onOutput, CancellationToken ct = default)
    {
        var lines = new List<string>();
        await CommandRunner.RunAsync($"pnputil /add-driver \"{Path.Combine(sourceDir, "*.inf")}\" /subdirs /install",
            l => { lines.Add(l); onOutput(l); }, ct);
        // pnputil : 0 = succès, 3010 = redémarrage requis ; le résumé final est lisible dans la sortie
        string summary = string.Join("\n", lines.Where(l => l.Trim().Length > 0).TakeLast(4));
        return (0, summary);
    }
}
