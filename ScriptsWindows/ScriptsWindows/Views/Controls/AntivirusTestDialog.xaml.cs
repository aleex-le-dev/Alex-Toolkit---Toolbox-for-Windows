using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Teste la réaction de l'antivirus avec des faux virus standard (EICAR, échantillon AMSI). Voir AntivirusTestService.</summary>
public partial class AntivirusTestDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<AvTest> _tests = AntivirusTestService.CreateTests();
    private CancellationTokenSource? _runCts;
    private bool _busy;

    public AntivirusTestDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Render();
        Loaded += async (_, _) => await ShowProductsAsync();
        Closing += (_, e) =>
        {
            if (!_busy) return;
            if (AppMessageBox.Show(this, "Le test est en cours. L'arrêter et fermer ?\n\nLes fichiers de test seront supprimés.", "Test en cours",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            else _runCts?.Cancel();
        };
    }

    private async Task ShowProductsAsync()
    {
        var products = await AntivirusTestService.GetProductsAsync(_ct);
        if (products.Count == 0) { TxtProduct.Text = "Antivirus installé : non déclaré au Centre de sécurité Windows (Windows Server ou antivirus non reconnu)."; return; }
        TxtProduct.Text = "Antivirus installé : " + string.Join("  ·  ", products.Select(p =>
            $"{p.Name} ({(p.Enabled ? "activé" : "désactivé")}{(p.Enabled && !p.UpToDate ? ", signatures pas à jour" : "")})"));
        TxtProduct.Foreground = ThemeManager.GetBrush(products.Any(p => p.Enabled) ? "TextPrimary" : "DangerText");
    }

    private static Brush B(string key) => ThemeManager.GetBrush(key);

    private void Render()
    {
        PanelTests.Children.Clear();
        int i = 0;
        foreach (var t in _tests)
        {
            i++;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

            var (glyph, brush) = t.Outcome switch
            {
                AvOutcome.Detected => ("", "SuccessText"),
                AvOutcome.NotDetected => ("", "DangerText"),
                AvOutcome.Error => ("", "WarningText"),
                AvOutcome.Running => ("", "AccentText"),
                _ => ("", "TextMuted")
            };
            g.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("FontIcon"), FontSize = 18, Foreground = B(brush),
                                           Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top });

            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = $"{i}. {t.Title}", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary") });
            info.Children.Add(new TextBlock { Text = t.Description, FontSize = 11.5, Foreground = B("TextSecondary"), TextWrapping = TextWrapping.Wrap });
            if (t.Detail.Length > 0 && t.Outcome != AvOutcome.Running)
                info.Children.Add(new TextBlock { Text = t.Detail, FontSize = 11.5, Foreground = B(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetColumn(info, 1);
            g.Children.Add(info);

            var state = new TextBlock
            {
                Text = t.Outcome switch
                {
                    AvOutcome.Detected => "Détecté", AvOutcome.NotDetected => "Non détecté", AvOutcome.Error => "Indéterminé",
                    AvOutcome.Running => "En cours…", AvOutcome.Skipped => "Non fait", _ => "En attente"
                },
                FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B(brush), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(state, 2);
            g.Children.Add(state);

            PanelTests.Children.Add(new Border { Child = g, Padding = new Thickness(8, 10, 8, 10), BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (AppMessageBox.Show(this,
                "Le test va écrire le fichier de test EICAR (inoffensif) dans un dossier temporaire et lancer un script de test.\n\n" +
                "Votre antivirus va afficher des alertes « menace détectée » : c'est normal et attendu.\n\nLancer le test ?",
                "Tester l'antivirus", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _tests = AntivirusTestService.CreateTests();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        SetBusy(true);
        PanelVerdict.Visibility = Visibility.Collapsed;
        Render();
        _onOutput("\n🚀 [LANCEMENT] Test de l'antivirus (fichier EICAR, archive ZIP, script AMSI)");
        try
        {
            await AntivirusTestService.RunAsync(_tests, _ => Dispatcher.BeginInvoke(Render), _onOutput, _runCts.Token);
            var names = await AntivirusTestService.GetRecentDefenderDetectionsAsync(_ct);
            ShowVerdict(names);
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Test arrêté. Les fichiers de test ont été supprimés.";
            _onOutput("🛑 [ANNULÉ] Test de l'antivirus arrêté.");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            _onOutput($"❌ [ERREUR] Test de l'antivirus : {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            Render();
        }
    }

    private void ShowVerdict(List<string> defenderNames)
    {
        int ok = _tests.Count(t => t.Outcome == AvOutcome.Detected);
        int ko = _tests.Count(t => t.Outcome == AvOutcome.NotDetected);
        bool realtimeOk = _tests.First(t => t.Id == "file").Outcome == AvOutcome.Detected;
        string title, detail, bg, fg;
        if (ok == _tests.Count)
        {
            (title, bg, fg) = ("Votre antivirus fonctionne : les 3 faux virus ont été arrêtés.", "SuccessBg", "SuccessText");
            detail = "Protection en temps réel, analyse des archives et analyse des scripts sont actives.";
        }
        else if (!realtimeOk)
        {
            (title, bg, fg) = ("Attention : la protection en temps réel n'a pas réagi.", "DangerBg", "DangerText");
            detail = "Un virus copié sur le PC ne serait pas arrêté. Vérifiez que l'antivirus est activé et à jour (bouton « Sécurité Windows » ou votre antivirus).";
        }
        else
        {
            (title, bg, fg) = ($"Protection partielle : {ok} test(s) sur {_tests.Count} réussi(s).", "WarningBg", "WarningText");
            detail = string.Join(" ", _tests.Where(t => t.Outcome != AvOutcome.Detected).Select(t => $"{t.Title} : {t.Detail}"));
        }
        if (defenderNames.Count > 0) detail += $"\nConfirmé par Microsoft Defender : {string.Join(", ", defenderNames)}.";

        PanelVerdict.Background = B(bg);
        TxtVerdict.Text = title; TxtVerdict.Foreground = B(fg);
        TxtVerdictDetail.Text = detail; TxtVerdictDetail.Foreground = B(fg);
        PanelVerdict.Visibility = Visibility.Visible;
        TxtStatus.Text = "Test terminé. Les fichiers de test ont été supprimés.";
        _onOutput(ko == 0 && ok == _tests.Count ? $"[OK] Antivirus : {ok}/{_tests.Count} faux virus arrêtés."
                                                : $"[!] Antivirus : {ok}/{_tests.Count} faux virus arrêtés, {ko} non détecté(s).");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnRun.IsEnabled = !busy;
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TxtRun.Text = busy ? "Test en cours…" : "Relancer le test";
        if (busy) TxtStatus.Text = "Test en cours : l'antivirus peut afficher des alertes, c'est normal.";
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
