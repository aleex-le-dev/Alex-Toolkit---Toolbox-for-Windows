using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScriptsWindows.Core;
using ScriptsWindows.Services;

namespace ScriptsWindows.Views.Controls;

/// <summary>BitLocker (BitLockerService) : état réel par disque, clé de récupération, suspension, déverrouillage, activation sûre, désactivation.</summary>
public partial class BitLockerDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly bool _home = BitLockerService.IsHomeEdition();
    private bool _busy;

    public BitLockerDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;
        _poll.Tick += async (_, _) => { if (!_busy) await LoadAsync(); };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _poll.Stop();
    }

    // ─── Construction de l'interface ───
    private static TextBlock Txt(string text, double size = 12, string brush = "TextSecondary", bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = ThemeManager.GetBrush(brush), TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static Button Btn(string text, string style, Func<Task> click, bool enabled = true)
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(StyleProperty, style);
        b.Click += async (_, _) => await click();
        return b;
    }

    private static string TextBrush(string level) => level == "TextMuted" ? "TextMuted" : level + "Text";

    private async Task LoadAsync()
    {
        if (!_busy && PanelVolumes.Children.Count == 0) TxtStatus.Text = "Lecture de l'état des disques…";
        try
        {
            var vols = await BitLockerService.GetVolumesAsync(_ct);
            RenderVolumes(vols);
        }
        catch (Exception ex) { TxtStatus.Text = $"Erreur de lecture : {ex.Message}"; }
    }

    internal void RenderVolumes(List<BlVolume> vols)
    {
        bool limited = vols.Any(v => v.Limited);
        var info = new List<string>();
        if (limited) info.Add("Lecture limitée : Windows refuse le détail BitLocker sans droits administrateur (relancez Alex Toolkit en administrateur pour gérer les disques).");
        if (_home) info.Add("Windows Famille : BitLocker ne peut pas être activé sur ce PC (seul le « chiffrement de l'appareil » des Paramètres, si le PC le prend en charge), mais un disque chiffré se lit et se déverrouille normalement.");
        PanelInfo.Visibility = info.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtInfo.Text = string.Join("\n", info);

        PanelVolumes.Children.Clear();
        foreach (var v in vols) PanelVolumes.Children.Add(Row(v));
        if (vols.Count == 0) PanelVolumes.Children.Add(Txt("Aucun disque trouvé.", 13));

        bool moving = vols.Any(v => v.State is BlState.Encrypting or BlState.Decrypting);
        if (moving) _poll.Start(); else _poll.Stop();
        if (!_busy) TxtStatus.Text = $"{vols.Count} disque(s), {vols.Count(v => v.State == BlState.Protected)} protégé(s)." + (moving ? " Avancement actualisé toutes les 5 s." : "");
    }

    private Border Row(BlVolume v)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        var head = new WrapPanel();
        string kind = v.IsOs ? "disque de Windows" : v.Removable ? "amovible" : "disque de données";
        head.Children.Add(Txt($"{v.Letter}:  {(v.Label.Length > 0 ? v.Label : "Disque local")}  —  {BitLockerService.Size(v.Size)}, {kind}", 13, "TextPrimary", true));
        var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(10, 0, 0, 0), Background = ThemeManager.GetBrush(v.Level == "TextMuted" ? "BgInput" : v.Level + "Bg") };
        badge.Child = Txt(v.StateLabel, 11.5, TextBrush(v.Level), true);
        head.Children.Add(badge);
        info.Children.Add(head);

        string detail = v.Limited ? "Détail indisponible sans droits administrateur."
            : v.State == BlState.NotEncrypted ? "Les données sont lisibles par quiconque récupère ce disque."
            : v.State == BlState.Locked ? "Contenu illisible tant que le disque n'est pas déverrouillé (mot de passe ou clé de récupération)."
            : v.State == BlState.AwaitingActivation ? "Chiffré mais la clé est stockée en clair : n'importe qui peut encore lire ce disque. La protection s'active en se connectant avec un compte Microsoft (Paramètres > Chiffrement de l'appareil)."
            : v.State == BlState.Suspended ? "Clé stockée en clair sur le disque : protection inactive jusqu'à la reprise."
            : "";
        if (!v.Limited && v.Protectors.Length > 0)
            detail = (detail.Length > 0 ? detail + "  ·  " : "") + "Déverrouillage : " + string.Join(", ", v.Protectors.Distinct().Select(BitLockerService.ProtectorLabel)) + (v.Method.Length > 0 && v.Method != "None" ? $"  ·  {v.Method}" : "");
        if (!v.Limited && v.State is not (BlState.NotEncrypted or BlState.Locked) && !v.HasRecoveryKey)
            detail += "  ·  Aucune clé de récupération !";
        if (detail.Length > 0) info.Children.Add(Txt(detail, 11.5, !v.Limited && v.State != BlState.NotEncrypted && v.State != BlState.Locked && !v.HasRecoveryKey ? "DangerText" : "TextSecondary"));

        if (v.State is BlState.Encrypting or BlState.Decrypting or BlState.Paused)
        {
            double pct = v.State == BlState.Decrypting ? 100 - v.Percent : v.Percent;
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(pct, 0, 100), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - Math.Clamp(pct, 0, 100), GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = ThemeManager.GetBrush("Accent") };
            var back = new Border { CornerRadius = new CornerRadius(3), Background = ThemeManager.GetBrush("BgInput") };
            Grid.SetColumnSpan(back, 2); bar.Children.Add(back); bar.Children.Add(fill);
            info.Children.Add(bar);
        }
        g.Children.Add(info);

        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (!v.Limited)
        {
            switch (v.State)
            {
                case BlState.Locked:
                    actions.Children.Add(Btn("Déverrouiller", "PrimaryActionButton", () => { ShowUnlock(v); return Task.CompletedTask; }));
                    break;
                case BlState.Protected:
                    actions.Children.Add(Btn("Clé de récupération", "ActionButton", () => ShowKeysAsync(v)));
                    if (v.IsOs) actions.Children.Add(Btn("Suspendre", "ActionButton", () => { ShowSuspend(v); return Task.CompletedTask; }));
                    else actions.Children.Add(Btn("Verrouiller", "ActionButton", () => LockAsync(v)));
                    actions.Children.Add(Btn("Désactiver", "DangerActionButton", () => DisableAsync(v)));
                    break;
                case BlState.Suspended:
                    actions.Children.Add(Btn("Reprendre", "PrimaryActionButton", () => Simple(v, "Reprise de la protection", () => BitLockerService.ResumeAsync(v.Letter, _ct), "protection réactivée")));
                    actions.Children.Add(Btn("Clé de récupération", "ActionButton", () => ShowKeysAsync(v)));
                    actions.Children.Add(Btn("Désactiver", "DangerActionButton", () => DisableAsync(v)));
                    break;
                case BlState.AwaitingActivation:
                    actions.Children.Add(Btn("Paramètres", "ActionButton", () => { CommandRunner.StartProcess("explorer.exe", "ms-settings:deviceencryption"); return Task.CompletedTask; }));
                    actions.Children.Add(Btn("Désactiver", "DangerActionButton", () => DisableAsync(v)));
                    break;
                case BlState.NotEncrypted:
                    if (!_home) actions.Children.Add(Btn("Activer", "ActionButton", () => { ShowEnable(v); return Task.CompletedTask; }));
                    break;
            }
        }
        Grid.SetColumn(actions, 1); g.Children.Add(actions);
        return new Border { Child = g, Padding = new Thickness(4, 10, 4, 10), BorderBrush = ThemeManager.GetBrush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private void OpenAction(params UIElement[] content)
    {
        ActionContent.Children.Clear();
        foreach (var c in content) ActionContent.Children.Add(c);
        PanelAction.Visibility = Visibility.Visible;
    }

    private void Report(bool ok, string msg)
    {
        TxtStatus.Text = msg;
        _onOutput((ok ? "[OK] " : "❌ [ERREUR] ") + msg);
    }

    private async Task Simple(BlVolume v, string label, Func<Task<(bool Ok, string Error)>> work, string done)
    {
        if (_busy) return;
        _busy = true; TxtStatus.Text = label + "…";
        try
        {
            var (ok, err) = await work();
            Report(ok, ok ? $"{v.Letter}: {done}." : $"{label} sur {v.Letter}: refusé : {err}");
            if (ok) PanelAction.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Report(false, $"{label} : {ex.Message}"); }
        finally { _busy = false; await LoadAsync(); }
    }

    // ─── Clé de récupération ───
    private async Task ShowKeysAsync(BlVolume v)
    {
        TxtStatus.Text = "Lecture des clés…";
        List<(string Id, string Key)> keys;
        try { keys = await BitLockerService.GetRecoveryKeysAsync(v.Letter, _ct); }
        catch (Exception ex) { TxtStatus.Text = $"Lecture impossible : {ex.Message}"; return; }
        var items = new List<UIElement> { Txt($"Clé de récupération de {v.Letter}:", 13, "TextPrimary", true),
            Txt("Demandée par l'écran bleu BitLocker après un changement de matériel, une mise à jour du BIOS ou une carte mère remplacée. L'identificateur affiché à l'écran doit correspondre.", 11.5) };
        if (keys.Count == 0)
            items.Add(Txt("Ce disque n'a aucune clé de récupération : si sa puce TPM ou son mot de passe est perdu, les données sont perdues.", 12, "DangerText", true));
        foreach (var (id, key) in keys)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            btns.Children.Add(Btn("Copier", "ActionButton", () => { try { Clipboard.SetText(key); TxtStatus.Text = "Clé copiée dans le presse-papiers."; } catch { TxtStatus.Text = "Presse-papiers indisponible."; } return Task.CompletedTask; }));
            btns.Children.Add(Btn("Enregistrer…", "ActionButton", () => { SaveKey(v.Letter, id, key); return Task.CompletedTask; }));
            DockPanel.SetDock(btns, Dock.Right); row.Children.Add(btns);
            var t = new StackPanel();
            t.Children.Add(Txt($"Identificateur : {id.Trim('{', '}')}", 11.5));
            var k = Txt(key, 15, "TextPrimary", true); k.FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono");
            t.Children.Add(k);
            row.Children.Add(t);
            items.Add(row);
        }
        OpenAction(items.ToArray());
        TxtStatus.Text = $"{keys.Count} clé(s) de récupération.";
        _onOutput($"[i] Clé(s) de récupération de {v.Letter}: affichée(s) ({keys.Count}).");
    }

    /// <summary>Enregistre la clé dans un .txt ; avertit si le fichier est sur le disque chiffré lui-même. Renvoie true si enregistrée.</summary>
    private bool SaveKey(char letter, string id, string key)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Cle_recuperation_BitLocker_{Environment.MachineName}_{letter}.txt",
            Filter = "Fichier texte|*.txt",
            InitialDirectory = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady)?.RootDirectory.FullName
                               ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        while (dlg.ShowDialog(this) == true)
        {
            if (char.ToUpperInvariant(dlg.FileName[0]) == letter &&
                AppMessageBox.Show(this, $"Ce fichier sera sur {letter}: : si ce disque se verrouille, la clé sera inaccessible.\n\nChoisir plutôt une clé USB ou un autre disque ?", "Emplacement déconseillé",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) continue;
            try
            {
                File.WriteAllText(dlg.FileName, BitLockerService.KeyFileText(letter, id, key));
                TxtStatus.Text = $"Clé enregistrée : {dlg.FileName}";
                _onOutput($"[OK] Clé de récupération de {letter}: enregistrée dans {dlg.FileName}");
                return true;
            }
            catch (Exception ex) { AppMessageBox.Show(this, $"Enregistrement impossible : {ex.Message}", "BitLocker", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        return false;
    }

    // ─── Suspension ───
    private void ShowSuspend(BlVolume v)
    {
        var btns = new WrapPanel { Margin = new Thickness(-6, 10, 0, 0) };
        btns.Children.Add(Btn("Pour 1 redémarrage", "PrimaryActionButton", () => Simple(v, "Suspension", () => BitLockerService.SuspendAsync(v.Letter, 1, _ct), "protection suspendue pour 1 redémarrage (reprise automatique ensuite)")));
        btns.Children.Add(Btn("Pour 3 redémarrages", "ActionButton", () => Simple(v, "Suspension", () => BitLockerService.SuspendAsync(v.Letter, 3, _ct), "protection suspendue pour 3 redémarrages")));
        btns.Children.Add(Btn("Jusqu'à reprise manuelle", "ActionButton", () => Simple(v, "Suspension", () => BitLockerService.SuspendAsync(v.Letter, 0, _ct), "protection suspendue jusqu'à la reprise manuelle")));
        OpenAction(Txt($"Suspendre la protection de {v.Letter}:", 13, "TextPrimary", true),
            Txt("À faire avant une mise à jour du BIOS / UEFI, un changement de carte mère ou de réglage du TPM ou du démarrage sécurisé : sinon Windows demandera la clé de récupération au démarrage. Le disque reste chiffré ; la protection reprend seule après le nombre de redémarrages choisi.", 11.5),
            btns);
    }

    // ─── Déverrouillage ───
    private void ShowUnlock(BlVolume v)
    {
        bool isKey = false;
        var pwd = new PasswordBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        pwd.SetResourceReference(StyleProperty, "FieldPasswordBox");
        var key = new TextBox { Width = 520, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono") };
        key.SetResourceReference(StyleProperty, "FieldTextBox");
        var choice = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 10, 0, 8) };
        Button bPwd = null!, bKey = null!;
        void Mode(bool k)
        {
            isKey = k;
            bPwd.SetResourceReference(StyleProperty, k ? "ActionButton" : "PrimaryActionButton");
            bKey.SetResourceReference(StyleProperty, k ? "PrimaryActionButton" : "ActionButton");
            pwd.Visibility = k ? Visibility.Collapsed : Visibility.Visible;
            key.Visibility = k ? Visibility.Visible : Visibility.Collapsed;
            if (k) key.Focus(); else pwd.Focus();
        }
        bPwd = Btn("Mot de passe", "PrimaryActionButton", () => { Mode(false); return Task.CompletedTask; });
        bKey = Btn("Clé de récupération (48 chiffres)", "ActionButton", () => { Mode(true); return Task.CompletedTask; });
        choice.Children.Add(bPwd); choice.Children.Add(bKey);
        var hint = Txt("", 11.5, "DangerText");
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(pwd); line.Children.Add(key);
        line.Children.Add(Btn("Déverrouiller", "PrimaryActionButton", async () =>
        {
            string secret = isKey ? BitLockerService.NormalizeRecoveryKey(key.Text) ?? "" : pwd.Password;
            if (secret.Length == 0) { hint.Text = isKey ? "Clé invalide : 48 chiffres attendus (8 blocs de 6), une faute de frappe est probable." : "Saisissez le mot de passe."; return; }
            hint.Text = "";
            await Simple(v, "Déverrouillage", () => BitLockerService.UnlockAsync(v.Letter, secret, isKey, _ct), "déverrouillé");
            pwd.Clear();
        }));
        OpenAction(Txt($"Déverrouiller {v.Letter}:", 13, "TextPrimary", true),
            Txt("Disque d'un autre PC : le mot de passe choisi à l'activation, ou la clé de récupération (compte Microsoft du client : account.microsoft.com/devicesrecoverykey, fichier ou impression).", 11.5),
            choice, line, hint);
        pwd.Focus();
    }

    // ─── Verrouillage / désactivation ───
    private Task LockAsync(BlVolume v)
    {
        if (AppMessageBox.Show(this, $"Verrouiller {v.Letter}: ? Les fichiers ouverts depuis ce disque seront fermés de force.", "Verrouiller", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        return Simple(v, "Verrouillage", () => BitLockerService.LockAsync(v.Letter, _ct), "verrouillé");
    }

    private Task DisableAsync(BlVolume v)
    {
        if (AppMessageBox.Show(this, $"Désactiver BitLocker sur {v.Letter}: ?\n\nLe disque sera entièrement déchiffré en arrière-plan (le PC reste utilisable, compter de quelques minutes à plusieurs heures). Ses données redeviendront lisibles par quiconque récupère le disque.",
                $"Désactiver BitLocker sur {v.Letter}:", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        return Simple(v, "Désactivation", () => BitLockerService.DisableAsync(v.Letter, _ct), "déchiffrement lancé");
    }

    // ─── Activation : clé de récupération créée et enregistrée AVANT le chiffrement ───
    private void ShowEnable(BlVolume v)
    {
        var p1 = new PasswordBox { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
        var p2 = new PasswordBox { Width = 260 };
        p1.SetResourceReference(StyleProperty, "FieldPasswordBox"); p2.SetResourceReference(StyleProperty, "FieldPasswordBox");
        var hint = Txt("", 11.5, "DangerText");
        var items = new List<UIElement>
        {
            Txt($"Activer BitLocker sur {v.Letter}:", 13, "TextPrimary", true),
            Txt((v.IsOs ? "Le disque de Windows se déverrouillera seul grâce à la puce TPM. " : "Ce disque demandera un mot de passe sur un autre PC" + (v.Removable ? "" : " (déverrouillage automatique possible ensuite dans l'outil Windows)") + ". ") +
                "Une clé de récupération est d'abord créée : vous devrez l'enregistrer (clé USB conseillée) avant que le chiffrement commence. Seul l'espace utilisé est chiffré (rapide), le PC reste utilisable.", 11.5)
        };
        if (!v.IsOs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(new StackPanel { Children = { Txt("Mot de passe (8 caractères min.)", 11.5), p1 } });
            row.Children.Add(new StackPanel { Children = { Txt("Confirmation", 11.5), p2 } });
            items.Add(row);
        }
        var go = Btn("Créer la clé de récupération et chiffrer", "PrimaryActionButton", () => EnableAsync(v, p1.Password, p2.Password, hint));
        go.Margin = new Thickness(0, 10, 0, 0); go.HorizontalAlignment = HorizontalAlignment.Left;
        items.Add(go); items.Add(hint);
        OpenAction(items.ToArray());
    }

    private async Task EnableAsync(BlVolume v, string pwd, string confirm, TextBlock hint)
    {
        if (_busy) return;
        if (!v.IsOs && pwd.Length < 8) { hint.Text = "Mot de passe trop court (8 caractères minimum)."; return; }
        if (!v.IsOs && pwd != confirm) { hint.Text = "Les deux mots de passe ne correspondent pas."; return; }
        hint.Text = "";
        _busy = true; TxtStatus.Text = "Création de la clé de récupération…";
        try
        {
            var k = await BitLockerService.AddRecoveryKeyAsync(v.Letter, _ct);
            if (k is null) { Report(false, $"Windows n'a pas créé de clé de récupération sur {v.Letter}: (TPM absent ou désactivé ? édition de Windows ?)."); return; }
            var (id, key) = k.Value;
            AppMessageBox.Show(this, $"Clé de récupération de {v.Letter}: :\n\n{key}\n\nEnregistrez-la à l'étape suivante (clé USB conseillée). Sans elle, les données peuvent être perdues après une panne ou un changement de matériel.",
                "Clé de récupération", MessageBoxButton.OK, MessageBoxImage.Information);
            if (!SaveKey(v.Letter, id, key))
            {
                await BitLockerService.RemoveProtectorAsync(v.Letter, id, _ct);
                Report(false, "Activation annulée : la clé de récupération n'a pas été enregistrée (rien n'a été chiffré).");
                return;
            }
            TxtStatus.Text = "Démarrage du chiffrement…";
            var (ok, err) = await BitLockerService.EnableAsync(v.Letter, v.IsOs, v.IsOs ? null : pwd, _ct);
            if (!ok) await BitLockerService.RemoveProtectorAsync(v.Letter, id, _ct);
            Report(ok, ok ? $"Chiffrement de {v.Letter}: lancé (clé de récupération enregistrée)." : $"Activation refusée sur {v.Letter}: : {err}");
            if (ok) PanelAction.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Report(false, $"Activation : {ex.Message}"); }
        finally { _busy = false; await LoadAsync(); }
    }

    private void BtnCloseAction_Click(object sender, RoutedEventArgs e) { PanelAction.Visibility = Visibility.Collapsed; ActionContent.Children.Clear(); }
    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    private void BtnPanel_Click(object sender, RoutedEventArgs e) => CommandRunner.StartProcess("control.exe", "/name Microsoft.BitLockerDriveEncryption");
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
