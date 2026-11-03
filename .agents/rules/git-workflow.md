# WORKFLOW GIT & GESTION DU DÉPÔT

## 1. Structure du Dépôt & Exclusion des Données Lourdes
- Les fichiers binaires de build (`bin/`, `obj/`, `publish/`) ne doivent **JAMAIS** être versionnés.
- Les flux d'images astrophotographiques lourds (`.ser`, `.fit`, `.fits`, `.avi`, `.mp4`) et les dossiers de sortie utilisateur (`cassiniflow_output/`) sont exclus via `.gitignore`.
- Seules les sources de code, configurations, documentations et tests synthétiques légers doivent être committés.

## 2. Convention de Commits (Conventional Commits)
Les messages de commit doivent suivre le standard Conventional Commits :
- `feat:` : Ajout d'une nouvelle fonctionnalité (ex: nouveau format d'entrée, nouveau filtre).
- `refactor:` : Refactorisation structurelle sans changement de comportement (ex: passage à l'architecture hexagonale).
- `fix:` : Correction de bug.
- `perf:` : Optimisation de performance ou réduction d'allocation mémoire.
- `docs:` : Mise à jour de la documentation ou des règles.
- `test:` : Ajout ou modification de tests unitaires.
- `chore:` : Tâches d'infrastructure de projet (ex: configuration Git, mise à jour des packages).

## 3. Branches & Stratégie de Release
- **`main`** : Branche de production officielle. Tout commit sur `main` correspond à une version stable et publique étiquetée (`v*`).
- **`develop`** : Branche d'intégration quotidienne. Toutes les PRs de développement y sont fusionnées.
- Branches de travail : `feat/...`, `fix/...`, `refactor/...`, qui ciblent systématiquement **`develop`**.
- **Publication d'une release** : s'effectue via une PR **`develop` ➔ `main`** lorsque le jalon (milestone) de la version est atteint. La fusion sur `main` déclenche le tag, le changelog et la création des installateurs (.exe, .deb, archives portables).

## 4. Pull Requests & Validation
- **Ne JAMAIS valider ou fusionner (merge) une Pull Request sans autorisation explicite préalable de l'utilisateur.**
- Toujours travailler dans la branche dédiée, exécuter les tests locaux, pousser la branche, créer la PR si demandé, et **attendre que l'utilisateur ait testé et validé lui-même** avant toute fusion sur `main`.
