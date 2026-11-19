using System.IO;
using System.Windows;
using Microsoft.Win32;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>Sauvegarde du profil utilisateur vers un support externe et restauration sur un autre PC.</summary>
public partial class ProfileMigrationDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private readonly List<MigrationItem> _items = ProfileMigrationService.GetItems();
    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _progressLive;

    public ProfileMigrationDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        TxtSubtitle.Text = $"Profil de « {Environment.UserName} » sur {Environment.MachineName} : fichiers personnels, favoris, Wi-Fi et logiciels.";
        ListItems.ItemsSource = _items;
        RefreshDrives();

        Loaded += async (_, _) =>
        {
            await ProfileMigrationService.ComputeSizesAsync(_items);
            UpdateTotal();
            if (!_running) TxtStatus.Text = "Cochez les éléments puis sauvegardez ou restaurez.";
        };
        foreach (var i in _items) i.PropertyChanged += (_, _) => UpdateTotal();

        Closing += (_, e) =>
        {
            if (!_running) return;
            if (AppMessageBox.Show(this, "Une copie est en cours. L'arrêter et fermer ?", "Copie en cours", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;
            else _runCts?.Cancel();
        };
    }

    private void UpdateTotal()
    {
        long total = _items.Where(i => i.IsSelected && i.Kind == MigrationKind.Folder).Sum(i => Math.Max(0, i.SizeBytes));
        TxtTotal.Text = total > 0 ? $"≈ {ProfileMigrationService.FormatSize(total)} de fichiers" : "";
    }

    private void SetRunning(bool running, string status)
    {
        _running = running;
        BtnBackup.IsEnabled = BtnRestore.IsEnabled = BtnClose.IsEnabled = BtnClientBackup.IsEnabled = BtnClientRestore.IsEnabled = BtnClientUnlock.IsEnabled = !running;
        BtnCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ListItems.IsEnabled = ChkSkipRegenerable.IsEnabled = !running;
        TxtStatus.Text = status;
    }

    private async void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        if (!_items.Any(i => i.IsSelected && i.IsAvailable)) { TxtStatus.Text = "Cochez au moins un élément."; return; }

        var dlg = new OpenFolderDialog { Title = "Choisissez où enregistrer la sauvegarde (disque externe, clé USB…)" };
        if (dlg.ShowDialog(this) != true) return;

        // Contrôle de l'espace disponible sur la destination
        long needed = _items.Where(i => i.IsSelected && i.Kind == MigrationKind.Folder).Sum(i => Math.Max(0, i.SizeBytes));
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(dlg.FolderName)!);
            if (drive.IsReady && drive.AvailableFreeSpace < needed)
            {
                AppMessageBox.Show(this, $"Espace insuffisant sur {drive.Name} : {ProfileMigrationService.FormatSize(drive.AvailableFreeSpace)} libres pour ≈ {ProfileMigrationService.FormatSize(needed)} à copier.",
                                "Espace insuffisant", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch { /* chemin réseau : pas de contrôle */ }

        bool skipRegenerable = ChkSkipRegenerable.IsChecked == true;
        await RunAsync("Sauvegarde en cours…", async (log, ct) =>
        {
            string root = await ProfileMigrationService.BackupAsync(_items, dlg.FolderName, log, ct, ShowProgress, skipRegenerable);
            Dispatcher.Invoke(() => TxtStatus.Text = $"Sauvegarde terminée : {root}");
            FolderDialogHelper.OpenInExplorer(root);
        });
    }

    private async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Sélectionnez le dossier « Sauvegarde_… » à restaurer" };
        if (dlg.ShowDialog(this) != true) return;

        var manifest = ProfileMigrationService.ReadManifest(dlg.FolderName);
        if (manifest == null)
        {
            AppMessageBox.Show(this, $"Ce dossier n'est pas une sauvegarde Alex Toolkit (fichier {ProfileMigrationService.ManifestName} absent).",
                            "Sauvegarde introuvable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var toRestore = _items.Where(i => i.IsSelected && manifest.Items.Contains(i.Key)).ToList();
        if (toRestore.Count == 0) { TxtStatus.Text = "Aucun élément coché n'est présent dans cette sauvegarde."; return; }

        bool reinstall = false;
        if (toRestore.Any(i => i.Kind == MigrationKind.Apps))
            reinstall = AppMessageBox.Show(this, "Réinstaller automatiquement les logiciels de l'ancien PC via Winget ?\n(Peut prendre longtemps ; les logiciels indisponibles sont ignorés.)",
                                        "Logiciels", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        string summary = string.Join(", ", toRestore.Select(i => i.Label));
        if (AppMessageBox.Show(this, $"Sauvegarde de « {manifest.User} » ({manifest.Computer}) du {manifest.Date:dd/MM/yyyy HH:mm}.\n\nRestaurer : {summary} ?",
                            "Restaurer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await RunAsync("Restauration en cours…", (log, ct) => ProfileMigrationService.RestoreAsync(toRestore, dlg.FolderName, reinstall, log, ct, ShowProgress));
    }

    private async Task RunAsync(string status, Func<Action<string>, CancellationToken, Task> work)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        SetRunning(true, status);
        TxtLog.Clear();
        TxtProgItem.Text = "Préparation…"; TxtProgDetail.Text = ""; TxtProgPct.Text = "";
        TxtProgOverall.Text = ""; SetBar(BarItem, 0); SetBar(BarOverall, 0);
        PanelProgress.Visibility = Visibility.Visible;
        _progressLive = true;
        _onOutput($"\n🚀 [LANCEMENT] {status.TrimEnd('…')}");

        // Chaque ligne va dans le journal de la fenêtre (horodatée) et dans celui de l'appli.
        void Log(string text)
        {
            _onOutput(text);
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var line in text.Split('\n'))
                    if (line.Trim().Length > 0) TxtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line.TrimEnd('\r')}{Environment.NewLine}");
                TxtLog.ScrollToEnd();
            });
        }

        try
        {
            await work(Log, _runCts.Token);
            _progressLive = false;   // une mise à jour encore en attente ne doit pas réafficher 99 %
            if (TxtStatus.Text == status) TxtStatus.Text = "Terminé.";
            SetBar(BarItem, 100); SetBar(BarOverall, 100);
            // Le total prévu inclut les dossiers ignorés (node_modules…) : à la fin, tout ce qui devait l'être est copié.
            TxtProgOverall.Text = "Total : 100 %";
            TxtProgPct.Text = "100 %"; TxtProgItem.Text = "Terminé"; TxtProgDetail.Text = TxtStatus.Text;
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Opération arrêtée."; Log("🛑 [ANNULÉ] Copie interrompue."); TxtProgItem.Text = "Arrêté"; }
        catch (Exception ex) { TxtStatus.Text = $"Erreur : {ex.Message}"; Log($"[ERR] {ex.Message}"); TxtProgItem.Text = "Erreur"; }
        finally { _progressLive = false; _running = false; SetRunning(false, TxtStatus.Text); }
    }

    private static void SetBar(System.Windows.Controls.Grid bar, double pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        bar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    /// <summary>Affiche l'avancement reçu du service (appelé depuis n'importe quel thread).</summary>
    private void ShowProgress(ProfileMigrationService.CopyProgress p) => Dispatcher.BeginInvoke(() =>
    {
        if (!_progressLive) return;
        string F(long b) => ProfileMigrationService.FormatSize(b);
        TxtProgItem.Text = $"{p.Item}  ({p.Index}/{p.Count})";
        if (p.ItemTotal > 0 || p.FilesTotal > 0)
        {
            double ratio = p.ItemTotal > 0 ? (double)p.ItemDone / p.ItemTotal : (double)p.FilesDone / Math.Max(1, p.FilesTotal);
            int pct = (int)Math.Floor(Math.Min(1.0, ratio) * 100);
            SetBar(BarItem, pct);
            TxtProgPct.Text = $"{pct} %";
            string detail = $"{F(p.ItemDone)} / {F(p.ItemTotal)}  ·  {p.FilesDone:N0} / {p.FilesTotal:N0} fichiers";
            if (p.BytesPerSecond > 0)
            {
                detail += $"  ·  {F((long)p.BytesPerSecond)}/s";
                if (p.ItemTotal > p.ItemDone)
                {
                    var left = TimeSpan.FromSeconds(Math.Min(359999, (p.ItemTotal - p.ItemDone) / p.BytesPerSecond));
                    detail += $"  ·  reste {(left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss"))}";
                }
            }
            TxtProgDetail.Text = detail;
        }
        else
        {
            // Wi-Fi, favoris, logiciels : durée inconnue
            SetBar(BarItem, 0); TxtProgPct.Text = ""; TxtProgDetail.Text = "En cours…";
        }
        if (p.OverallTotal > 0)
        {
            int all = (int)Math.Floor(Math.Min(1.0, (double)p.OverallDone / p.OverallTotal) * 100);
            SetBar(BarOverall, all);
            TxtProgOverall.Text = $"Total : {all} %  ({F(p.OverallDone)} / {F(p.OverallTotal)})";
        }
    });

    // ─── Intervention client (scripts console) ───

    private void RefreshDrives()
    {
        var drives = ProfileMigrationService.GetBackupDrives();
        CmbDrive.ItemsSource = drives;
        CmbDrive.SelectedItem = drives.FirstOrDefault(d => !d.IsSystem) ?? drives.FirstOrDefault();
    }

    private void BtnClientBackup_Click(object sender, RoutedEventArgs e)
    {
        string client = ProfileMigrationService.SanitizeClientName(TxtClient.Text);
        if (client.Length == 0) { TxtStatus.Text = "Indiquez le nom du client."; TxtClient.Focus(); return; }
        if (CmbDrive.SelectedItem is not ProfileMigrationService.DriveChoice drive) { TxtStatus.Text = "Branchez puis choisissez le disque externe."; RefreshDrives(); return; }
        if (drive.IsSystem && AppMessageBox.Show(this,
                $"{drive.Letter}: est le disque de Windows : la sauvegarde serait perdue en cas de formatage.\n\nSauvegarder quand même sur ce disque ?",
                "Disque de Windows", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        LaunchClient(true, client, drive.Letter,
            $"Sauvegarde du client « {client} » vers {drive.Letter}:\\{ProfileMigrationService.ClientBackupFolder}");
    }

    private void BtnClientRestore_Click(object sender, RoutedEventArgs e)
        => LaunchClient(false, null, null, "Restauration : choisissez la sauvegarde dans la console");

    /// <summary>Ancien disque inaccessible (droits NTFS d'un autre compte) : console Unlock-ClientDrive.ps1 sur le dossier choisi.</summary>
    private void BtnClientUnlock_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier ou disque à débloquer (ex. E:\\Users\\Client sur l'ancien disque)" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ProfileMigrationService.LaunchUnlockScript(dlg.FolderName);
            TxtStatus.Text = $"Déblocage de {dlg.FolderName} : confirmez dans la console PowerShell qui vient de s'ouvrir.";
            _onOutput($"[OK] Déblocage de l'accès lancé pour {dlg.FolderName} (console PowerShell)");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            _onOutput($"❌ [ERREUR] Déblocage de l'accès : {ex.Message}");
        }
    }

    private void LaunchClient(bool backup, string? client, char? drive, string what)
    {
        try
        {
            ProfileMigrationService.LaunchClientScript(backup, client, drive);
            TxtStatus.Text = $"{what} : suivez la console PowerShell qui vient de s'ouvrir.";
            _onOutput($"[OK] {what} (console PowerShell ouverte ; rapport_intervention.log dans le dossier du client)");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            _onOutput($"❌ [ERREUR] Lancement du script d'intervention : {ex.Message}");
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
