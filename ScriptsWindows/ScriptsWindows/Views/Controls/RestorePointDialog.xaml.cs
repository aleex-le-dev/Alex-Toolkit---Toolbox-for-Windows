using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Points de restauration : protection, espace, liste typée, création vérifiée (RestorePointService).</summary>
public partial class RestorePointDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private bool _busy;

    public RestorePointDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        TxtPointDescription.Text = $"Avant modification du {DateTime.Now:dd/MM/yyyy}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} Go" : $"{b / (double)(1L << 20):0} Mo";

    private async Task LoadAsync()
    {
        TxtFeedback.Text = "Lecture des points de restauration…";
        try
        {
            var st = await RestorePointService.GetStatusAsync(_ct);
            TxtProtection.Text = st.Protected ? "Protection du système activée sur le disque de Windows." : "Protection du système DÉSACTIVÉE : aucun point ne peut être créé.";
            TxtProtection.Foreground = ThemeManager.GetBrush(st.Protected ? "SuccessText" : "DangerText");
            BtnEnable.Visibility = st.Protected ? Visibility.Collapsed : Visibility.Visible;
            TxtSpace.Text = st.MaxBytes > 0
                ? $"Espace utilisé : {Size(st.UsedBytes)} sur {Size(st.MaxBytes)} réservés" + (st.VolumeBytes > 0 ? $" ({st.MaxBytes * 100.0 / st.VolumeBytes:0} % du disque). Les plus anciens points sont effacés quand la réserve est pleine." : ".")
                : st.Protected ? "Espace réservé non communiqué." : "Activez la protection pour pouvoir revenir en arrière après un problème.";
            BtnCreatePoint.IsEnabled = !_busy;

            PanelPoints.Children.Clear();
            TxtCount.Text = st.Points.Count == 0 ? "Aucun point de restauration" : $"{st.Points.Count} point(s) de restauration";
            foreach (var p in st.Points)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = p.Date?.ToString("dd/MM/yyyy HH:mm") ?? "?", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.GetBrush("TextPrimary") });
                var d = new TextBlock { Text = p.Description, FontSize = 12.5, Foreground = ThemeManager.GetBrush("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
                Grid.SetColumn(d, 1); g.Children.Add(d);
                var k = new TextBlock { Text = p.Kind, FontSize = 11.5, Foreground = ThemeManager.GetBrush("TextMuted") };
                Grid.SetColumn(k, 2); g.Children.Add(k);
                PanelPoints.Children.Add(new Border { Child = g, Padding = new Thickness(0, 8, 0, 8), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
            }
            var last = st.Points.FirstOrDefault();
            TxtFeedback.Text = last?.Date is DateTime dt ? $"Dernier point : il y a {Ago(DateTime.Now - dt)}." : "Aucun point : créez-en un avant toute intervention.";
        }
        catch (Exception ex) { TxtFeedback.Text = $"Erreur de lecture : {ex.Message}"; }
    }

    private static string Ago(TimeSpan t) => t.TotalHours < 1 ? $"{Math.Max(1, (int)t.TotalMinutes)} min" : t.TotalDays < 2 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalDays} jours";

    private async void BtnCreatePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; BtnCreatePoint.IsEnabled = false;
        TxtResult.Visibility = Visibility.Visible;
        TxtResult.Text = "Création du point en cours (jusqu'à une minute)…";
        TxtResult.Foreground = ThemeManager.GetBrush("AccentText");
        try
        {
            var (ok, msg) = await RestorePointService.CreateAsync(TxtPointDescription.Text, _onOutput, _ct);
            TxtResult.Text = msg;
            TxtResult.Foreground = ThemeManager.GetBrush(ok ? "SuccessText" : "DangerText");
        }
        catch (Exception ex) { TxtResult.Text = $"Erreur : {ex.Message}"; TxtResult.Foreground = ThemeManager.GetBrush("DangerText"); }
        finally { _busy = false; await LoadAsync(); }
    }

    private async void BtnEnable_Click(object sender, RoutedEventArgs e)
    {
        if (AppMessageBox.Show(this, "Activer la protection du système sur le disque de Windows et lui réserver 5 % du disque ?\n\nC'est le réglage conseillé : il permet de revenir en arrière après une mise à jour ou un pilote qui pose problème.",
                "Activer la protection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BtnEnable.IsEnabled = false;
        try { await RestorePointService.EnableProtectionAsync(5, _onOutput, _ct); }
        finally { BtnEnable.IsEnabled = true; await LoadAsync(); }
    }

    private void BtnOpenRstrui_Click(object sender, RoutedEventArgs e)
    {
        _onOutput("[i] Ouverture de l'assistant de restauration du système (rstrui).");
        CommandRunner.StartProcess("rstrui.exe");
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
