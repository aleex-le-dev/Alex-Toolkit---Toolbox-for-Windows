using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Onglet « Clavier et souris » : clavier 105 touches dessiné à partir des codes physiques (scancodes),
/// libellés lus dans la disposition active (AZERTY, QWERTY…). Les messages clavier sont interceptés au niveau
/// de la fenêtre : les touches sont identifiées exactement (Entrée ≠ Entrée du pavé) et n'agissent pas
/// sur la fenêtre pendant le test (Alt, F10, Tab, Espace, Alt+F4).
/// </summary>
public partial class KeyboardTestView : UserControl
{
    // Identifiant d'une touche : scancode, + 0xE000 pour les touches étendues ; quelques cas repérés par leur code virtuel.
    private const int KeyPause = 0x1E045, KeyNumLock = 0xE045, KeyPrintScreen = 0xE037;
    private const double Unit = 40, Gap = 3;

    private sealed record KeyDef(int Id, double X, double Y, double W, string? Label, double H = 1);

    private readonly Dictionary<int, (Border Box, TextBlock Text, string Label)> _keys = new();
    private readonly HashSet<int> _tested = new(), _down = new();
    private readonly Dictionary<string, Border> _mouseChips = new();
    private readonly HashSet<string> _mouseTested = new();
    private HwndSource? _source;
    private bool _built;

