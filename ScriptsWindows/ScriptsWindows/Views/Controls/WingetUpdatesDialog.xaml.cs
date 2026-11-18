using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

public class WingetUpdateItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public string Source { get; set; } = "winget";

    public string VersionDisplay => $"Actuelle : {CurrentVersion}";
    public string AvailableDisplay => $"Nouvelle : {AvailableVersion}";

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class WingetUpdatesDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;

    public ObservableCollection<WingetUpdateItem> Updates { get; } = new();
    private List<ScriptsWindows.Services.InstalledApp> _allInstalled = new();
    private bool _inventoryLoaded;

    public WingetUpdatesDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput;
        _ct = ct;

        LstUpdates.ItemsSource = Updates;

        Loaded += async (_, _) => await CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        TxtFeedback.Text = "Recherche des mises à jour applicatives via Winget...";
        TxtNoUpdates.Text = "Analyse en cours...";
        TxtNoUpdates.Visibility = Visibility.Visible;
        Updates.Clear();

        try
        {
            string output = await CommandRunner.RunAsync("winget upgrade", _ct);
            var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            int headerIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Nom", StringComparison.OrdinalIgnoreCase) || lines[i].StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                {
                    headerIdx = i;
                    break;
                }
            }

            if (headerIdx >= 0 && headerIdx + 2 < lines.Length)
            {
                string header = lines[headerIdx];
                int idPos   = header.IndexOf("ID", StringComparison.OrdinalIgnoreCase);
                int verPos  = header.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
                int dispPos = header.IndexOf("Disponible", StringComparison.OrdinalIgnoreCase);
                if (dispPos < 0) dispPos = header.IndexOf("Available", StringComparison.OrdinalIgnoreCase);
                int srcPos  = header.IndexOf("Source", StringComparison.OrdinalIgnoreCase);

                for (int i = headerIdx + 2; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd();
                    if (string.IsNullOrWhiteSpace(line) || line.Contains("mises à niveau", StringComparison.OrdinalIgnoreCase) || line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (dispPos > 0 && line.Length > dispPos)
                    {
                        string name = idPos > 0 && line.Length > idPos ? line.Substring(0, idPos).Trim() : line.Trim();
                        string id   = verPos > 0 && line.Length > verPos ? line.Substring(idPos, verPos - idPos).Trim() : "";
                        string ver  = dispPos > 0 && line.Length > dispPos ? line.Substring(verPos, dispPos - verPos).Trim() : "";
                        string disp = srcPos > 0 && line.Length > srcPos ? line.Substring(dispPos, srcPos - dispPos).Trim() : line.Substring(dispPos).Trim();
                        string src  = srcPos > 0 && line.Length > srcPos ? line.Substring(srcPos).Trim() : "winget";

                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                        {
                            Updates.Add(new WingetUpdateItem
                            {
                                Name = name,
                                Id = id,
                                CurrentVersion = ver,
                                AvailableVersion = disp,
                                Source = src
                            });
                        }
                    }
                }
            }

            if (Updates.Count > 0)
            {
                TxtNoUpdates.Visibility = Visibility.Collapsed;
                TxtFeedback.Text = $"{Updates.Count} mise(s) à jour trouvée(s).";
            }
            else
            {
                TxtNoUpdates.Text = "Toutes vos applications sont à jour ! Aucune mise à niveau requise.";
                TxtFeedback.Text = "Système et applications à jour.";
            }
        }
        catch (Exception ex)
        {
            TxtNoUpdates.Text = $"Erreur lors de la détection : {ex.Message}";
            TxtFeedback.Text = $"Erreur : {ex.Message}";
        }
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        => await CheckUpdatesAsync();

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var u in Updates) u.IsSelected = true;
    }

    private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var u in Updates) u.IsSelected = false;
    }

    private async void BtnUpdateSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = Updates.Where(u => u.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AppMessageBox.Show("Veuillez cocher au moins une application à mettre à jour.", "Aucune sélection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BtnUpdateSelected.IsEnabled = false;
        _onOutput($"\n🚀 [LANCEMENT] Mise à jour de {selected.Count} application(s) sélectionnée(s)...");

        try
        {
            var failed = new List<string>();
            foreach (var app in selected)
                if (!await UpgradeApplicationAsync(app)) failed.Add(app.Name);

            int ok = selected.Count - failed.Count;
            if (failed.Count == 0)
            {
                _onOutput($"\n✅ [TERMINÉ] {ok} application(s) mise(s) à jour.");
                TxtFeedback.Text = $"{ok} application(s) mise(s) à jour.";
            }
            else
            {
                _onOutput($"\n❌ [ERREUR] {ok} mise(s) à jour réussie(s), {failed.Count} en échec : {string.Join(", ", failed)} (détail dans le journal).");
                TxtFeedback.Text = $"{ok} réussie(s), {failed.Count} en échec : {string.Join(", ", failed)}. Voir le journal.";
            }
            await CheckUpdatesAsync();
        }
        catch (Exception ex)
        {
            _onOutput($"[ERR] Échec mise à jour : {ex.Message}");
            TxtFeedback.Text = $"Erreur : {ex.Message}";
        }
        finally
        {
            BtnUpdateSelected.IsEnabled = true;
        }
    }

    private async void BtnUpdateSingle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not WingetUpdateItem app) return;

        btn.IsEnabled = false;
        try
        {
            bool ok = await UpgradeApplicationAsync(app);
            TxtFeedback.Text = ok ? $"{app.Name} mis à jour en {app.AvailableVersion}." : $"{app.Name} n'a pas été mis à jour : voir le journal.";
            await CheckUpdatesAsync();
        }
        catch (Exception ex)
        {
            _onOutput($"[ERR] {ex.Message}");
            TxtFeedback.Text = $"Erreur : {ex.Message}";
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    /// <summary>Met à jour une application et vérifie la version installée ensuite. Renvoie true seulement si elle a changé.</summary>
    private async Task<bool> UpgradeApplicationAsync(WingetUpdateItem app)
    {
        bool force = ChkForceUpgrade.IsChecked == true;
        _onOutput($"\n[i] Mise à niveau de {app.Name} ({app.Id}) : {app.CurrentVersion} → {app.AvailableVersion}…");
        TxtFeedback.Text = $"Mise à niveau de {app.Name}…";

        var r = await AppService.UpgradePackageAsync(app.Id, app.Source, app.CurrentVersion, app.AvailableVersion, force, _onOutput, _ct);
        if (r.Updated)
        {
            _onOutput($"[OK] {app.Name} mis à jour en {app.AvailableVersion}.");
            return true;
        }
        _onOutput($"❌ [ERREUR] {app.Name} n'a pas été mis à jour : {r.Reason}." +
                  (r.InstallerLog.Length > 0 ? $" Journal de l'installateur : {r.InstallerLog}" : ""));
        return false;
    }

    private async void BtnUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = AppMessageBox.Show(
            "Voulez-vous lancer la mise à niveau automatique de toutes les applications disponibles ?\n\n(Commande : winget upgrade --all --include-unknown)",
            "Tout mettre à jour",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _onOutput("\n🚀 [LANCEMENT] Mise à niveau de toutes les applications (winget upgrade --all --include-unknown)...");
        TxtFeedback.Text = "Mise à jour globale en cours...";

        try
        {
            await CommandRunner.RunAsync("winget upgrade --all --include-unknown --accept-package-agreements --accept-source-agreements", _onOutput, _ct);
            _onOutput("\n✅ [TERMINÉ] Mise à jour globale terminée.");
            TxtFeedback.Text = "Mise à jour globale terminée.";
            await CheckUpdatesAsync();
        }
        catch (Exception ex)
        {
            _onOutput($"[ERR] {ex.Message}");
            TxtFeedback.Text = $"Erreur : {ex.Message}";
        }
    }


    // ─── Inventaire des logiciels installés ──────────────────────────────

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is TabControl && TabInstalled.IsSelected && !_inventoryLoaded)
            await LoadInventoryAsync();
    }

    private async Task LoadInventoryAsync()
    {
        _inventoryLoaded = true;
        TxtNoInstalled.Text = "Chargement de l'inventaire…";
        TxtNoInstalled.Visibility = Visibility.Visible;
        TxtFeedback.Text = "Inventaire des logiciels installés…";
        try
        {
            _allInstalled = await ScriptsWindows.Services.InstalledAppsService.GetAppsAsync(_ct);
            ApplyInventoryFilter();
            TxtFeedback.Text = $"{_allInstalled.Count} logiciel(s) installé(s).";
        }
        catch (Exception ex)
        {
            _inventoryLoaded = false;
            TxtNoInstalled.Text = $"Erreur d'inventaire : {ex.Message}";
            TxtFeedback.Text = $"Erreur : {ex.Message}";
        }
    }

    private List<ScriptsWindows.Services.InstalledApp> FilteredApps()
    {
        IEnumerable<ScriptsWindows.Services.InstalledApp> q = _allInstalled;
        string search = TxtSearchApp.Text.Trim();
        if (search.Length > 0)
            q = q.Where(a => a.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                          || a.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase));

        q = CmbAppFilter.SelectedIndex switch
        {
            1 => q.Where(a => !a.IsStore),
            2 => q.Where(a => a.IsStore),
            3 => q.Where(a => a.InstallDate >= DateTime.Today.AddDays(-30)),
            4 => q.Where(a => a.SizeMB >= 1024),
            _ => q
        };

        q = CmbAppSort.SelectedIndex switch
        {
            1 => q.OrderByDescending(a => a.SizeMB),
            2 => q.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue),
            3 => q.OrderBy(a => a.Publisher, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        return q.ToList();
    }

    private void ApplyInventoryFilter()
    {
        if (!IsLoaded || LstInstalledApps == null) return;
        var shown = FilteredApps();
        LstInstalledApps.ItemsSource = shown;

        double totalGb = _allInstalled.Sum(a => a.SizeMB) / 1024;
        int recent = _allInstalled.Count(a => a.InstallDate >= DateTime.Today.AddDays(-30));
        int store = _allInstalled.Count(a => a.IsStore);
        TxtInventorySummary.Text = $"{_allInstalled.Count} logiciels ({_allInstalled.Count - store} de bureau, {store} du Store) • {totalGb:0.0} Go déclarés • {recent} installé(s) ces 30 derniers jours"
                                 + (shown.Count != _allInstalled.Count ? $" • {shown.Count} affiché(s)" : "");

        TxtNoInstalled.Text = _allInstalled.Count == 0 ? "Aucun logiciel trouvé." : "Aucun logiciel ne correspond aux critères.";
        TxtNoInstalled.Visibility = shown.Count == 0 && _inventoryLoaded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtSearchApp_TextChanged(object sender, TextChangedEventArgs e) => ApplyInventoryFilter();
    private void AppFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyInventoryFilter();

    private void BtnOpenAppFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScriptsWindows.Services.InstalledApp app } && app.HasLocation)
            CommandRunner.StartProcess("explorer.exe", $"\"{app.Location}\"");
    }

    private async void BtnUninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScriptsWindows.Services.InstalledApp app }) return;
        if (AppMessageBox.Show(this, $"Désinstaller « {app.Name} » ?\n\n{app.Details}\n\nLe programme de désinstallation du logiciel va s'ouvrir.",
                            "Désinstaller", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        TxtFeedback.Text = $"Désinstallation de {app.Name}…";
        _onOutput($"\n🚀 [LANCEMENT] Désinstallation : {app.Name}");
        try { await ScriptsWindows.Services.InstalledAppsService.UninstallAsync(app, _onOutput, _ct); }
        catch (Exception ex) { _onOutput($"[ERR] {ex.Message}"); }
        await LoadInventoryAsync(); // la liste reflète le résultat réel
    }

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e) => Export(csv: true);
    private void BtnExportHtml_Click(object sender, RoutedEventArgs e) => Export(csv: false);

    private void Export(bool csv)
    {
        var apps = FilteredApps();
        if (apps.Count == 0) { TxtFeedback.Text = "Rien à exporter."; return; }
        try
        {
            string path = csv ? ScriptsWindows.Services.InstalledAppsService.ExportCsv(apps)
                              : ScriptsWindows.Services.InstalledAppsService.ExportHtml(apps);
            TxtFeedback.Text = $"Exporté : {System.IO.Path.GetFileName(path)}";
            _onOutput($"[OK] Inventaire des logiciels exporté : {path}");
            CommandRunner.OpenUrl(path);
        }
        catch (Exception ex) { TxtFeedback.Text = $"Erreur d'export : {ex.Message}"; }
    }
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
