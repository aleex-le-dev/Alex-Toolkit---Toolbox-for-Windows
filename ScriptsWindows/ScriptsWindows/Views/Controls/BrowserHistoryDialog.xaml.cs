using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Historique de tous les navigateurs : recherche, filtres navigateur / période, sites les plus visités, export CSV.</summary>
public partial class BrowserHistoryDialog : Window
{
    private const int MaxPerProfile = 3000;
    private const string AllBrowsers = "Tous les navigateurs";

    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<Row> _all = new();
    private bool _loading;

    public BrowserHistoryDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        BtnRefresh.IsEnabled = false;
        LstHistory.ItemsSource = null;
        PanelEmpty.Visibility = Visibility.Visible;
        ProgLoad.Visibility = Visibility.Visible;
        TxtEmpty.Text = "Lecture de l'historique des navigateurs…";

        var errors = new List<string>();
        try
        {
            var entries = await Task.Run(() => BrowserHistoryService.Read(MaxPerProfile, _ct, e => { lock (errors) errors.Add(e); }), _ct);
            _all = entries.Select(e => new Row(e)).ToList();

            TxtPages.Text = _all.Count.ToString("N0");
            TxtSites.Text = _all.Select(r => r.Domain).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("N0");
            var sources = _all.Select(r => r.Source).Distinct().ToList();
            TxtBrowsers.Text = sources.Count.ToString();
            TxtLast.Text = _all.Count > 0 ? _all[0].Entry.LastVisit.ToString("dd/MM/yyyy HH:mm") : "—";

            CmbBrowser.SelectionChanged -= Filter_Changed;
            CmbBrowser.Items.Clear();
            CmbBrowser.Items.Add(AllBrowsers);
            foreach (var g in _all.GroupBy(r => r.Source).OrderByDescending(g => g.Count()))
                CmbBrowser.Items.Add($"{g.Key} ({g.Count():N0})");
            CmbBrowser.SelectedIndex = 0;
            CmbBrowser.SelectionChanged += Filter_Changed;

            ApplyFilter();
            foreach (var err in errors) _onOutput(err);
            _onOutput($"[OK] Historique de navigation : {_all.Count} page(s) lue(s) dans {sources.Count} navigateur(s) / profil(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ProgLoad.Visibility = Visibility.Collapsed;
            TxtEmpty.Text = $"Lecture impossible : {ex.Message}";
        }
        finally
        {
            _loading = false;
            BtnRefresh.IsEnabled = true;
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private List<Row> Visible() => (LstHistory.ItemsSource as List<Row>) ?? new();

    private void ApplyFilter()
    {
        if (!IsInitialized || LstHistory == null) return;

        string q = TxtSearch.Text.Trim();
        string? source = CmbBrowser.SelectedIndex > 0 ? CmbBrowser.SelectedItem as string : null;
        if (source != null) source = source[..source.LastIndexOf(" (", StringComparison.Ordinal)];
        DateTime? since = CmbPeriod.SelectedIndex switch
        {
            1 => DateTime.Today,
            2 => DateTime.Today.AddDays(-6),
            3 => DateTime.Today.AddDays(-29),
            _ => null,
        };

        var list = _all.Where(r =>
            (source == null || r.Source == source) &&
            (since == null || r.Entry.LastVisit >= since) &&
            (q.Length == 0 || r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Url.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        LstHistory.ItemsSource = list;
        PanelEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgLoad.Visibility = Visibility.Collapsed;
        if (list.Count == 0)
            TxtEmpty.Text = _all.Count == 0 ? "Aucun historique trouvé (aucun navigateur pris en charge, ou historique vide)." : "Aucune page ne correspond aux filtres.";

        BtnExport.IsEnabled = list.Count > 0;
        TxtStatus.Text = list.Count == _all.Count ? $"{list.Count:N0} page(s)." : $"{list.Count:N0} page(s) affichée(s) sur {_all.Count:N0}.";
        BuildTopSites(list);
    }

    private void BuildTopSites(List<Row> list)
    {
        PanelTopSites.Children.Clear();
        var top = list.Where(r => r.Domain.Length > 0)
                      .GroupBy(r => r.Domain, StringComparer.OrdinalIgnoreCase)
                      .Select(g => (Domain: g.Key, Visits: g.Sum(r => r.Entry.Visits)))
                      .OrderByDescending(x => x.Visits).Take(8);
        foreach (var (domain, visits) in top)
        {
            var text = new TextBlock { FontSize = 11.5 };
            text.Inlines.Add(new System.Windows.Documents.Run(domain) { Foreground = ThemeManager.GetBrush("TextPrimary") });
            text.Inlines.Add(new System.Windows.Documents.Run($"  {visits:N0}") { Foreground = ThemeManager.GetBrush("TextMuted") });
            var chip = new Button { Content = text, Style = (Style)FindResource("SiteChip"), ToolTip = $"Afficher uniquement {domain}" };
            chip.Click += (_, _) => TxtSearch.Text = domain;
            PanelTopSites.Children.Add(chip);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row r) return;
        // Uniquement des adresses web (pas de file:// ni de schéma d'application)
        if (!r.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !r.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            TxtStatus.Text = "Seules les adresses web (http / https) peuvent être ouvertes d'ici.";
            return;
        }
        try { Process.Start(new ProcessStartInfo(r.Url) { UseShellExecute = true }); }
        catch (Exception ex) { TxtStatus.Text = $"Ouverture impossible : {ex.Message}"; }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row r) return;
        try { Clipboard.SetText(r.Url); TxtStatus.Text = "Adresse copiée."; }
        catch { TxtStatus.Text = "Presse-papiers indisponible, réessayez."; }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var rows = Visible();
        if (rows.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter l'historique",
            Filter = "Fichier CSV (*.csv)|*.csv",
            FileName = $"Historique_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd}.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            BrowserHistoryService.ExportCsv(rows.Select(r => r.Entry), dlg.FileName);
            TxtStatus.Text = $"{rows.Count:N0} page(s) exportée(s) : {dlg.FileName}";
            _onOutput($"[OK] Historique exporté ({rows.Count} pages) : {dlg.FileName}");
            Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, $"Export impossible :\n\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class Row
    {
        public BrowserHistoryService.HistoryEntry Entry { get; }
        public string Title => Entry.Title;
        public string Url => Entry.Url;
        public string Domain => Entry.Domain;
        public string Source { get; }
        public string Browser { get; }
        public string DateText { get; }
        public string TimeText { get; }
        public string PathText { get; }
        public string VisitsText => Entry.Visits <= 1 ? "1 visite" : $"{Entry.Visits:N0} visites";

        public Row(BrowserHistoryService.HistoryEntry e)
        {
            Entry = e;
            Source = e.Profile == "Profil principal" ? e.Browser : $"{e.Browser} — {e.Profile}";
            Browser = Source;
            var day = e.LastVisit.Date;
            DateText = day == DateTime.Today ? "Aujourd'hui" : day == DateTime.Today.AddDays(-1) ? "Hier" : e.LastVisit.ToString("dd/MM/yyyy");
            TimeText = e.LastVisit.ToString("HH:mm");
            PathText = Uri.TryCreate(e.Url, UriKind.Absolute, out var u) ? u.PathAndQuery is "/" ? "" : u.PathAndQuery : e.Url;
        }
    }
}
