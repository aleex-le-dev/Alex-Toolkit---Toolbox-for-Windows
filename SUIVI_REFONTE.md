# 📊 Suivi refonte — Alex Toolkit v4 (C# + WPF)

> Dernière mise à jour : **2026-11-16**
> Statut global : 🟢 **v4.2.31 — audit, refonte de l'interface et outils techniciens terminés** (non publié)

---

## 🗺️ Vue d'ensemble des phases

| Phase | Nom | Statut |
|---|---|---|
| **1** | Fondations (projet, admin, runners, styles) | ✅ Terminé |
| **2** | Shell UI (barre latérale, cartes, console, recherche) | ✅ Terminé |
| **3** | Services principaux (Diagnostic, Réparation, Nettoyage, Réseau, DNS) | ✅ Terminé |
| **4** | Services secondaires (Apps, Sécurité, Matériel, Export, Personnalisation) | ✅ Terminé |
| **5** | Centres unifiés (dialogues par domaine, suppression des doublons) | ✅ Terminé |
| **6** | Audit performances & qualité du code | ✅ Terminé |
| **7** | Refonte de l'interface (design professionnel, thèmes Sombre / Clair) | ✅ Terminé |
| **8** | Outils techniciens & fusion des outils d'un même domaine | ✅ Terminé |
| **9** | Exécutable autonome (runtime inclus) | ✅ Terminé |
| **10** | Validation terrain (PC vierge, actions sensibles) | ⏳ À faire |

---

## 🎨 Interface (phase 7)
- **Système de design** : palette neutre, une seule couleur d'accent, couleurs sémantiques (succès / avertissement / erreur). Plus aucune couleur codée en dur : jetons `DynamicResource` (XAML) et `ThemeManager.GetBrush` (C#).
- **Thèmes Sombre et Clair** appliqués à toutes les fenêtres et mémorisés (`%LocalAppData%\ScriptsWindows\theme.txt`). Les 5 anciens thèmes néon (Aero, Cyberpunk, Matrix, Nordic, Obsidian) ont été retirés.
- **Icônes Segoe Fluent** à la place des emojis (navigation, cartes, en-têtes, console).
- **Boutons unifiés** : famille globale `ActionButton`, `PrimaryActionButton`, `SecondaryActionButton`, `WarningActionButton`, `DangerActionButton`, `IconButton` (72 styles locaux supprimés).
- **Cartes d'outils** épurées : points clés cochés, bouton « Exécuter », bouton **« Bureau »** générique pour les outils qui peuvent créer un script sur le Bureau.
- **Barre latérale** : navigation au survol, mode compact automatique (64 px ↔ 240 px), compteurs discrets, indicateur de sélection.
- **Console** : rendu par lots (1 mise à jour par frame, 3 000 lignes max), barres de progression sur une ligne, barre d'outils à icônes. Sélection de texte corrigée (crash « FlowDocument n'est pas un objet Visual »).
- **Paramètres** : fiche technique du PC copiable, arborescence des outils générée depuis `ToolRegistry` (signale Id en double ou commande manquante), réglages de mise à jour.

## ⚡ Audit performances & qualité (phase 6)
- Collectes PowerShell parallélisées (Centre de diagnostic, Centre réseau), préchauffage des pages au repos, favoris reconstruits seulement si modifiés.
- `PowerShellRunner` : échappement conforme à la ligne de commande Windows, barre de progression désactivée, annulation qui tue le processus, résultats analysés sur stdout uniquement (erreurs dans `LastErrors`).
- Scanner LAN réécrit en .NET natif (l'ancien code nécessitait PowerShell 7), benchmark DNS en vraies requêtes UDP.
- Verrou global d'exécution (`ScriptExecutionCoordinator`) : un seul outil à la fois.
- Mise à jour fiabilisée : versions stables avec exe uniquement, contrôle taille + signature, relance de l'ancienne version en cas d'échec.

## 🧰 Outils techniciens & fusions (phase 8)
| Outil | Domaine | Interface |
|---|---|---|
| Intervention technicien et rapport client | Diagnostic | `InterventionDialog` |
| Écrans bleus et périphériques en erreur | Diagnostic | `CrashDriverDialog` |
| Gérer les comptes utilisateurs | Sécurité | `UserAccountsDialog` |
| Désinfection et détournements | Sécurité | `MalwareCleanupDialog` |
| Sauvegarder et transférer un profil utilisateur | Disque | `ProfileMigrationDialog` |
| Supprimer les applications préinstallées | Applications | `DebloatDialog` |
| Tester la puissance globale de mon PC (graphique) | Matériel | `BenchmarkDialog` |
| **Dépannage des imprimantes** (fusion avec « Débloquer l'imprimante ») | Réparation | `PrinterCenterDialog` + `Debloquer_Impression.bat` |
| Logiciels installés (onglet ajouté) | Mises à jour | `WingetUpdatesDialog` |
| Documents confidentiels (nouveau moteur) | Secrets | `SensitiveDataScanner` |

Outils existants enrichis : S.M.A.R.T. réel et audit complet (Centre de diagnostic), état et réinitialisation de Windows Update (Intégrité système), point de restauration corrigé, export Wi-Fi fiabilisé, menu contextuel « PowerShell (Admin) » corrigé.

## 📦 Répartition actuelle (49 outils)
| Catégorie | Outils | Catégorie | Outils |
|---|---|---|---|
| Diagnostic | 6 | Matériel | 4 |
| Réparation | 7 | Applications | 3 |
| Réseau | 7 | Personnalisation | 3 |
| Sécurité | 6 | Secrets | 4 |
| Disque | 5 | Démarrage / Nettoyage / Mises à jour / Nouveautés | 1 chacune |

---

## 🚀 Résultat de publication
- **Exécutable** : `AlexToolkit_v4.2.31.exe` (à la racine, généré par `build.bat`)
- **Taille** : ≈ 68 Mo — **autonome**, runtime .NET 10 Desktop inclus et compressé
- **Architecture** : Windows x64, .NET 10 LTS, ReadyToRun
- **Manifeste** : élévation administrateur requise (UAC)

## ⏳ Reste à faire
- Tester l'exe sur un PC sans .NET installé.
- Tester en réel les actions sensibles : réinstallation / suppression d'imprimante, modification de comptes, réinitialisation de Windows Update, suppression d'applications, restauration de profil.
- Dater la section « Non publié » du `CHANGELOG.md`, commiter et publier la release.
