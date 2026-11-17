using System.Windows;
using System.Windows.Controls;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Fenêtre-hôte générique affichant plusieurs outils (UserControl) dans des onglets.
/// Permet de regrouper des outils d'une même famille en une seule carte (ex. Réseau).
/// </summary>
public partial class ToolTabsWindow : Window
{
    public ToolTabsWindow(string title, params (string Header, UserControl Content)[] tabs)
    {
        InitializeComponent();
        Title = title;
        foreach (var (header, content) in tabs)
        {
            Tabs.Items.Add(new TabItem
            {
                Header = header,
                Content = content
            });
        }
        if (Tabs.Items.Count > 0) Tabs.SelectedIndex = 0;
    }
}
