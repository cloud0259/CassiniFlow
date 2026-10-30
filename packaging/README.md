# CassiniFlow — Packaging & Installateurs

Ce répertoire contient l'outillage complet de packaging pour générer les distributions **Portables** et **Installables** de CassiniFlow sous Windows et Linux.

---

## 1. Windows (x64)

### Mode Portable (`.zip`)
- **Fichier** : `CassiniFlow-win-x64-portable.zip`
- **Contenu** : Exécutables autonomes `CassiniFlow.Desktop.exe` et `cli/CassiniFlow.Cli.exe` avec toutes les dépendances requises (runtime .NET 10 embarqué en mode *self-contained*).
- **Utilisation** : Dézipper dans n'importe quel dossier et lancer directement `CassiniFlow.Desktop.exe`. Ne nécessite aucun droit administrateur ni prérequis .NET.

### Mode Installable (`Setup .exe`)
- **Fichier** : `CassiniFlow-Setup-x64.exe`
- **Technologie** : [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`packaging/windows/CassiniFlow.iss`)
- **Fonctionnalités** :
  - Installation utilisateur (sans élévation) ou globale système (Program Files)
  - Création du raccourci dans le menu Démarrer et sur le Bureau
  - Enregistrement des associations de fichiers astronomiques (`.ser`, `.fit`, `.fits`)
  - Désinstallation propre et conforme dans les paramètres Windows

### Compilation locale
```powershell
.\packaging\windows\build-installer.ps1 -Version "1.0.0"
```

---

## 2. Linux (x64)

### Mode Portable (`.tar.gz`)
- **Fichier** : `CassiniFlow-linux-x64-portable.tar.gz`
- **Contenu** : Binaires autonomes `CassiniFlow.Desktop` et `cli/CassiniFlow.Cli`, script d'installation `install.sh`, script de désinstallation `uninstall.sh`, raccourci bureau `cassiniflow.desktop` et icône.
- **Installation en un clic** :
```bash
tar -xzf CassiniFlow-linux-x64-portable.tar.gz
cd CassiniFlow-linux-x64
./install.sh            # Pour l'utilisateur actuel (~/.local/share/cassiniflow)
# ou sudo ./install.sh --system  # Pour tout le système (/opt/cassiniflow)
```

### Mode Installable Paquet Debian / Ubuntu (`.deb`)
- **Fichier** : `cassiniflow-linux-x64.deb` / `cassiniflow_<version>_amd64.deb`
- **Installation** :
```bash
sudo dpkg -i cassiniflow-linux-x64.deb || sudo apt-get install -f
```
- **Compilation locale** :
```bash
./packaging/linux/build-deb.sh "1.0.0"
```

---

## 3. Automatisation CI/CD (GitHub Actions)

Le workflow `.github/workflows/release.yml` s'exécute automatiquement lors de la création d'un tag Git `v*` ou manuellement via `workflow_dispatch` :
1. Exécute la suite complète de tests unitaires sur Ubuntu et Windows.
2. Compile les binaires en mode `self-contained: true`.
3. Compile l'installateur Windows Inno Setup (`CassiniFlow-Setup-x64.exe`).
4. Compile le paquet Debian (`cassiniflow-linux-x64.deb`).
5. Empaquète les archives portables (`.zip` et `.tar.gz`).
6. Publie l'ensemble des 4 artéfacts sur la Release GitHub correspondante.

