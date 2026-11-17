# 🗺️ Roadmap — Alex Toolkit

Feuille de route d'**Alex Toolkit**. Les fonctionnalités sont priorisées selon les retours et les besoins des techniciens de maintenance.

---

## 🟢 Version actuelle : v4.2.31
- [x] Application C# / WPF (.NET 10 LTS) en **exécutable unique autonome** : runtime inclus, aucune installation ni téléchargement (fonctionne hors ligne, sur clé USB).
- [x] **49 outils** répartis en 13 catégories, avec recherche instantanée, favoris et arborescence générée automatiquement.
- [x] Interface professionnelle : thèmes **Sombre / Clair** mémorisés, icônes Windows (Segoe Fluent), boutons et couleurs unifiés.
- [x] Console latérale intégrée (PowerShell / CMD en UTF-8, rendu par lots, barres de progression sur une ligne).
- [x] Mise à jour intégrée via GitHub Releases avec vérification du fichier téléchargé.
- [x] **Outils techniciens** : intervention + rapport client HTML, écrans bleus et périphériques en erreur, comptes utilisateurs, désinfection, sauvegarde / transfert de profil, suppression des applications préinstallées.
- [x] **Dépannage des imprimantes** en interface graphique (+ script de déblocage sur le Bureau).
- [x] **Test de puissance du PC** en graphique (jauge, comparatifs, historique).
- [x] Inventaire des logiciels installés (export CSV / HTML) et recherche avancée de documents confidentiels (≈ 200 mots-clés, analyse du contenu).
- [x] Audit Windows 11 24H2 / NPU, bouclier de confidentialité, optimiseur Gaming, benchmark DNS.

---

## 🟡 Prochaine version : v4.3 (en préparation)
- [ ] **Validation terrain** : lancement de l'exe sur un PC vierge sans .NET ; tests réels des actions sensibles (réinstallation d'imprimante, comptes, debloat, restauration de profil).
- [ ] **Détecteur de gros fichiers et doublons** : scan rapide des volumes pour retrouver les fichiers volumineux oubliés.
- [ ] **Analyse des minidumps** : lecture du pilote fautif dans les fichiers `.dmp` (en complément des codes d'arrêt déjà traduits).
- [ ] **Export / import des réglages de l'application** (favoris, thème, préférences de mise à jour).

---

## 🔵 Idées à l'étude (v4.4+)
- [ ] **Raccourci clavier global** pour ouvrir l'application ou la console.
- [ ] **Mode portable** : données de l'application stockées à côté de l'exe (clé USB) au lieu de `%LocalAppData%`.
- [ ] **Comparaison avant / après intervention** automatique (démarrage, disque, réseau) intégrée au rapport client.
- [ ] **Version anglaise** avec bascule automatique selon la langue du système.

---

💬 **Une idée de fonctionnalité ?**
Participez aux discussions sur [GitHub Discussions → Ideas](https://github.com/aleex-le-dev/Alex-Toolkit---Toolbox-for-Windows/discussions) !
