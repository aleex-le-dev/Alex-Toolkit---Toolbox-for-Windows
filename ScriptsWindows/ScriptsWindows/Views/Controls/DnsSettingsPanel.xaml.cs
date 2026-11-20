using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Onglet DNS du Centre Internet (DnsService) : DNS réels par connexion (IPv4 + IPv6, manuel / automatique), test, application vérifiée avec retour arrière.</summary>
public partial class DnsSettingsPanel : UserControl
{
    private Action<string> _onOutput = _ => { };
    private CancellationToken _ct;
    private List<DnsAdapter> _adapters = new();
    private Dictionary<string, int> _latency = new();
    private bool _loaded, _busy;

    /// <summary>Déclenché après un changement de DNS (le diagnostic de l'onglet 1 peut se rafraîchir).</summary>
    public event Action? Changed;

    public DnsSettingsPanel()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue && !_loaded) { _loaded = true; await LoadAsync(); } };
    }

    public void Init(Action<string> onOutput, CancellationToken ct) { _onOutput = onOutput ?? (_ => { }); _ct = ct; }

    private static TextBlock Txt(string text, double size = 12, string brush = "TextSecondary", bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = ThemeManager.GetBrush(brush), TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    /// <summary>Cartes modifiées : les cartes physiques actives (Wi-Fi, Ethernet), sinon toutes.</summary>
    private List<DnsAdapter> Targets => _adapters.Any(a => a.Hardware) ? _adapters.Where(a => a.Hardware).ToList() : _adapters;

    public async Task LoadAsync()
    {
        TxtStatus.Text = "Lecture des connexions…"; TxtStatus.Foreground = ThemeManager.GetBrush("TextSecondary");
        try { _adapters = await DnsService.GetAdaptersAsync(_ct); }
        catch (Exception ex) { TxtStatus.Text = $"Lecture impossible : {ex.Message}"; return; }
        RenderAll();
        if (!_busy) TxtStatus.Text = _adapters.Count == 0 ? "Aucune connexion active." : "";
    }

    internal void RenderAll()
    {
        PanelAdapters.Children.Clear();
        foreach (var a in _adapters) PanelAdapters.Children.Add(AdapterRow(a));
        if (_adapters.Count == 0) PanelAdapters.Children.Add(Txt("Aucune connexion réseau active.", 12.5));

        var t = Targets;
        TxtTarget.Text = t.Count == 0 ? "Aucune connexion à modifier." :
            $"Appliqué à : {string.Join(", ", t.Select(a => $"{a.Name} ({a.Kind})"))}. IPv4 et IPv6. Le serveur est testé avant, et Internet vérifié après : sinon la configuration précédente est remise.";

        // Préréglage actuellement en place sur toutes les cartes visées
        string? current = t.Count > 0 && t.All(a => a.Static4) ? t.Select(a => DnsService.FindPreset(a.Dns4.FirstOrDefault() ?? "")?.Id).Distinct().SingleOrDefault() : null;
        PanelPresets.Children.Clear();
        foreach (var p in DnsService.Presets)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(Txt($"{p.Name}  ·  {string.Join(" / ", p.V4)}", 13, "TextPrimary", true));
            info.Children.Add(Txt(p.Description, 11.5));
            g.Children.Add(info);
            UIElement right;
            if (p.Id == current) right = Txt("Actif", 12, "SuccessText", true);
            else
            {
                var b = new Button { Content = "Appliquer", IsEnabled = t.Count > 0, VerticalAlignment = VerticalAlignment.Center };
                b.SetResourceReference(StyleProperty, "ActionButton");
                b.Click += async (_, _) => await ApplyAsync(p.Name, p.V4, p.V6);
                right = b;
            }
            if (right is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1); g.Children.Add(right);
            PanelPresets.Children.Add(new Border { Child = g, Background = ThemeManager.GetBrush("BgDeep"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8) });
        }
        BtnAuto.IsEnabled = t.Any(a => a.IsStatic);
        BtnCustom.IsEnabled = t.Count > 0;
    }

    private string Servers(DnsAdapter a, string[] list)
    {
        if (list.Length == 0) return "aucun";
        return string.Join(",  ", list.Select(s =>
        {
            string lat = _latency.TryGetValue(s, out int ms) ? (ms < 0 ? " — ne répond pas" : $" — {ms} ms") : "";
            return $"{s} ({DnsService.ServerName(s, a.Gateway)}{lat})";
        }));
    }

    private Border AdapterRow(DnsAdapter a)
    {
        var sp = new StackPanel();
        sp.Children.Add(Txt(a.Name + (a.Name.Equals(a.Kind, StringComparison.OrdinalIgnoreCase) ? "" : $"  —  {a.Kind}") + (a.Ipv4.Length > 0 ? $"  ·  IP {a.Ipv4}" : ""), 13, "TextPrimary", true));
        sp.Children.Add(Txt(a.IsStatic ? "DNS choisis manuellement" + (a.Static4 && !a.Static6 && a.HasIpv6 && a.Dns6.Length > 0 ? " en IPv4 seulement : en IPv6, ceux de la box restent utilisés" : "")
                                       : "DNS automatiques (fournis par la box ou le routeur)",
            11.5, a.Static4 && !a.Static6 && a.HasIpv6 && a.Dns6.Length > 0 ? "WarningText" : "TextSecondary"));
        sp.Children.Add(Txt("IPv4 : " + Servers(a, a.Dns4), 12, "TextPrimary"));
        if (a.HasIpv6) sp.Children.Add(Txt("IPv6 : " + Servers(a, a.Dns6), 12, "TextPrimary"));
        if (_latency.Count > 0 && a.Dns4.Concat(a.Dns6).Any() && a.Dns4.Concat(a.Dns6).All(s => _latency.GetValueOrDefault(s, 0) < 0))
            sp.Children.Add(Txt("Aucun DNS de cette connexion ne répond : les sites ne s'ouvriront pas. Essayez un autre serveur ou « Revenir en automatique ».", 11.5, "DangerText", true));
        return new Border { Child = sp, Padding = new Thickness(0, 8, 0, 8), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private async Task ApplyAsync(string label, string[] v4, string[] v6)
    {
        if (_busy) return;
        _busy = true; IsEnabled = false;
        TxtStatus.Text = v4.Length == 0 ? "Retour aux DNS automatiques…" : $"Test de {v4[0]} puis application de {label}…";
        _onOutput(v4.Length == 0 ? "🌐 [DNS] Retour aux DNS automatiques" : $"🌐 [DNS] {label} : {string.Join(", ", v4.Concat(v6))}");
        try
        {
            var (ok, msg) = await DnsService.ApplyAsync(Targets, v4, v6, _ct);
            TxtStatus.Text = (ok ? $"{label} : " : $"{label} non appliqué : ") + msg + ".";
            TxtStatus.Foreground = ThemeManager.GetBrush(ok ? "SuccessText" : "DangerText");
            _onOutput((ok ? "[OK] " : "❌ [ERREUR] ") + $"DNS {label} : {msg}");
            _latency.Clear();
            if (ok) Changed?.Invoke();
        }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; TxtStatus.Foreground = ThemeManager.GetBrush("DangerText"); }
        finally
        {
            _busy = false; IsEnabled = true;
            string keep = TxtStatus.Text; var brush = TxtStatus.Foreground;
            await LoadAsync();
            TxtStatus.Text = keep; TxtStatus.Foreground = brush;
        }
    }

    private async void BtnCustom_Click(object sender, RoutedEventArgs e)
    {
        var parts = TxtCustom.Text.Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var parsed = parts.Select(DnsService.ParseServer).ToList();
        if (parts.Length == 0 || parts.Length > 4 || parsed.Any(p => p == null))
        {
            TxtStatus.Text = "Adresses invalides : 1 à 4 adresses IPv4 (ex. 94.140.14.14) ou IPv6, séparées par des espaces.";
            TxtStatus.Foreground = ThemeManager.GetBrush("DangerText");
            return;
        }
        var v4 = parsed.Where(p => !p!.Contains(':')).Cast<string>().ToArray();
        var v6 = parsed.Where(p => p!.Contains(':')).Cast<string>().ToArray();
        if (v4.Length == 0) { TxtStatus.Text = "Indiquez au moins une adresse IPv4."; TxtStatus.Foreground = ThemeManager.GetBrush("DangerText"); return; }
        await ApplyAsync("Serveurs personnalisés", v4, v6);
    }

    private async void BtnAuto_Click(object sender, RoutedEventArgs e) => await ApplyAsync("Automatique", Array.Empty<string>(), Array.Empty<string>());

    private async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; BtnTest.IsEnabled = false; TxtStatus.Foreground = ThemeManager.GetBrush("TextSecondary");
        TxtStatus.Text = "Test de chaque serveur DNS…";
        try
        {
            _latency = await DnsService.TestServersAsync(_adapters.SelectMany(a => a.Dns4.Concat(a.Dns6)), _ct);
            RenderAll();
            int dead = _latency.Count(kv => kv.Value < 0);
            TxtStatus.Text = dead == 0 ? $"{_latency.Count} serveur(s) testé(s) : tous répondent." : $"{dead} serveur(s) sur {_latency.Count} ne répondent pas.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Test impossible : {ex.Message}"; }
        finally { _busy = false; BtnTest.IsEnabled = true; }
    }

    private async void BtnFlush_Click(object sender, RoutedEventArgs e)
    {
        await DnsService.FlushAsync(_ct);
        TxtStatus.Foreground = ThemeManager.GetBrush("TextSecondary");
        TxtStatus.Text = "Cache DNS vidé.";
        _onOutput("[OK] Cache DNS vidé.");
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) { _latency.Clear(); await LoadAsync(); }
}
