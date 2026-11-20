using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public sealed record PowerPlan(Guid Id, string Name, bool Active);

public enum PowerMode { Unknown, BestEfficiency, Balanced, BestPerformance }

public sealed record PowerState(List<PowerPlan> Plans, bool ModernStandby, PowerMode Mode, bool OnBattery, int SleepAcMin, int SleepDcMin, int ScreenAcMin, int ScreenDcMin);

/// <summary>
/// Alimentation : mode d'alimentation de Windows 11 (curseur Efficacité / Équilibré / Performances, API
/// PowerSetActiveOverlayScheme comme Paramètres), modes de gestion (powercfg) VÉRIFIÉS après activation, plans
/// Haute performance / Performances optimales créés UNE fois sous un GUID fixe (plus de doublon à chaque clic),
/// suppression des doublons, délais de veille et d'écran.
/// </summary>
public static class PowerPlanService
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighTemplate = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    // Copies créées par l'outil (GUID fixes : une seule copie, réutilisée ensuite)
    private static readonly Guid HighCopy = new("a1e7c0de-0001-4a1e-9000-000000000001");
    private static readonly Guid UltimateCopy = new("a1e7c0de-0002-4a1e-9000-000000000002");

    private static readonly Guid OverlayEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid OverlayPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus s);

    private static readonly Regex PlanLine = new(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]+)\)(\s*\*)?", RegexOptions.Compiled);

    public static async Task<PowerState> GetStateAsync(CancellationToken ct = default)
    {
        string list = await CommandRunner.RunAsync("powercfg /list", ct);
        var plans = PlanLine.Matches(list).Select(m => new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success)).ToList();
        string a = await CommandRunner.RunAsync("powercfg /a", ct);
        // « S0 » listé avant la phrase « ne sont pas disponibles » = veille moderne disponible
        int notAvail = a.IndexOf("not available", StringComparison.OrdinalIgnoreCase) is int i1 and >= 0 ? i1 : a.IndexOf("ne sont pas disponibles", StringComparison.OrdinalIgnoreCase);
        int s0 = a.IndexOf("S0", StringComparison.Ordinal);
        bool modern = s0 >= 0 && (notAvail < 0 || s0 < notAvail);
        PowerMode mode = PowerMode.Unknown;
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) == 0)
                mode = g == OverlayEfficiency ? PowerMode.BestEfficiency : g == OverlayPerformance ? PowerMode.BestPerformance : g == Guid.Empty ? PowerMode.Balanced : PowerMode.Unknown;
        }
        catch { }
        bool battery = GetSystemPowerStatus(out var ps) && ps.ACLineStatus == 0;
        int Q(string sub, string setting, bool ac) => ReadMinutes(sub, setting, ac);
        return new PowerState(plans, modern, mode, battery,
            Q("SUB_SLEEP", "STANDBYIDLE", true), Q("SUB_SLEEP", "STANDBYIDLE", false), Q("SUB_VIDEO", "VIDEOIDLE", true), Q("SUB_VIDEO", "VIDEOIDLE", false));
    }

    /// <summary>Délai en minutes (0 = jamais, -1 = inconnu) lu par powercfg /query (valeurs hexadécimales en secondes).</summary>
    private static int ReadMinutes(string sub, string setting, bool ac)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var x in new[] { "/query", "SCHEME_CURRENT", sub, setting }) psi.ArgumentList.Add(x);
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
            var hex = Regex.Matches(o, @"0x([0-9a-fA-F]{8})").Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToList();
            // Les deux dernières valeurs : courant alternatif puis continu (batterie)
            if (hex.Count < 2) return -1;
            int sec = ac ? hex[^2] : hex[^1];
            return sec / 60;
        }
        catch { return -1; }
    }

    /// <summary>Change le mode d'alimentation (curseur de Paramètres) et vérifie.</summary>
    public static bool SetMode(PowerMode mode)
    {
        Guid g = mode switch { PowerMode.BestEfficiency => OverlayEfficiency, PowerMode.BestPerformance => OverlayPerformance, _ => Guid.Empty };
        try { PowerSetActiveOverlayScheme(g); return PowerGetEffectiveOverlayScheme(out var now) == 0 && now == g; }
        catch { return false; }
    }

    /// <summary>Active un mode de gestion et vérifie qu'il est bien actif.</summary>
    public static async Task<bool> ActivatePlanAsync(Guid id, CancellationToken ct = default)
    {
        await CommandRunner.RunAsync($"powercfg /setactive {id}", _ => { }, ct);
        string active = await CommandRunner.RunAsync("powercfg /getactivescheme", ct);
        return active.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Crée (une seule fois, GUID fixe) puis active Haute performance ou Performances optimales.</summary>
    public static async Task<(bool Ok, string Message)> EnablePerformancePlanAsync(bool ultimate, CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct);
        Guid copy = ultimate ? UltimateCopy : HighCopy, template = ultimate ? UltimateTemplate : HighTemplate;
        Guid target = state.Plans.Any(p => p.Id == template) ? template : copy;
        if (!state.Plans.Any(p => p.Id == target))
            await CommandRunner.RunAsync($"powercfg -duplicatescheme {template} {copy}", _ => { }, ct);
        bool ok = await ActivatePlanAsync(target, ct);
        string name = ultimate ? "Performances optimales" : "Haute performance";
        return ok ? (true, $"Mode « {name} » activé." + (state.ModernStandby ? " Sur ce PC (veille moderne), le curseur « Mode d'alimentation » de Paramètres disparaît tant que ce mode est actif." : ""))
                  : (false, $"Windows n'a pas activé « {name} » : ce PC ne le propose pas. Utilisez plutôt le mode d'alimentation « Meilleures performances ».");
    }

    /// <summary>Supprime les modes en double (même nom), en gardant l'actif ou le premier.</summary>
    public static async Task<int> RemoveDuplicatesAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct);
        int removed = 0;
        foreach (var grp in state.Plans.GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1))
        {
            var keep = grp.FirstOrDefault(p => p.Active) ?? grp.First();
            foreach (var p in grp.Where(p => p.Id != keep.Id && !p.Active))
            {
                await CommandRunner.RunAsync($"powercfg /delete {p.Id}", _ => { }, ct);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>Délais de veille / d'écran sur secteur (ac) ou batterie, en minutes (0 = jamais).</summary>
    public static async Task SetTimeoutAsync(bool sleep, bool ac, int minutes, CancellationToken ct = default)
    {
        string what = sleep ? "standby-timeout" : "monitor-timeout";
        await CommandRunner.RunAsync($"powercfg /change {what}-{(ac ? "ac" : "dc")} {Math.Max(0, minutes)}", _ => { }, ct);
    }

    public static string ModeLabel(PowerMode m) => m switch
    {
        PowerMode.BestEfficiency => "Meilleure efficacité énergétique",
        PowerMode.Balanced => "Équilibré",
        PowerMode.BestPerformance => "Meilleures performances",
        _ => "Inconnu"
    };
}