    public KeyboardTestView(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_built) { _built = true; BuildKeyboard(); BuildMouseChips(); UpdateCount(); }
            Attach();
        };
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible) Attach(); else Detach(); };
    }

    // ── Interception clavier (niveau fenêtre) ────────────────────────────────

    private void Attach()
    {
        if (_source != null || !IsVisible) return;
        var w = Window.GetWindow(this);
        if (w == null) return;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
        _source?.AddHook(Hook);
        Keyboard.ClearFocus();   // aucun bouton ne doit réagir à Espace / Entrée pendant le test
    }

    private void Detach()
    {
        _source?.RemoveHook(Hook);
        _source = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        if (msg is not (WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)) return IntPtr.Zero;

        int vk = wParam.ToInt32();
        long lp = lParam.ToInt64();
        int scan = (int)((lp >> 16) & 0xFF);
        bool extended = ((lp >> 24) & 1) != 0;
        int id = vk switch
        {
            0x13 => KeyPause,
            0x90 => KeyNumLock,
            0x2C => KeyPrintScreen,
            _ => (extended ? 0xE000 : 0) | scan,
        };
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;

        if (down || id == KeyPrintScreen)   // Impr. écran n'envoie que le relâchement
        {
            _tested.Add(id);
            if (down) _down.Add(id);
            string name = _keys.TryGetValue(id, out var k) ? k.Label.Replace('\n', ' ') : KeyInterop.KeyFromVirtualKey(vk).ToString();
            TxtLast.Text = _keys.ContainsKey(id)
                ? $"Dernière touche : {name}"
                : $"Dernière touche : {name} (hors schéma, code {vk:X2})";
            UpdateCount();
        }
        if (!down) _down.Remove(id);
        Paint(id);
        handled = true;   // la touche n'agit pas sur la fenêtre pendant le test
        return IntPtr.Zero;
    }

    // ── Clavier ──────────────────────────────────────────────────────────────

    private void BuildKeyboard()
    {
        var defs = new List<KeyDef>
        {
            // Ligne des fonctions
            new(0x01, 0, 0, 1, "Échap"),
            new(0x3B, 2, 0, 1, "F1"), new(0x3C, 3, 0, 1, "F2"), new(0x3D, 4, 0, 1, "F3"), new(0x3E, 5, 0, 1, "F4"),
            new(0x3F, 6.5, 0, 1, "F5"), new(0x40, 7.5, 0, 1, "F6"), new(0x41, 8.5, 0, 1, "F7"), new(0x42, 9.5, 0, 1, "F8"),
            new(0x43, 11, 0, 1, "F9"), new(0x44, 12, 0, 1, "F10"), new(0x57, 13, 0, 1, "F11"), new(0x58, 14, 0, 1, "F12"),
            new(KeyPrintScreen, 15.5, 0, 1, "Impr\nécran"), new(0x46, 16.5, 0, 1, "Arrêt\ndéfil"), new(KeyPause, 17.5, 0, 1, "Pause"),
        };

        // Ligne des chiffres
        defs.Add(new(0x29, 0, 1.5, 1, null));
        for (int i = 0; i < 12; i++) defs.Add(new(0x02 + i, 1 + i, 1.5, 1, null));
        defs.Add(new(0x0E, 13, 1.5, 2, "Retour"));
        // Ligne A / Q
        defs.Add(new(0x0F, 0, 2.5, 1.5, "Tab"));
        for (int i = 0; i < 12; i++) defs.Add(new(0x10 + i, 1.5 + i, 2.5, 1, null));
        defs.Add(new(0x1C, 13.75, 2.5, 1.25, "Entrée", 2));
        // Ligne Q / A
        defs.Add(new(0x3A, 0, 3.5, 1.75, "Verr.\nmaj"));
        for (int i = 0; i < 11; i++) defs.Add(new(0x1E + i, 1.75 + i, 3.5, 1, null));
        defs.Add(new(0x2B, 12.75, 3.5, 1, null));
        // Ligne W / Z
        defs.Add(new(0x2A, 0, 4.5, 1.25, "Maj"));
        defs.Add(new(0x56, 1.25, 4.5, 1, null));
        for (int i = 0; i < 10; i++) defs.Add(new(0x2C + i, 2.25 + i, 4.5, 1, null));
        defs.Add(new(0x36, 12.25, 4.5, 2.75, "Maj"));
        // Ligne espace
        defs.AddRange(new KeyDef[]
        {
            new(0x1D, 0, 5.5, 1.25, "Ctrl"), new(0xE05B, 1.25, 5.5, 1.25, "Win"), new(0x38, 2.5, 5.5, 1.25, "Alt"),
            new(0x39, 3.75, 5.5, 6.25, "Espace"), new(0xE038, 10, 5.5, 1.25, "Alt Gr"), new(0xE05C, 11.25, 5.5, 1.25, "Win"),
            new(0xE05D, 12.5, 5.5, 1.25, "Menu"), new(0xE01D, 13.75, 5.5, 1.25, "Ctrl"),
            // Bloc de navigation
            new(0xE052, 15.5, 1.5, 1, "Inser"), new(0xE047, 16.5, 1.5, 1, "Début"), new(0xE049, 17.5, 1.5, 1, "Pg\npréc"),
            new(0xE053, 15.5, 2.5, 1, "Suppr"), new(0xE04F, 16.5, 2.5, 1, "Fin"), new(0xE051, 17.5, 2.5, 1, "Pg\nsuiv"),
            new(0xE048, 16.5, 4.5, 1, "↑"), new(0xE04B, 15.5, 5.5, 1, "←"), new(0xE050, 16.5, 5.5, 1, "↓"), new(0xE04D, 17.5, 5.5, 1, "→"),
            // Pavé numérique
            new(KeyNumLock, 19, 1.5, 1, "Verr\nnum"), new(0xE035, 20, 1.5, 1, "/"), new(0x37, 21, 1.5, 1, "*"), new(0x4A, 22, 1.5, 1, "-"),
            new(0x47, 19, 2.5, 1, "7"), new(0x48, 20, 2.5, 1, "8"), new(0x49, 21, 2.5, 1, "9"), new(0x4E, 22, 2.5, 1, "+", 2),
            new(0x4B, 19, 3.5, 1, "4"), new(0x4C, 20, 3.5, 1, "5"), new(0x4D, 21, 3.5, 1, "6"),
            new(0x4F, 19, 4.5, 1, "1"), new(0x50, 20, 4.5, 1, "2"), new(0x51, 21, 4.5, 1, "3"), new(0xE01C, 22, 4.5, 1, "Entrée", 2),
            new(0x52, 19, 5.5, 2, "0"), new(0x53, 21, 5.5, 1, "."),
        });

        KeyCanvas.Width = 23 * Unit;
        KeyCanvas.Height = 6.5 * Unit;
        foreach (var d in defs)
        {
            string label = d.Label ?? LayoutLabel(d.Id);
            var text = new TextBlock
            {
                Text = label, FontSize = label.Contains('\n') || label.Length > 6 ? 10.5 : 13, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 12, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            var box = new Border { Width = d.W * Unit - Gap, Height = d.H * Unit - Gap, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Child = text };
            Canvas.SetLeft(box, d.X * Unit);
            Canvas.SetTop(box, d.Y * Unit);
            KeyCanvas.Children.Add(box);
            _keys[d.Id] = (box, text, label);
            Paint(d.Id);
        }
    }

    private void Paint(int id)
    {
        if (!_keys.TryGetValue(id, out var k)) return;
        if (_down.Contains(id)) Set("Accent", "Accent", "OnAccent");
        else if (_tested.Contains(id)) Set("SuccessBg", "Success", "SuccessText");
        else Set("BgInput", "Border", "TextPrimary");

        void Set(string bg, string border, string fg)
        {
            k.Box.Background = ThemeManager.GetBrush(bg);
            k.Box.BorderBrush = ThemeManager.GetBrush(border);
            k.Text.Foreground = ThemeManager.GetBrush(fg);
        }
    }

    private void UpdateCount()
    {
        int done = _tested.Count(_keys.ContainsKey);
        TxtCount.Text = $"{done} / {_keys.Count} touches testées";
        TxtCount.Foreground = ThemeManager.GetBrush(done == _keys.Count ? "SuccessText" : "TextPrimary");
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _tested.Clear();
        _down.Clear();
        foreach (var id in _keys.Keys) Paint(id);
        _mouseTested.Clear();
        foreach (var name in _mouseChips.Keys) PaintChip(name);
        TxtLast.Text = "";
        UpdateCount();
        Keyboard.ClearFocus();
    }

    // ── Libellés selon la disposition du clavier ────────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buffer, int size, uint flags, IntPtr hkl);

    /// <summary>Caractère(s) imprimé(s) sur la touche : majuscule seule pour une lettre, sinon « avec Maj » au-dessus de « sans Maj ».</summary>
    private static string LayoutLabel(int scan)
    {
        string normal = Char(scan, false), shifted = Char(scan, true);
        if (normal.Length == 1 && char.IsLetter(normal[0]) && shifted == normal.ToUpperInvariant()) return shifted;
        if (shifted.Length == 0 || shifted == normal) return normal;
        return normal.Length == 0 ? shifted : $"{shifted}\n{normal}";
    }

    private static string Char(int scan, bool shift)
    {
        IntPtr hkl = GetKeyboardLayout(0);
        uint vk = MapVirtualKeyEx((uint)scan, 1, hkl);   // MAPVK_VSC_TO_VK
        if (vk == 0) return "";
        var state = new byte[256];
        if (shift) state[0x10] = 0x80;
        var sb = new StringBuilder(8);
        int n = ToUnicodeEx(vk, (uint)scan, state, sb, sb.Capacity, 0x4, hkl);   // 0x4 : sans modifier l'état des touches mortes
        string s = sb.ToString();
        if (n == 0 || s.Length == 0) return "";
        s = n < 0 ? s[..1] : s[..Math.Min(n, s.Length)];
        return s.Any(char.IsControl) ? "" : s;
    }

    // ── Souris ───────────────────────────────────────────────────────────────

    private void BuildMouseChips()
    {
        foreach (var name in new[] { "Clic gauche", "Clic droit", "Clic molette", "Bouton précédent", "Bouton suivant", "Molette vers le haut", "Molette vers le bas", "Double-clic" })
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold },
            };
            _mouseChips[name] = chip;
            PanelMouse.Children.Add(chip);
            PaintChip(name);
        }
    }

    private void PaintChip(string name)
    {
        var chip = _mouseChips[name];
        bool ok = _mouseTested.Contains(name);
        chip.Background = ThemeManager.GetBrush(ok ? "SuccessBg" : "BgInput");
        chip.BorderBrush = ThemeManager.GetBrush(ok ? "Success" : "Border");
        ((TextBlock)chip.Child).Foreground = ThemeManager.GetBrush(ok ? "SuccessText" : "TextSecondary");
    }

    private void Mark(string name)
    {
        _mouseTested.Add(name);
        PaintChip(name);
    }

    private void MouseZone_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Mark(e.ChangedButton switch
        {
            MouseButton.Left => "Clic gauche",
            MouseButton.Right => "Clic droit",
            MouseButton.Middle => "Clic molette",
            MouseButton.XButton1 => "Bouton précédent",
            _ => "Bouton suivant",
        });
        if (e.ClickCount == 2) Mark("Double-clic");
        e.Handled = true;
    }

    private void MouseZone_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Mark(e.Delta > 0 ? "Molette vers le haut" : "Molette vers le bas");
        e.Handled = true;
    }

    private void MouseZone_RightUp(object sender, MouseButtonEventArgs e) => e.Handled = true;   // pas de menu contextuel
}
