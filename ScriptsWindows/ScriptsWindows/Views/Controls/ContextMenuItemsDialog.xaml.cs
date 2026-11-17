using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ScriptsWindows.Services;

using ScriptsWindows.Core;

namespace ScriptsWindows.Views.Controls;

public partial class ContextMenuItemsDialog : Window
{
    private readonly Action<string> _onOutput;
    private readonly CancellationToken _ct;

    public ContextMenuItemsDialog(Action<string>? onOutput = null, CancellationToken ct = default)
    {
        InitializeComponent();
        _onOutput = onOutput ?? (_ => { });
        _ct = ct;

        Loaded += (s, e) =>
        {
            // Une nouvelle version de l'application met à jour les entrées et scripts déjà installés.
            PersonalizationService.RefreshForceDeleteScriptIfInstalled();
            PersonalizationService.RefreshAdminTerminalEntriesIfInstalled();
            RefreshStatus();
        };
    }

    private void RefreshStatus()
    {
        bool isClassic = IsClassicMenuEnabled();
        bool isCmdAdminActive = PersonalizationService.IsCmdAdminContextMenuEnabled();
        bool isPsAdminActive = PersonalizationService.IsPowerShellAdminContextMenuEnabled();

        // 1. Statut Menu Classique Windows 10
        if (isClassic)
        {
            BadgeClassicStatus.Background = (SolidColorBrush)ThemeManager.GetBrush("AccentDim"); // Indigo foncé
            BadgeClassicStatus.BorderBrush = (SolidColorBrush)ThemeManager.GetBrush("Accent");
            TxtClassicStatus.Text = "● Windows 10 Classique Actif";
            TxtClassicStatus.Foreground = (SolidColorBrush)ThemeManager.GetBrush("AccentText");

            BtnToggleClassicMenu.Content = "↩ Rétablir menu Win 11";
            BtnToggleClassicMenu.Style = (Style)FindResource("DangerActionButton");
        }
        else
        {
            BadgeClassicStatus.Background = (SolidColorBrush)ThemeManager.GetBrush("BgInput");
            BadgeClassicStatus.BorderBrush = (SolidColorBrush)ThemeManager.GetBrush("Border");
            TxtClassicStatus.Text = "○ Windows 11 Moderne (Par défaut)";
            TxtClassicStatus.Foreground = (SolidColorBrush)ThemeManager.GetBrush("TextSecondary");

            BtnToggleClassicMenu.Content = "✓ Passer au menu Win 10";
            BtnToggleClassicMenu.Style = (Style)FindResource("IndigoActionButton");
        }

        // 2 et 3. Statut CMD Admin et PowerShell Admin
        UpdateToggle(BadgeCmdAdminStatus, TxtCmdAdminStatus, BtnToggleCmdAdmin, isCmdAdminActive);
        UpdateToggle(BadgePowerShellAdminStatus, TxtPowerShellAdminStatus, BtnTogglePowerShellAdmin, isPsAdminActive);

        // 4. Statut Forcer la suppression
        bool isForceDeleteActive = PersonalizationService.IsForceDeleteContextMenuEnabled();
        UpdateToggle(BadgeForceDeleteStatus, TxtForceDeleteStatus, BtnToggleForceDelete, isForceDeleteActive);
    }

    /// <summary>Badge « Actif / Non actif » et bouton Activer / Retirer d'une entrée du clic droit.</summary>
    private void UpdateToggle(System.Windows.Controls.Border badge, System.Windows.Controls.TextBlock text, System.Windows.Controls.Button button, bool active)
    {
        UpdateBadge(badge, text, active);
        button.Content = active ? "Retirer du menu contextuel" : "Activer au clic droit";
        button.Style = (Style)FindResource(active ? "DangerActionButton" : "SuccessActionButton");
    }

