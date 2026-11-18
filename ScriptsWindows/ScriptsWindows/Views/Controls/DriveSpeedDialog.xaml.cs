using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Testeur de vitesse des clés USB et disques : mesure sans cache, verdict, temps de copie estimés,
/// et comparatif de tous les supports déjà testés (gardés en mémoire, renommables).
/// </summary>
public partial class DriveSpeedDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private CancellationTokenSource? _runCts;
    private bool _busy;
    private List<DriveSpeedResult> _history = new();
    private string? _currentKey;

    private readonly object _progressLock = new();
    private DateTime _lastProgress = DateTime.MinValue;

    /// <summary>Les 5 mesures : titre, usage, valeur, format.</summary>
    private static readonly (string Title, string Usage, Func<DriveSpeedResult, double> Value, Func<double, string> Format)[] Metrics =
    {
        ("Écriture séquentielle", "copier de gros fichiers : vidéos, ISO", r => r.SeqWriteMBs, DriveSpeedService.FormatSpeed),
        ("Lecture séquentielle", "relire ou restaurer de gros fichiers", r => r.SeqReadMBs, DriveSpeedService.FormatSpeed),
        ("Écriture aléatoire 4 Ko", "petites modifications, logiciels", r => r.RandWriteIops, v => $"{v:N0} op/s"),
        ("Lecture aléatoire 4 Ko", "lancer un logiciel depuis le support", r => r.RandReadIops, v => $"{v:N0} op/s"),
        ("Petits fichiers (16 Ko)", "documents, photos : sauvegarde d'un profil", r => r.SmallFilesPerSec, v => $"{v:N0} fichiers/s"),
    };

    public DriveSpeedDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        CmbDrive.SelectionChanged += (_, _) => ShowLastResultOfSelection();
        Loaded += async (_, _) =>
        {
            _history = DriveSpeedService.LoadHistory();
            RenderCompare();
            var last = _history.OrderByDescending(h => h.Date).FirstOrDefault();
            if (last != null) ShowResult(last);
            await RefreshDrivesAsync();
        };
        Closing += (_, e) =>
        {
            if (!_busy) return;
            if (AppMessageBox.Show(this, "Un test est en cours. L'arrêter et fermer ?\n\nLes fichiers de test seront supprimés du support.",
                    "Test en cours", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            else _runCts?.Cancel();
        };
    }

    // ─── Lecteurs ────────────────────────────────────────────────────────────────

    private async Task RefreshDrivesAsync()
    {
        TxtStatus.Text = "Recherche des clés et disques…";
        BtnRefresh.IsEnabled = false;
        try
        {
            var previous = (CmbDrive.SelectedItem as DriveTarget)?.Letter;
            var drives = await DriveSpeedService.GetDrivesAsync(_ct);
            CmbDrive.ItemsSource = drives;
            CmbDrive.SelectedItem = drives.FirstOrDefault(d => d.Letter == previous) ?? drives.FirstOrDefault();
            TxtStatus.Text = drives.Count == 0
                ? "Aucun lecteur trouvé : branchez une clé USB ou un disque puis cliquez sur Actualiser."
                : $"{drives.Count} lecteur(s) trouvé(s). Choisissez celui à tester puis lancez le test.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; }
        finally { BtnRefresh.IsEnabled = !_busy; }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshDrivesAsync();

    /// <summary>Un support déjà testé est sélectionné : on réaffiche son dernier résultat.</summary>
    private void ShowLastResultOfSelection()
    {
        if (_busy || CmbDrive.SelectedItem is not DriveTarget d) return;
        var last = _history.Where(h => h.DriveKey == d.Key).OrderByDescending(h => h.Date).FirstOrDefault();
        if (last != null) ShowResult(last);
    }

    // ─── Test ────────────────────────────────────────────────────────────────────

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDrive.SelectedItem is not DriveTarget drive) { TxtStatus.Text = "Choisissez d'abord une clé ou un disque."; return; }
        int sizeMB = int.Parse((string)((ComboBoxItem)CmbSize.SelectedItem).Tag);

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        SetBusy(true);
        TxtStep.Text = "Préparation…"; TxtLive.Text = ""; SetBar(BarProgress, 0);
        TxtStatus.Text = $"Test de {drive.Letter}: {drive.Name}…" + (drive.IsSystem ? " (disque de Windows : le fichier de test est supprimé à la fin)" : "");
        _onOutput($"\n🚀 [LANCEMENT] Test de vitesse de {drive.Letter}: {drive.Name} ({sizeMB} Mo)");
        try
        {
            var result = await DriveSpeedService.RunAsync(drive, sizeMB, OnProgress, _runCts.Token);
            _history = DriveSpeedService.AddToHistory(result);
            var saved = _history.Where(h => h.DriveKey == result.DriveKey).OrderByDescending(h => h.Date).First();
            ShowResult(saved);
            RenderCompare();
            var v = DriveSpeedService.Verdict(saved);
            string summary = $"écriture {DriveSpeedService.FormatSpeed(saved.SeqWriteMBs)}, lecture {DriveSpeedService.FormatSpeed(saved.SeqReadMBs)}, " +
                             $"{saved.SmallFilesPerSec:0} petits fichiers/s ({v.Title})";
            TxtStatus.Text = $"Test terminé : {summary}.";
            _onOutput($"[OK] Vitesse de {saved.Letter} {saved.Shown} : {summary}");
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Test arrêté. Les fichiers de test ont été supprimés du support.";
            _onOutput("🛑 [ANNULÉ] Test de vitesse arrêté.");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            _onOutput($"❌ [ERREUR] Test de vitesse de {drive.Letter}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            await RefreshDrivesAsync();   // espace libre à jour
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnRun.IsEnabled = CmbDrive.IsEnabled = CmbSize.IsEnabled = BtnRefresh.IsEnabled = !busy;
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PanelProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Avancement reçu du thread de mesure : au plus 10 mises à jour par seconde (un SSD rapporte des milliers de blocs/s).</summary>
    private void OnProgress(DriveSpeedStep s)
    {
        lock (_progressLock)
        {
            var now = DateTime.UtcNow;
            bool stepChanged = TxtStepTag != s.Index;
            if (!stepChanged && (now - _lastProgress).TotalMilliseconds < 100) return;
            _lastProgress = now;
            TxtStepTag = s.Index;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (!_busy) return;
            TxtStep.Text = $"Étape {s.Index}/{s.Count} : {s.Title}";
            TxtLive.Text = s.Live;
            SetBar(BarProgress, ((s.Index - 1) + Math.Clamp(s.Fraction, 0, 1)) / s.Count * 100);
        });
    }
    private int TxtStepTag = -1;

    // ─── Affichage ───────────────────────────────────────────────────────────────

    private static Brush B(string key) => ThemeManager.GetBrush(key);

    private static void SetBar(Grid bar, double pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        bar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    /// <summary>Barre horizontale dessinée (colonnes en étoile), aux couleurs du thème.</summary>
    private static Border MakeBar(double ratio, string brushKey, double height)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var fill = new Border { CornerRadius = new CornerRadius(height / 2), Background = B(brushKey) };
        grid.Children.Add(fill);
        SetBar(grid, ratio * 100);
        return new Border
        {
            Height = height, CornerRadius = new CornerRadius(height / 2), Background = B("BgInput"),
            ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Child = grid
        };
    }

    private static TextBlock Text(string text, double size = 12, string brush = "TextPrimary", FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = B(brush), FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Résultat détaillé d'un test (barres relatives au meilleur support de l'historique).</summary>
    private void ShowResult(DriveSpeedResult r)
    {
        _currentKey = r.DriveKey;
        PanelResult.Visibility = Visibility.Visible;
        TxtResultName.Text = $"{r.Letter}  {r.Shown}";
        TxtResultInfo.Text = $"{r.Bus} · {DriveSpeedService.FormatSize(r.Size)} · {r.FileSystem} · testé le {r.Date:dd/MM/yyyy à HH:mm} ({r.TestSizeMB} Mo)"
                             + (r.Partial ? " · mesure limitée à 25 s (support lent)" : "");

        PanelMetrics.Children.Clear();
        var best = _history.Count > 0 ? _history : new List<DriveSpeedResult> { r };
        foreach (var m in Metrics)
        {
            double value = m.Value(r);
            double max = Math.Max(value, best.Max(h => m.Value(h)));
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            var label = new StackPanel();
            label.Children.Add(Text(m.Title, 12.5, "TextPrimary", FontWeights.SemiBold));
            label.Children.Add(Text(m.Usage, 11, "TextMuted"));
            row.Children.Add(label);
            var bar = MakeBar(max > 0 ? value / max : 0, "Accent", 8);
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var val = Text(m.Format(value), 14, "TextPrimary", FontWeights.SemiBold);
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 2);
            row.Children.Add(val);
            PanelMetrics.Children.Add(row);
        }

        var v = DriveSpeedService.Verdict(r);
        TxtVerdict.Text = v.Title;
        TxtVerdict.Foreground = B(v.BrushKey);
        TxtVerdictDetail.Text = v.Detail;
        PanelEstimates.Children.Clear();
        foreach (var (label, duration) in DriveSpeedService.Estimates(r))
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var d = Text(duration, 12, "TextPrimary", FontWeights.SemiBold);
            DockPanel.SetDock(d, Dock.Right);
            line.Children.Add(d);
            line.Children.Add(Text(label, 12, "TextSecondary"));
            PanelEstimates.Children.Add(line);
        }
        RenderCompare();
    }

    /// <summary>Tableau comparatif : dernier test de chaque support, renommable et supprimable.</summary>
    private void RenderCompare()
    {
        PanelCompare.Children.Clear();
        var rows = DriveSpeedService.Compare(_history);
        TxtNoHistory.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtCompareCount.Text = rows.Count == 0 ? "" : $"{rows.Count} support(s) en mémoire";
        if (rows.Count == 0) return;

        Grid NewRow()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 200 });
            foreach (var _ in Metrics) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            return g;
        }

        // En-tête
        var head = NewRow();
        head.Margin = new Thickness(10, 0, 10, 6);
        head.Children.Add(Text("Support", 11, "TextMuted", FontWeights.SemiBold));
        string[] heads = { "Écriture", "Lecture", "Écr. 4 Ko", "Lect. 4 Ko", "Petits fichiers" };
        for (int i = 0; i < heads.Length; i++)
        {
            var t = Text(heads[i], 11, "TextMuted", FontWeights.SemiBold);
            t.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(t, i + 1);
            head.Children.Add(t);
        }
        PanelCompare.Children.Add(head);

        var maxima = Metrics.Select(m => rows.Max(x => m.Value(x.Last))).ToArray();
        foreach (var (last, count) in rows)
        {
            bool current = last.DriveKey == _currentKey;
            var g = NewRow();

            // Nom (renommable sur place) + détails
            var nameCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameText = Text(last.Shown, 13, "TextPrimary", FontWeights.SemiBold);
            var nameBox = new TextBox { Text = last.Shown, Visibility = Visibility.Collapsed, FontSize = 12.5, Margin = new Thickness(0, 0, 8, 0) };
            nameBox.SetResourceReference(StyleProperty, "FieldTextBox");
            nameCell.Children.Add(nameText);
            nameCell.Children.Add(nameBox);
            string detail = $"{last.Letter} · {last.Bus} · {DriveSpeedService.FormatSize(last.Size)} · {count} test{(count > 1 ? "s" : "")} · {last.Date:dd/MM/yy}";
            if (!string.IsNullOrWhiteSpace(last.Nickname)) detail = $"{last.DriveName} · " + detail;
            nameCell.Children.Add(Text(detail, 11, "TextMuted"));
            g.Children.Add(nameCell);

            for (int i = 0; i < Metrics.Length; i++)
            {
                double value = Metrics[i].Value(last);
                bool isBest = rows.Count > 1 && value > 0 && value >= maxima[i];
                var cell = new StackPanel { Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(Text(Metrics[i].Format(value), 12.5, isBest ? "SuccessText" : "TextPrimary", isBest ? FontWeights.SemiBold : FontWeights.Normal));
                var bar = MakeBar(maxima[i] > 0 ? value / maxima[i] : 0, isBest ? "Success" : "Accent", 4);
                bar.Margin = new Thickness(0, 4, 0, 0);
                cell.Children.Add(bar);
                Grid.SetColumn(cell, i + 1);
                g.Children.Add(cell);
            }

            // Actions : renommer, oublier
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Button Icon(string glyph, string tip)
            {
                var b = new Button { ToolTip = tip, Margin = new Thickness(2, 0, 0, 0), Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("FontIcon"), FontSize = 13 } };
                b.SetResourceReference(StyleProperty, "IconButton");
                return b;
            }
            var btnRename = Icon("", "Renommer ce support");
            var btnForget = Icon("", "Oublier ce support (supprime ses tests)");
            string key = last.DriveKey, shown = last.Shown;
            void Commit()
            {
                if (nameBox.Visibility != Visibility.Visible) return;
                nameBox.Visibility = Visibility.Collapsed; nameText.Visibility = Visibility.Visible;
                string newName = nameBox.Text.Trim();
                if (newName == shown) return;
                // Nom vide ou identique au modèle : on revient au nom du modèle
                DriveSpeedService.Rename(key, newName == last.DriveName ? "" : newName);
                _history = DriveSpeedService.LoadHistory();
                var again = _history.Where(h => h.DriveKey == _currentKey).OrderByDescending(h => h.Date).FirstOrDefault();
                if (again != null) ShowResult(again); else RenderCompare();
                TxtStatus.Text = newName.Length > 0 ? $"Support renommé « {newName} »." : "Nom du modèle rétabli.";
            }
            btnRename.Click += (_, _) =>
            {
                nameText.Visibility = Visibility.Collapsed; nameBox.Visibility = Visibility.Visible;
                nameBox.Focus(); nameBox.SelectAll();
            };
            nameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { nameBox.Text = shown; nameBox.Visibility = Visibility.Collapsed; nameText.Visibility = Visibility.Visible; e.Handled = true; }
            };
            nameBox.LostKeyboardFocus += (_, _) => Commit();
            btnForget.Click += (_, _) =>
            {
                if (AppMessageBox.Show(this, $"Oublier « {shown} » et ses {count} test(s) ?", "Oublier ce support",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                DriveSpeedService.Forget(key);
                _history = DriveSpeedService.LoadHistory();
                if (_currentKey == key) { PanelResult.Visibility = Visibility.Collapsed; _currentKey = null; }
                RenderCompare();
                TxtStatus.Text = $"« {shown} » oublié.";
            };
            actions.Children.Add(btnRename);
            actions.Children.Add(btnForget);
            Grid.SetColumn(actions, Metrics.Length + 1);
            g.Children.Add(actions);

            PanelCompare.Children.Add(new Border
            {
                Child = g, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 4),
                Background = current ? B("AccentDim") : Brushes.Transparent,
                BorderBrush = current ? B("Accent") : B("Border"), BorderThickness = new Thickness(current ? 1 : 0, current ? 1 : 0, current ? 1 : 0, 1)
            });
        }
    }
}
