using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Microsoft Defender : état, mise à jour des signatures, analyse rapide / complète avec chronomètre, menaces.</summary>
public partial class DefenderScanDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private CancellationTokenSource? _actionCts;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _watch = new();
    private string _actionLabel = "";

    public DefenderScanDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _timer.Tick += (_, _) => TxtActionTitle.Text = $"{_actionLabel} — {_watch.Elapsed:mm\\:ss}";
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) =>
        {
            if (_actionCts == null) return;
            if (AppMessageBox.Show(this, "Une opération est en cours. Fermer la fenêtre ?\n\nL'analyse peut se poursuivre en arrière-plan dans Defender.",
                    "Analyse en cours", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            else _actionCts.Cancel();
        };
    }

    private async Task RefreshAsync()
    {
        TxtStatus.Text = "Lecture de l'état de Microsoft Defender…";
        var statusTask = DefenderService.GetStatusAsync(_ct);
        var threatsTask = DefenderService.GetThreatsAsync(_ct);
        try { await Task.WhenAll(statusTask, threatsTask); }
        catch (OperationCanceledException) { return; }
        catch { /* chaque tâche est traitée ci-dessous */ }

        if (statusTask.IsCompletedSuccessfully) ShowStatus(statusTask.Result);
        else ShowNotice($"État de Defender illisible : {statusTask.Exception?.InnerException?.Message}");

        ShowThreats(threatsTask.IsCompletedSuccessfully ? threatsTask.Result : new());
        TxtStatus.Text = "";
    }

    private void ShowStatus(DefenderService.DefenderStatus s)
    {
        bool usable = s.Available;
        BtnUpdate.IsEnabled = BtnQuick.IsEnabled = BtnFull.IsEnabled = usable;

        TxtRealtime.Text = !usable ? "Indisponible" : s.RealTimeEnabled ? "Activée" : "Désactivée";
        TxtRealtime.Foreground = ThemeManager.GetBrush(!usable ? "TextMuted" : s.RealTimeEnabled ? "SuccessText" : "DangerText");

        if (s.SignaturesUpdated is DateTime sig)
        {
            int days = (int)(DateTime.Now - sig).TotalDays;
            TxtSignatures.Text = days <= 0 ? "À jour (aujourd'hui)" : days == 1 ? "Hier" : $"Il y a {days} jours";
            TxtSignatures.Foreground = ThemeManager.GetBrush(days <= 1 ? "SuccessText" : days <= 7 ? "WarningText" : "DangerText");
            TxtSigVersion.Text = $"Version {s.SignatureVersion} — {sig:dd/MM/yyyy HH:mm}";
        }
        else { TxtSignatures.Text = "Inconnues"; TxtSignatures.Foreground = ThemeManager.GetBrush("TextMuted"); }

        TxtLastScan.Text = s.LastQuickScan is DateTime q ? FormatAgo(q) : "Jamais";
        TxtLastScan.Foreground = ThemeManager.GetBrush(s.LastQuickScan is DateTime q2 && (DateTime.Now - q2).TotalDays <= 7 ? "TextPrimary" : "WarningText");
        TxtLastFull.Text = s.LastFullScan is DateTime f ? $"Analyse complète : {f:dd/MM/yyyy}" : "Aucune analyse complète";

        bool healthy = usable && s.RealTimeEnabled;
        HeaderBadge.Background = ThemeManager.GetBrush(healthy ? "SuccessBg" : "WarningBg");
        HeaderGlyph.Foreground = ThemeManager.GetBrush(healthy ? "SuccessText" : "WarningText");

        if (s.OtherAntivirus.Count > 0)
            ShowNotice($"Autre antivirus installé : {string.Join(", ", s.OtherAntivirus)}. Defender est alors en retrait : utilisez de préférence l'analyse de cet antivirus.");
        else if (s.Error != null) ShowNotice(s.Error);
        else if (usable && !s.RealTimeEnabled) ShowNotice("La protection en temps réel est désactivée : réactivez-la dans Sécurité Windows > Protection contre les virus et menaces.");
    }

    private void ShowNotice(string text)
    {
        NoticeBox.Visibility = Visibility.Visible;
        TxtNotice.Text = text;
    }

    private static string FormatAgo(DateTime d)
    {
        var span = DateTime.Now - d;
        if (span.TotalMinutes < 60) return "Il y a quelques minutes";
        if (span.TotalHours < 24) return $"Il y a {(int)span.TotalHours} h";
        if (span.TotalDays < 2) return "Hier";
        return $"Il y a {(int)span.TotalDays} jours";
    }

    private void ShowThreats(List<DefenderService.Threat> threats)
    {
        var rows = threats.Select(t => new ThreatRow(t)).ToList();
        ListThreats.ItemsSource = rows;
        PanelNoThreat.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtNoThreat.Text = "Aucune menace dans l'historique de Defender.";
        NoThreatBadge.Visibility = Visibility.Visible;
        BtnRemove.Visibility = threats.Any(t => t.Active) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Actions ─────────────────────────────────────────────────────────

    private async Task RunActionAsync(string label, string detail, Func<CancellationToken, Task<string?>> action, string okMessage)
    {
        if (_actionCts != null) return;
        _actionCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        _actionLabel = label;
        BtnUpdate.IsEnabled = BtnQuick.IsEnabled = BtnFull.IsEnabled = false;
        BtnRemove.IsEnabled = false;
        ProgAction.Visibility = Visibility.Visible;
        TxtActionTitle.Text = label;
        TxtActionTitle.Foreground = ThemeManager.GetBrush("TextPrimary");
        TxtActionDetail.Text = detail;
        _watch.Restart();
        _timer.Start();
        _onOutput($"[i] Defender : {label.ToLowerInvariant()}…");

        string? error;
        try { error = await action(_actionCts.Token); }
        catch (OperationCanceledException) { error = "Opération interrompue."; }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            _timer.Stop();
            _watch.Stop();
            ProgAction.Visibility = Visibility.Collapsed;
            _actionCts.Dispose();
            _actionCts = null;
            BtnRemove.IsEnabled = true;
        }

        string duration = _watch.Elapsed.TotalMinutes >= 1 ? $"{(int)_watch.Elapsed.TotalMinutes} min {_watch.Elapsed.Seconds:00} s" : $"{_watch.Elapsed.Seconds} s";
        TxtActionTitle.Text = error == null ? $"{okMessage} ({duration})" : $"{label} : échec";
        TxtActionDetail.Text = error ?? "";
        TxtActionTitle.Foreground = ThemeManager.GetBrush(error == null ? "SuccessText" : "DangerText");
        _onOutput(error == null ? $"[OK] Defender : {okMessage} ({duration})." : $"[!] Defender : {label} — {error}");
        await RefreshAsync();
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        => await RunActionAsync("Mise à jour des signatures", "Téléchargement des dernières définitions de virus…",
            ct => DefenderService.UpdateSignaturesAsync(_onOutput, ct), "Signatures à jour");

    private async void BtnQuick_Click(object sender, RoutedEventArgs e)
        => await RunActionAsync("Analyse rapide", "Mémoire, démarrage automatique et dossiers sensibles. Vous pouvez continuer à utiliser le PC.",
            ct => DefenderService.ScanAsync(false, ct), "Analyse rapide terminée");

    private async void BtnFull_Click(object sender, RoutedEventArgs e)
    {
        if (AppMessageBox.Show(this, "L'analyse complète examine tous les fichiers du PC et peut durer plus d'une heure.\n\nLe PC reste utilisable mais sera plus lent pendant l'analyse. Continuer ?",
                "Analyse complète", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunActionAsync("Analyse complète", "Tous les fichiers de tous les disques sont analysés. Laissez le PC allumé et branché.",
            ct => DefenderService.ScanAsync(true, ct), "Analyse complète terminée");
    }

    private async void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (AppMessageBox.Show(this, "Appliquer l'action recommandée par Defender (quarantaine ou suppression) à toutes les menaces encore actives ?",
                "Neutraliser les menaces", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunActionAsync("Neutralisation des menaces", "Mise en quarantaine / suppression des éléments détectés…",
            ct => DefenderService.RemoveActiveThreatsAsync(ct), "Menaces neutralisées");
    }

    private void BtnAvTest_Click(object sender, RoutedEventArgs e)
        => new AntivirusTestDialog(_onOutput, _ct) { Owner = this }.ShowDialog();

    private void BtnWindowsSecurity_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("windowsdefender://threat") { UseShellExecute = true }); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class ThreatRow
    {
        private readonly DefenderService.Threat _t;
        public ThreatRow(DefenderService.Threat t) => _t = t;
        public string Name => _t.Name;
        public string SeverityText => $"Gravité {_t.SeverityText.ToLowerInvariant()}";
        public string Resources => _t.Resources.Length > 0 ? _t.Resources : "Emplacement non communiqué";
        public string StatusText => _t.StatusText;
        public string DateText => _t.Detected is DateTime d ? d.ToString("dd/MM/yyyy HH:mm") : "";
        public string Glyph => _t.Active ? "" : "";
        public Brush Bg => ThemeManager.GetBrush(_t.Active || _t.IsSevere ? "DangerBg" : "WarningBg");
        public Brush Fg => ThemeManager.GetBrush(_t.Active || _t.IsSevere ? "DangerText" : "WarningText");
        public Brush StatusFg => ThemeManager.GetBrush(_t.Active ? "DangerText" : "SuccessText");
    }
}
