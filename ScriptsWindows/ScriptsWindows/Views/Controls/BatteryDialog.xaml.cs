using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Santé de la batterie (capacité, cycles, évolution) et diagnostic énergétique powercfg lu en clair.</summary>
public partial class BatteryDialog : Window
{
    private const int DiagSeconds = 60;
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<BatteryService.EnergyIssue> _issues = new();
    private CancellationTokenSource? _diagCts;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _watch = new();

    public BatteryDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _timer.Tick += (_, _) =>
        {
            double s = Math.Min(DiagSeconds, _watch.Elapsed.TotalSeconds);
            ProgDiag.Value = s;
            TxtDiagButton.Text = s < DiagSeconds ? $"Observation… {DiagSeconds - (int)s} s" : "Analyse du rapport…";
        };
        Loaded += async (_, _) => await LoadBatteryAsync();
        Closing += (_, _) => _diagCts?.Cancel();
    }

    private async Task LoadBatteryAsync()
    {
        try
        {
            var b = await BatteryService.GetBatteryHealthAsync(_ct);
            if (!b.HasBattery)
            {
                TxtHealth.Text = "—";
                TxtVerdict.Text = "Aucune batterie détectée (PC fixe ou batterie déconnectée). Le diagnostic énergétique reste disponible ci-dessous.";
                GridDetails.Visibility = Visibility.Collapsed;
                _onOutput("[i] Batterie : aucune batterie détectée.");
                return;
            }

            double health = b.HealthPercent;
            string tone = health >= 80 ? "Success" : health >= 60 ? "Warning" : "Danger";
            TxtHealth.Text = $"{health:0} %";
            TxtHealth.Foreground = ThemeManager.GetBrush(tone + "Text");
            HealthBar.Background = ThemeManager.GetBrush(tone);
            HealthBar.Width = 0;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (HealthBar.Parent is FrameworkElement track) HealthBar.Width = track.ActualWidth * health / 100;
            }, DispatcherPriority.Loaded);
            TxtVerdict.Text = b.Verdict;

            TxtDesign.Text = $"{b.DesignCapacity / 1000.0:0.#} Wh";
            TxtFull.Text = $"{b.FullChargeCapacity / 1000.0:0.#} Wh (−{100 - health:0} %)";
            TxtCycles.Text = b.CycleCount > 0 ? $"{b.CycleCount:N0}" : "Non communiqué";
            TxtRuntime.Text = b.RuntimeFull is TimeSpan rt
                ? $"{(int)rt.TotalHours} h {rt.Minutes:00}" + (b.RuntimeDesign is TimeSpan rd ? $"  (neuve : {(int)rd.TotalHours} h {rd.Minutes:00})" : "")
                : "Pas encore estimée";
            TxtModel.Text = string.Join(" • ", new[] { b.Name, b.Manufacturer, b.Chemistry }.Where(s => s.Length > 0));

            if (b.History.Count >= 2)
            {
                var points = b.History.TakeLast(40).ToList();
                // Échelle resserrée (de 90 % de la plus faible valeur à la capacité d'origine) : l'usure reste visible
                double max = Math.Max(b.DesignCapacity, points.Max(h => h.FullCharge));
                double floor = Math.Min(points.Min(h => h.FullCharge) * 0.9, max * 0.95);
                ListHistory.ItemsSource = points.Select(p => new
                {
                    BarHeight = 8 + 78.0 * Math.Clamp((p.FullCharge - floor) / (max - floor), 0, 1),
                    Tip = $"{p.Day:dd/MM/yyyy} : {p.FullCharge / 1000.0:0.#} Wh ({100.0 * p.FullCharge / b.DesignCapacity:0} %)",
                }).ToList();
                TxtHistoryRange.Text = $"{points[0].Day:dd/MM/yyyy} → {points[^1].Day:dd/MM/yyyy} (survolez une barre)";
                CardHistory.Visibility = Visibility.Visible;
            }
            _onOutput($"[OK] Batterie : santé {health:0} % ({b.FullChargeCapacity} / {b.DesignCapacity} mWh), {b.CycleCount} cycle(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtHealth.Text = "—";
            TxtVerdict.Text = $"Rapport de batterie illisible : {ex.Message}";
        }
    }

    private async void BtnDiag_Click(object sender, RoutedEventArgs e)
    {
        if (_diagCts != null) return;
        _diagCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        BtnDiag.IsEnabled = false;
        ProgDiag.Visibility = Visibility.Visible;
        ProgDiag.Value = 0;
        PanelSummary.Visibility = Visibility.Collapsed;
        ListIssues.ItemsSource = null;
        TxtStatus.Text = "Diagnostic en cours : évitez d'utiliser le PC pendant une minute.";
        _watch.Restart();
        _timer.Start();
        _onOutput("[i] Diagnostic énergétique Windows (60 s)…");

        try
        {
            _issues = await BatteryService.RunEnergyDiagnosticsAsync(DiagSeconds, _diagCts.Token);
            int err = _issues.Count(i => i.Rank == 0), warn = _issues.Count(i => i.Rank == 1), info = _issues.Count(i => i.Rank == 2);
            TxtErrCount.Text = $"{err} erreur(s)";
            TxtWarnCount.Text = $"{warn} avertissement(s)";
            TxtInfoCount.Text = $"Afficher aussi les {info} information(s)";
            ChkShowInfo.IsChecked = err + warn == 0;
            PanelSummary.Visibility = Visibility.Visible;
            ShowIssues();
            TxtStatus.Text = err + warn == 0 ? "Aucun problème de consommation détecté." : $"{err + warn} point(s) à examiner, les plus importants en premier.";
            _onOutput($"[OK] Diagnostic énergétique : {err} erreur(s), {warn} avertissement(s), {info} information(s).");
            foreach (var i in _issues.Where(i => i.Rank < 2)) _onOutput($"   [{i.SeverityText}] {i.Name}");
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Diagnostic interrompu."; }
        catch (Exception ex)
        {
            TxtStatus.Text = "Diagnostic impossible.";
            AppMessageBox.Show(this, ex.Message, "Diagnostic énergétique", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _timer.Stop();
            _watch.Stop();
            ProgDiag.Visibility = Visibility.Collapsed;
            TxtDiagButton.Text = "Relancer le diagnostic (60 s)";
            BtnDiag.IsEnabled = true;
            _diagCts?.Dispose();
            _diagCts = null;
        }
    }

    private void ChkShowInfo_Click(object sender, RoutedEventArgs e) => ShowIssues();

    private void ShowIssues()
    {
        bool withInfo = ChkShowInfo.IsChecked == true;
        ListIssues.ItemsSource = _issues.Where(i => withInfo || i.Rank < 2).Select(i => new IssueRow(i)).ToList();
    }

    private void BtnPowerOptions_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class IssueRow
    {
        private readonly BatteryService.EnergyIssue _i;
        public IssueRow(BatteryService.EnergyIssue i) => _i = i;
        public string Name => _i.Name;
        public string Description => _i.Description;
        public string Details => _i.Details;
        public Visibility DetailsVisibility => _i.Details.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string Glyph => _i.Rank switch { 0 => "", 1 => "", _ => "" };
        public Brush Bg => ThemeManager.GetBrush(_i.Rank switch { 0 => "DangerBg", 1 => "WarningBg", _ => "AccentDim" });
        public Brush Fg => ThemeManager.GetBrush(_i.Rank switch { 0 => "DangerText", 1 => "WarningText", _ => "AccentText" });
    }
}
