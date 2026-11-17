using System.IO;
using System.Xml;
using System.Xml.Linq;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>
/// Santé de la batterie (powercfg /batteryreport) et diagnostic énergétique (powercfg /energy) lus en XML
/// pour être affichés en clair au lieu d'ouvrir les rapports HTML bruts.
/// </summary>
public static class BatteryService
{
    public record BatteryHealth(
        bool HasBattery, string Name, string Manufacturer, string Chemistry,
        int DesignCapacity, int FullChargeCapacity, int CycleCount,
        TimeSpan? RuntimeFull, TimeSpan? RuntimeDesign,
        IReadOnlyList<(DateTime Day, int FullCharge)> History)
    {
        public double HealthPercent => DesignCapacity > 0 ? Math.Min(100, 100.0 * FullChargeCapacity / DesignCapacity) : 0;

        public string Verdict => HealthPercent switch
        {
            >= 80 => "Batterie en bon état.",
            >= 60 => "Usure notable : l'autonomie a nettement baissé. Remplacement à prévoir si elle ne suffit plus.",
            > 0 => "Batterie très usée : remplacement conseillé.",
            _ => "Capacité inconnue.",
        };
    }

    public record EnergyIssue(string Severity, string Name, string Description, string Details)
    {
        public int Rank => Severity switch { "Error" => 0, "Warning" => 1, _ => 2 };
        public string SeverityText => Severity switch { "Error" => "Erreur", "Warning" => "Avertissement", _ => "Information" };
    }

    public static async Task<BatteryHealth> GetBatteryHealthAsync(CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"atk_battery_{Guid.NewGuid():N}.xml");
        try
        {
            await CommandRunner.RunAndGetExitCodeAsync($"powercfg /batteryreport /xml /output \"{path}\"", ct);
            if (!File.Exists(path)) return NoBattery();
            return ParseBatteryReport(XDocument.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static BatteryHealth NoBattery() => new(false, "", "", "", 0, 0, 0, null, null, Array.Empty<(DateTime, int)>());

    public static BatteryHealth ParseBatteryReport(XDocument doc)
    {
        XElement? El(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        int Int(XElement? e) => int.TryParse(e?.Value.Trim(), out var v) ? v : 0;

        var root = doc.Root;
        var battery = El(El(root, "Batteries"), "Battery");
        if (battery == null) return NoBattery();

        TimeSpan? Runtime(string capacityNode)
        {
            var node = El(El(El(root, "RuntimeEstimates"), capacityNode), "ActiveRuntime");
            try { return node != null && node.Value.Length > 0 ? XmlConvert.ToTimeSpan(node.Value) : null; } catch { return null; }
        }

        var history = new List<(DateTime, int)>();
        foreach (var h in El(root, "History")?.Elements().Where(e => e.Name.LocalName == "HistoryEntry") ?? Enumerable.Empty<XElement>())
        {
            if (DateTime.TryParse((string?)h.Attribute("LocalStartDate"), out var day) && int.TryParse((string?)h.Attribute("FullChargeCapacity"), out var fc) && fc > 0)
                history.Add((day, fc));
        }

        string chem = El(battery, "Chemistry")?.Value.Trim() ?? "";
        return new BatteryHealth(true,
            El(battery, "Id")?.Value.Trim() ?? "",
            El(battery, "Manufacturer")?.Value.Trim() ?? "",
            chem.ToUpperInvariant() switch { "LION" or "LI-I" => "Lithium-ion", "LIP" or "LIPO" => "Lithium-polymère", "PBAC" => "Plomb", "NIMH" => "Nickel-métal hydrure", _ => chem },
            Int(El(battery, "DesignCapacity")), Int(El(battery, "FullChargeCapacity")), Int(El(battery, "CycleCount")),
            Runtime("FullChargeCapacity"), Runtime("DesignCapacity"), history);
    }

    /// <summary>Diagnostic énergétique Windows (durée en secondes). Nécessite les droits administrateur.</summary>
    public static async Task<List<EnergyIssue>> RunEnergyDiagnosticsAsync(int seconds, CancellationToken ct = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"atk_energy_{Guid.NewGuid():N}.xml");
        try
        {
            await CommandRunner.RunAndGetExitCodeAsync($"powercfg /energy /xml /duration {seconds} /output \"{path}\"", ct);
            if (!File.Exists(path))
                throw new InvalidOperationException("Le diagnostic n'a pas pu être généré (droits administrateur requis, ou autre analyse powercfg en cours).");
            return ParseEnergyReport(XDocument.Load(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>Entrées « LogEntry » (Severity, Name, Description, détails) — schéma http://schemas.microsoft.com/energy/2007.</summary>
    public static List<EnergyIssue> ParseEnergyReport(XDocument doc)
    {
        var list = new List<EnergyIssue>();
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "LogEntry"))
        {
            string Child(string n) => entry.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim() ?? "";
            string severity = Child("Severity");
            string name = Child("Name");
            if (name.Length == 0) continue;

            // Détails : paires Nom / Valeur éventuelles sous l'entrée
            var details = entry.Descendants()
                .Where(e => e.Elements().Any(c => c.Name.LocalName == "Name") && e.Elements().Any(c => c.Name.LocalName == "Value"))
                .Select(e => $"{e.Elements().First(c => c.Name.LocalName == "Name").Value.Trim()} : {e.Elements().First(c => c.Name.LocalName == "Value").Value.Trim()}")
                .Where(s => s.Length > 3)
                .Take(6);
            list.Add(new EnergyIssue(severity, name, Child("Description"), string.Join("\n", details)));
        }
        return list.OrderBy(i => i.Rank).ThenBy(i => i.Name).ToList();
    }
}
