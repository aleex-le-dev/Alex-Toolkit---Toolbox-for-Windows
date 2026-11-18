using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Onglet « Écran » : écrans branchés et test plein écran sur celui choisi.</summary>
public partial class ScreenTestView : UserControl
{
    private readonly Action<string> _onOutput;
    private bool _booted;

    public ScreenTestView(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        Loaded += (_, _) => { if (_booted) return; _booted = true; LoadDisplays(); };
    }

    private void LoadDisplays()
    {
        PanelDisplays.Children.Clear();
        List<HardwareTestService.DisplayInfo> displays;
        try { displays = HardwareTestService.GetDisplays(); }
        catch (Exception ex) { displays = new(); _onOutput($"[!] Écrans : {ex.Message}"); }

        if (displays.Count == 0)
        {
            PanelDisplays.Children.Add(new TextBlock { Text = "Aucun écran détecté : le test s'ouvrira sur l'écran principal.", Foreground = ThemeManager.GetBrush("TextSecondary") });
            displays.Add(new HardwareTestService.DisplayInfo("", "Écran principal", "", 0, 0, 0, 0, 0, true));
        }

        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            var row = new DockPanel { Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0) };
            var btn = new Button { Style = (Style)FindResource("PrimaryActionButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            btn.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("FontIcon"), FontSize = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Tester cet écran", VerticalAlignment = VerticalAlignment.Center },
                }
            };
            btn.Click += (_, _) =>
            {
                var w = new ScreenTestWindow(d.X, d.Y) { Owner = Window.GetWindow(this) };
                w.Show();
                _onOutput($"[i] Test d'écran lancé sur « {d.Monitor} »{(d.Width > 0 ? $" ({d.Width} × {d.Height})" : "")}.");
            };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);

            var icon = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0),
                Background = ThemeManager.GetBrush("BgInput"),
                Child = new TextBlock { Text = "", FontFamily = (System.Windows.Media.FontFamily)FindResource("FontIcon"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.GetBrush("TextSecondary") }
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = $"Écran {i + 1} · {d.Monitor}", FontWeight = FontWeights.SemiBold, FontSize = 13 });
            if (d.Primary)
                title.Children.Add(new Border
                {
                    Background = ThemeManager.GetBrush("AccentDim"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock { Text = "Principal", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.GetBrush("AccentText") }
                });
            info.Children.Add(title);
            if (d.Width > 0)
                info.Children.Add(new TextBlock
                {
                    Text = $"{d.Width} × {d.Height} pixels · {d.Hz} Hz" + (d.Adapter.Length > 0 ? $" · {d.Adapter}" : ""),
                    FontSize = 12, Foreground = ThemeManager.GetBrush("TextMuted"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
                });
            row.Children.Add(info);
            PanelDisplays.Children.Add(row);
        }
    }
}
