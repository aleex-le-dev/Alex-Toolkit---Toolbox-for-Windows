using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Grand nettoyage : catalogue d'éléments (CleanupCatalog) recommandés / avancés, taille récupérable de chacun,
/// nettoyage avec avancement et espace réellement libéré par élément. Les anciennes versions des pilotes en font
/// partie (bouton « Détail » vers la fenêtre dédiée).
/// </summary>
public partial class SystemCleanupDialog : Window
{
    private sealed class Row
    {
        public required CleanupItem Item { get; init; }
        public bool Selected { get; set; }
        /// <summary>Taille mesurée : null = en cours, -1 inconnue, -2 sans espace disque.</summary>
        public long? Size { get; set; }
        public long? Freed { get; set; }
        public string State { get; set; } = "";
    }

    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private readonly List<Row> _rows;
    private CancellationTokenSource? _runCts;
    private bool _busy;
    private int _scanId;

    public SystemCleanupDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _rows = CleanupCatalog.GetItems().Select(i => new Row { Item = i, Selected = i.DefaultChecked }).ToList();
        Render();
        Loaded += async (_, _) => await ScanAsync();
        Closing += (_, e) =>
        {
            if (!_busy) return;
            if (AppMessageBox.Show(this, "Un nettoyage est en cours. L'arrêter et fermer ?", "Nettoyage en cours",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            else _runCts?.Cancel();
        };
    }

    // ─── Analyse ─────────────────────────────────────────────────────────────────

    /// <summary>Mesure chaque élément en parallèle ; chaque ligne s'affiche dès que sa taille est connue.</summary>
    private async Task ScanAsync()
    {
        int id = ++_scanId;
        foreach (var r in _rows) { r.Size = null; r.Freed = null; r.State = ""; }
        Render();
        TxtStatus.Text = "Analyse de l'espace récupérable…";
        await Task.WhenAll(_rows.Select(async r =>
        {
            long size;
            try { size = await r.Item.Measure(_ct); }
            catch (OperationCanceledException) { return; }
            catch { size = -1; }
            if (id != _scanId) return;
            r.Size = size;
            Render();
        }));
        if (id == _scanId && !_busy) TxtStatus.Text = "Analyse terminée. Cochez ce que vous voulez nettoyer.";
    }

    private async void BtnAnalyze_Click(object sender, RoutedEventArgs e) { if (!_busy) await ScanAsync(); }

    // ─── Affichage ───────────────────────────────────────────────────────────────

    private static Brush B(string key) => ThemeManager.GetBrush(key);

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text, FontSize = size, Foreground = B(brush), FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
    };

    private static string SizeLabel(Row r) => r.Size switch
    {
        null => "Calcul…",
        -2 => "Pas d'espace disque",
        -1 => "Variable",
        0 => "Rien à nettoyer",
        long s => CleanupCatalog.FormatSize(s)
    };

    private void Render()
    {
        PanelItems.Children.Clear();
        foreach (var level in new[] { CleanupLevel.Recommended, CleanupLevel.Advanced })
        {
            var title = Text(level == CleanupLevel.Recommended ? "RECOMMANDÉS — sans risque" : "AVANCÉS — à cocher en connaissance de cause",
                             10.5, level == CleanupLevel.Recommended ? "TextMuted" : "WarningText", FontWeights.SemiBold);
            title.Margin = new Thickness(4, level == CleanupLevel.Recommended ? 6 : 18, 0, 6);
            PanelItems.Children.Add(title);
            foreach (var r in _rows.Where(r => r.Item.Level == level)) PanelItems.Children.Add(BuildRow(r));
        }

        var sel = _rows.Where(r => r.Selected).ToList();
        long known = sel.Where(r => r.Size > 0).Sum(r => r.Size!.Value);
        bool pending = _rows.Any(r => r.Size == null);
        bool variable = sel.Any(r => r.Size == -1);
        long all = _rows.Where(r => r.Size > 0).Sum(r => r.Size!.Value);
        if (!_busy)
        {
            TxtTotal.Text = pending && all == 0 ? "Analyse de l'espace récupérable…"
                          : $"{CleanupCatalog.FormatSize(known)}{(variable ? " et plus" : "")} à libérer avec la sélection";
            TxtTotalDetail.Text = $"{sel.Count} élément(s) coché(s) sur {_rows.Count}  ·  {CleanupCatalog.FormatSize(all)} récupérables au total" + (pending ? "  ·  analyse en cours…" : "");
            TxtCleanLabel.Text = sel.Count == 0 ? "Nettoyer la sélection" : $"Nettoyer la sélection ({sel.Count})";
        }
    }

    private Border BuildRow(Row r)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