    private void BtnToggleForceDelete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PersonalizationService.IsForceDeleteContextMenuEnabled())
            {
                bool removed = PersonalizationService.RemoveForceDeleteContextMenu();
                TxtStatus.Text = removed
                    ? "« Forcer la suppression » a été retiré du menu contextuel."
                    : "Retrait incomplet : relancez l'outil en administrateur.";
                TxtStatus.Foreground = ThemeManager.GetBrush(removed ? "TextSecondary" : "DangerText");
                _onOutput(removed ? "[INFO] « Forcer la suppression » retiré du menu contextuel." : "[WARN] Retrait incomplet de « Forcer la suppression ».");
            }
            else
            {
                var answer = AppMessageBox.Show(this,
                    "Ajouter « Forcer la suppression » au clic droit des fichiers et dossiers ?\n\n" +
                    "Cette commande supprime DÉFINITIVEMENT (sans Corbeille) en passant outre les autorisations : " +
                    "elle ferme les programmes et services qui bloquent et prend le contrôle des fichiers. " +
                    "Une confirmation est demandée à chaque utilisation, et les dossiers de Windows et des profils sont protégés.\n\n" +
                    "Sous Windows 11, elle apparaît dans « Afficher d'autres options » (ou directement avec le menu classique).",
                    "Forcer la suppression", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;

                PersonalizationService.InstallForceDeleteContextMenu();
                TxtStatus.Text = "✓ « Forcer la suppression » est disponible au clic droit des fichiers et dossiers.";
                TxtStatus.Foreground = ThemeManager.GetBrush("SuccessText");
                _onOutput($"[SUCCÈS] « Forcer la suppression » ajouté au clic droit (script : {PersonalizationService.ForceDeleteScriptPath}).");
            }
            RefreshStatus();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            TxtStatus.Foreground = ThemeManager.GetBrush("DangerText");
            _onOutput($"[ERR] Forcer la suppression : {ex.Message}");
        }
    }

    private static bool IsClassicMenuEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private async void BtnToggleClassicMenu_Click(object sender, RoutedEventArgs e)
    {
        BtnToggleClassicMenu.IsEnabled = false;
        try
        {
            bool isClassic = IsClassicMenuEnabled();
            if (isClassic)
            {
                TxtStatus.Text = "Rétablissement du menu moderne Windows 11...";
                _onOutput("[i] Rétablissement du menu contextuel moderne Windows 11...");
                await PersonalizationService.SetModernMenu(_onOutput, _ct);
                TxtStatus.Text = "Redémarrage de l'Explorateur Windows...";
                await PersonalizationService.RestartExplorer(_onOutput, _ct);
                TxtStatus.Text = "✓ Menu moderne Windows 11 d'origine restauré.";
                _onOutput("[OK] Menu moderne Windows 11 restauré.");
            }
            else
            {
                TxtStatus.Text = "Activation du menu classique Windows 10...";
                _onOutput("[i] Activation du grand menu classique Windows 10...");
                await PersonalizationService.SetClassicMenu(_onOutput, _ct);
                TxtStatus.Text = "Redémarrage de l'Explorateur Windows...";
                await PersonalizationService.RestartExplorer(_onOutput, _ct);
                TxtStatus.Text = "✓ Menu contextuel classique Windows 10 activé avec succès !";
                _onOutput("[OK] Menu classique Windows 10 activé.");
            }
            RefreshStatus();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            _onOutput($"[ERR] {ex.Message}");
        }
        finally
        {
            BtnToggleClassicMenu.IsEnabled = true;
        }
    }

    private async void BtnRestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtStatus.Text = "Redémarrage de l'Explorateur Windows...";
            await PersonalizationService.RestartExplorer(_onOutput, _ct);
            TxtStatus.Text = "✓ Explorateur Windows redémarré avec succès.";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            _onOutput($"[ERR] {ex.Message}");
        }
    }

    private static void UpdateBadge(System.Windows.Controls.Border badge, System.Windows.Controls.TextBlock textBlock, bool exists)
    {
        if (exists)
        {
            badge.Background = (SolidColorBrush)ThemeManager.GetBrush("SuccessBg"); // Vert foncé
            badge.BorderBrush = (SolidColorBrush)ThemeManager.GetBrush("Success"); // Vert
            textBlock.Text = "● Actif dans le menu";
            textBlock.Foreground = (SolidColorBrush)ThemeManager.GetBrush("SuccessText");
        }
        else
        {
            badge.Background = (SolidColorBrush)ThemeManager.GetBrush("BgInput"); // Gris foncé
            badge.BorderBrush = (SolidColorBrush)ThemeManager.GetBrush("Border");
            textBlock.Text = "○ Non actif";
            textBlock.Foreground = (SolidColorBrush)ThemeManager.GetBrush("TextSecondary");
        }
    }

    private void BtnToggleCmdAdmin_Click(object sender, RoutedEventArgs e) =>
        ToggleAdminEntry("Ouvrir CMD (Admin)", PersonalizationService.IsCmdAdminContextMenuEnabled,
            PersonalizationService.InstallCmdAdminContextMenu, PersonalizationService.RemoveCmdAdminContextMenu);

    private void BtnTogglePowerShellAdmin_Click(object sender, RoutedEventArgs e) =>
        ToggleAdminEntry("Ouvrir PowerShell (Admin)", PersonalizationService.IsPowerShellAdminContextMenuEnabled,
            PersonalizationService.InstallPowerShellAdminContextMenu, PersonalizationService.RemovePowerShellAdminContextMenu);

    /// <summary>Ajoute ou retire une entrée « Ouvrir … (Admin) » : écriture directe dans le registre, sans fichier .reg.</summary>
    private void ToggleAdminEntry(string label, Func<bool> isEnabled, Func<bool> install, Func<bool> remove)
    {
        try
        {
            bool wasActive = isEnabled();
            bool ok = wasActive ? remove() : install();
            if (wasActive)
            {
                TxtStatus.Text = ok ? $"« {label} » a été retiré du menu contextuel." : $"Retrait incomplet de « {label} » : relancez l'outil en administrateur.";
                _onOutput(ok ? $"[INFO] « {label} » retiré du menu contextuel." : $"[WARN] Retrait incomplet de « {label} ».");
            }
            else
            {
                TxtStatus.Text = ok ? $"✓ « {label} » est disponible au clic droit (Bureau, dossiers, lecteurs, fichiers)."
                                    : $"Impossible d'ajouter « {label} » : relancez l'outil en administrateur.";
                _onOutput(ok ? $"[SUCCÈS] « {label} » ajouté au clic droit (Bureau, dossiers, lecteurs, fichiers)." : $"[WARN] Échec de l'ajout de « {label} ».");
            }
            TxtStatus.Foreground = ThemeManager.GetBrush(!ok ? "DangerText" : wasActive ? "TextSecondary" : "SuccessText");
            RefreshStatus();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Erreur : {ex.Message}";
            TxtStatus.Foreground = ThemeManager.GetBrush("DangerText");
            _onOutput($"[ERR] {label} : {ex.Message}");
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
