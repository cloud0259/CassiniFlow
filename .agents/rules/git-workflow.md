# WORKFLOW GIT & GESTION DU DÃ‰PÃ”T

## 1. Structure du DÃ©pÃ´t & Exclusion des DonnÃ©es Lourde
- Les fichiers binaires de build (`bin/`, `obj/`, `publish/`) ne doivent **JAMAIS** Ãªtre versionnÃ©s.
- Les flux d'images astrophotographiques lourds (`.ser`, `.fit`, `.fits`, `.avi`, `.mp4`) et les dossiers de sortie utilisateur (`cassiniflow_output/`) sont exclus via `.gitignore`.
- Seules les sources de code, configurations, documentations et tests synthÃ©tiques lÃ©gers doivent Ãªtre committÃ©s.

## 2. Convention de Commits (Conventional Commits)
Les messages de commit doivent suivre le standard Conventional Commits :
- `feat:` : Ajout d'une nouvelle fonctionnalitÃ© (ex: nouveau format d'entrÃ©e, nouveau filtre).
- `refactor:` : Refactorisation structurelle sans changement de comportement (ex: passage Ã  l'architecture hexagonale).
- `fix:` : Correction de bug.
- `perf:` : Optimisation de performance ou rÃ©duction d'allocation mÃ©moire.
- `docs:` : Mise Ã  jour de la documentation ou des rÃ¨gles.
- `test:` : Ajout ou modification de tests unitaires.
- `chore:` : TÃ¢ches d'infrastructure de projet (ex: configuration Git, mise Ã  jour des packages).

## 3. Branches
- Branche principale : `main`.
- Branches de fonctionnalitÃ©s / refactorings : `feat/...`, `refactor/...`.
