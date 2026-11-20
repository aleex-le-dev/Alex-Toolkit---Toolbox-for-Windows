using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Alimentation (PowerPlanService) : mode d'alimentation Windows 11, modes de gestion vérifiés, doublons, veille et écran.</summary>
public partial class PowerPlanDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private PowerState? _state;
    private bool _busy;

    public PowerPlanDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadPlansAsync();
    }

    private static string Min(int m) => m < 0 ? "?" : m == 0 ? "jamais" : m >= 60 && m % 60 == 0 ? $"{m / 60} h" : $"{m} min";

    private async Task LoadPlansAsync()
    {
        try
        {
            var s = _state = await PowerPlanService.GetStateAsync(_ct);
            TxtSubtitle.Text = (s.ModernStandby ? "PC à veille moderne : réglez surtout le mode d'alimentation. " : "") + (s.OnBattery ? "Sur batterie actuellement." : "Sur secteur actuellement.");

            foreach (var (btn, mode) in new[] { (BtnModeEff, PowerMode.BestEfficiency), (BtnModeBal, PowerMode.Balanced), (BtnModePerf, PowerMode.BestPerformance) })
                btn.SetResourceReference(StyleProperty, s.Mode == mode ? "PrimaryActionButton" : "ActionButton");
            bool customPlan = s.Plans.FirstOrDefault(p => p.Active)?.Id != PowerPlanService.Balanced;
            TxtMode.Text = s.Mode == PowerMode.Unknown
                ? (customPlan ? "Indisponible tant qu'un mode de gestion autre que « Utilisation normale » est actif." : "Non communiqué par ce PC.")
                : $"Actuel : {PowerPlanService.ModeLabel(s.Mode)}.";
            TxtMode.Foreground = ThemeManager.GetBrush(s.Mode == PowerMode.Unknown ? "TextMuted" : "AccentText");

            PanelPlans.Children.Clear();
            foreach (var p in s.Plans)
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = p.Name, FontSize = 13, FontWeight = p.Active ? FontWeights.SemiBold : FontWeights.Normal,
                                               Foreground = ThemeManager.GetBrush(p.Active ? "SuccessText" : "TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
                if (p.Active)
                {
                    var t = new TextBlock { Text = "Actif", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.GetBrush("SuccessText"), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(t, 1); g.Children.Add(t);
                }
                else
                {
                    var b = new Button { Content = "Activer", Tag = p.Id };
                    b.SetResourceReference(StyleProperty, "ActionButton");
                    b.Click += async (_, _) => await RunAsync($"Activation de « {p.Name} »", async () =>
                    {
                        bool ok = await PowerPlanService.ActivatePlanAsync(p.Id, _ct);
                        Report(ok, ok ? $"Mode « {p.Name} » activé." : $"Windows n'a pas activé « {p.Name} ».");
                    });
                    Grid.SetColumn(b, 1); g.Children.Add(b);
                }
                PanelPlans.Children.Add(g);
            }
            int dup = s.Plans.GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Sum(g => g.Count() - 1);
            BtnDuplicates.Visibility = dup > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnDuplicates.Content = $"Supprimer les {dup} doublon(s)";
            TxtPlansInfo.Text = s.Plans.Count <= 1 ? "Ce PC ne propose qu'un mode (normal sur les PC récents) : vous pouvez en ajouter un plus rapide, surtout utile sur un PC fixe."
                              : dup > 0 ? $"{dup} mode(s) en double (souvent créés par d'anciens outils d'optimisation)." : "Le mode actif s'applique immédiatement.";
            TxtTimeouts.Text = $"Veille : {Min(s.SleepAcMin)} sur secteur, {Min(s.SleepDcMin)} sur batterie  ·  Écran éteint : {Min(s.ScreenAcMin)} sur secteur, {Min(s.ScreenDcMin)} sur batterie";
            if (!_busy) TxtStatus.Text = "Réglages lus.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; }
    }

    private void Report(bool ok, string msg)
    {
        TxtStatus.Text = msg;
        _onOutput((ok ? "[OK] " : "❌ [ERREUR] ") + msg);
    }

    private async Task RunAsync(string label, Func<Task> work)
    {
        if (_busy) return;
        _busy = true; TxtStatus.Text = label + "…";
        try { await work(); }
        catch (Exception ex) { Report(false, $"{label} : {ex.Message}"); }
        finally { _busy = false; await LoadPlansAsync(); }
    }

    private async void BtnMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = ((string)((Button)sender).Tag) switch { "eff" => PowerMode.BestEfficiency, "perf" => PowerMode.BestPerformance, _ => PowerMode.Balanced };
        await RunAsync("Changement du mode d'alimentation", async () =>
        {
            if (_state != null && _state.Plans.FirstOrDefault(p => p.Active)?.Id != PowerPlanService.Balanced)
                await PowerPlanService.ActivatePlanAsync(PowerPlanService.Balanced, _ct);   // le curseur n'agit qu'avec le mode normal
            bool ok = PowerPlanService.SetMode(mode);
            Report(ok, ok ? $"Mode d'alimentation : {PowerPlanService.ModeLabel(mode)}." : "Ce PC n'a pas accepté le changement de mode d'alimentation.");
        });
    }

    private async void BtnSetHigh_Click(object sender, RoutedEventArgs e) => await RunAsync("Haute performance", async () =>
    {
        var (ok, msg) = await PowerPlanService.EnablePerformancePlanAsync(false, _ct); Report(ok, msg);
    });

    private async void BtnSetUltimate_Click(object sender, RoutedEventArgs e) => await RunAsync("Performances optimales", async () =>
    {
        var (ok, msg) = await PowerPlanService.EnablePerformancePlanAsync(true, _ct); Report(ok, msg);
    });

    private async void BtnDuplicates_Click(object sender, RoutedEventArgs e) => await RunAsync("Suppression des doublons", async () =>
    {
        int n = await PowerPlanService.RemoveDuplicatesAsync(_ct); Report(true, $"{n} mode(s) en double supprimé(s).");
    });

    private async void BtnNoSleep_Click(object sender, RoutedEventArgs e) => await RunAsync("Veille sur secteur", async () =>
    {
        await PowerPlanService.SetTimeoutAsync(true, true, 0, _ct); Report(true, "Le PC ne se mettra plus en veille sur secteur (pensez à remettre un délai).");
    });

    private async void BtnSleep30_Click(object sender, RoutedEventArgs e) => await RunAsync("Veille sur secteur", async () =>
    {
        await PowerPlanService.SetTimeoutAsync(true, true, 30, _ct); Report(true, "Veille après 30 min sur secteur.");
    });

    private async void BtnScreen10_Click(object sender, RoutedEventArgs e) => await RunAsync("Écran", async () =>
    {
        await PowerPlanService.SetTimeoutAsync(false, true, 10, _ct); await PowerPlanService.SetTimeoutAsync(false, false, 5, _ct);
        Report(true, "Écran éteint après 10 min sur secteur et 5 min sur batterie.");
    });

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("explorer.exe", "ms-settings:powersleep");
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
