using System.IO;
using System.Windows;
using System.Windows.Controls;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Métadonnées cachées des photos et documents (MetadataService) : analyse, carte GPS, copies nettoyées.</summary>
public partial class MetadataDialog : Window
{
    private const int MaxFiles = 3000;
    private readonly Action<string> _onOutput;
    private readonly List<MetaReport> _reports = new();
    private bool _riskOnly, _busy;

    public MetadataDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AnalyzeAsync(paths); };
        Loaded += (_, _) => Render();
    }

    private static TextBlock Txt(string text, double size = 12, string brush = "TextSecondary", bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = ThemeManager.GetBrush(brush), TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private async void BtnFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true, Title = "Photos et documents à vérifier",
            Filter = "Photos et documents|*.jpg;*.jpeg;*.jfif;*.png;*.heic;*.heif;*.webp;*.tif;*.tiff;*.dng;*.docx;*.xlsx;*.pptx;*.docm;*.xlsm;*.pptm;*.pdf|Tous les fichiers|*.*"
        };
        if (dlg.ShowDialog(this) == true) await AnalyzeAsync(dlg.FileNames);
    }

    private async void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier à vérifier (sous-dossiers compris)" };
        if (dlg.ShowDialog(this) == true) await AnalyzeAsync(new[] { dlg.FolderName });
    }

    /// <summary>Fichiers pris en charge (dossiers parcourus récursivement, jonctions ignorées), au plus MaxFiles.</summary>
    internal static List<string> Expand(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) { if (MetadataService.Supported(p)) files.Add(p); continue; }
            if (!Directory.Exists(p)) continue;
            var stack = new Stack<string>(); stack.Push(p);
            while (stack.Count > 0 && files.Count < MaxFiles)
            {
                string dir = stack.Pop();
                try
                {
                    files.AddRange(Directory.EnumerateFiles(dir).Where(MetadataService.Supported).Take(MaxFiles - files.Count));
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
                }
                catch { }
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
    }

    internal async Task AnalyzeAsync(IEnumerable<string> paths)
    {
        if (_busy) return;
        _busy = true;
        TxtStatus.Text = "Recherche des fichiers…";
        try
        {
            var files = await Task.Run(() => Expand(paths));
            if (files.Count == 0) { TxtStatus.Text = "Aucune photo ni aucun document pris en charge (JPEG, PNG, HEIC, WebP, TIFF, Word, Excel, PowerPoint, PDF)."; return; }
            var progress = new Progress<int>(n => TxtStatus.Text = $"Analyse : {n} / {files.Count}…");
            var reports = await Task.Run(() =>
            {
                var list = new List<MetaReport>(); int i = 0;
                foreach (var f in files) { list.Add(MetadataService.Inspect(f)); if (++i % 20 == 0) ((IProgress<int>)progress).Report(i); }
                return list;
            });
            _reports.Clear(); _reports.AddRange(reports.OrderByDescending(r => r.Gps != null).ThenByDescending(r => r.SensitiveCount).ThenBy(r => r.Path));
            int gps = _reports.Count(r => r.Gps != null), personal = _reports.Count(r => r.SensitiveCount > 0);
            TxtStatus.Text = $"{_reports.Count} fichier(s) analysé(s)" + (files.Count >= MaxFiles ? $" (limité à {MaxFiles})" : "") + ".";
            _onOutput($"[i] Métadonnées : {_reports.Count} fichier(s), {gps} avec position GPS, {personal} avec des informations personnelles.");
            Render();
        }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; }
        finally { _busy = false; }
    }

    private void BtnRiskOnly_Click(object sender, RoutedEventArgs e)
    {
        _riskOnly = !_riskOnly;
        BtnRiskOnly.SetResourceReference(StyleProperty, _riskOnly ? "PrimaryActionButton" : "ActionButton");
        Render();
    }

    internal void Render()
    {
        PanelFiles.Children.Clear();
        int gps = _reports.Count(r => r.Gps != null), personal = _reports.Count(r => r.SensitiveCount > 0);
        PanelSummary.Visibility = _reports.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string level = gps > 0 ? "Danger" : personal > 0 ? "Warning" : "Success";
        PanelSummary.Background = ThemeManager.GetBrush(level + "Bg");
        TxtSummary.Foreground = ThemeManager.GetBrush(level + "Text");
        TxtSummary.Text = personal == 0 ? $"{_reports.Count} fichier(s) : aucune information personnelle cachée trouvée."
            : (gps > 0 ? $"{gps} fichier(s) révèlent l'endroit exact où ils ont été pris (position GPS). " : "") +
              $"{personal} fichier(s) sur {_reports.Count} contiennent des informations personnelles. Partagez plutôt les copies nettoyées.";
        BtnCleanAll.IsEnabled = _reports.Any(r => r.NeedsClean);
        BtnCsv.IsEnabled = _reports.Count > 0;
        if (_reports.Count == 0)
        {
            PanelFiles.Children.Add(Txt("Choisissez des photos ou des documents, ou un dossier entier (par exemple les photos à publier).", 12.5));
            return;
        }
        foreach (var r in _reports.Where(r => !_riskOnly || r.SensitiveCount > 0).Take(500)) PanelFiles.Children.Add(Row(r));
        int hidden = _reports.Count(r => !_riskOnly || r.SensitiveCount > 0) - 500;
        if (hidden > 0) PanelFiles.Children.Add(Txt($"… et {hidden} autre(s) fichier(s) (affichez seulement les fichiers à risque).", 12, "TextMuted"));
    }

    private Button Btn(string text, string style, Action click, string tip = "")
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (tip.Length > 0) b.ToolTip = tip;
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => { try { click(); } catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; } };
        return b;
    }

    private Border Row(MetaReport r)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var head = new WrapPanel();
        head.Children.Add(Txt(Path.GetFileName(r.Path), 13, "TextPrimary", true));
        void Badge(string text, string level)
        {
            var b = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(10, 0, 0, 0), Background = ThemeManager.GetBrush(level + "Bg") };
            b.Child = Txt(text, 11, level + "Text", true); head.Children.Add(b);
        }
        if (r.Error.Length > 0) Badge("illisible", "Warning");
        else if (r.Gps != null) Badge("position GPS", "Danger");
        if (r.Error.Length == 0 && r.SensitiveCount - (r.Gps != null ? 2 : 0) > 0) Badge($"{r.SensitiveCount - (r.Gps != null ? 2 : 0)} info(s) personnelle(s)", "Warning");
        if (r.Error.Length == 0 && r.SensitiveCount == 0) Badge(r.Items.Count == 0 ? "aucune métadonnée" : "rien de personnel", "Success");
        sp.Children.Add(head);
        sp.Children.Add(Txt($"{r.Kind}  ·  {Path.GetDirectoryName(r.Path)}", 11, "TextMuted"));
        if (r.Error.Length > 0) sp.Children.Add(Txt("Lecture impossible : " + r.Error, 11.5, "WarningText"));
        foreach (var i in r.Items.Where(i => i.Label != "Orientation").OrderByDescending(i => i.Sensitive).Take(10))
            sp.Children.Add(Txt($"{i.Label} : {i.Value}", 11.5, i.Sensitive ? "TextPrimary" : "TextSecondary"));
        g.Children.Add(sp);
        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (r.Gps != null) actions.Children.Add(Btn("Voir sur la carte", "ActionButton", () => CommandRunner.OpenUrlAsUser(r.MapUrl), "Ouvre la position sur OpenStreetMap"));
        actions.Children.Add(Btn("Emplacement", "ActionButton", () => CommandRunner.StartProcess("explorer.exe", $"/select,\"{r.Path}\"")));
        if (r.NeedsClean)
            actions.Children.Add(Btn("Copie nettoyée", "PrimaryActionButton", () =>
            {
                string dest = MetadataService.CleanCopy(r.Path);
                TxtStatus.Text = $"Copie créée : {Path.GetFileName(dest)}";
                _onOutput($"[OK] Copie sans métadonnées : {dest}");
            }, "Crée « nom (sans métadonnées) » à côté ; l'original reste intact"));
        if (r.Items.Any(i => i.Label.StartsWith("Commentaires dans") || i.Label.StartsWith("Modifications suivies")))
            sp.Children.Add(Txt("Commentaires et modifications suivies : la copie nettoyée les garde. Dans Word : Révision → Supprimer tous les commentaires et Accepter toutes les modifications, ou Fichier → Informations → Inspecter le document.", 11, "WarningText"));
        if (r.SensitiveCount > 0 && !r.CanClean)
            sp.Children.Add(Txt(r.Kind == "PDF" ? "Pour un PDF propre : rouvrez le document d'origine et exportez-le en PDF sans les propriétés, ou « Imprimer » → « Microsoft Print to PDF »."
                                                : "Nettoyage automatique non disponible pour ce format : convertissez-le en JPEG ou PNG, puis analysez la copie.", 11, "WarningText"));
        Grid.SetColumn(actions, 1); g.Children.Add(actions);
        return new Border { Child = g, Padding = new Thickness(4, 8, 4, 8), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private async void BtnCleanAll_Click(object sender, RoutedEventArgs e)
    {
        var todo = _reports.Where(r => r.NeedsClean).ToList();
        if (todo.Count == 0) return;
        if (AppMessageBox.Show(this, $"Créer une copie nettoyée de {todo.Count} fichier(s) ?\n\nChaque copie « (sans métadonnées) » est enregistrée à côté de l'original, qui n'est pas modifié.",
                "Copies nettoyées", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BtnCleanAll.IsEnabled = false;
        var (ok, ko) = await Task.Run(() =>
        {
            int a = 0, b = 0;
            foreach (var r in todo) { try { MetadataService.CleanCopy(r.Path); a++; } catch { b++; } }
            return (a, b);
        });
        TxtStatus.Text = $"{ok} copie(s) nettoyée(s) créée(s)" + (ko > 0 ? $", {ko} échec(s)" : "") + ".";
        _onOutput($"[OK] Métadonnées : {ok} copie(s) nettoyée(s), {ko} échec(s).");
        BtnCleanAll.IsEnabled = true;
    }

    private void BtnCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"Metadonnees_{DateTime.Now:yyyy-MM-dd}.csv", Filter = "Tableau CSV|*.csv", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, MetadataService.ToCsv(_reports)); TxtStatus.Text = $"Tableau enregistré : {dlg.FileName}"; }
        catch (Exception ex) { TxtStatus.Text = $"Enregistrement impossible : {ex.Message}"; }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
