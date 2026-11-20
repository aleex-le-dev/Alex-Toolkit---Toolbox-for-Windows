using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Store et icônes : état des paquets avant / après, réparation ciblée, icônes en deux niveaux (ShellRepairService).</summary>
public partial class AppsShellRepairDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private bool _busy;

    public AppsShellRepairDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await ShowStoreStateAsync();
        Closing += (_, e) => { if (_busy) e.Cancel = AppMessageBox.Show(this, "Une réparation est en cours. Fermer quand même ?", "Réparation en cours", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes; };
    }

    private void Log(string line)
    {
        _onOutput(line);
        Dispatcher.BeginInvoke(() => { TxtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}"); TxtLog.ScrollToEnd(); });
    }

    private async Task<bool> ShowStoreStateAsync()
    {
        PanelStore.Children.Clear();
        PanelStore.Children.Add(new TextBlock { Text = "Lecture de l'état du Store…", FontSize = 12, Foreground = ThemeManager.GetBrush("TextMuted") });
        var state = await ShellRepairService.GetStoreStateAsync(_ct);
        PanelStore.Children.Clear();
        foreach (var p in state)
        {
            string status = ShellRepairService.StatusLabel(p);
            bool ok = status == "OK";
            bool optional = p.Name is "Microsoft.Services.Store.Engagement" or "Microsoft.StorePurchaseApp";
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = ShellRepairService.PackageLabel(p.Name), FontSize = 12.5, Foreground = ThemeManager.GetBrush("TextPrimary") });
            var v = new TextBlock { Text = p.Version, FontSize = 11.5, Foreground = ThemeManager.GetBrush("TextMuted") };
            Grid.SetColumn(v, 1); g.Children.Add(v);
            var s = new TextBlock { Text = status, FontSize = 12, FontWeight = FontWeights.SemiBold,
                                    Foreground = ThemeManager.GetBrush(ok ? "SuccessText" : !p.Installed && optional ? "TextMuted" : "DangerText") };
            Grid.SetColumn(s, 2); g.Children.Add(s);
            PanelStore.Children.Add(g);
        }
        var store = state.FirstOrDefault(p => p.Name == "Microsoft.WindowsStore");
        bool storeOk = store != null && ShellRepairService.StatusLabel(store) == "OK";
        TxtStatus.Text = storeOk ? "Le Store est installé et en bon état selon Windows." : "Le Store est absent ou en mauvais état : utilisez « Réparer le Store ».";
        return storeOk;
    }

    private async Task RunAsync(string label, Func<Task> work)
    {
        if (_busy) return;
        _busy = true;
        foreach (var b in new[] { BtnRepairStore, BtnRefreshIcons, BtnRepairIcons }) b.IsEnabled = false;
        Log($"🚀 [LANCEMENT] {label}");
        try { await work(); }
        catch (Exception ex) { Log($"❌ [ERREUR] {label} : {ex.Message}"); TxtStatus.Text = $"Erreur : {ex.Message}"; }
        finally { _busy = false; foreach (var b in new[] { BtnRepairStore, BtnRefreshIcons, BtnRepairIcons }) b.IsEnabled = true; }
    }

    private async void BtnRepairStore_Click(object sender, RoutedEventArgs e) => await RunAsync("Réparation du Microsoft Store", async () =>
    {
        await ShellRepairService.RepairStoreAsync(s => Dispatcher.Invoke(() => TxtStatus.Text = s), Log, _ct);
        bool ok = await ShowStoreStateAsync();
        Log(ok ? "[OK] Microsoft Store réparé : ouvrez-le pour vérifier." : "[!] Le Store reste en mauvais état : redémarrez le PC, puis vérifiez Windows Update (Réparer les fichiers système).");
        TxtStatus.Text = ok ? "Store réparé. Ouvrez-le pour vérifier (le premier lancement peut être lent)." : "Le Store reste en mauvais état : redémarrez puis réessayez.";
    });

    private async void BtnRefreshIcons_Click(object sender, RoutedEventArgs e) => await RunAsync("Rafraîchissement des icônes", async () =>
    {
        await ShellRepairService.RefreshIconsAsync(Log, _ct);
        TxtStatus.Text = "Icônes rafraîchies. Si elles restent blanches, utilisez la reconstruction complète.";
    });

    private async void BtnRepairIcons_Click(object sender, RoutedEventArgs e)
    {
        if (AppMessageBox.Show(this, "L'Explorateur va redémarrer : les fenêtres de dossiers ouvertes se ferment et le Bureau disparaît quelques secondes. Vos fichiers et programmes ne sont pas touchés.\n\nContinuer ?",
                "Reconstruction des icônes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync("Reconstruction du cache d'icônes", async () =>
        {
            long freed = await ShellRepairService.RebuildIconCacheAsync(Log, _ct);
            TxtStatus.Text = $"Caches reconstruits ({freed / 1048576.0:0.#} Mo supprimés). Les icônes se redessinent au fil de l'utilisation.";
        });
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
