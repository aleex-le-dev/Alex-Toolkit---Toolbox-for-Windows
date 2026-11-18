using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Programmes au démarrage (mêmes sources et même état que le Gestionnaire des tâches, + applications du Store et
/// tâches planifiées à l'ouverture de session). Activer / désactiver est réversible : rien n'est supprimé.
/// </summary>
public partial class StartupManagerDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private List<StartupEntry> _entries = new();
    private bool _busy;

    public StartupManagerDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            await CheckSessionAccountAsync();
        };
    }

    private async Task LoadAsync()
    {
        _busy = true;
        BtnRefresh.IsEnabled = false;
        TxtSummary.Text = "Analyse des programmes au démarrage…";
        try
        {
            _entries = await StartupService.GetEntriesAsync(_ct);
            Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TxtSummary.Text = "Analyse impossible.";
            TxtFeedback.Text = $"Erreur : {ex.Message}";
            _onOutput($"❌ [ERREUR] Programmes au démarrage : {ex.Message}");
        }
        finally { _busy = false; BtnRefresh.IsEnabled = true; }
    }

    /// <summary>L'appli tourne en administrateur : si la session ouverte est celle d'un autre compte, « ce compte » n'est pas le sien.</summary>
    private async Task CheckSessionAccountAsync()
    {
        try
        {
            string owner = (await PowerShellRunner.RunAsync(
                "(Get-CimInstance Win32_Process -Filter \"name='explorer.exe'\" | Select-Object -First 1 | Invoke-CimMethod -MethodName GetOwner).User", _ct)).Trim();
            if (owner.Length > 0 && !owner.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                TxtAccount.Text = $"La session ouverte est celle de « {owner} », mais l'outil tourne sous « {Environment.UserName} » (compte administrateur saisi à l'ouverture) : " +
                                  $"les programmes « ce compte » sont ceux de « {Environment.UserName} ». Pour gérer ceux de « {owner} », utilisez le Gestionnaire des tâches dans sa session.";
                PanelAccount.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private static Brush B(string key) => ThemeManager.GetBrush(key);

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text, FontSize = size, Foreground = B(brush), FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    private void Render()
    {
        int on = _entries.Count(e => e.Enabled), off = _entries.Count - on, missing = _entries.Count(e => e.FileMissing);
        TxtSummary.Text = _entries.Count == 0
            ? "Aucun programme ne se lance au démarrage."
            : $"{_entries.Count} programme(s) au démarrage  ·  {on} activé(s)  ·  {off} désactivé(s)" + (missing > 0 ? $"  ·  {missing} fichier(s) introuvable(s)" : "");

        IEnumerable<StartupEntry> shown = CmbFilter.SelectedIndex switch
        {
            1 => _entries.Where(e => e.Enabled),
            2 => _entries.Where(e => !e.Enabled),
            3 => _entries.Where(e => e.FileMissing),
            _ => _entries
        };
        PanelList.Children.Clear();
        foreach (var e in shown) PanelList.Children.Add(BuildRow(e));
        TxtEmpty.Visibility = PanelList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtEmpty.Text = _entries.Count == 0 ? "Aucun programme ne se lance avec Windows sur ce compte." : "Aucun programme dans ce filtre.";
    }

    private Border BuildRow(StartupEntry e)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Nom + source, éditeur / description, commande
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Text(e.Name, 13, e.Enabled ? "TextPrimary" : "TextSecondary", FontWeights.SemiBold));
        title.Children.Add(new Border
        {
            Background = B("BgInput"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = Text(e.SourceLabel, 10.5, "TextMuted")
        });
        info.Children.Add(title);
        string who = string.Join("  ·  ", new[] { e.Publisher, e.Description != e.Name ? e.Description : "" }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (who.Length > 0) info.Children.Add(Text(who, 11.5, "TextSecondary"));
        var cmd = Text(e.Command, 11, "TextMuted");
        cmd.ToolTip = e.Command;
        info.Children.Add(cmd);
        g.Children.Add(info);

        // État (point de couleur + texte) ; fichier introuvable signalé en plus de l'état
        var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                                       Background = B(e.Enabled ? "Success" : "TextMuted") });
        line.Children.Add(Text(e.Locked ? (e.Enabled ? "Activé (stratégie)" : "Désactivé (stratégie)") : e.Enabled ? "Activé" : "Désactivé",
                               12, e.Enabled ? "SuccessText" : "TextMuted", FontWeights.SemiBold));
        state.Children.Add(line);
        if (e.FileMissing)
        {
            var miss = Text("Fichier introuvable", 11.5, "WarningText");
            miss.Margin = new Thickness(15, 2, 0, 0);
            miss.ToolTip = "Le programme a été désinstallé ou déplacé : cette entrée ne lance plus rien, vous pouvez la désactiver sans risque.";
            state.Children.Add(miss);
        }
        Grid.SetColumn(state, 1);
        g.Children.Add(state);

        // Action explicite
        var action = new Button
        {
            Content = e.Locked ? "Imposé" : e.Enabled ? "Désactiver" : "Réactiver", MinWidth = 104, IsEnabled = !e.Locked,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = e.Locked ? "Réglé par une stratégie de l'entreprise" : e.Enabled ? "Ne plus lancer au démarrage (réversible)" : "Relancer au démarrage"
        };
        action.SetResourceReference(StyleProperty, e.Enabled ? "ActionButton" : "PrimaryActionButton");
        action.Click += async (_, _) => await ToggleAsync(e, action);
        Grid.SetColumn(action, 2);
        g.Children.Add(action);

        // Emplacement du fichier
        var open = new Button
        {
            ToolTip = "Ouvrir l'emplacement du fichier", IsEnabled = e.TargetPath.Length > 0 && !e.FileMissing, VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = "\uE838", FontFamily = (FontFamily)FindResource("FontIcon"), FontSize = 13 }
        };
        open.SetResourceReference(StyleProperty, "IconButton");
        open.Click += (_, _) => { if (!StartupService.OpenLocation(e)) TxtFeedback.Text = "Fichier introuvable."; };
        Grid.SetColumn(open, 3);
        g.Children.Add(open);

        return new Border { Child = g, Padding = new Thickness(10, 8, 6, 8), BorderBrush = B("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private async Task ToggleAsync(StartupEntry e, Button button)
    {
        bool wanted = !e.Enabled;
        if (!wanted && e.Name.Contains("SecurityHealth", StringComparison.OrdinalIgnoreCase)
            && AppMessageBox.Show(this, "« SecurityHealth » affiche l'icône de Sécurité Windows (antivirus) près de l'horloge.\n\nLa désactiver masque seulement l'icône et ses alertes, la protection reste active. Continuer ?",
                                  "Sécurité Windows", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        button.IsEnabled = false;
        try
        {
            bool now = await StartupService.SetEnabledAsync(e, wanted, _ct);
            e.Enabled = now;
            if (now != wanted) throw new InvalidOperationException("Windows n'a pas pris en compte le changement.");
            TxtFeedback.Text = wanted
                ? $"« {e.Name} » se lancera de nouveau au prochain démarrage."
                : $"« {e.Name} » ne se lancera plus au démarrage (réactivable ici ou dans le Gestionnaire des tâches).";
            _onOutput($"[OK] « {e.Name} » {(wanted ? "réactivé" : "désactivé")} au démarrage ({e.SourceLabel}).");
        }
        catch (Exception ex)
        {
            TxtFeedback.Text = $"Impossible de modifier « {e.Name} » : {ex.Message}";
            _onOutput($"❌ [ERREUR] Démarrage de « {e.Name} » : {ex.Message}");
        }
        Render();
    }
    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !_busy) Render(); }

    private void BtnOpenTaskmgr_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("taskmgr.exe", "/0 /startup");

    private void BtnOpenStartupFolder_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("explorer.exe", "shell:startup");

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
