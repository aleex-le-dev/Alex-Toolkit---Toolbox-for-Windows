using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Pilotes tiers : inventaire, sauvegarde sélective avec progression, réinstallation depuis un dossier.</summary>
public partial class DriverBackupDialog : Window
{
    private const string AllClasses = "Tous les types";

    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<Row> _all = new();
    private bool _busy, _bulk;

    public DriverBackupDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) =>
        {
            if (_busy && AppMessageBox.Show(this, "Une opération sur les pilotes est en cours. Fermer quand même ?", "Opération en cours",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            var drivers = await DriverBackupService.GetThirdPartyDriversAsync(_ct);
            _all = drivers.Select(d => new Row(d)).ToList();
            foreach (var r in _all)
            {
                r.IsChecked = true;
                r.PropertyChanged += (_, _) => { if (!_bulk) UpdateSelection(); };
            }

            CmbClass.SelectionChanged -= Filter_Changed;
            CmbClass.Items.Clear();
            CmbClass.Items.Add(AllClasses);
            foreach (var g in _all.GroupBy(r => r.DeviceClass).OrderByDescending(g => g.Count()))
                CmbClass.Items.Add($"{g.Key} ({g.Count()})");
            CmbClass.SelectedIndex = 0;
            CmbClass.SelectionChanged += Filter_Changed;

            ApplyFilter();
            _onOutput($"[OK] Pilotes : {_all.Count} pilote(s) de fabricant installé(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ProgLoad.Visibility = Visibility.Collapsed;
            TxtEmpty.Text = $"Inventaire impossible : {ex.Message}";
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private List<Row> Visible() => (LstDrivers.ItemsSource as List<Row>) ?? new();

    private void ApplyFilter()
    {
        if (!IsInitialized || LstDrivers == null) return;
        string q = TxtSearch.Text.Trim();
        string? cls = CmbClass.SelectedIndex > 0 ? CmbClass.SelectedItem as string : null;
        if (cls != null) cls = cls[..cls.LastIndexOf(" (", StringComparison.Ordinal)];

        var list = _all.Where(r => (cls == null || r.DeviceClass == cls) &&
            (q.Length == 0 || r.Devices.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Details.Contains(q, StringComparison.CurrentCultureIgnoreCase)
             || r.Inf.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        LstDrivers.ItemsSource = list;
        PanelEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgLoad.Visibility = Visibility.Collapsed;
        if (list.Count == 0) TxtEmpty.Text = _all.Count == 0 ? "Aucun pilote de fabricant installé (uniquement des pilotes Microsoft)." : "Aucun pilote ne correspond aux filtres.";
        UpdateSelection();
    }

    private void ChkAll_Click(object sender, RoutedEventArgs e)
    {
        bool check = ChkAll.IsChecked == true;
        _bulk = true;
        foreach (var r in Visible()) r.IsChecked = check;
        _bulk = false;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var visible = Visible();
        int vChecked = visible.Count(r => r.IsChecked);
        ChkAll.IsChecked = visible.Count > 0 && vChecked == visible.Count ? true : vChecked == 0 ? false : null;
        int total = _all.Count(r => r.IsChecked);
        BtnExport.IsEnabled = total > 0 && !_busy;
        TxtExport.Text = total == _all.Count && total > 0 ? $"Tout sauvegarder ({total})" : $"Sauvegarder la sélection ({total})";
        if (!_busy) TxtStatus.Text = $"{_all.Count} pilote(s) de fabricant, {total} sélectionné(s).";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnImport.IsEnabled = !busy;
        ChkAll.IsEnabled = TxtSearch.IsEnabled = CmbClass.IsEnabled = LstDrivers.IsEnabled = !busy;
        UpdateSelection();
    }

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var selected = _all.Where(r => r.IsChecked).Select(r => r.Driver).ToList();
        if (selected.Count == 0) return;

        var dlg = new OpenFolderDialog { Title = "Choisissez où sauvegarder les pilotes (clé USB, disque externe…)" };
        if (dlg.ShowDialog(this) != true) return;
        string target = Path.Combine(dlg.FolderName, $"Pilotes_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd}");

        SetBusy(true);
        ProgExport.Visibility = Visibility.Visible;
        ProgExport.Value = 0;
        try
        {
            var (ok, errors) = await DriverBackupService.ExportAsync(selected, target,
                (i, total, d) => Dispatcher.InvokeAsync(() =>
                {
                    ProgExport.Value = total == 0 ? 1 : (double)i / total;
                    TxtStatus.Text = i < total ? $"Sauvegarde {i + 1} / {total} : {d.Devices}" : "Finalisation…";
                }),
                _onOutput, _ct);

            TxtStatus.Text = $"{ok} pilote(s) sauvegardé(s) dans {target}" + (errors.Count > 0 ? $" — {errors.Count} échec(s)" : "");
            if (errors.Count > 0)
                AppMessageBox.Show(this, $"{ok} pilote(s) sauvegardé(s).\n\n{errors.Count} pilote(s) n'ont pas pu être sauvegardés :\n" + string.Join("\n", errors.Take(8)),
                    "Sauvegarde terminée avec des erreurs", MessageBoxButton.OK, MessageBoxImage.Warning);
            Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Sauvegarde interrompue."; }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, $"La sauvegarde a échoué :\n\n{ex.Message}", "Sauvegarde des pilotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgExport.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Sélectionnez le dossier de sauvegarde des pilotes (« Pilotes_… »)" };
        if (dlg.ShowDialog(this) != true) return;

        int infCount;
        try { infCount = Directory.EnumerateFiles(dlg.FolderName, "*.inf", SearchOption.AllDirectories).Count(); }
        catch { infCount = 0; }
        if (infCount == 0)
        {
            AppMessageBox.Show(this, "Ce dossier ne contient aucun pilote (fichier .inf).", "Réinstaller des pilotes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AppMessageBox.Show(this, $"{infCount} pilote(s) trouvé(s) dans ce dossier.\n\nLes installer sur ce PC ? Seuls les pilotes correspondant à un matériel présent seront utilisés.",
                "Réinstaller des pilotes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true);
        ProgExport.Visibility = Visibility.Visible;
        ProgExport.IsIndeterminate = true;
        TxtStatus.Text = $"Installation de {infCount} pilote(s)…";
        try
        {
            var (_, summary) = await DriverBackupService.ImportAsync(dlg.FolderName, _onOutput, _ct);
            TxtStatus.Text = "Installation terminée.";
            AppMessageBox.Show(this, $"Installation terminée.\n\n{summary}\n\nUn redémarrage peut être nécessaire pour que certains pilotes soient pris en compte. Le détail complet est dans le journal.",
                "Réinstaller des pilotes", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadAsync();
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Installation interrompue."; }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, $"L'installation a échoué :\n\n{ex.Message}", "Réinstaller des pilotes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgExport.IsIndeterminate = false;
            ProgExport.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class Row : INotifyPropertyChanged
    {
        public DriverBackupService.DriverInfo Driver { get; }
        public string Inf => Driver.Inf;
        public string DeviceClass => Driver.DeviceClass;
        public string Devices => Driver.Devices.Length > 0 ? Driver.Devices : "(périphérique sans nom)";
        public string Details => string.Join(" • ", new[] { Driver.Provider, Driver.Version.Length > 0 ? "version " + Driver.Version : "", Driver.Date }.Where(s => s.Length > 0));

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
        }

        public Row(DriverBackupService.DriverInfo d) => Driver = d;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
