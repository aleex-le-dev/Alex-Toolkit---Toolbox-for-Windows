using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScriptsWindows.Services;
using Level = ScriptsWindows.Services.SensitiveDataScanner.Level;

namespace ScriptsWindows.Views.Controls;

/// <summary>
/// Recherche des documents et codes confidentiels : analyse du profil avec progression, classement par gravité,
/// filtres, et actions (afficher, copier vers un dossier, rapport HTML, corbeille). Les valeurs ne sont jamais affichées en clair.
/// </summary>
public partial class SensitiveDocsDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private CancellationTokenSource? _scanCts;
    private readonly List<ResultItem> _all = new();
    private bool _suppressFilter;
    private bool _bulkCheck;
    private bool _hasScanned;

    // Progression de l'analyse (écrite par le thread d'analyse, lue par le minuteur UI)
    private volatile string _currentDir = "";
    private int _dirCount;
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Stopwatch _watch = new();

    private const string AllCategories = "Toutes les catégories";

    public SensitiveDocsDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _progressTimer.Tick += (_, _) =>
        {
            TxtCurrentDir.Text = _currentDir;
            EmptyText.Text = $"{Volatile.Read(ref _dirCount):N0} dossiers parcourus • {_watch.Elapsed:mm\\:ss}";
        };
        ResetCategoryCombo();
        Loaded += async (_, _) => await ScanAsync();
        Closing += (_, _) => _scanCts?.Cancel();
    }

    // ─── Analyse ─────────────────────────────────────────────────────────

    private async Task ScanAsync()
    {
        if (_scanCts != null) return;
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var token = _scanCts.Token;

        _all.Clear();
        LstResults.ItemsSource = null;
        Volatile.Write(ref _dirCount, 0);
        _currentDir = "";
        SetScanning(true);
        _watch.Restart();
        _progressTimer.Start();
        _onOutput("[i] Documents et codes confidentiels : analyse du profil en cours…");

        List<SensitiveDataScanner.Finding> findings;
        try
        {
            findings = await Task.Run(() => SensitiveDataScanner.Scan(null, token,
                d => { _currentDir = d; Interlocked.Increment(ref _dirCount); }), token);
        }
        catch (OperationCanceledException) { findings = new(); }
        catch (Exception ex)
        {
            findings = new();
            AppMessageBox.Show(this, $"L'analyse a échoué :\n\n{ex.Message}", "Documents confidentiels", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _progressTimer.Stop();
            _watch.Stop();
        }

        bool cancelled = token.IsCancellationRequested;
        _scanCts.Dispose();
        _scanCts = null;
        if (!IsLoaded) return; // fenêtre fermée pendant l'analyse

        foreach (var f in findings)
        {
            var item = new ResultItem(f);
            item.PropertyChanged += (_, e) => { if (!_bulkCheck && e.PropertyName == nameof(ResultItem.IsChecked)) UpdateSelectionState(); };
            _all.Add(item);
        }

        // Les plus graves d'abord, puis les plus récents
        _all.Sort((a, b) => a.Level != b.Level ? a.Level.CompareTo(b.Level) : b.Modified.CompareTo(a.Modified));

        int crit = _all.Count(i => i.Level == Level.Critical);
        int pers = _all.Count(i => i.Level == Level.Personal);
        int tech = _all.Count(i => i.Level == Level.Technical);
        TxtTotal.Text = _all.Count.ToString("N0");
        TxtCritical.Text = crit.ToString("N0");
        TxtPersonal.Text = pers.ToString("N0");
        TxtTechnical.Text = tech.ToString("N0");

        _hasScanned = true;
        ResetCategoryCombo();
        SetScanning(false);
        ApplyFilter();

        string duration = _watch.Elapsed.TotalSeconds < 60 ? $"{_watch.Elapsed.TotalSeconds:0.#} s" : $"{_watch.Elapsed:mm\\:ss}";
        TxtStatus.Text = cancelled
            ? $"Analyse arrêtée : {_all.Count} élément(s) trouvé(s) dans {_dirCount:N0} dossiers (résultats partiels)."
            : $"{_all.Count} élément(s) trouvé(s) dans {_dirCount:N0} dossiers en {duration}.";
        _onOutput(cancelled
            ? $"[!] Analyse arrêtée : {_all.Count} élément(s) trouvé(s) (résultats partiels)."
            : $"[OK] Analyse terminée : {_all.Count} élément(s) — {crit} secret(s) en clair, {pers} document(s) personnel(s), {tech} accès technique(s).");
    }

    private void SetScanning(bool scanning)
    {
        BtnScan.IsEnabled = !scanning;
        BtnCancel.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        TxtScanLabel.Text = _hasScanned ? "Relancer l'analyse" : "Lancer l'analyse";
        ProgScan.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        TxtCurrentDir.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        CmbLevel.IsEnabled = CmbCategory.IsEnabled = TxtSearch.IsEnabled = ChkAll.IsEnabled = !scanning;

        if (scanning)
        {
            PanelEmpty.Visibility = Visibility.Visible;
            EmptyBadge.Background = ThemeManager.GetBrush("AccentDim");
            EmptyGlyph.Text = "";
            EmptyGlyph.Foreground = ThemeManager.GetBrush("AccentText");
            EmptyTitle.Text = "Analyse en cours…";
            EmptyText.Text = "Préparation…";
            TxtStatus.Text = "Analyse des dossiers personnels…";
            foreach (var t in new[] { TxtTotal, TxtCritical, TxtPersonal, TxtTechnical }) t.Text = "…";
            BtnReport.IsEnabled = BtnCopy.IsEnabled = BtnRecycle.IsEnabled = false;
        }
    }

    // ─── Filtres ─────────────────────────────────────────────────────────

    private void ResetCategoryCombo()
    {
        _suppressFilter = true;
        CmbCategory.Items.Clear();
        CmbCategory.Items.Add(AllCategories);
        foreach (var g in _all.GroupBy(i => i.CategoryLabel).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            CmbCategory.Items.Add($"{g.Key} ({g.Count()})");
        CmbCategory.SelectedIndex = 0;
        _suppressFilter = false;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private List<ResultItem> Visible() => (LstResults.ItemsSource as List<ResultItem>) ?? new();

    private void ApplyFilter()
    {
        if (_suppressFilter || !IsInitialized || LstResults == null) return;

        string q = TxtSearch.Text.Trim();
        int level = CmbLevel.SelectedIndex; // 0 = tous
        string? cat = CmbCategory.SelectedIndex > 0 ? CmbCategory.SelectedItem as string : null;
        if (cat != null) cat = cat[..cat.LastIndexOf(" (", StringComparison.Ordinal)];

        var list = _all.Where(i =>
            (level <= 0 || (int)i.Level == level - 1) &&
            (cat == null || i.CategoryLabel == cat) &&
            (q.Length == 0
             || i.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
             || i.Folder.Contains(q, StringComparison.OrdinalIgnoreCase)
             || i.Details.Contains(q, StringComparison.OrdinalIgnoreCase)
             || i.CategoryLabel.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        LstResults.ItemsSource = list;

        bool hasResults = _all.Count > 0;
        PanelEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count == 0)
        {
            bool clean = !hasResults;
            EmptyBadge.Background = ThemeManager.GetBrush(clean ? "SuccessBg" : "BgInput");
            EmptyGlyph.Text = clean ? "" : "";
            EmptyGlyph.Foreground = ThemeManager.GetBrush(clean ? "SuccessText" : "TextSecondary");
            EmptyTitle.Text = clean ? "Aucun élément sensible trouvé" : "Aucun résultat pour ce filtre";
            EmptyText.Text = clean
                ? "Aucun mot de passe en clair, document personnel ou clé d'accès n'a été repéré dans les dossiers analysés."
                : "Modifiez la recherche, le niveau ou la catégorie.";
        }

        BtnReport.IsEnabled = list.Count > 0;
        UpdateSelectionState();
    }

    private void ChkAll_Click(object sender, RoutedEventArgs e)
    {
        bool check = ChkAll.IsChecked == true;
        _bulkCheck = true;
        foreach (var i in Visible()) i.IsChecked = check;
        _bulkCheck = false;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        int checkedCount = _all.Count(i => i.IsChecked);
        var visible = Visible();
        int visibleChecked = visible.Count(i => i.IsChecked);
        ChkAll.IsChecked = visible.Count > 0 && visibleChecked == visible.Count ? true : visibleChecked == 0 ? false : null;

        BtnCopy.IsEnabled = BtnRecycle.IsEnabled = checkedCount > 0;
        TxtCopyLabel.Text = checkedCount > 0 ? $"Copier dans un dossier ({checkedCount})" : "Copier dans un dossier";
    }

    // ─── Actions ─────────────────────────────────────────────────────────

    private async void BtnScan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        TxtStatus.Text = "Arrêt de l'analyse…";
    }

    private void BtnReveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ResultItem item) return;
        try
        {
            // Jamais d'ouverture directe : un .bat / .ps1 trouvé serait exécuté
            if (File.Exists(item.Path)) Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
            else if (Directory.Exists(item.Path)) Process.Start("explorer.exe", $"\"{item.Path}\"");
            else TxtStatus.Text = "Ce fichier n'existe plus.";
        }
        catch (Exception ex) { TxtStatus.Text = $"Impossible d'ouvrir l'Explorateur : {ex.Message}"; }
    }

    // Fichiers qui s'exécuteraient au lieu de s'ouvrir : affichés dans le Bloc-notes
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".sh", ".exe", ".com", ".scr",
        ".msi", ".lnk", ".reg", ".hta", ".rdp", ".ovpn",
    };

    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ResultItem item) return;
        try
        {
            if (Directory.Exists(item.Path))
            {
                Process.Start("explorer.exe", $"\"{item.Path}\"");
            }
            else if (!File.Exists(item.Path))
            {
                TxtStatus.Text = "Ce fichier n'existe plus.";
            }
            else if (ExecutableExtensions.Contains(System.IO.Path.GetExtension(item.Path))
                     || System.IO.Path.GetFileName(item.Path).StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                     || System.IO.Path.GetExtension(item.Path).Length == 0)
            {
                // Scripts, raccourcis, connexions, fichiers sans extension : lecture seule dans le Bloc-notes, jamais exécutés
                Process.Start("notepad.exe", $"\"{item.Path}\"");
                TxtStatus.Text = $"Ouvert dans le Bloc-notes : {item.FileName}";
            }
            else
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                TxtStatus.Text = $"Ouvert : {item.FileName}";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Aucune application associée (ex. .kdbx sans KeePass) : choix de l'application par Windows
            try { Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {item.Path}"); }
            catch (Exception ex) { TxtStatus.Text = $"Impossible d'ouvrir le fichier : {ex.Message}"; }
        }
        catch (Exception ex) { TxtStatus.Text = $"Impossible d'ouvrir le fichier : {ex.Message}"; }
    }

    private void BtnReport_Click(object sender, RoutedEventArgs e)
    {
        var items = Visible();
        if (items.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "Enregistrer le rapport",
            Filter = "Rapport HTML (*.html)|*.html",
            FileName = $"Documents_confidentiels_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd_HHmm}.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, SensitiveDataScanner.BuildHtmlReport(items.Select(i => i.Finding).ToList()), new UTF8Encoding(true));
            TxtStatus.Text = $"Rapport enregistré : {dlg.FileName}";
            _onOutput($"[OK] Rapport des documents confidentiels : {dlg.FileName}");
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, $"Le rapport n'a pas pu être enregistré :\n\n{ex.Message}", "Rapport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        var items = _all.Where(i => i.IsChecked).ToList();
        if (items.Count == 0) return;

        var dlg = new OpenFolderDialog { Title = "Choisissez où copier les documents (clé USB, disque externe…)" };
        if (dlg.ShowDialog(this) != true) return;

        string target = Path.Combine(dlg.FolderName, $"Sauvegarde_Confidentiels_{DateTime.Now:yyyy-MM-dd_HHmm}");
        SetActionsBusy(true, $"Copie de {items.Count} fichier(s)…");
        try
        {
            Directory.CreateDirectory(target);
            var (copied, errors) = await Task.Run(() => SensitiveDataScanner.CopyFindings(items.Select(i => i.Finding), target, _ct));
            TxtStatus.Text = $"{copied} fichier(s) copié(s) dans {target}" + (errors.Count > 0 ? $" — {errors.Count} erreur(s)" : "");
            _onOutput($"[OK] {copied} document(s) confidentiel(s) copié(s) dans {target}");
            foreach (var err in errors) _onOutput($"[!] Copie impossible : {err}");
            if (errors.Count > 0)
                AppMessageBox.Show(this, $"{copied} fichier(s) copié(s).\n\n{errors.Count} fichier(s) n'ont pas pu être copiés :\n" + string.Join("\n", errors.Take(8)) + (errors.Count > 8 ? "\n…" : ""),
                    "Copie terminée avec des erreurs", MessageBoxButton.OK, MessageBoxImage.Warning);
            Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show(this, $"La copie a échoué :\n\n{ex.Message}", "Copie", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetActionsBusy(false, null); }
    }

    private async void BtnRecycle_Click(object sender, RoutedEventArgs e)
    {
        var items = _all.Where(i => i.IsChecked).ToList();
        if (items.Count == 0) return;

        string preview = string.Join("\n", items.Take(6).Select(i => "• " + i.FileName)) + (items.Count > 6 ? $"\n… et {items.Count - 6} autre(s)" : "");
        if (AppMessageBox.Show(this,
                $"Envoyer {items.Count} élément(s) dans la Corbeille ?\n\n{preview}\n\nIls restent récupérables depuis la Corbeille tant qu'elle n'est pas vidée. Pensez à en garder une copie si nécessaire.",
                "Mettre à la corbeille", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetActionsBusy(true, $"Envoi de {items.Count} élément(s) dans la Corbeille…");
        var removed = await Task.Run(() => items.Where(i => SensitiveDataScanner.MoveToRecycleBin(i.Path)).ToList());
        SetActionsBusy(false, null);

        // Un fichier peut apparaître dans plusieurs catégories : on retire toutes ses lignes
        var removedPaths = new HashSet<string>(removed.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        _all.RemoveAll(i => removedPaths.Contains(i.Path));
        TxtTotal.Text = _all.Count.ToString("N0");
        TxtCritical.Text = _all.Count(i => i.Level == Level.Critical).ToString("N0");
        TxtPersonal.Text = _all.Count(i => i.Level == Level.Personal).ToString("N0");
        TxtTechnical.Text = _all.Count(i => i.Level == Level.Technical).ToString("N0");
        ResetCategoryCombo();
        ApplyFilter();

        int failed = items.Count - removed.Count;
        TxtStatus.Text = $"{removed.Count} élément(s) envoyé(s) dans la Corbeille" + (failed > 0 ? $", {failed} impossible(s) (fichier ouvert ou protégé)." : ".");
        _onOutput($"[OK] {removed.Count} document(s) confidentiel(s) envoyé(s) dans la Corbeille.");
    }

    private void SetActionsBusy(bool busy, string? status)
    {
        BtnScan.IsEnabled = !busy;
        BtnCopy.IsEnabled = BtnRecycle.IsEnabled = !busy && _all.Any(i => i.IsChecked);
        BtnReport.IsEnabled = !busy && Visible().Count > 0;
        if (status != null) TxtStatus.Text = status;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ─── Modèle d'une ligne ──────────────────────────────────────────────

    public sealed class ResultItem : INotifyPropertyChanged
    {
        public SensitiveDataScanner.Finding Finding { get; }
        public string Path => Finding.Path;
        public string FileName { get; }
        public string Folder { get; }
        public string Details { get; }
        public string CategoryLabel { get; }
        public Level Level { get; }
        public string Glyph { get; }
        public Brush LevelFg { get; }
        public Brush LevelBg { get; }
        public DateTime Modified { get; }
        public string DateText { get; }
        public string SizeText { get; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ResultItem(SensitiveDataScanner.Finding f)
        {
            Finding = f;
            FileName = System.IO.Path.GetFileName(f.Path.TrimEnd('\\'));
            Folder = System.IO.Path.GetDirectoryName(f.Path.TrimEnd('\\')) ?? "";
            Level = SensitiveDataScanner.GetLevel(f.Category);

            // Taille et date sont affichées dans leur propre colonne : on les retire du détail
            string d = System.Text.RegularExpressions.Regex.Replace(f.Details, @"(?:\s*•\s*)?[\d\s,\.]+\s?Ko, modifié le \d{2}/\d{2}/\d{4} \d{2}:\d{2}\s*$", "").Trim();
            Details = d.Length > 0 ? d : "Repéré par son nom ou son emplacement";

            string label = f.Category.StartsWith("Contenu : ", StringComparison.Ordinal) ? f.Category["Contenu : ".Length..] : f.Category;
            CategoryLabel = label.Length > 0 ? char.ToUpper(label[0]) + label[1..] : label;

            (LevelFg, LevelBg) = Level switch
            {
                Level.Critical => (ThemeManager.GetBrush("DangerText"), ThemeManager.GetBrush("DangerBg")),
                Level.Personal => (ThemeManager.GetBrush("WarningText"), ThemeManager.GetBrush("WarningBg")),
                _              => (ThemeManager.GetBrush("AccentText"), ThemeManager.GetBrush("AccentDim")),
            };
            Glyph = GlyphFor(f.Category);

            try
            {
                if (File.Exists(f.Path))
                {
                    var fi = new FileInfo(f.Path);
                    Modified = fi.LastWriteTime;
                    SizeText = fi.Length < 1024 * 1024 ? $"{Math.Max(1, fi.Length / 1024.0):0.#} Ko" : $"{fi.Length / 1048576.0:0.#} Mo";
                }
                else if (Directory.Exists(f.Path))
                {
                    Modified = Directory.GetLastWriteTime(f.Path);
                    SizeText = "Dossier";
                }
                else SizeText = "";
            }
            catch { SizeText = ""; }
            DateText = Modified == default ? "" : Modified.ToString("dd/MM/yyyy HH:mm");
        }

        private static string GlyphFor(string category)
        {
            string c = category.ToLowerInvariant();
            if (c.Contains("iban") || c.Contains("carte bancaire") || c.Contains("banque") || c.Contains("crypto")) return ""; // PaymentCard
            if (c.Contains("identité") || c.Contains("sécurité sociale")) return "";                                           // ContactInfo
            if (c.Contains("santé")) return "";                                                                                  // Health
            if (c.Contains("contrat") || c.Contains("juridique")) return "";                                                    // Document
            if (c.Contains("licence")) return "";                                                                                // Tag
            if (c.Contains("certificat") || c.Contains("coffre")) return "";                                                    // Lock
            if (c.Contains(".env") || c.Contains("api") || c.Contains("jeton")) return "";                                   // Code
            if (c.Contains("serveur") || c.Contains("accès techniques")) return "";                                           // Ordinateur / serveur
            return "";                                                                                                           // Permissions (clé)
        }
    }
}
