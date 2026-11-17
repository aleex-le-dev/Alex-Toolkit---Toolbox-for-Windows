using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Licence Windows : état d'activation, édition, clés BIOS / installée / de secours, copie et export.</summary>
public partial class WindowsKeyDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private LicenseService.LicenseInfo? _info;

    public WindowsKeyDialog(Action<string> onOutput, CancellationToken ct = default)
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
            var info = await LicenseService.GetAsync(_ct);
            _info = info;

            bool ok = info.IsActivated;
            StatusBadge.Background = ThemeManager.GetBrush(ok ? "SuccessBg" : "WarningBg");
            StatusGlyph.Text = ok ? "" : "";
            StatusGlyph.Foreground = ThemeManager.GetBrush(ok ? "SuccessText" : "WarningText");
            TxtActivation.Text = info.StatusText;
            TxtEdition.Text = $"{info.Edition}{(info.Version.Length > 0 ? " " + info.Version : "")} — build {info.Build}";
            TxtChannel.Text = $"Type de licence : {info.ChannelText}";

            SetKey(TxtOemKey, BtnCopyOem, info.OemKey, "Aucune clé gravée dans la carte mère (PC monté, licence achetée à part ou licence numérique).");

            string? installedHint = null;
            if (info.InstalledKeyIsGeneric)
                installedHint = "Clé générique de Microsoft : l'activation repose sur une licence numérique liée au matériel ou au compte Microsoft (normal après une mise à niveau gratuite).";
            else if (info.ChannelText.StartsWith("Licence en volume"))
                installedHint = "Licence d'entreprise : la clé est gérée par l'organisation (KMS / MAK).";
            if (installedHint != null) TxtInstalledHint.Text = installedHint;
            SetKey(TxtInstalledKey, BtnCopyInstalled, info.InstalledKey, "Clé illisible (les 5 derniers caractères actifs sont " + (info.PartialKey ?? "inconnus") + ").");

            if (info.BackupKey != null && !string.Equals(info.BackupKey, info.InstalledKey, StringComparison.OrdinalIgnoreCase))
            {
                CardBackup.Visibility = Visibility.Visible;
                TxtBackupKey.Text = info.BackupKey;
            }

            BtnExport.IsEnabled = true;
            TxtStatus.Text = info.PartialKey != null ? $"Clé active se terminant par {info.PartialKey}." : "";
            _onOutput($"[OK] Licence Windows : {info.StatusText} — {info.Edition} — clé BIOS {(info.OemKey != null ? "présente" : "absente")}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtActivation.Text = "Lecture de la licence impossible";
            TxtEdition.Text = ex.Message;
            TxtOemKey.Text = TxtInstalledKey.Text = "—";
        }
    }

    private void SetKey(System.Windows.Controls.TextBox box, System.Windows.Controls.Button copy, string? key, string missing)
    {
        if (key != null)
        {
            box.Text = key;
            box.Foreground = ThemeManager.GetBrush("TextPrimary");
            box.FontSize = 15;
            copy.IsEnabled = true;
        }
        else
        {
            box.Text = missing;
            box.Foreground = ThemeManager.GetBrush("TextMuted");
            box.FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMain");
            box.FontSize = 12;
            box.TextWrapping = TextWrapping.Wrap;
            copy.IsEnabled = false;
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        string? key = (((FrameworkElement)sender).Tag as string) switch
        {
            "Oem" => _info.OemKey,
            "Installed" => _info.InstalledKey,
            "Backup" => _info.BackupKey,
            _ => null,
        };
        if (key == null) return;
        try { Clipboard.SetText(key); TxtStatus.Text = "Clé copiée dans le presse-papiers."; }
        catch { TxtStatus.Text = "Presse-papiers indisponible, réessayez."; }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Enregistrer la licence Windows",
            Filter = "Fichier texte (*.txt)|*.txt",
            FileName = $"Licence_Windows_{Environment.MachineName}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            LicenseService.ExportText(_info, dlg.FileName);
            TxtStatus.Text = $"Exporté : {dlg.FileName}";
            _onOutput($"[OK] Licence Windows exportée : {dlg.FileName}");
            Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, $"Export impossible :\n\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnActivation_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:activation") { UseShellExecute = true }); } catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
