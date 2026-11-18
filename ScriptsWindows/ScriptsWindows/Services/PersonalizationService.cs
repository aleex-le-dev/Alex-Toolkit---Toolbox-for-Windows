using System.IO;
using System.Windows;
using Microsoft.Win32;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

/// <summary>Service personnalisation — context_menu, god_mode, gaming_mode, photo_viewer.</summary>
public class PersonalizationService
{
    // Menu classique Windows 10 : réglage propre à chaque compte (HKCU), écrit directement dans le registre.
    private const string ClassicMenuClsidKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    // Menu contextuel classique  (activate_classic)
    public static Task SetClassicMenu(Action<string> onOutput, CancellationToken ct = default)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(ClassicMenuClsidKey + @"\InprocServer32", writable: true))
            key.SetValue("", "");   // valeur par défaut vide : désactive le menu moderne de Windows 11
        onOutput("[OK] Menu contextuel classique activé");
        return Task.CompletedTask;
    }

    // Menu contextuel moderne  (restore_modern)
    public static Task SetModernMenu(Action<string> onOutput, CancellationToken ct = default)
    {
        Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuClsidKey, throwOnMissingSubKey: false);
        onOutput("[OK] Menu contextuel moderne restauré");
        return Task.CompletedTask;
    }

    // Redémarrage de l'Explorateur Windows
    public static async Task RestartExplorer(Action<string> onOutput, CancellationToken ct = default)
    {
        onOutput("[i] Redémarrage de l'Explorateur Windows...");
        await CommandRunner.RunAsync("taskkill /f /im explorer.exe & start explorer.exe", onOutput, ct);
        onOutput("[OK] Explorateur Windows redémarré");
    }

    /// <summary>Ouvre la boîte de dialogue de configuration du menu contextuel (Windows 10 / Windows 11).</summary>
    public static async Task OpenContextMenuDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Views.Controls.ContextMenuDialog(onOutput, ct)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();
        });
    }

    // God Mode  (sys_god_mode)
    public static Task CreateGodMode(Action<string> onOutput, CancellationToken ct = default)
    {
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                                "GodMode.{ED7BA470-8E54-465E-825C-99712043E01C}");
        if (!Directory.Exists(dest))
            Directory.CreateDirectory(dest);
        onOutput("[OK] Dossier God Mode créé sur le Bureau");
        return Task.CompletedTask;
    }

    // Mode Gaming  (sys_gaming_mode)
    public static async Task ToggleGamingMode(bool enable, Action<string> onOutput, CancellationToken ct = default)
    {
        var val = enable ? "1" : "0";
        await CommandRunner.RunAsync($"reg add \"HKCU\\Software\\Microsoft\\GameBar\" /v AutoGameModeEnabled /t REG_DWORD /d {val} /f", onOutput, ct);
        onOutput($"[OK] Mode Gaming {(enable ? "activé" : "désactivé")}");
    }

    // Visionneuse de photos Windows  (photo_viewer_toggle)
    public static async Task EnablePhotoViewer(Action<string> onOutput, CancellationToken ct = default)
    {
        await PowerShellRunner.RunAsync(@"
$cmd = '%SystemRoot%\System32\rundll32.exe ""%ProgramFiles%\Windows Photo Viewer\PhotoViewer.dll"", ImageView_Fullscreen %1'
reg add 'HKCR\Applications\photoviewer.dll\shell\open' /v 'MuiVerb' /t REG_SZ /d '@photoviewer.dll,-3043' /f | Out-Null
reg add 'HKCR\Applications\photoviewer.dll\shell\open\command' /ve /t REG_EXPAND_SZ /d $cmd /f | Out-Null
Write-Output '[OK] Visionneuse de photos Windows activée'
", onOutput, ct);
    }

    public static async Task DisablePhotoViewer(Action<string> onOutput, CancellationToken ct = default)
    {
        await CommandRunner.RunAsync("reg delete \"HKCR\\Applications\\photoviewer.dll\" /f", onOutput, ct);
        onOutput("[OK] Visionneuse de photos Windows désactivée");
    }

    // ─── Raccourcis Bureau : Arrêter et Redémarrer avec icônes officielles ───
    public const string ShutdownShortcutName = "Arrêter le PC.lnk";
    public const string RestartShortcutName = "Redémarrer le PC.lnk";

    /// <summary>
    /// Ouvre l'interface moderne de création des boutons d'arrêt et de redémarrage sur le Bureau.
    /// </summary>
    public static async Task OpenPowerShortcutsDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Views.Controls.PowerShortcutsDialog(onOutput, ct)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();
        });
    }

    /// <summary>
    /// Vérifie si un raccourci est présent sur le Bureau.
    /// </summary>
    public static bool IsShortcutPresent(string shortcutName)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return File.Exists(Path.Combine(desktop, shortcutName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Supprime un raccourci du Bureau.
    /// </summary>
    public static bool RemoveShortcut(string shortcutName)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var file = Path.Combine(desktop, shortcutName);
            if (File.Exists(file))
            {
                File.Delete(file);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Crée le bouton "Arrêter le PC" avec l'icône rouge officielle Windows Power (immédiat).
    /// </summary>
    public static bool CreateShutdownShortcut()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        var icon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll") + ",27";
        return CreateDesktopLnk(ShutdownShortcutName, target, "/s /t 0", icon, "Éteindre complètement l'ordinateur immédiatement");
    }

    /// <summary>
    /// Crée le bouton "Redémarrer le PC" avec l'icône de redémarrage officielle Windows (immédiat).
    /// </summary>
    public static bool CreateRestartShortcut()
    {
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        var icon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll") + ",238";
        return CreateDesktopLnk(RestartShortcutName, target, "/r /t 0", icon, "Redémarrer l'ordinateur immédiatement");
    }

    /// <summary>
    /// Crée un raccourci Windows .lnk sur le Bureau avec cible, arguments et icône personnalisée.
    /// </summary>
    public static bool CreateDesktopLnk(string shortcutFileName, string targetPath, string arguments, string iconLocation, string description)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!Directory.Exists(desktop))
        {
            Directory.CreateDirectory(desktop);
        }
        var shortcutPath = Path.Combine(desktop, shortcutFileName);

        // 1. Création via COM WScript.Shell
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.Arguments = arguments;
                shortcut.IconLocation = iconLocation;
                shortcut.Description = description;
                shortcut.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                shortcut.Save();
                if (File.Exists(shortcutPath)) return true;
            }
        }
        catch
        {
            // Secours ci-dessous
        }

        // 2. Secours via commande PowerShell
        try
        {
            var psCode = $"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{shortcutPath.Replace("'", "''")}'); $s.TargetPath = '{targetPath.Replace("'", "''")}'; $s.Arguments = '{arguments.Replace("'", "''")}'; $s.IconLocation = '{iconLocation.Replace("'", "''")}'; $s.Description = '{description.Replace("'", "''")}'; $s.Save()";
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCode}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(3000);
            return File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
    }

    // Raccourcis bureau hérités (sys_shortcuts_bureau)
    public static Task CreateShortcuts(Action<string> onOutput, CancellationToken ct = default)
    {
        bool ok1 = CreateShutdownShortcut();
        bool ok2 = CreateRestartShortcut();
        if (ok1 && ok2)
        {
            onOutput("[OK] Raccourcis 'Arrêter le PC' et 'Redémarrer le PC' créés sur le Bureau avec leurs icônes.");
        }
        else
        {
            onOutput("[INFO] Création des raccourcis terminée.");
        }
        return Task.CompletedTask;
    }

    // ─── Personnalisation des éléments du Menu Contextuel (Clic Droit) ───
    // ─── Écriture directe des entrées « Ouvrir … (Admin) » (plus de fichier .reg ni de confirmation de l'Éditeur du Registre) ───
    // HKLM : l'entrée apparaît pour tous les comptes du PC, y compris quand l'application a été élevée avec un autre compte administrateur.
    // Le registre lance OpenAdminTerminal.ps1 (dossier protégé) en lui passant le chemin comme simple argument :
    // les noms avec apostrophe (« Projets d'été ») fonctionnent, et un clic droit sur un fichier ouvre son dossier.
    private const string CmdAdminVerb = "OpenCmdAdmin";
    private const string PowerShellAdminVerb = "OpenPowerShellAdmin";

    /// <summary>Commande du registre. « %V » suivi d'une espace : « C:\ » ne doit pas finir par \" (guillemet échappé).</summary>
    private static string AdminTerminalCommand(string shell) =>
        $"\"{WindowsPowerShellExe}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{AdminTerminalScriptPath}\" -Shell {shell} -Path \"%V \"";

    /// <summary>Crée l'entrée de clic droit dans chaque emplacement (Bureau, fond de dossier, dossier, lecteur, bibliothèque, fichier).</summary>
    private static void WriteAdminShellEntries(string verb, string label, string icon, string command)
    {
        foreach (var container in ContextMenuContainerPaths)
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"{container}\{verb}", writable: true);
            key.SetValue("", label);
            key.SetValue("Icon", icon);
            key.SetValue("HasLUAShield", "");
            key.SetValue("Position", "Top");
            using var cmd = key.CreateSubKey("command", writable: true);
            cmd.SetValue("", command);
        }
    }

    /// <summary>Ajoute « Ouvrir CMD (Admin) » au clic droit (écriture directe dans le registre).</summary>
    public static bool InstallCmdAdminContextMenu()
    {
        InstallToolScript(AdminTerminalScriptPath, "OpenAdminTerminal.ps1");
        RemoveShellEntries(GetMatchingSubKeyNames);   // anciennes variantes (HKCU de l'ancien .reg, autres noms)
        WriteAdminShellEntries(CmdAdminVerb, "Ouvrir CMD (Admin)", "cmd.exe", AdminTerminalCommand("cmd"));
        return IsCmdAdminContextMenuEnabled();
    }

    /// <summary>Ajoute « Ouvrir PowerShell (Admin) » au clic droit (écriture directe dans le registre).</summary>
    public static bool InstallPowerShellAdminContextMenu()
    {
        InstallToolScript(AdminTerminalScriptPath, "OpenAdminTerminal.ps1");
        RemoveShellEntries(GetMatchingPowerShellSubKeyNames);
        WriteAdminShellEntries(PowerShellAdminVerb, "Ouvrir PowerShell (Admin)", "powershell.exe", AdminTerminalCommand("powershell"));
        return IsPowerShellAdminContextMenuEnabled();
    }

    /// <summary>
    /// À l'ouverture de l'outil : remet à niveau les entrées déjà actives (ancienne commande, emplacement « fichiers » manquant,
    /// script absent ou d'une autre version). Uniquement en administrateur, pour ne jamais retirer sans pouvoir réécrire.
    /// </summary>
    public static void RefreshAdminTerminalEntriesIfInstalled()
    {
        if (!IsElevated()) return;
        try
        {
            bool cmd = IsCmdAdminContextMenuEnabled(), ps = IsPowerShellAdminContextMenuEnabled();
            if (!cmd && !ps) return;
            bool scriptOk = File.Exists(AdminTerminalScriptPath) &&
                            File.ReadAllBytes(AdminTerminalScriptPath).AsSpan().SequenceEqual(ReadEmbeddedScript("OpenAdminTerminal.ps1"));
            if (cmd && (!scriptOk || !HasExpectedCommand(CmdAdminVerb, AdminTerminalCommand("cmd")))) InstallCmdAdminContextMenu();
            if (ps && (!scriptOk || !HasExpectedCommand(PowerShellAdminVerb, AdminTerminalCommand("powershell")))) InstallPowerShellAdminContextMenu();
        }
        catch { }
    }

    private static bool HasExpectedCommand(string verb, string command) =>
        ContextMenuContainerPaths.All(container =>
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{container}\{verb}\command");
            return k?.GetValue("") as string == command;
        });

    /// <summary>
    /// Ouvre l'interface de personnalisation du menu contextuel (CMD Admin sur Bureau & dossiers).
    /// </summary>
    public static async Task OpenContextMenuItemsDialog(Action<string> onOutput, CancellationToken ct = default)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Views.Controls.ContextMenuItemsDialog(onOutput, ct)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();
        });
    }

    private static readonly string[] ContextMenuSubKeyNames = new[]
    {
        "OpenCmdAdmin",
        "runas_cmd",
        "OpenCmdHere"
    };

    private static readonly string[] ContextMenuContainerPaths = new[]
    {
        @"Software\Classes\DesktopBackground\shell",
        @"Software\Classes\Directory\Background\shell",
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Drive\shell",
        @"Software\Classes\LibraryFolder\Background\shell",
        @"Software\Classes\*\shell"   // fichiers : la console s'ouvre dans le dossier du fichier
    };

    /// <summary>
    /// Recherche toutes les sous-clés correspondantes à CMD Admin (noms connus ou libellés contenant CMD + Admin).
    /// </summary>
    private static HashSet<string> GetMatchingSubKeyNames(RegistryKey rootKey, string containerPath)
    {
        var keys = new HashSet<string>(ContextMenuSubKeyNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var container = rootKey.OpenSubKey(containerPath);
            if (container != null)
            {
                foreach (var name in container.GetSubKeyNames())
                {
                    using var sub = container.OpenSubKey(name);
                    if (sub != null)
                    {
                        var defVal = sub.GetValue("") as string ?? "";
                        if (defVal.Contains("cmd", StringComparison.OrdinalIgnoreCase) &&
                            (defVal.Contains("admin", StringComparison.OrdinalIgnoreCase) || defVal.Contains("administrateur", StringComparison.OrdinalIgnoreCase)))
                        {
                            keys.Add(name);
                        }
                    }
                }
            }
        }
        catch { }
        return keys;
    }

    /// <summary>
    /// Vérifie si une entrée "Ouvrir CMD (Admin)" est activée dans le menu contextuel (HKCU ou HKLM).
    /// </summary>
    public static bool IsCmdAdminContextMenuEnabled()
    {
        try
        {
            foreach (var container in ContextMenuContainerPaths)
            {
                // Vérification HKCU
                foreach (var subKey in GetMatchingSubKeyNames(Registry.CurrentUser, container))
                {
                    using var k1 = Registry.CurrentUser.OpenSubKey($@"{container}\{subKey}");
                    if (k1 != null) return true;
                }

                // Vérification HKLM
                foreach (var subKey in GetMatchingSubKeyNames(Registry.LocalMachine, container))
                {
                    using var k2 = Registry.LocalMachine.OpenSubKey($@"{container}\{subKey}");
                    if (k2 != null) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Supprime directement (HKCU et HKLM) les entrées de clic droit reconnues par <paramref name="matcher"/>.</summary>
    private static void RemoveShellEntries(Func<RegistryKey, string, HashSet<string>> matcher)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var container in ContextMenuContainerPaths)
            {
                foreach (var subKey in matcher(hive, container))
                {
                    try
                    {
                        using var parent = hive.OpenSubKey(container, writable: true);
                        parent?.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
                    }
                    catch { }
                }
            }
        }
    }

    /// <summary>Retire toutes les entrées « Ouvrir CMD (Admin) » du menu contextuel (HKCU et HKLM, anciens noms compris).</summary>
    public static bool RemoveCmdAdminContextMenu()
    {
        RemoveShellEntries(GetMatchingSubKeyNames);
        if (!IsPowerShellAdminContextMenuEnabled()) RemoveToolFiles("OpenAdminTerminal.ps1");
        return !IsCmdAdminContextMenuEnabled();
    }

    // ─── Personnalisation : Ouvrir PowerShell (Admin) au Clic Droit ───
    private static readonly string[] ContextMenuPowerShellSubKeyNames = new[]
    {
        "OpenPowerShellAdmin",
        "PowerShellAdmin",
        "runas_ps",
        "OpenPsAdmin",
        "PowerShellHereAdmin"
    };

    /// <summary>
    /// Recherche toutes les sous-clés correspondantes à PowerShell Admin (noms connus ou libellés contenant PowerShell + Admin).
    /// </summary>
    private static HashSet<string> GetMatchingPowerShellSubKeyNames(RegistryKey rootKey, string containerPath)
    {
        var keys = new HashSet<string>(ContextMenuPowerShellSubKeyNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var container = rootKey.OpenSubKey(containerPath);
            if (container != null)
            {
                foreach (var name in container.GetSubKeyNames())
                {
                    using var sub = container.OpenSubKey(name);
                    if (sub != null)
                    {
                        var defVal = sub.GetValue("") as string ?? "";
                        if (defVal.Contains("powershell", StringComparison.OrdinalIgnoreCase) &&
                            (defVal.Contains("admin", StringComparison.OrdinalIgnoreCase) || defVal.Contains("administrateur", StringComparison.OrdinalIgnoreCase)))
                        {
                            keys.Add(name);
                        }
                    }
                }
            }
        }
        catch { }
        return keys;
    }

    /// <summary>
    /// Vérifie si une entrée "Ouvrir PowerShell (Admin)" est activée dans le menu contextuel (HKCU ou HKLM).
    /// </summary>
    public static bool IsPowerShellAdminContextMenuEnabled()
    {
        try
        {
            foreach (var container in ContextMenuContainerPaths)
            {
                // Vérification HKCU
                foreach (var subKey in GetMatchingPowerShellSubKeyNames(Registry.CurrentUser, container))
                {
                    using var k1 = Registry.CurrentUser.OpenSubKey($@"{container}\{subKey}");
                    if (k1 != null) return true;
                }

                // Vérification HKLM
                foreach (var subKey in GetMatchingPowerShellSubKeyNames(Registry.LocalMachine, container))
                {
                    using var k2 = Registry.LocalMachine.OpenSubKey($@"{container}\{subKey}");
                    if (k2 != null) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Retire toutes les entrées « Ouvrir PowerShell (Admin) » du menu contextuel (HKCU et HKLM, anciens noms compris).</summary>
    public static bool RemovePowerShellAdminContextMenu()
    {
        RemoveShellEntries(GetMatchingPowerShellSubKeyNames);
        if (!IsCmdAdminContextMenuEnabled()) RemoveToolFiles("OpenAdminTerminal.ps1");
        return !IsPowerShellAdminContextMenuEnabled();
    }

    // ─── Dossier des scripts du clic droit : C:\ProgramData\AlexToolkit ───
    // Les scripts y sont lancés avec les droits administrateur : le dossier n'est modifiable que par les administrateurs et SYSTEM.
    public static string ToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlexToolkit");
    public static string ForceDeleteScriptPath => Path.Combine(ToolsDir, "ForceDelete.ps1");
    public static string AdminTerminalScriptPath => Path.Combine(ToolsDir, "OpenAdminTerminal.ps1");
    private static string WindowsPowerShellExe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static byte[] ReadEmbeddedScript(string name)
    {
        using var s = typeof(PersonalizationService).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Script {name} introuvable dans l'application.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Crée le dossier (ou le reprend) avec des droits « écriture pour les administrateurs seulement ».
    /// Un dossier préexistant qui n'appartenait ni aux administrateurs ni à SYSTEM (créé par un autre utilisateur) est vidé.
    /// </summary>
    private static void EnsureToolsDir()
    {
        var dir = new DirectoryInfo(ToolsDir);
        // Un dossier qui serait un lien (jonction) est retiré : on ne suit jamais un lien vers ailleurs.
        if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { dir.Delete(); dir.Refresh(); }
        bool untrusted = false;
        if (!dir.Exists) dir.Create();
        else
        {
            var owner = dir.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
            untrusted = owner == null || !(owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid)
                                           || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid));
        }

        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        const System.Security.AccessControl.InheritanceFlags inherit =
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;

        var sec = new System.Security.AccessControl.DirectorySecurity();
        sec.SetOwner(admins);
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new(system, System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        sec.AddAccessRule(new(admins, System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        sec.AddAccessRule(new(users, System.Security.AccessControl.FileSystemRights.ReadAndExecute, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        dir.SetAccessControl(sec);

        if (untrusted)
            foreach (var entry in dir.EnumerateFileSystemInfos()) DeleteToolEntry(entry);
    }

    private static void DeleteToolEntry(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) sub.Delete(true);
            else { entry.Attributes = FileAttributes.Normal; entry.Delete(); }
        }
        catch { }
    }

    /// <summary>Écrit un script embarqué dans le dossier protégé (l'ancien fichier, ou un lien posé à sa place, est d'abord supprimé).</summary>
    private static void InstallToolScript(string path, string resourceName)
    {
        EnsureToolsDir();
        var existing = new FileInfo(path);
        if (existing.Exists) DeleteToolEntry(existing);
        File.WriteAllBytes(path, ReadEmbeddedScript(resourceName));
    }

    /// <summary>
    /// Installe (ou remet à jour) un script embarqué dans le dossier protégé et renvoie son chemin.
    /// Utilisé par les outils qui lancent un script autonome en console (ex. intervention client).
    /// </summary>
    public static string InstallSharedScript(string resourceName)
    {
        string path = Path.Combine(ToolsDir, resourceName);
        InstallToolScript(path, resourceName);
        return path;
    }

    /// <summary>Supprime les fichiers indiqués, puis le dossier s'il est vide.</summary>
    private static void RemoveToolFiles(params string[] patterns)
    {
        try
        {
            var dir = new DirectoryInfo(ToolsDir);
            if (!dir.Exists) return;
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { dir.Delete(); return; }
            foreach (var pattern in patterns)
                foreach (var f in dir.EnumerateFiles(pattern)) DeleteToolEntry(f);
            if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete();
        }
        catch { }
    }

    // ─── Personnalisation : « Forcer la suppression » au clic droit (fichiers et dossiers) ───
    private const string ForceDeleteVerb = "AlexForceDelete";
    private static readonly string[] ForceDeleteShellKeys =
    {
        @"SOFTWARE\Classes\*\shell\" + ForceDeleteVerb,
        @"SOFTWARE\Classes\Directory\shell\" + ForceDeleteVerb,
    };

    public static bool IsForceDeleteContextMenuEnabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(ForceDeleteShellKeys[0]);
            return k != null && File.Exists(ForceDeleteScriptPath);
        }
        catch { return false; }
    }

    /// <summary>Installe le script et ajoute « Forcer la suppression » au clic droit des fichiers et dossiers.</summary>
    public static void InstallForceDeleteContextMenu()
    {
        InstallToolScript(ForceDeleteScriptPath, "ForceDelete.ps1");

        string command = $"\"{WindowsPowerShellExe}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ForceDeleteScriptPath}\" -Path \"%1\"";
        string icon = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",131";

        foreach (var keyPath in ForceDeleteShellKeys)
        {
            using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
            key.SetValue("", "Forcer la suppression");
            key.SetValue("Icon", icon);
            using var cmd = key.CreateSubKey("command", writable: true);
            cmd.SetValue("", command);
        }
    }

    /// <summary>Met à jour le script installé s'il diffère de celui de cette version de l'application (administrateur uniquement).</summary>
    public static void RefreshForceDeleteScriptIfInstalled()
    {
        if (!IsElevated()) return;
        try
        {
            if (!IsForceDeleteContextMenuEnabled()) return;
            if (File.ReadAllBytes(ForceDeleteScriptPath).AsSpan().SequenceEqual(ReadEmbeddedScript("ForceDelete.ps1"))) return;
            InstallForceDeleteContextMenu();
        }
        catch { }
    }

    /// <summary>Retire l'entrée du clic droit et supprime le script installé (et sa DLL en cache).</summary>
    public static bool RemoveForceDeleteContextMenu()
    {
        foreach (var keyPath in ForceDeleteShellKeys)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); } catch { }
        }
        RemoveToolFiles("ForceDelete.ps1", "ForceDelete_*.dll");
        return !IsForceDeleteContextMenuEnabled();
    }
}
