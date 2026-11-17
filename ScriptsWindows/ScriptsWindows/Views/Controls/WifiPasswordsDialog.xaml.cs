using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Réseaux Wi-Fi mémorisés : mot de passe masqué / affiché, copie, export texte.</summary>
public partial class WifiPasswordsDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<Row> _rows = new();
    private bool _revealAll;

    public WifiPasswordsDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var profiles = await WifiProfileService.GetProfilesAsync(_ct);
            _rows = profiles.Select(p => new Row(p)).ToList();
            ApplyFilter();
            int withKey = profiles.Count(p => !p.IsOpen);
            TxtStatus.Text = profiles.Count == 0 ? "Aucun réseau Wi-Fi enregistré." : $"{profiles.Count} réseau(x) enregistré(s), dont {withKey} protégé(s) par un mot de passe.";
            BtnExport.IsEnabled = profiles.Count > 0;
            _onOutput($"[OK] Mots de passe Wi-Fi : {profiles.Count} réseau(x) enregistré(s).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ProgLoad.Visibility = Visibility.Collapsed;
            TxtEmpty.Text = $"Lecture impossible : {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        string q = TxtSearch.Text.Trim();
        var list = q.Length == 0 ? _rows : _rows.Where(r => r.Ssid.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ListProfiles.ItemsSource = list;
        PanelEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgLoad.Visibility = Visibility.Collapsed;
        if (list.Count == 0) TxtEmpty.Text = _rows.Count == 0 ? "Aucun réseau Wi-Fi n'est enregistré sur ce PC." : "Aucun réseau ne correspond à la recherche.";
    }

    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void BtnRevealAll_Click(object sender, RoutedEventArgs e)
    {
        _revealAll = !_revealAll;
        foreach (var r in _rows) r.Revealed = _revealAll;
        TxtRevealAll.Text = _revealAll ? "Masquer les mots de passe" : "Afficher les mots de passe";
        TxtRevealGlyph.Text = _revealAll ? "" : "";
    }

    private void BtnReveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Row r) r.Revealed = !r.Revealed;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row r || r.Profile.Password is not { } pwd) return;
        try { Clipboard.SetText(pwd); TxtStatus.Text = $"Mot de passe de « {r.Ssid} » copié dans le presse-papiers."; }
        catch { TxtStatus.Text = "Presse-papiers indisponible, réessayez."; }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Enregistrer les mots de passe Wi-Fi",
            Filter = "Fichier texte (*.txt)|*.txt",
            FileName = $"Wi-Fi_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            WifiProfileService.ExportText(_rows.Select(r => r.Profile), dlg.FileName);
            TxtStatus.Text = $"Exporté : {dlg.FileName}";
            _onOutput($"[OK] Mots de passe Wi-Fi exportés : {dlg.FileName}");
            Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, $"Export impossible :\n\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class Row : INotifyPropertyChanged
    {
        public WifiProfileService.WifiProfile Profile { get; }
        public string Ssid => Profile.Ssid;
        public bool HasPassword => !Profile.IsOpen;
        public string SecurityText => Profile.IsOpen
            ? (Profile.Authentication.Length > 0 ? $"{Profile.Authentication} • sans mot de passe enregistré" : "Réseau ouvert")
            : Profile.Authentication.Length > 0 ? Profile.Authentication : "Protégé";
        public Visibility ConnectedVisibility => Profile.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        public Brush IconBg => ThemeManager.GetBrush(Profile.IsConnected ? "SuccessBg" : "AccentDim");
        public Brush IconFg => ThemeManager.GetBrush(Profile.IsConnected ? "SuccessText" : "AccentText");

        private bool _revealed;
        public bool Revealed
        {
            get => _revealed;
            set
            {
                if (_revealed == value || !HasPassword) return;
                _revealed = value;
                PropertyChanged?.Invoke(this, new(nameof(DisplayPassword)));
                PropertyChanged?.Invoke(this, new(nameof(EyeGlyph)));
                PropertyChanged?.Invoke(this, new(nameof(PasswordFg)));
            }
        }

        public string DisplayPassword => !HasPassword ? "Aucun (ouvert ou entreprise)" : _revealed ? Profile.Password! : "••••••••••";
        public Brush PasswordFg => ThemeManager.GetBrush(!HasPassword ? "TextMuted" : _revealed ? "TextPrimary" : "TextSecondary");
        public string EyeGlyph => _revealed ? "" : "";

        public Row(WifiProfileService.WifiProfile p) => Profile = p;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
