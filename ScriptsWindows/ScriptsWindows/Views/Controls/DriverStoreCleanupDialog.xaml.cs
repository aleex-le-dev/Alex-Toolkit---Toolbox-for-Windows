using System.ComponentModel;
using System.Windows;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Magasin de pilotes : liste des anciennes versions remplacées, suppression sélective avec progression.</summary>
public partial class DriverStoreCleanupDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<Row> _rows = new();
    private bool _busy, _bulk;

    public DriverStoreCleanupDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await AnalyzeAsync();
        Closing += (_, e) =>
        {
            if (_busy && AppMessageBox.Show(this, "La suppression des anciens pilotes est en cours. Fermer quand même ?\n\nLe pilote en cours se terminera en arrière-plan.",
                    "Suppression en cours", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }

    private async Task AnalyzeAsync()
    {
        _busy = true;
        BtnDelete.IsEnabled = false;
        ChkAll.IsEnabled = false;
        LstDrivers.ItemsSource = null;
        PanelEmpty.Visibility = Visibility.Visible;
        ProgLoad.Visibility = Visibility.Visible;
        TxtEmptyIcon.Visibility = Visibility.Collapsed;
        TxtEmpty.Text = "Lecture des pilotes installés et de leurs versions…";
        TxtSummary.Text = "Analyse du magasin de pilotes…";
        try
        {
            var analysis = await DriverStoreService.AnalyzeAsync(_ct);
            _rows = analysis.OldVersions.Select(o => new Row(o)).ToList();
            foreach (var r in _rows) r.PropertyChanged += (_, _) => { if (!_bulk) UpdateSelection(); };

            LstDrivers.ItemsSource = _rows;
            ProgLoad.Visibility = Visibility.Collapsed;
            if (_rows.Count == 0)
            {
                TxtEmptyIcon.Visibility = Visibility.Visible;
                TxtEmpty.Text = $"Aucune ancienne version : le magasin de pilotes est déjà propre ({analysis.PackageCount} pilotes de fabricant installés).";
            }
            else PanelEmpty.Visibility = Visibility.Collapsed;

            long reclaimable = _rows.Where(r => r.CanDelete).Sum(r => r.Old.Package.Size);
            int inUse = _rows.Count(r => !r.CanDelete);
            TxtSummary.Text = _rows.Count == 0
                ? $"{analysis.PackageCount} pilotes de fabricant analysés, aucune ancienne version."
                : $"{analysis.PackageCount} pilotes analysés · {_rows.Count} ancienne(s) version(s) · {DriverStoreService.FormatSize(reclaimable)} récupérables" +
                  (inUse > 0 ? $" · {inUse} encore en service (conservée{(inUse > 1 ? "s" : "")})" : "");
            ChkAll.IsEnabled = _rows.Any(r => r.CanDelete);
            _onOutput($"[OK] Magasin de pilotes : {_rows.Count} ancienne(s) version(s), {DriverStoreService.FormatSize(reclaimable)} récupérables.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ProgLoad.Visibility = Visibility.Collapsed;
            TxtEmpty.Text = $"Analyse impossible : {ex.Message}";
            TxtSummary.Text = "Analyse impossible.";
            _onOutput($"❌ [ERREUR] Analyse du magasin de pilotes : {ex.Message}");
        }
        finally
        {
            _busy = false;
            UpdateSelection();   // après _busy = false, sinon le bouton reste grisé
        }
    }

    private void UpdateSelection()
    {
        var selected = _rows.Where(r => r.IsChecked && r.CanDelete).ToList();
        long size = selected.Sum(r => r.Old.Package.Size);
        TxtDelete.Text = selected.Count == 0 ? "Supprimer" : $"Supprimer {selected.Count} ({DriverStoreService.FormatSize(size)})";
        BtnDelete.IsEnabled = !_busy && selected.Count > 0;
        var deletable = _rows.Where(r => r.CanDelete).ToList();
        ChkAll.IsChecked = deletable.Count > 0 && deletable.All(r => r.IsChecked) ? true : deletable.Any(r => r.IsChecked) ? null : false;
        if (!_busy) TxtStatus.Text = selected.Count == 0 ? "" : $"{selected.Count} ancienne(s) version(s) sélectionnée(s).";
    }

    private void ChkAll_Click(object sender, RoutedEventArgs e)
    {
        bool check = ChkAll.IsChecked == true;
        _bulk = true;
        foreach (var r in _rows.Where(r => r.CanDelete)) r.IsChecked = check;
        _bulk = false;
        UpdateSelection();
    }

    private async void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.IsChecked && r.CanDelete).ToList();
        if (selected.Count == 0) return;
        long size = selected.Sum(r => r.Old.Package.Size);
        if (AppMessageBox.Show(this,
                $"Supprimer {selected.Count} ancienne(s) version(s) de pilotes ({DriverStoreService.FormatSize(size)}) ?\n\n" +
                "La version la plus récente de chaque pilote est conservée, et Windows refuse de supprimer un pilote encore utilisé.\n\n" +
                "Vous ne pourrez plus revenir à ces anciennes versions avec « Restaurer le pilote » du Gestionnaire de périphériques.",
                "Anciennes versions des pilotes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _busy = true;
        BtnDelete.IsEnabled = false;
        ChkAll.IsEnabled = false;
        ProgDelete.Visibility = Visibility.Visible;
        ProgDelete.Value = 0;
        int ok = 0;
        long freed = 0;
        var failed = new List<string>();
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var p = selected[i].Old.Package;
                TxtStatus.Text = $"Suppression {i + 1}/{selected.Count} : {selected[i].Title}…";
                var (success, code) = await DriverStoreService.DeleteAsync(p.Inf, _ct);
                if (success) { ok++; freed += p.Size; _onOutput($"[OK] Ancien pilote supprimé : {selected[i].Title} v{p.VersionText} ({p.Inf})"); }
                else { failed.Add($"{selected[i].Title} v{p.VersionText}"); _onOutput($"[!] Non supprimé (en service ou protégé) : {selected[i].Title} v{p.VersionText} ({p.Inf}), code {code}"); }
                ProgDelete.Value = (i + 1) / (double)selected.Count;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _onOutput($"❌ [ERREUR] Suppression des anciens pilotes : {ex.Message}"); failed.Add(ex.Message); }
        finally
        {
            _busy = false;
            ProgDelete.Visibility = Visibility.Collapsed;
        }

        _onOutput($"[OK] Magasin de pilotes : {ok} ancienne(s) version(s) supprimée(s), {DriverStoreService.FormatSize(freed)} libérés.");
        if (!IsLoaded) return;
        AppMessageBox.Show(this,
            $"{ok} ancienne(s) version(s) supprimée(s), {DriverStoreService.FormatSize(freed)} libérés." +
            (failed.Count > 0 ? $"\n\n{failed.Count} non supprimée(s) (encore utilisée(s) par Windows) :\n• " + string.Join("\n• ", failed.Take(8)) : ""),
            "Anciennes versions des pilotes", MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        await AnalyzeAsync();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Ligne affichée : une ancienne version et la version qui la remplace.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        private bool _isChecked;
        public DriverStoreService.OldDriver Old { get; }
        public bool CanDelete => !Old.Package.InUse;
        public string DeviceClass { get; }
        public string Title { get; }
        public string Details { get; }
        public string SizeText => Old.Package.Folder == null ? "Taille inconnue" : DriverStoreService.FormatSize(Old.Package.Size);
        public Visibility InUseVisibility => CanDelete ? Visibility.Collapsed : Visibility.Visible;

        public Row(DriverStoreService.OldDriver old)
        {
            Old = old;
            var p = old.Package;
            DeviceClass = DriverBackupService.ClassLabel(p.ClassName);
            Title = $"{(p.Provider.Length > 0 ? p.Provider : "Fabricant inconnu")} · {p.OriginalName}";
            static string Date(DateTime? d) => d.HasValue ? $" du {d:dd/MM/yyyy}" : "";
            Details = $"Version {p.VersionText}{Date(p.Date)} · remplacée par la {old.Newest.VersionText}{Date(old.Newest.Date)} · {p.Inf}";
            _isChecked = CanDelete;
        }

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
