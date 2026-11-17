using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Centre de dépannage des imprimantes : spouleur, état et actions par imprimante, pilotes, erreurs récentes.</summary>
public partial class PrinterCenterDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<PrinterDriverInfo> _drivers = new();
    private bool _busy;

    public PrinterCenterDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private PrinterInfo? Selected => ListPrinters.SelectedItem as PrinterInfo;

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ProgBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnRefresh.IsEnabled = BtnUnblock.IsEnabled = BtnPnpRescan.IsEnabled = !busy;
        PanelSelected.IsEnabled = !busy && Selected != null;
        if (status.Length > 0) TxtStatus.Text = status;
    }

    private async Task RefreshAsync(string? reselect = null)
    {
        reselect ??= Selected?.Name;
        SetBusy(true, "Analyse des imprimantes…");
        try
        {
            var spoolerTask = PrinterCenterService.GetSpoolerAsync(_ct);
            var printersTask = PrinterCenterService.GetPrintersAsync(_ct);
            var driversTask = PrinterCenterService.GetDriversAsync(_ct);
            var errorsTask = PrinterCenterService.GetRecentErrorsAsync(_ct);
            await Task.WhenAll(spoolerTask, printersTask, driversTask, errorsTask);

            // Service d'impression
            var (status, start) = spoolerTask.Result;
            bool running = status == "Running";
            TxtSpooler.Text = running ? "En cours d'exécution" : status == "Stopped" ? "Arrêté" : status;
            TxtSpooler.Foreground = ThemeManager.GetBrush(running ? "SuccessText" : "DangerText");
            BadgeSpooler.Background = ThemeManager.GetBrush(running ? "SuccessBg" : "DangerBg");
            TxtSpoolerStart.Text = start switch
            {
                "Automatic" => "démarrage automatique",
                "Manual" => "démarrage manuel",
                "Disabled" => "désactivé : aucune impression possible",
                _ => ""
            };
            TxtSpoolerStart.ToolTip = start is "Manual" or "Disabled"
                ? "« Débloquer l'impression » repasse le service en démarrage automatique."
                : null;

            // Imprimantes
            var printers = printersTask.Result;
            ListPrinters.ItemsSource = printers;
            ListPrinters.SelectedItem = printers.FirstOrDefault(p => p.Name == reselect)
                                        ?? printers.FirstOrDefault(p => p.State.Level == "error")
                                        ?? printers.FirstOrDefault(p => p.IsDefault);
            int problems = printers.Count(p => p.State.Level != "ok");
            TxtPrintersTitle.Text = printers.Count == 0 ? "Imprimantes" : $"Imprimantes ({printers.Count}{(problems > 0 ? $", {problems} à vérifier" : "")})";
            TxtNoPrinter.Text = !running ? "Le service d'impression est arrêté : cliquez sur « Débloquer l'impression » pour afficher les imprimantes."
                                         : "Aucune imprimante installée. Utilisez « Ajouter une imprimante ».";
            TxtNoPrinter.Visibility = printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Pilotes
            _drivers = driversTask.Result;
            ListDrivers.ItemsSource = _drivers;
            int removable = _drivers.Count(d => d.CanRemove);
            TxtDriversTitle.Text = $"Pilotes d'impression ({_drivers.Count})";
            BtnRemoveDrivers.Content = removable > 0 ? $"Supprimer les inutilisés ({removable})" : "Aucun pilote inutilisé";
            BtnRemoveDrivers.IsEnabled = removable > 0;

            // Erreurs
            var errors = errorsTask.Result;
            ListErrors.ItemsSource = errors;
            TxtNoErrors.Visibility = errors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            SetBusy(false, problems == 0 ? "Toutes les imprimantes sont prêtes." : $"{problems} imprimante(s) à vérifier : sélectionnez-la pour voir le conseil.");
            UpdateSelection();
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Erreur d'analyse : {ex.Message}");
        }
    }

    private void ListPrinters_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var p = Selected;
        PanelSelected.IsEnabled = p != null && !_busy;
        if (p == null)
        {
            TxtSelName.Text = "Sélectionnez une imprimante";
            TxtSelInfo.Text = TxtSelStatus.Text = "";
            TxtSelAdvice.Text = "Choisissez une imprimante dans la liste pour voir son état et les actions possibles.";
            AdviceBox.Background = ThemeManager.GetBrush("BgInput");
            return;
        }
        TxtSelName.Text = p.Name;
        TxtSelInfo.Text = $"Pilote : {p.DriverName}\nPort : {p.PortName}{(p.Shared ? " • partagée" : "")}";
        TxtSelStatus.Text = p.StatusLabel;
        TxtSelStatus.Foreground = p.StatusBrush;
        TxtSelAdvice.Text = p.Advice;
        AdviceBox.Background = p.StatusBg;
        BtnDefault.IsEnabled = !p.IsDefault;
        BtnDefault.Content = p.IsDefault ? "Déjà par défaut" : "Par défaut";
        BtnClearQueue.Content = p.Jobs > 0 ? $"Vider la file ({p.Jobs})" : "Vider la file";
    }

    /// <summary>Exécute une action puis actualise l'affichage.</summary>
    private async Task RunAsync(string status, Func<Task> action)
    {
        if (_busy) return;
        SetBusy(true, status);
        _onOutput($"\n[i] Imprimantes : {status}");
        try { await action(); }
        catch (Exception ex) { _onOutput($"[ERR] {ex.Message}"); }
        _busy = false;
        await RefreshAsync();
    }

    private bool Confirm(string message, string title)
        => AppMessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ── Service ──
    private async void BtnUnblock_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Tous les documents en attente sur toutes les imprimantes seront supprimés, puis le service d'impression sera relancé.\n\nContinuer ?",
                     "Débloquer l'impression")) return;
        await RunAsync("Déblocage de l'impression…", () => PrinterCenterService.UnblockPrinting(_onOutput, _ct));
    }

    private async void BtnRestartSpooler_Click(object sender, RoutedEventArgs e)
        => await RunAsync("Redémarrage du service d'impression…", () => PrinterCenterService.RestartSpooler(_onOutput, _ct));

    private void BtnAddPrinter_Click(object sender, RoutedEventArgs e) => PrinterCenterService.OpenAddPrinter();
    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ── Imprimante sélectionnée ──
    private async void BtnTestPage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } p) await RunAsync($"Page de test sur « {p.Name} »…", () => PrinterCenterService.PrintTestPage(p.Name, _onOutput, _ct));
    }

    private async void BtnOnline_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } p) await RunAsync($"Remise en ligne de « {p.Name} »…", () => PrinterCenterService.SetOnline(p.Name, _onOutput, _ct));
    }

    private async void BtnDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } p) await RunAsync($"« {p.Name} » définie par défaut…", () => PrinterCenterService.SetDefault(p.Name, _onOutput, _ct));
    }

    private async void BtnClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (p.Jobs > 0 && !Confirm($"Supprimer les {p.Jobs} document(s) en attente sur « {p.Name} » ?", "Vider la file")) return;
        await RunAsync($"Vidage de la file de « {p.Name} »…", () => PrinterCenterService.ClearQueue(p.Name, _onOutput, _ct));
    }

    private void BtnOpenQueue_Click(object sender, RoutedEventArgs e) { if (Selected is { } p) PrinterCenterService.OpenQueue(p.Name); }
    private void BtnProperties_Click(object sender, RoutedEventArgs e) { if (Selected is { } p) PrinterCenterService.OpenProperties(p.Name); }

    private async void BtnReinstall_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (!Confirm($"« {p.Name} » va être supprimée puis recréée avec le même pilote ({p.DriverName}) et le même port ({p.PortName}).\n\n" +
                     "Les préférences personnalisées (format, recto-verso…) reviendront aux valeurs par défaut. Continuer ?", "Réinstaller l'imprimante")) return;
        await RunAsync($"Réinstallation de « {p.Name} »…", () => PrinterCenterService.ReinstallPrinter(p.Name, _onOutput, _ct));
    }

    private async void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        if (!Confirm($"Supprimer définitivement l'imprimante « {p.Name} » ?\n\nPour la retrouver, il faudra l'ajouter à nouveau.", "Supprimer l'imprimante")) return;
        await RunAsync($"Suppression de « {p.Name} »…", () => PrinterCenterService.RemovePrinter(p.Name, _onOutput, _ct));
    }

    // ── Pilotes ──
    private async void BtnDeleteSingleDriver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PrinterDriverInfo d) return;

        string prompt = d.InUse
            ? $"Le pilote « {d.Name} » est actuellement UTILISÉ par une ou plusieurs imprimantes.\n\nLa suppression du pilote supprimera également les imprimantes qui l'utilisent.\n\nÊtes-vous sûr de vouloir supprimer ce pilote ?"
            : $"Voulez-vous supprimer le pilote « {d.Name} » de Windows ?";

        if (!Confirm(prompt, "Supprimer le pilote")) return;
        await RunAsync($"Suppression du pilote « {d.Name} »…", () => PrinterCenterService.RemoveSingleDriver(d.Name, purgeFromStore: false, _onOutput, _ct));
    }

    private async void BtnPurgeSingleDriver_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PrinterDriverInfo d) return;

        string prompt = $"Voulez-vous supprimer le pilote « {d.Name} » et purger complètement son package OEM (.inf) du magasin Windows (DriverStore) ?\n\n" +
                        (d.InUse ? "ATTENTION : Les imprimantes utilisant ce pilote seront également supprimées.\n\n" : "") +
                        "Cette action élimine définitivement les fichiers d'installation tiers pour permettre une réinstallation neuve.";

        if (!Confirm(prompt, "Purger le pilote du DriverStore")) return;
        await RunAsync($"Purge complète du pilote « {d.Name} »…", () => PrinterCenterService.RemoveSingleDriver(d.Name, purgeFromStore: true, _onOutput, _ct));
    }

    private async void BtnPnpRescan_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Windows va analyser tous les ports matériels (USB) et le réseau local (WSD/UPnP) afin de redétecter et réinstaller automatiquement toutes les imprimantes connectées.\n\nContinuer ?",
                     "Réinstallation automatique Plug & Play")) return;
        await RunAsync("Détection et réinstallation automatique Plug & Play…", () => PrinterCenterService.RescanPlugAndPlay(_onOutput, _ct));
    }

    private async void BtnRemoveDrivers_Click(object sender, RoutedEventArgs e)
    {
        var unused = _drivers.Where(d => d.CanRemove).ToList();
        if (unused.Count == 0) return;
        string list = string.Join("\n", unused.Take(12).Select(d => $"• {d.Name}")) + (unused.Count > 12 ? $"\n… et {unused.Count - 12} autre(s)" : "");
        if (!Confirm($"Supprimer {unused.Count} pilote(s) qui ne servent à aucune imprimante ?\n\n{list}\n\nLes pilotes Microsoft intégrés à Windows sont conservés.",
                     "Pilotes inutilisés")) return;
        await RunAsync("Suppression des pilotes inutilisés…", () => PrinterCenterService.RemoveDrivers(unused.Select(d => d.Name), _onOutput, _ct));
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
