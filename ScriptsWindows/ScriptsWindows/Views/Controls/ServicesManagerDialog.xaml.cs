using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Gestionnaire de services complet (WindowsServicesService) : filtres, éditeur, actions et démarrage protégés.</summary>
public partial class ServicesManagerDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<WinServiceInfo> _all = new();
    private WinServiceInfo? _selected;
    private bool _busy;

    public ServicesManagerDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) => await LoadServicesAsync();
    }

    private static Brush B(string k) => ThemeManager.GetBrush(k);
    private static TextBlock T(string text, double size, string brush, FontWeight? w = null) => new()
    { Text = text, FontSize = size, Foreground = B(brush), FontWeight = w ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    private async Task LoadServicesAsync()
    {
        TxtFeedback.Text = "Lecture des services…";
        try
        {
            string? keep = _selected?.Name;
            _all = await WindowsServicesService.GetAllAsync(_ct);
            int broken = _all.Count(s => s.LooksBroken);
            TxtFeedback.Text = $"{_all.Count} services · {_all.Count(s => s.State == "Running")} en cours · {broken} automatique(s) arrêté(s) · {_all.Count(s => !s.IsMicrosoft)} hors Microsoft";
            if (CmbFilter.SelectedIndex == 0 && broken == 0) CmbFilter.SelectedIndex = 1;
            _selected = keep == null ? null : _all.FirstOrDefault(s => s.Name == keep);
            Render();
        }
        catch (Exception ex) { TxtFeedback.Text = $"Erreur : {ex.Message}"; }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Render(); }

    private void Render()
    {
        string q = TxtSearch.Text.Trim();
        IEnumerable<WinServiceInfo> list = CmbFilter.SelectedIndex switch
        {
            0 => _all.Where(s => s.LooksBroken),
            1 => _all.Where(s => !s.IsMicrosoft),
            2 => _all.Where(s => s.State == "Running"),
            3 => _all.Where(s => s.StartMode == "Disabled"),
            _ => _all
        };
        if (q.Length > 0)
            list = list.Where(s => (s.DisplayName + " " + s.Name + " " + s.Description + " " + s.Company).Contains(q, StringComparison.CurrentCultureIgnoreCase));
        var shown = list.ToList();
        PanelList.Children.Clear();
        foreach (var s in shown) PanelList.Children.Add(BuildRow(s));
        TxtEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtEmpty.Text = CmbFilter.SelectedIndex == 0 && q.Length == 0 ? "Aucun service automatique arrêté : tout tourne normalement." : "Aucun service ne correspond.";
        ShowDetail();
    }

    private Border BuildRow(WinServiceInfo s)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        info.Children.Add(T(s.DisplayName, 12.5, "TextPrimary", FontWeights.SemiBold));
        info.Children.Add(T(s.Name + (s.IsCritical ? "  ·  vital pour Windows" : ""), 11, "TextMuted"));
        g.Children.Add(info);
        var co = T(s.Company.Length > 0 ? s.Company : s.IsMicrosoft ? "Microsoft" : "Éditeur inconnu", 11.5, s.IsMicrosoft ? "TextMuted" : "AccentText");
        Grid.SetColumn(co, 1); g.Children.Add(co);
        var st = T(s.StateLabel, 12, s.State == "Running" ? "SuccessText" : s.LooksBroken ? "DangerText" : "TextMuted", FontWeights.SemiBold);
        Grid.SetColumn(st, 2); g.Children.Add(st);
        var sm = T(s.StartLabel, 11.5, s.StartMode == "Disabled" ? "WarningText" : "TextSecondary");
        Grid.SetColumn(sm, 3); g.Children.Add(sm);
        bool sel = _selected?.Name == s.Name;
        var border = new Border
        {
            Child = g, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = s.Description.Length > 0 ? s.Description : null,
            Background = sel ? B("AccentDim") : Brushes.Transparent, BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1)
        };
        border.MouseLeftButtonUp += (_, _) => { _selected = s; Render(); };
        return border;
    }

    private void ShowDetail()
    {
        if (_selected is not WinServiceInfo s) { PanelDetail.Visibility = Visibility.Collapsed; return; }
        PanelDetail.Visibility = Visibility.Visible;
        TxtDetailName.Text = $"{s.DisplayName}  —  {s.StateLabel}";
        TxtDetailName.Foreground = B(s.State == "Running" ? "SuccessText" : s.LooksBroken ? "DangerText" : "TextPrimary");
        TxtDetailInfo.Text = $"{s.Name} · {s.StartLabel} · {(s.Company.Length > 0 ? s.Company : "éditeur inconnu")} · {s.ExePath}" + (s.IsCritical ? " · VITAL : ne peut être ni arrêté ni désactivé ici" : "");
        TxtDetailDesc.Text = s.Description.Length > 0 ? s.Description : "Pas de description.";
        BtnStart.IsEnabled = !_busy && s.State != "Running" && s.StartMode != "Disabled";
        BtnStop.IsEnabled = !_busy && s.State == "Running" && !s.IsCritical;
        BtnRestart.IsEnabled = !_busy && s.State == "Running" && !s.IsCritical;
        BtnApplyStart.IsEnabled = !_busy;
        CmbStart.SelectedIndex = s.StartMode switch { "Auto" => s.Delayed ? 1 : 0, "Manual" => 2, "Disabled" => 3, _ => -1 };
        BtnStart.ToolTip = s.StartMode == "Disabled" ? "Service désactivé : choisissez d'abord un type de démarrage" : null;
    }

    private bool ConfirmMicrosoft(WinServiceInfo s, string what) =>
        s.IsMicrosoft == false || AppMessageBox.Show(this, $"« {s.DisplayName} » est un service de Windows.\n\n{what} peut faire disparaître une fonction (impression, son, réseau, mises à jour…). Continuer ?",
            "Service de Windows", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async Task ActAsync(string label, Func<WinServiceInfo, Task> work)
    {
        if (_busy || _selected is not WinServiceInfo s) return;
        _busy = true; ShowDetail();
        TxtFeedback.Text = $"{label} : {s.DisplayName}…";
        try
        {
            await work(s);
            _onOutput($"[OK] {label} : {s.DisplayName} ({s.StateLabel}, {s.StartLabel}).");
            TxtFeedback.Text = $"{label} : {s.DisplayName} → {s.StateLabel}, {s.StartLabel}.";
        }
        catch (Exception ex)
        {
            _onOutput($"❌ [ERREUR] {label} {s.DisplayName} : {ex.Message}");
            TxtFeedback.Text = $"Impossible : {ex.Message}";
        }
        finally { _busy = false; Render(); }
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e) => await ActAsync("Démarrage", s => WindowsServicesService.ControlAsync(s, "start", _ct));
    private async void BtnRestart_Click(object sender, RoutedEventArgs e) => await ActAsync("Redémarrage", s => WindowsServicesService.ControlAsync(s, "restart", _ct));

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is WinServiceInfo s && !ConfirmMicrosoft(s, "L'arrêter")) return;
        await ActAsync("Arrêt", sv => WindowsServicesService.ControlAsync(sv, "stop", _ct));
    }

    private async void BtnApplyStart_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not WinServiceInfo s || CmbStart.SelectedItem is not ComboBoxItem item) return;
        string mode = (string)item.Tag;
        if ((mode == "disabled" || mode == "manual") && !ConfirmMicrosoft(s, mode == "disabled" ? "Le désactiver" : "Le passer en manuel")) return;
        await ActAsync("Type de démarrage", sv => WindowsServicesService.SetStartAsync(sv, mode, _ct));
    }

    private void BtnOpenServicesMsc_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("services.msc");
    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadServicesAsync();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