        // Case dessinée (toujours visible, cochée ou non)
        var box = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(r.Selected ? 0 : 1.5), BorderBrush = B("TextMuted"), Background = r.Selected ? B("Accent") : Brushes.Transparent,
            Child = r.Selected ? new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("FontIcon"), FontSize = 12, Foreground = B("OnAccent"),
                                                 HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null
        };
        g.Children.Add(box);

        var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        info.Children.Add(Text(r.Item.Title, 13, "TextPrimary", FontWeights.SemiBold));
        info.Children.Add(Text(r.Item.Description, 11.5, "TextSecondary", wrap: true));
        if (r.Item.Warning.Length > 0)
        {
            var w = Text(r.Item.Warning, 11.5, "WarningText", wrap: true);
            w.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(w);
        }
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        if (r.Item.Id == "drivers")
        {
            var detail = new Button { Content = "Détail…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                      ToolTip = "Voir chaque ancienne version et choisir lesquelles supprimer", IsEnabled = !_busy };
            detail.SetResourceReference(StyleProperty, "ActionButton");
            detail.Click += (_, e) =>
            {
                e.Handled = true;
                new DriverStoreCleanupDialog(_onOutput, _ct) { Owner = this }.ShowDialog();
                _ = ScanAsync();
            };
            Grid.SetColumn(detail, 2);
            g.Children.Add(detail);
        }

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var size = Text(SizeLabel(r), 13, r.Size > 0 ? "TextPrimary" : "TextMuted", r.Size > 0 ? FontWeights.SemiBold : FontWeights.Normal);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(size);
        if (r.Freed is long f)
        {
            var freed = Text(f > 0 ? $"Libéré : {CleanupCatalog.FormatSize(f)}" : r.Size == -2 ? "Fait" : "Rien de libéré", 11.5, f > 0 || r.Size == -2 ? "SuccessText" : "TextMuted", FontWeights.SemiBold);
            freed.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(freed);
        }
        else if (r.State.Length > 0)
        {
            var st = Text(r.State, 11.5, r.State.StartsWith("Erreur") ? "DangerText" : "AccentText", FontWeights.SemiBold);
            st.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(st);
        }
        Grid.SetColumn(right, 3);
        g.Children.Add(right);

        var border = new Border
        {
            Child = g, Padding = new Thickness(10, 10, 10, 10), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 4),
            Background = r.Selected ? B("AccentDim") : Brushes.Transparent, Cursor = _busy ? Cursors.Arrow : Cursors.Hand,
            ToolTip = _busy ? null : (r.Selected ? "Cliquer pour décocher" : "Cliquer pour cocher")
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (_busy) return;
            r.Selected = !r.Selected;
            Render();
        };
        return border;
    }

    private void SetBar(double pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        BarProgress.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        BarProgress.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    // ─── Sélection rapide ────────────────────────────────────────────────────────

    private void BtnRecommended_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = r.Item.DefaultChecked; Render(); }
    private void BtnAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = true; Render(); }
    private void BtnNone_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = false; Render(); }

    // ─── Nettoyage ───────────────────────────────────────────────────────────────

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        var sel = _rows.Where(r => r.Selected).ToList();
        if (sel.Count == 0) { TxtStatus.Text = "Cochez au moins un élément."; return; }
        var advanced = sel.Where(r => r.Item.Level == CleanupLevel.Advanced).ToList();
        if (advanced.Count > 0 && AppMessageBox.Show(this,
                "Éléments avancés cochés :\n\n" + string.Join("\n", advanced.Select(a => $"• {a.Item.Title} : {a.Item.Warning}")) + "\n\nContinuer ?",
                "Nettoyage avancé", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        SetBusy(true);
        long total = 0;
        _onOutput($"\n🚀 [LANCEMENT] Grand nettoyage : {string.Join(", ", sel.Select(s => s.Item.Title))}");
        try
        {
            for (int i = 0; i < sel.Count; i++)
            {
                var r = sel[i];
                _runCts.Token.ThrowIfCancellationRequested();
                TxtStep.Text = $"{i + 1}/{sel.Count} : {r.Item.Title}…";
                SetBar(i * 100.0 / sel.Count);
                r.State = "En cours…"; Render();
                try
                {
                    long freed = await r.Item.Clean(_onOutput, _runCts.Token);
                    r.Freed = freed; r.State = "";
                    total += Math.Max(0, freed);
                    TxtFreed.Text = $"{CleanupCatalog.FormatSize(total)} libérés";
                    _onOutput(r.Size == -2 ? $"[OK] {r.Item.Title} : vidé." : $"[OK] {r.Item.Title} : {CleanupCatalog.FormatSize(freed)} libérés.");
                }
                catch (OperationCanceledException) { r.State = "Arrêté"; throw; }
                catch (Exception ex)
                {
                    r.State = "Erreur";
                    _onOutput($"❌ [ERREUR] {r.Item.Title} : {ex.Message}");
                }
                Render();
            }
            SetBar(100);
            TxtStep.Text = "Nettoyage terminé.";
            TxtStatus.Text = $"Terminé : {CleanupCatalog.FormatSize(total)} libérés.";
            _onOutput($"✅ [TERMINÉ] Grand nettoyage : {CleanupCatalog.FormatSize(total)} libérés au total.");
        }
        catch (OperationCanceledException)
        {
            TxtStep.Text = "Nettoyage arrêté.";
            TxtStatus.Text = $"Arrêté : {CleanupCatalog.FormatSize(total)} libérés avant l'arrêt.";
            _onOutput($"🛑 [ANNULÉ] Grand nettoyage arrêté ({CleanupCatalog.FormatSize(total)} libérés).");
        }
        finally
        {
            SetBusy(false);
            // Nouvelle mesure, en gardant l'espace libéré affiché sur chaque ligne
            var freedById = _rows.ToDictionary(r => r.Item.Id, r => r.Freed);
            await ScanAsync();
            foreach (var r in _rows) r.Freed = freedById[r.Item.Id];
            Render();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnClean.IsEnabled = BtnRecommended.IsEnabled = BtnAll.IsEnabled = BtnNone.IsEnabled = BtnAnalyze.IsEnabled = !busy;
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) { PanelProgress.Visibility = Visibility.Visible; TxtFreed.Text = ""; SetBar(0); }
        Render();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    private void BtnCleanmgr_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true }); }
        catch (Exception ex) { TxtStatus.Text = $"Impossible de lancer le nettoyage de disque : {ex.Message}"; }
    }

    private void BtnStorageSense_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
        catch (Exception ex) { TxtStatus.Text = $"Impossible d'ouvrir les paramètres : {ex.Message}"; }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
