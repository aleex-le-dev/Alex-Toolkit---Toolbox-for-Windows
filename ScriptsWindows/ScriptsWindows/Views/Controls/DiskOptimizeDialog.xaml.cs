using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Optimisation des disques (DiskOptimizeService) : type de support, dernière optimisation, TRIM / défragmentation avec avancement.</summary>
public partial class DiskOptimizeDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private CancellationTokenSource? _runCts;
    private bool _busy;
    private bool? _schedule;

    public DiskOptimizeDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) =>
        {
            if (!_busy) return;
            if (AppMessageBox.Show(this, "Une optimisation est en cours. L'arrêter et fermer ?", "Optimisation en cours", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            else _runCts?.Cancel();
        };
    }

    private async Task LoadAsync()
    {
        TxtStatus.Text = "Lecture des disques…";
        try
        {
            var (vols, schedule) = await DiskOptimizeService.GetStateAsync(_ct);
            _schedule = schedule;
            TxtSchedule.Text = schedule switch { true => "Optimisation automatique de Windows : activée (chaque semaine).", false => "Optimisation automatique de Windows : DÉSACTIVÉE.", _ => "Optimisation automatique : état inconnu." };
            TxtSchedule.Foreground = ThemeManager.GetBrush(schedule == false ? "WarningText" : "TextPrimary");
            BtnSchedule.Content = schedule == false ? "Réactiver" : "Désactiver";
            BtnSchedule.Visibility = schedule == null ? Visibility.Collapsed : Visibility.Visible;

            PanelVolumes.Children.Clear();
            foreach (var v in vols)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                info.Children.Add(new TextBlock { Text = $"{v.Letter}:  {(v.Label.Length > 0 ? v.Label : "Disque local")}  —  {v.MediaLabel} {v.Bus}, {DiskOptimizeService.Size(v.Size)}", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.GetBrush("TextPrimary") });
                int days = v.LastRun is DateTime d ? (int)(DateTime.Now - d).TotalDays : -1;
                info.Children.Add(new TextBlock
                {
                    Text = $"Opération : {v.Action}  ·  " + (days < 0 ? "jamais optimisé (ou journal effacé)" : days == 0 ? "optimisé aujourd'hui" : $"dernière optimisation il y a {days} jour(s)"),
                    FontSize = 11.5, Foreground = ThemeManager.GetBrush(days < 0 || days > 35 ? "WarningText" : "TextSecondary"), TextWrapping = TextWrapping.Wrap
                });
                g.Children.Add(info);
                var b = new Button { Content = "Optimiser", VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_busy };
                b.SetResourceReference(StyleProperty, days < 0 || days > 35 ? "PrimaryActionButton" : "ActionButton");
                b.Click += async (_, _) => await OptimizeAsync(v);
                Grid.SetColumn(b, 1); g.Children.Add(b);
                PanelVolumes.Children.Add(new Border { Child = g, Padding = new Thickness(4, 10, 4, 10), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
            }
            if (!_busy) TxtStatus.Text = $"{vols.Count} volume(s).";
        }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; }
    }

    private void Progress(double pct) => Dispatcher.BeginInvoke(() =>
    {
        TxtPct.Text = $"{pct:0} %";
        BarProgress.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        BarProgress.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
    });

    private async Task OptimizeAsync(OptimizeVolume v)
    {
        if (_busy) return;
        _busy = true;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        PanelProgress.Visibility = Visibility.Visible; BtnCancel.Visibility = Visibility.Visible;
        TxtStep.Text = $"{v.Letter}: — {v.Action}…"; TxtPct.Text = ""; Progress(0);
        _onOutput($"🚀 [LANCEMENT] Optimisation de {v.Letter}: ({v.Action})");
        try
        {
            int code = await DiskOptimizeService.OptimizeAsync(v.Letter, Progress, _ => { }, _runCts.Token);
            TxtStatus.Text = code == 0 ? $"{v.Letter}: optimisé ({v.Action})." : $"Optimisation de {v.Letter}: terminée avec le code {code}.";
            _onOutput(code == 0 ? $"[OK] {v.Letter}: optimisé ({v.Action})." : $"[!] Optimisation de {v.Letter}: code {code}.");
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Optimisation arrêtée (sans risque pour les données)."; _onOutput("🛑 [ANNULÉ] Optimisation"); }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; _onOutput($"❌ [ERREUR] Optimisation de {v.Letter}: {ex.Message}"); }
        finally { _busy = false; PanelProgress.Visibility = Visibility.Collapsed; BtnCancel.Visibility = Visibility.Collapsed; await LoadAsync(); }
    }

    private async void BtnSchedule_Click(object sender, RoutedEventArgs e)
    {
        bool on = _schedule == false;
        bool ok = await DiskOptimizeService.SetScheduleAsync(on, _ct);
        TxtStatus.Text = ok ? (on ? "Optimisation automatique réactivée." : "Optimisation automatique désactivée.") : "Changement refusé par Windows.";
        await LoadAsync();
    }

    private void BtnDfrgui_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("dfrgui.exe");
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
