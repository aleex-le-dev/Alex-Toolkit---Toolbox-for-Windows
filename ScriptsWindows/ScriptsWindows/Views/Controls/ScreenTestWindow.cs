using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Test d'écran plein écran : couleurs unies (pixels morts / bloqués, fuites de lumière), dégradés (bandes) et damier fin (netteté).
/// Clic, Espace ou → : suivant ; ← : précédent ; Échap : quitter. S'ouvre sur l'écran indiqué (coordonnées physiques).
/// Les couleurs de mire sont le contenu du test, pas des couleurs d'interface.
/// </summary>
public sealed class ScreenTestWindow : Window
{
    private readonly (string Name, Brush Brush)[] _patterns;
    private readonly Border _surface = new();
    private readonly Border _hint;
    private readonly TextBlock _hintText;
    private readonly int _x, _y;
    private int _index;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public ScreenTestWindow(int physicalX, int physicalY)
    {
        _x = physicalX;
        _y = physicalY;
        _patterns = new (string, Brush)[]
        {
            ("Noir : repérez les pixels allumés et les fuites de lumière sur les bords", Brushes.Black),
            ("Blanc : repérez les pixels morts (points noirs) et les taches", Brushes.White),
            ("Rouge", new SolidColorBrush(Colors.Red)),
            ("Vert", new SolidColorBrush(Colors.Lime)),
            ("Bleu", new SolidColorBrush(Colors.Blue)),
            ("Gris moyen : vérifiez l'uniformité de la luminosité", new SolidColorBrush(Color.FromRgb(128, 128, 128))),
            ("Dégradé : il doit être fluide, sans bandes marquées", new LinearGradientBrush(Colors.Black, Colors.White, 0)),
            ("Dégradé de couleurs : transitions fluides attendues", Rainbow()),
            ("Damier fin : il doit paraître gris uni et net, sans moiré", Checker(1)),
        };

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; Width = 200; Height = 200;

        _hintText = new TextBlock { Foreground = Brushes.White, FontSize = 16, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 12, 18, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(40, 0, 40, 60),
            MaxWidth = 900,
            Child = _hintText,
            IsHitTestVisible = false,
        };
        RenderOptions.SetEdgeMode(_surface, EdgeMode.Aliased);   // damier net, sans lissage
        _surface.SnapsToDevicePixels = true;
        Content = new Grid { Children = { _surface, _hint } };

        SourceInitialized += (_, _) =>
        {
            // Place la fenêtre sur l'écran voulu (pixels physiques), puis l'agrandit : elle remplit alors cet écran.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, _x + 50, _y + 50, 300, 300, 0x0004 | 0x0010);   // SWP_NOZORDER | SWP_NOACTIVATE
            WindowState = WindowState.Maximized;
        };
        Loaded += (_, _) =>
        {
            // Damier recalculé à l'échelle de l'écran testé : 1 case = 1 pixel physique
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _patterns[^1] = (_patterns[^1].Name, Checker(scale));
            Activate(); Show(0, "Clic, Espace ou → : couleur suivante   ·   ← : précédente   ·   Échap : quitter");
        };
        MouseLeftButtonUp += (_, _) => Next(1);
        MouseRightButtonUp += (_, _) => Next(-1);
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Left: case Key.Up: case Key.PageUp: case Key.Back: Next(-1); break;
                default: Next(1); break;
            }
            e.Handled = true;
        };
    }

    private void Next(int step)
    {
        int i = _index + step;
        if (i >= _patterns.Length) { Close(); return; }
        Show(Math.Max(0, i), null);
    }

    private void Show(int index, string? extra)
    {
        _index = index;
        _surface.Background = _patterns[index].Brush;
        _hintText.Text = $"{index + 1}/{_patterns.Length} · {_patterns[index].Name}" + (extra != null ? "\n" + extra : "");
        _hint.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(extra != null ? 4 : 2.2))),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(extra != null ? 4.6 : 2.8))),
            }
        });
    }

    private static Brush Rainbow()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var colors = new[] { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red };
        for (int i = 0; i < colors.Length; i++) b.GradientStops.Add(new GradientStop(colors[i], i / (double)(colors.Length - 1)));
        return b;
    }

    /// <summary>Damier d'1 pixel physique : uni et net si l'écran est à sa résolution native.</summary>
    private static Brush Checker(double scale)
    {
        double px = 1 / scale;   // un pixel physique en unités WPF
        var group = new GeometryGroup();
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, px, px)));
        group.Children.Add(new RectangleGeometry(new Rect(px, px, px, px)));
        return new DrawingBrush(new GeometryDrawing(Brushes.White, null, group))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 2 * px, 2 * px),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
    }
}
