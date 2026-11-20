using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>SFC / DISM avec avancement en %, résultats vérifiés (SystemHealthService) et verdict ; état et réinitialisation de Windows Update.</summary>
public partial class SystemIntegrityDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private CancellationTokenSource? _runCts;
    private bool _busy;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _start;

    public SystemIntegrityDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _timer.Tick += (_, _) => TxtElapsed.Text = $"Durée : {DateTime.Now - _start:mm\\:ss} — ne fermez pas cette fenêtre.";
        Loaded += async (_, _) => await RefreshStateAsync();
        Closing += (_, e) =>
        {
            if (!_busy) return;
            if (AppMessageBox.Show(this, "Une réparation est en cours. L'arrêter et fermer ?", "Réparation en cours", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
            else _runCts?.Cancel();
        };
    }

    private async Task RefreshStateAsync()
    {
        try
        {
            var h = await SystemHealthService.GetImageHealthAsync(_ct);
            TxtImageState.Text = SystemHealthService.HealthLabel(h);
            TxtImageState.Foreground = ThemeManager.GetBrush(h switch { ImageHealth.Healthy => "SuccessText", ImageHealth.Unknown => "TextPrimary", _ => "WarningText" });
            string pending = await PowerShellRunner.RunAsync(@"[bool]((Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'))", _ct);
            TxtPending.Text = pending.Trim() == "True"
                ? "Un redémarrage est en attente pour terminer des mises à jour : redémarrez d'abord, la réparation sera plus fiable."
                : "État enregistré par Windows (rapide). « Analyser l'image » refait une vérification complète.";
        }
        catch (Exception ex) { TxtImageState.Text = $"État de l'image illisible : {ex.Message}"; }
    }

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

    private void SetBusy(bool busy, string step = "")
    {
        _busy = busy;
        foreach (var b in new[] { BtnRunPipeline, BtnRunScanHealth, BtnRunRestoreHealth, BtnRunSfc, BtnWuStatus, BtnWuReset }) b.IsEnabled = !busy;
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            PanelProgress.Visibility = Visibility.Visible; PanelVerdict.Visibility = Visibility.Collapsed;
            TxtStep.Text = step; Progress(0); _start = DateTime.Now; _timer.Start();
        }
        else { _timer.Stop(); PanelProgress.Visibility = Visibility.Collapsed; }
    }

    private void ShowVerdict(string title, string detail, string level)
    {
        PanelVerdict.Background = ThemeManager.GetBrush(level + "Bg");
        TxtVerdict.Text = title; TxtVerdict.Foreground = ThemeManager.GetBrush(level + "Text");
        TxtVerdictDetail.Text = detail; TxtVerdictDetail.Foreground = ThemeManager.GetBrush(level + "Text");
        PanelVerdict.Visibility = Visibility.Visible;
    }

    /// <summary>Exécute une opération longue avec avancement, annulation et verdict.</summary>
    private async Task RunAsync(string label, Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        SetBusy(true, label);
        Log($"🚀 [LANCEMENT] {label}");
        try { await work(_runCts.Token); }
        catch (OperationCanceledException) { ShowVerdict("Opération arrêtée.", "Relancez-la plus tard : une analyse interrompue ne laisse pas Windows en mauvais état.", "Warning"); Log("🛑 [ANNULÉ] " + label); }
        catch (Exception ex) { ShowVerdict("Erreur", ex.Message, "Danger"); Log($"❌ [ERREUR] {label} : {ex.Message}"); }
        finally { SetBusy(false); await RefreshStateAsync(); }
    }

    private Task<DismResult> Dism(string mode, string step, CancellationToken ct)
    {
        Dispatcher.Invoke(() => { TxtStep.Text = step; Progress(0); });
        return SystemHealthService.RunDismAsync(mode, Progress, Log, ct);
    }

    private Task<SfcResult> Sfc(string step, CancellationToken ct)
    {
        Dispatcher.Invoke(() => { TxtStep.Text = step; Progress(0); });
        return SystemHealthService.RunSfcAsync(Progress, Log, ct);
    }

    private void ShowSfc(SfcResult s, DismResult? d)
    {
        string level = s.Unrepaired > 0 ? "Danger" : s.Repaired > 0 ? "Warning" : "Success";
        string detail = (d != null ? $"Image : {d.Message}\n" : "") + $"Fichiers : {s.Message}";
        if (s.UnrepairedFiles.Count > 0) detail += "\nNon réparés : " + string.Join(", ", s.UnrepairedFiles.Take(6)) + (s.UnrepairedFiles.Count > 6 ? "…" : "");
        ShowVerdict(level switch { "Success" => "Fichiers système intacts.", "Warning" => "Fichiers système réparés : redémarrez le PC.", _ => "Des fichiers système restent endommagés." }, detail, level);
        Log((s.Unrepaired > 0 ? "[!] " : "[OK] ") + "SFC : " + s.Message);
    }

    private async void BtnRunPipeline_Click(object sender, RoutedEventArgs e) => await RunAsync("Réparation complète (DISM puis SFC)", async ct =>
    {
        var d = await Dism("RestoreHealth", "1/2 — Réparation de l'image Windows (DISM /RestoreHealth)", ct);
        Log((d.ExitCode == 0 ? "[OK] " : "[!] ") + "DISM : " + d.Message);
        var s = await Sfc("2/2 — Vérification des fichiers système (SFC /scannow)", ct);
        ShowSfc(s, d);
    });

    private async void BtnRunScanHealth_Click(object sender, RoutedEventArgs e) => await RunAsync("Analyse de l'image (DISM /ScanHealth)", async ct =>
    {
        var d = await Dism("ScanHealth", "Analyse de l'image Windows (DISM /ScanHealth)", ct);
        ShowVerdict(d.Health == ImageHealth.Healthy ? "Image Windows saine." : d.ExitCode != 0 ? "Analyse impossible." : "Image Windows endommagée.",
                    d.Message + (d.Health == ImageHealth.Repairable ? "\nLancez « Réparation complète »." : ""),
                    d.Health == ImageHealth.Healthy ? "Success" : d.Health == ImageHealth.NonRepairable || d.ExitCode != 0 ? "Danger" : "Warning");
        Log("[i] DISM : " + d.Message);
    });

    private async void BtnRunRestoreHealth_Click(object sender, RoutedEventArgs e) => await RunAsync("Réparation de l'image (DISM /RestoreHealth)", async ct =>
    {
        var d = await Dism("RestoreHealth", "Réparation de l'image Windows (DISM /RestoreHealth)", ct);
        bool ok = d.ExitCode == 0 && d.Health == ImageHealth.Healthy;
        ShowVerdict(ok ? "Image Windows réparée et saine." : "La réparation de l'image n'a pas abouti.", d.Message + (ok ? "\nLancez ensuite « Vérifier les fichiers (SFC) »." : ""), ok ? "Success" : "Danger");
        Log((ok ? "[OK] " : "[!] ") + "DISM : " + d.Message);
    });

    private async void BtnRunSfc_Click(object sender, RoutedEventArgs e) => await RunAsync("Vérification des fichiers (SFC /scannow)", async ct =>
        ShowSfc(await Sfc("Vérification des fichiers système (SFC /scannow)", ct), null));

    private async void BtnWuStatus_Click(object sender, RoutedEventArgs e) => await RunAsync("État de Windows Update", async ct =>
    {
        Dispatcher.Invoke(() => TxtStep.Text = "Lecture de l'état de Windows Update…");
        await RepairService.GetWindowsUpdateStatus(Log, ct);
        Dispatcher.Invoke(() => TxtStatus.Text = "État de Windows Update affiché ci-dessous.");
    });

    private async void BtnWuReset_Click(object sender, RoutedEventArgs e)
    {
        if (AppMessageBox.Show(this,
                "Les services Windows Update vont être arrêtés, puis les caches SoftwareDistribution et catroot2 recréés.\n\n" +
                "L'historique affiché dans Windows Update sera vidé (les mises à jour installées ne sont pas touchées). Continuer ?",
                "Réinitialiser Windows Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync("Réinitialisation de Windows Update", async ct =>
        {
            Dispatcher.Invoke(() => TxtStep.Text = "Réinitialisation des composants Windows Update…");
            await RepairService.ResetWindowsUpdate(Log, ct);
            ShowVerdict("Windows Update réinitialisé.", "Relancez la recherche dans Paramètres > Windows Update.", "Success");
        });
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
