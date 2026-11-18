using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Onglet « Webcam » : caméras reconnues par Windows, blocage de confidentialité, ouverture de l'application Caméra.</summary>
public partial class CameraTestView : UserControl
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private bool _booted;

    public CameraTestView(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => { if (_booted) return; _booted = true; await LoadAsync(); };
    }

    private async Task LoadAsync()
    {
        PanelCamDenied.Visibility = HardwareTestService.IsCapabilityDenied("webcam") ? Visibility.Visible : Visibility.Collapsed;
        PanelCameras.Children.Clear();
        PanelCameras.Children.Add(new TextBlock { Text = "Recherche des caméras…", Foreground = ThemeManager.GetBrush("TextSecondary"), FontSize = 12.5 });
        List<HardwareTestService.CameraInfo> cameras;
        try { cameras = await HardwareTestService.GetCamerasAsync(_ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { cameras = new(); _onOutput($"[!] Webcam : {ex.Message}"); }

        PanelCameras.Children.Clear();
        if (cameras.Count == 0)
        {
            PanelCameras.Children.Add(new TextBlock
            {
                Text = "Aucune webcam détectée. Vérifiez qu'elle est branchée, activée dans le BIOS ou par sa touche de coupure, et que son pilote est installé.",
                Foreground = ThemeManager.GetBrush("WarningText"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            });
            _onOutput("[!] Webcam : aucune caméra détectée.");
            return;
        }

        foreach (var c in cameras)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock
            {
                Text = c.Ok ? "" : "", FontFamily = (System.Windows.Media.FontFamily)FindResource("FontIcon"), FontSize = 14, Margin = new Thickness(0, 1, 10, 0),
                Foreground = ThemeManager.GetBrush(c.Ok ? "SuccessText" : "WarningText"),
            });
            row.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 });
            // Caméra infrarouge (Windows Hello) : reconnaissance faciale, pas d'image dans l'application Caméra
            bool infrared = System.Text.RegularExpressions.Regex.IsMatch(c.Name, @"\bIR\b|infrared|infrarouge", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            row.Children.Add(new TextBlock
            {
                Text = !c.Ok ? "  · problème signalé par Windows (pilote, désactivée)"
                     : infrared ? "  · caméra infrarouge (Windows Hello) : pas d'image dans l'application Caméra"
                     : "  · fonctionne normalement",
                Foreground = ThemeManager.GetBrush(c.Ok ? "TextMuted" : "WarningText"), FontSize = 12,
            });
            PanelCameras.Children.Add(row);
        }
        _onOutput($"[OK] Webcam : {cameras.Count} caméra(s) détectée(s).");
    }

    private void BtnOpenCamera_Click(object sender, RoutedEventArgs e)
    {
        try { CommandRunner.OpenUrl("microsoft.windows.camera:"); }
        catch (Exception ex)
        {
            AppMessageBox.Show(Window.GetWindow(this), $"Impossible d'ouvrir l'application Caméra : {ex.Message}\n\nElle peut être réinstallée depuis le Microsoft Store (« Caméra Windows »).",
                "Webcam", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void BtnPrivacy_Click(object sender, RoutedEventArgs e) => CommandRunner.OpenUrl("ms-settings:privacy-webcam");
}
