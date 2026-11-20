using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Bilan de sécurité (SecurityAuditService) : protections corrigeables, incidents expliqués, mémoire par programme.</summary>
public partial class SecurityCenterDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private bool _eventsLoaded, _ramLoaded, _busy;

    public SecurityCenterDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadDefenderStatusAsync();
    }

    private static Brush B(string k) => ThemeManager.GetBrush(k);
    private static TextBlock T(string text, double size, string brush, FontWeight? w = null, bool wrap = false) => new()
    { Text = text, FontSize = size, Foreground = B(brush), FontWeight = w ?? FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis };

    private async void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string tag = btn.Tag?.ToString() ?? "";
        foreach (var b in new[] { BtnTabDefender, BtnTabEvents, BtnTabRam })
            b.SetResourceReference(StyleProperty, b == btn ? "PrimaryActionButton" : "ActionButton");
        PanelDefender.Visibility = tag == "TabDefender" ? Visibility.Visible : Visibility.Collapsed;
        PanelEvents.Visibility = tag == "TabEvents" ? Visibility.Visible : Visibility.Collapsed;
        PanelRam.Visibility = tag == "TabRam" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "TabEvents" && !_eventsLoaded) await LoadCriticalEventsAsync();
        else if (tag == "TabRam" && !_ramLoaded) await LoadRamProcessesAsync();
    }

    // ─── Protection ──────────────────────────────────────────────────────────────

    private async Task LoadDefenderStatusAsync()
    {
        TxtFeedback.Text = "Vérification des protections…";
        try
        {
            var checks = await SecurityAuditService.AuditAsync(_ct);
            var important = checks.Where(c => c.Important && c.Ok != null).ToList();
            int ok = important.Count(c => c.Ok == true), bad = important.Count - ok;
            TxtScore.Text = bad == 0 ? $"Bien protégé : {ok}/{important.Count} protections actives." : $"{bad} point(s) à corriger sur {important.Count}.";
            TxtScore.Foreground = B(bad == 0 ? "SuccessText" : bad <= 2 ? "WarningText" : "DangerText");
            TxtScoreDetail.Text = bad == 0 ? "Antivirus, pare-feu et protections de Windows sont actifs." : "Cliquez sur « Corriger » à côté de chaque point en rouge.";
            TxtScoreDetail.Foreground = B("TextSecondary");
            PanelChecks.Children.Clear();
            foreach (var c in checks.OrderBy(c => c.Ok == false ? 0 : c.Ok == null ? 2 : 1)) PanelChecks.Children.Add(BuildCheck(c));
            TxtFeedback.Text = $"Bilan fait à {DateTime.Now:HH:mm}.";
            _onOutput(bad == 0 ? $"[OK] Bilan de sécurité : {ok}/{important.Count} protections actives." : $"[!] Bilan de sécurité : {bad} point(s) à corriger.");
        }
        catch (Exception ex) { TxtScore.Text = "Bilan impossible."; TxtFeedback.Text = ex.Message; }
    }

    private Border BuildCheck(SecurityCheck c)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var (glyph, brush) = c.Ok switch { true => ("", "SuccessText"), false => ("", c.Important ? "DangerText" : "WarningText"), _ => ("", "TextMuted") };
        g.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("FontIcon"), FontSize = 16, Foreground = B(brush), Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top });
        var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        info.Children.Add(T(c.Title, 13, "TextPrimary", FontWeights.SemiBold));
        info.Children.Add(T(c.Detail, 11.5, c.Ok == false ? brush : "TextSecondary", wrap: true));
        Grid.SetColumn(info, 1); g.Children.Add(info);
        if (c.Ok == false && c.FixLabel.Length > 0)
        {
            var btn = new Button { Content = c.FixLabel, VerticalAlignment = VerticalAlignment.Center, MinWidth = 96 };
            btn.SetResourceReference(StyleProperty, "PrimaryActionButton");
            btn.Click += async (_, _) => await FixAsync(c, btn);
            Grid.SetColumn(btn, 2); g.Children.Add(btn);
        }
        return new Border { Child = g, Padding = new Thickness(6, 10, 6, 10), BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private async Task FixAsync(SecurityCheck c, Button btn)
    {
        switch (c.Id)
        {
            case "tamper" or "smartscreen" or "third": CommandRunner.StartProcess("windowsdefender://threatsettings"); return;
            case "scan": BtnQuickScan_Click(this, new RoutedEventArgs()); return;
            case "wu": new SystemIntegrityDialog(_onOutput, _ct) { Owner = this }.ShowDialog(); await LoadDefenderStatusAsync(); return;
        }
        if (_busy) return;
        _busy = true; btn.IsEnabled = false;
        TxtFeedback.Text = $"Correction : {c.Title}…";
        try
        {
            string msg = await SecurityAuditService.FixAsync(c.Id, _ct);
            _onOutput(msg.Length == 0 || msg.Contains("redémarrez") ? $"[OK] {c.Title} : corrigé. {msg}" : $"❌ [ERREUR] {c.Title} : {msg}");
            await LoadDefenderStatusAsync();
            if (msg.Length > 0) TxtFeedback.Text = msg;
        }
        finally { _busy = false; }
    }

    private async void BtnRefreshDefender_Click(object sender, RoutedEventArgs e) => await LoadDefenderStatusAsync();

    /// <summary>Analyses, signatures et menaces : outil dédié (évite le doublon et les sorties en vrac dans le journal).</summary>
    private async void BtnQuickScan_Click(object sender, RoutedEventArgs e)
    {
        new DefenderScanDialog(_onOutput, _ct) { Owner = this }.ShowDialog();
        await LoadDefenderStatusAsync();
    }

    private void BtnEicar_Click(object sender, RoutedEventArgs e) => new AntivirusTestDialog(_onOutput, _ct) { Owner = this }.ShowDialog();
    private void BtnOpenWindowsSecurity_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("windowsdefender:");

    // ─── Incidents ───────────────────────────────────────────────────────────────

    private async Task LoadCriticalEventsAsync()
    {
        TxtEventsCount.Text = "Lecture des journaux de Windows…";
        PanelIncidents.Children.Clear();
        try
        {
            var groups = await SecurityAuditService.GetIncidentsAsync(_ct);
            _eventsLoaded = true;
            int serious = groups.Where(g => g.Level == "Danger").Sum(g => g.Count);
            TxtEventsCount.Text = groups.Count == 0 ? "Aucune erreur enregistrée ces 7 derniers jours."
                : serious > 0 ? $"{serious} incident(s) grave(s) et {groups.Sum(g => g.Count) - serious} autre(s) erreur(s) en 7 jours."
                : $"Aucun incident grave ; {groups.Sum(g => g.Count)} erreur(s) mineure(s) en 7 jours.";
            TxtEventsCount.Foreground = B(serious > 0 ? "DangerText" : "SuccessText");
            foreach (var g in groups)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                info.Children.Add(T(g.Title, 13, g.Level switch { "Danger" => "DangerText", "Warning" => "WarningText", _ => "TextPrimary" }, FontWeights.SemiBold));
                info.Children.Add(T(g.Explanation, 11.5, "TextSecondary", wrap: true));
                var sample = T(g.Sample, 11, "TextMuted"); sample.ToolTip = g.Sample; info.Children.Add(sample);
                grid.Children.Add(info);
                var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
                right.Children.Add(T($"{g.Count} fois", 13, "TextPrimary", FontWeights.SemiBold));
                right.Children.Add(T($"dernier : {g.Last:dd/MM HH:mm}", 11, "TextMuted"));
                Grid.SetColumn(right, 1); grid.Children.Add(right);
                PanelIncidents.Children.Add(new Border { Child = grid, Padding = new Thickness(6, 10, 6, 10), BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
            }
            _onOutput($"[OK] Incidents des 7 derniers jours : {groups.Sum(g => g.Count)} ({serious} grave(s)).");
        }
        catch (Exception ex) { TxtEventsCount.Text = "Lecture des journaux impossible."; TxtFeedback.Text = ex.Message; }
    }

    private async void BtnRefreshEvents_Click(object sender, RoutedEventArgs e) => await LoadCriticalEventsAsync();
    private void BtnPerfmon_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("perfmon", "/rel");
    private void BtnEventvwr_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("eventvwr.msc");

    // ─── Mémoire ─────────────────────────────────────────────────────────────────

    private async Task LoadRamProcessesAsync()
    {
        TxtRamTotalUsage.Text = "Mesure de la mémoire…";
        PanelMemory.Children.Clear();
        try
        {
            var (used, total, top) = await SecurityAuditService.GetMemoryAsync(_ct);
            _ramLoaded = true;
            double pct = total > 0 ? used * 100 / total : 0;
            TxtRamTotalUsage.Text = $"{used:0.0} Go utilisés sur {total:0.0} Go ({pct:0} %)" + (pct > 85 ? " : mémoire presque pleine, le PC ralentit." : "");
            BarRam.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
            BarRam.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
            BarRamFill.Background = B(pct > 85 ? "Danger" : pct > 70 ? "Warning" : "Accent");
            double max = top.Count > 0 ? top.Max(t => t.MegaBytes) : 1;
            foreach (var m in top)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                g.Children.Add(T(m.Processes > 1 ? $"{m.Name}  ({m.Processes} processus)" : m.Name, 12.5, "TextPrimary", FontWeights.SemiBold));
                var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(m.MegaBytes, GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, max - m.MegaBytes), GridUnitType.Star) });
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = B("Accent") });
                Grid.SetColumn(bar, 1); g.Children.Add(bar);
                var v = T(m.MegaBytes >= 1024 ? $"{m.MegaBytes / 1024:0.0} Go" : $"{m.MegaBytes:0} Mo", 12.5, "TextPrimary");
                v.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(v, 2); g.Children.Add(v);
                PanelMemory.Children.Add(g);
            }
            _onOutput($"[OK] Mémoire : {used:0.0} Go / {total:0.0} Go ({pct:0} %).");
        }
        catch (Exception ex) { TxtRamTotalUsage.Text = "Mesure impossible."; TxtFeedback.Text = ex.Message; }
    }

    private async void BtnRefreshRam_Click(object sender, RoutedEventArgs e) => await LoadRamProcessesAsync();
    private void BtnTaskMgr_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("taskmgr");
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
