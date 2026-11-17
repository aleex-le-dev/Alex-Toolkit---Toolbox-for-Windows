using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ScriptsWindows.Core;

/// <summary>
/// Boîte de message aux couleurs de l'application (remplace <see cref="System.Windows.MessageBox"/>).
/// Mêmes signatures et mêmes énumérations WPF : <c>AppMessageBox.Show(texte, titre, MessageBoxButton.YesNo, MessageBoxImage.Question)</c>.
/// Thème courant (jetons), icône Segoe Fluent colorée selon la gravité, boutons globaux,
/// Entrée = bouton principal, Échap = annuler / non. Repli sur la boîte Windows si le thème n'est pas disponible
/// (ex. erreur au tout début du démarrage).
/// </summary>
public static class AppMessageBox
{
    public static MessageBoxResult Show(string text)
        => Show(null, text, string.Empty, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string text, string caption)
        => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button)
        => Show(null, text, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        => Show(null, text, caption, button, icon);

    public static MessageBoxResult Show(Window? owner, string text)
        => Show(owner, text, string.Empty, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string text, string caption)
        => Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button)
        => Show(owner, text, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        var app = Application.Current;
        if (app == null)
            return MessageBox.Show(text, caption, button, icon);

        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(owner, text, caption, button, icon));

        try
        {
            if (app.TryFindResource("BgCard") is not Brush)
                throw new InvalidOperationException("Thème non chargé");
            return ShowThemed(owner, text, caption, button, icon);
        }
        catch
        {
            return owner != null
                ? MessageBox.Show(owner, text, caption, button, icon)
                : MessageBox.Show(text, caption, button, icon);
        }
    }

    // ─── Construction de la fenêtre ─────────────────────────────────────

    private static MessageBoxResult ShowThemed(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        owner ??= FindOwner();

        // Le titre de la fenêtre contient souvent le suffixe « — Alex Toolkit » : inutile dans l'en-tête
        string title = caption.Replace(" — Alex Toolkit", "").Replace(" - Alex Toolkit", "").Trim();
        if (title.Length == 0) title = "Alex Toolkit";

        var (glyph, fg, bg) = icon switch
        {
            MessageBoxImage.Error    => ("", "DangerText",  "DangerBg"),   // ErrorBadge
            MessageBoxImage.Warning  => ("", "WarningText", "WarningBg"),  // Warning
            MessageBoxImage.Question => ("", "AccentText",  "AccentDim"),  // Unknown (point d'interrogation)
            MessageBoxImage.Information => ("", "AccentText", "AccentDim"), // Info
            _                        => ("", "AccentText",  "AccentDim"),
        };

        var result = button switch
        {
            MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => MessageBoxResult.No,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK,
        };
        if (button == MessageBoxButton.YesNoCancel) result = MessageBoxResult.Cancel;

        var window = new Window
        {
            Title                 = caption.Length > 0 ? caption : "Alex Toolkit",
            WindowStyle           = WindowStyle.None,
            AllowsTransparency    = true,
            Background            = Brushes.Transparent,
            ResizeMode            = ResizeMode.NoResize,
            SizeToContent         = SizeToContent.WidthAndHeight,
            ShowInTaskbar         = owner == null,
            Topmost               = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner                 = owner,
            FontFamily            = (FontFamily)Application.Current.FindResource("FontMain"),
            UseLayoutRounding     = true,
        };

        // ── En-tête : pastille d'icône + titre ──
        var iconBadge = new Border
        {
            Width = 36, Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = ThemeManager.GetBrush(bg),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("FontIcon"),
                FontSize = 17,
                Foreground = ThemeManager.GetBrush(fg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.FindResource("FontDisplay"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.GetBrush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var messageBlock = new TextBlock
        {
            Text = text,
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = ThemeManager.GetBrush("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        };

        // Les longs messages (listes d'applications, chemins…) défilent au lieu d'agrandir la fenêtre à l'infini
        var messageScroll = new ScrollViewer
        {
            Content = messageBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
            Focusable = false,
        };

        var textColumn = new StackPanel { Margin = new Thickness(16, 6, 0, 0) };
        textColumn.Children.Add(titleBlock);
        textColumn.Children.Add(messageScroll);

        var body = new Grid { Margin = new Thickness(24, 22, 24, 22), MinWidth = 380, MaxWidth = 520 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(textColumn, 1);
        body.Children.Add(iconBadge);
        body.Children.Add(textColumn);

        // ── Pied : boutons à droite ──
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Button AddButton(string label, MessageBoxResult value, bool primary, bool isCancel)
        {
            string style = primary
                ? (icon == MessageBoxImage.Warning || icon == MessageBoxImage.Error) && button != MessageBoxButton.OK
                    ? "WarningActionButton"
                    : "PrimaryActionButton"
                : "SecondaryActionButton";

            var b = new Button
            {
                Content   = label,
                MinWidth  = 96,
                Margin    = new Thickness(8, 0, 0, 0),
                IsDefault = primary,
                IsCancel  = isCancel,
            };
            if (Application.Current.TryFindResource(style) is Style s) b.Style = s;
            b.Click += (_, _) => { result = value; window.Close(); };
            buttons.Children.Add(b);
            return b;
        }

        Button first = button switch
        {
            MessageBoxButton.YesNo => AddSecondaryThenPrimary("Non", MessageBoxResult.No, "Oui", MessageBoxResult.Yes),
            MessageBoxButton.YesNoCancel => AddYesNoCancel(),
            MessageBoxButton.OKCancel => AddSecondaryThenPrimary("Annuler", MessageBoxResult.Cancel, "OK", MessageBoxResult.OK),
            _ => AddButton("OK", MessageBoxResult.OK, primary: true, isCancel: true),
        };

        Button AddSecondaryThenPrimary(string secLabel, MessageBoxResult secValue, string priLabel, MessageBoxResult priValue)
        {
            AddButton(secLabel, secValue, primary: false, isCancel: true);
            return AddButton(priLabel, priValue, primary: true, isCancel: false);
        }

        Button AddYesNoCancel()
        {
            AddButton("Annuler", MessageBoxResult.Cancel, primary: false, isCancel: true);
            AddButton("Non", MessageBoxResult.No, primary: false, isCancel: false);
            return AddButton("Oui", MessageBoxResult.Yes, primary: true, isCancel: false);
        }

        var footer = new Border
        {
            Background = ThemeManager.GetBrush("BgSidebar"),
            BorderBrush = ThemeManager.GetBrush("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Padding = new Thickness(24, 14, 24, 14),
            Child = buttons,
        };

        var layout = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(body);

        // ── Carte arrondie avec ombre (marge transparente pour laisser l'ombre visible) ──
        var card = new Border
        {
            Background = ThemeManager.GetBrush("BgCard"),
            BorderBrush = ThemeManager.GetBrush("BorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(18),
            Child = layout,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 6, Direction = 270,
                Opacity = ThemeManager.CurrentTheme.IsDark ? 0.55 : 0.18,
                Color = Colors.Black,
            },
        };

        window.Content = card;

        // Déplacement de la fenêtre en la saisissant n'importe où (hors boutons)
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                try { window.DragMove(); } catch (InvalidOperationException) { }
        };

        // Ctrl+C copie le message (comme la boîte Windows)
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                try { Clipboard.SetText($"{title}\n\n{text}"); } catch { }
                e.Handled = true;
            }
        };

        window.Loaded += (_, _) => first.Focus();
        if (icon is MessageBoxImage.Error or MessageBoxImage.Warning)
            window.Loaded += (_, _) => System.Media.SystemSounds.Exclamation.Play();

        window.ShowDialog();
        return result;
    }

    /// <summary>Fenêtre active de l'application (dialogue d'outil ouvert) sinon fenêtre principale.</summary>
    private static Window? FindOwner()
    {
        var app = Application.Current;
        if (app == null) return null;
        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        if (active != null) return active;
        return app.MainWindow is { IsLoaded: true, IsVisible: true } main ? main : null;
    }
}
