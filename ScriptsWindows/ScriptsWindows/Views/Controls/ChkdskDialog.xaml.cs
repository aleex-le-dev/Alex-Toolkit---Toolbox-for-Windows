using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>CHKDSK modernisé : état réel des disques, analyse sans redémarrage, réparation au bon moment, verdict (DiskCheckService).</summary>
public partial class ChkdskDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private CancellationTokenSource? _runCts;
    private bool _busy;

    public ChkdskDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadDrivesAsync();
        Closing += (_, e) =>
        {
            if (!_busy) return;
            if (AppMessageBox.Show(this, "Une vérification est en cours. L'arrêter et fermer ?", "Vérification en cours", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
            else _runCts?.Cancel();
        };
    }

    private CheckVolume? Selected => CmbDrives.SelectedItem as CheckVolume;

    private async Task LoadDrivesAsync()
    {
        char? previous = Selected?.Letter;
        TxtFeedback.Text = "Lecture des disques…";
        try
        {
            var vols = await DiskCheckService.GetVolumesAsync(_ct);
            CmbDrives.ItemsSource = vols;
            CmbDrives.SelectedItem = vols.FirstOrDefault(v => v.Letter == previous) ?? vols.FirstOrDefault(v => v.Dirty) ?? vols.FirstOrDefault();
            TxtFeedback.Text = $"{vols.Count} disque(s). " + (vols.Any(v => v.Dirty) ? "Au moins un disque est marqué comme endommagé." : "");
            TxtLast.Text = await DiskCheckService.LastResultAsync(_ct);
        }
        catch (Exception ex) { TxtFeedback.Text = $"Erreur : {ex.Message}"; }
    }

    private async void CmbDrives_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not CheckVolume v) return;
        TxtDriveDirtyStatus.Text = $"{v.Letter}: — {DiskCheckService.HealthLabel(v)}";
        TxtDriveDirtyStatus.Foreground = ThemeManager.GetBrush(v.Dirty || v.Health == "Unhealthy" ? "DangerText" : v.Health == "Warning" ? "WarningText" : "SuccessText");
        if (await DiskCheckService.IsScheduledAsync(v.Letter, _ct))
            TxtDriveDirtyStatus.Text += "  ·  Vérification programmée au prochain démarrage.";
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) { if (!_busy) await LoadDrivesAsync(); }

    private void Log(string line)
    {
        _onOutput(line);
        Dispatcher.BeginInvoke(() => { TxtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}"); TxtLog.ScrollToEnd(); });
    }

    private void Progress(double pct) => Dispatcher.BeginInvoke(() =>
    {
        TxtPct.Text = $"{pct:0} %";
        BarProgress.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        BarProgress.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
    });

    private void Verdict(string text, string level)
    {
        PanelVerdict.Background = ThemeManager.GetBrush(level + "Bg");
        TxtVerdict.Text = text; TxtVerdict.Foreground = ThemeManager.GetBrush(level + "Text");
        PanelVerdict.Visibility = Visibility.Visible;
    }

    private async Task RunAsync(string label, Func<CheckVolume, CancellationToken, Task> work)
    {
        if (_busy || Selected is not CheckVolume v) return;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        _busy = true;
        foreach (var b in new[] { BtnScan, BtnFix, BtnFull, BtnCancelSchedule, BtnRefresh }) b.IsEnabled = false;
        CmbDrives.IsEnabled = false;
        BtnCancel.Visibility = Visibility.Visible;
        PanelProgress.Visibility = Visibility.Visible; PanelVerdict.Visibility = Visibility.Collapsed;
        TxtStep.Text = $"{label} de {v.Letter}:…"; TxtPct.Text = ""; Progress(0);
        Log($"🚀 [LANCEMENT] {label} de {v.Letter}:");
        try { await work(v, _runCts.Token); }
        catch (OperationCanceledException) { Verdict("Opération arrêtée.", "Warning"); Log("🛑 [ANNULÉ] " + label); }
        catch (Exception ex) { Verdict($"Erreur : {ex.Message}", "Danger"); Log($"❌ [ERREUR] {label} : {ex.Message}"); }
        finally
        {
            _busy = false;
            foreach (var b in new[] { BtnScan, BtnFix, BtnFull, BtnCancelSchedule, BtnRefresh }) b.IsEnabled = true;
            CmbDrives.IsEnabled = true;
            BtnCancel.Visibility = Visibility.Collapsed; PanelProgress.Visibility = Visibility.Collapsed;
            await LoadDrivesAsync();
        }
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e) => await RunAsync("Analyse", async (v, ct) =>
    {
        var (verdict, msg) = await DiskCheckService.ScanAsync(v, Progress, Log, ct);
        Verdict($"{v.Letter}: — {msg}", verdict == ScanVerdict.NoErrors ? "Success" : "Warning");
        Log((verdict == ScanVerdict.NoErrors ? "[OK] " : "[!] ") + $"{v.Letter}: {msg}");
    });

    private async void BtnFix_Click(object sender, RoutedEventArgs e) => await Repair(full: false);
    private async void BtnFull_Click(object sender, RoutedEventArgs e) => await Repair(full: true);

    private async Task Repair(bool full)
    {
        if (Selected is not CheckVolume v) return;
        string what = full ? "La réparation complète lit toute la surface du disque : de 30 minutes à plusieurs heures." : "La réparation prend en général quelques minutes.";
        string when = v.IsSystem ? "C'est le disque de Windows : elle aura lieu au PROCHAIN REDÉMARRAGE (écran noir de vérification, ne pas éteindre)."
                                 : $"Le disque {v.Letter}: sera brièvement indisponible : fermez les fichiers ouverts dessus.";
        if (AppMessageBox.Show(this, $"{what}\n\n{when}\n\nContinuer ?", full ? "Réparation complète" : "Réparer les erreurs",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(full ? "Réparation complète" : "Réparation", async (vol, ct) =>
        {
            string msg = await DiskCheckService.RepairAsync(vol, full, Progress, Log, ct);
            Verdict($"{vol.Letter}: — {msg}", msg.Contains("restantes") || msg.Contains("n'a pas") ? "Danger" : vol.IsSystem ? "Warning" : "Success");
            Log($"[i] {vol.Letter}: {msg}");
        });
    }

    private async void BtnCancelSchedule_Click(object sender, RoutedEventArgs e) => await RunAsync("Annulation de la vérification programmée", async (v, ct) =>
    {
        await DiskCheckService.CancelScheduledAsync(v.Letter, ct);
        bool still = await DiskCheckService.IsScheduledAsync(v.Letter, ct);
        Verdict(still ? $"{v.Letter}: est marqué comme endommagé : Windows le vérifiera quand même tant qu'il n'est pas réparé."
                      : $"{v.Letter}: ne sera plus vérifié au prochain démarrage.", still ? "Warning" : "Success");
        Log($"[i] Vérification programmée de {v.Letter}: annulée.");
    });

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
