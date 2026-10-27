# CassiniFlow 🪐

[![CI & Build Pipeline](https://github.com/your-username/CassiniFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/your-username/CassiniFlow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-blue)](#prerequisites)
[![UI](https://img.shields.io/badge/UI-Avalonia%2011.2-purple)](https://avaloniaui.net/)

**CassiniFlow** est un processeur moderne, haute performance et multiplateforme (.NET 10 / C# 14 / Avalonia UI / CLI) dédié au prétraitement des séquences d'astrophotographie planétaire, lunaire et solaire (*lucky imaging*).

Conçu comme une relève moderne aux outils historiques de prétraitement (comme PIPP), il prend en charge l'ingestion de flux volumineux, le centrage et la stabilisation planétaire automatique, le tri par qualité de seeing atmosphérique à très faible empreinte mémoire, la calibration et l'export direct vers vos logiciels d'empilement favoris (**AutoStakkert!**, **Siril**, **Registax**).

---

## 🌟 Fonctionnalités Clés

### 📹 Formats & Codecs (Entrées / Sorties)
- **SER (Lucam Recorder)** : Lecture et écriture haute vitesse 8-bit et 16-bit (Mono, Bayer RAW, RGB) avec conservation des horodatages UTC précis par trame (trailer timestamps).
- **FITS (Flexible Image Transport System)** : Lecture et export des fichiers FITS astronomiques mono et couleur 16-bit.
- **Conteneurs Vidéo Universels** : Décodage haute vitesse (AVI, MP4, MOV, etc.) via **OpenCvSharp** avec fallback et transcodage transparent via le moteur CLI **FFmpeg**.
- **Images standards** : Séquences d'images (BMP, etc.).
- **Intégration WinJUPOS** : Génération automatique des noms de fichiers normés WinJUPOS (`YYYY-MM-DD-HHMM_S-System`) pour faciliter la dé-rotation planétaire ultérieure.

### 🔬 Moteur Algorithmique Haute Performance
- **Débayérisation (Démosaïçage RAW)** :
  - Support de toutes les matrices de Bayer : `RGGB`, `BGGR`, `GBRG`, `GRBG`.
  - Algorithmes : *Bilinear*, *HQ Linear*, et *Malvar-He-Cutler* (gradient-corrected).
- **Détection & Centrage Planétaire** :
  - Détection automatique du limbe planétaire et calcul du centre de gravité (Center of Gravity / CoG).
  - Mesure automatique du diamètre apparent en pixels (`PlanetSizeAnalyzer`).
  - Alignement automatique de rotation (`AutoRotationAligner`).
  - Rejet automatique des trames vides, corrompues ou surexposées.
- **Tri & Sélection par Qualité (Seeing atmosphérique)** :
  - Évaluation de netteté par gradient d'énergie et variance laplacienne.
  - **Tri par Min-Heap borné (*Single-Pass Bounded Top-K Heap*)** : maintien d'une consommation RAM inférieure à 900 Mo même sur des séquences de plus de 10 000 trames 16-bit.
- **Recadrage Dynamique ou Fixe** : Stabilisation et recadrage centré (ex. 512×512, 800×800, ou ajusté sur la planète).
- **Calibration Astrophotographique** : Soustraction de Master Dark, division par Master Flat, et élimination automatique des pixels chauds (*Hot Pixels*).
- **Modes de Traitement** :
  - **Batch Mode** : Traitement automatisé fichier par fichier.
  - **Join Mode** : Concaténation chronologique de plusieurs vidéos consécutives en un unique fichier SER horodaté.

### 🖥️ Double Interface Utilisateur
- **CassiniFlow.Desktop** : Interface graphique moderne et fluide sous **Avalonia 11** :
  - Contrôle d'affichage en temps réel `AstroViewerControl` avec zoom fluide, pan et réticules de centrage.
  - Bilinguisme natif à la volée (**Français / Anglais**).
  - Gestion des profils de traitement préenregistrés (Jupiter, Saturne, Lune, Soleil, etc.).
  - Compatible Windows et Linux (testé sous Ubuntu / WSLg).
- **CassiniFlow.Cli** : Exécutable en ligne de commande headless, idéal pour scripter des pipelines et automatiser le traitement par lot.

---

## 🏛️ Architecture & Conception

CassiniFlow est développé selon les principes de l'**Architecture Hexagonale (Ports & Adapters)** et du **Domain-Driven Design (DDD)** :

```text
       ┌────────────────────────┐      ┌────────────────────────┐
       │   CassiniFlow.Desktop  │      │    CassiniFlow.Cli     │
       │     (Avalonia UI)      │      │       (Console)        │
       └───────────┬────────────┘      └───────────┬────────────┘
                   │                               │
                   ▼                               ▼
       ┌────────────────────────────────────────────────────────┐
       │                 CassiniFlow.Application                │
       │    Use Cases (RunPipeline, Preview, MeasurePlanet)     │
       │               Pipeline Step Pattern                    │
       └───────────┬───────────────────────────────▲────────────┘
                   │                               │
                   ▼                               │
       ┌────────────────────────┐                  │
       │   CassiniFlow.Domain   │                  │
       │ (Pur, zéro dépendance) │                  │
       │  AstroFrame, Centroid, │                  │
       │ QualityScore, Invariants│                 │
       └────────────────────────┘                  │
                                                   │
       ┌───────────────────────────────────────────┴────────────┐
       │               CassiniFlow.Infrastructure               │
       │ Codecs (SER, FITS), Adapters (OpenCV, FFmpeg CLI)      │
       │ Readers/Writers Factories, ProfileSerializer           │
       └────────────────────────────────────────────────────────┘
```

- **Zero-Allocation dans les boucles de rendu** : Utilisation intensive de `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>` et `ArrayPool<T>` pour traiter des dizaines de gigaoctets de données vidéo sans déclencher le ramasse-miettes (GC).
- **Multiplateforme** : Exécution native sur Windows et Linux x64/arm64.

---

## 🚀 Démarrage Rapide

### Prérequis
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) ou version ultérieure.
- *(Optionnel pour les flux MP4/vidéo non-SER)* : [FFmpeg](https://ffmpeg.org/) installé dans votre `PATH`.

### Compilation

Clonez le dépôt et compilez la solution :

```bash
git clone https://github.com/your-username/CassiniFlow.git
cd CassiniFlow
dotnet build CassiniFlow.slnx -c Release
```

### Lancement des Tests Unitaires

```bash
dotnet test CassiniFlow.slnx
```

---

## 📖 Utilisation

### Interface Graphique (Desktop)

Pour démarrer l'application avec interface graphique :

```bash
dotnet run --project src/CassiniFlow.Desktop/CassiniFlow.Desktop.csproj -c Release
```

1. **Glissez-déposez** vos fichiers SER, FITS ou vidéos dans l'application.
2. Choisissez un profil prédéfini (ex. *Planétaire*, *Surface Solaire/Lunaire*, etc.) ou ajustez vos paramètres.
3. Prévisualisez le centrage et la qualité grâce au visualiseur interactif.
4. Cliquez sur **Lancer le traitement** pour exporter le résultat calibré et stabilisé.

### Ligne de Commande (CLI)

Pour les traitements headless ou automatisés par script :

```bash
dotnet run --project src/CassiniFlow.Cli/CassiniFlow.Cli.csproj -- [options]
```

#### Exemples de Commandes CLI

- **Centrage de Jupiter et recadrage 512×512 en conservant les 25% meilleures images :**
  ```bash
  CassiniFlow.Cli -i "captures/jupiter_raw.ser" -o "output/" --center --crop 512,512 --quality-limit 25% --debayer RGGB
  ```

- **Stabilisation d'un croissant lunaire et export au format SER 16-bit :**
  ```bash
  CassiniFlow.Cli -i "captures/moon.avi" -o "output/moon_stabilized.ser" --center --format ser16
  ```

- **Mode Join : Concaténation de plusieurs séquences avec nommage WinJUPOS automatique :**
  ```bash
  CassiniFlow.Cli -i "captures/*.ser" --mode join -o "output/" --winjupos
  ```

---

## 🛠️ Intégration Continue (CI/CD)

Le projet intègre un pipeline GitHub Actions complet (`.github/workflows/ci.yml`) qui :
- Compile et valide les tests unitaires sur **Windows** et **Ubuntu Linux**.
- Génère les rapports de couverture de code.
- Prépare et publie automatiquement les binaires prêts à l'emploi (`win-x64` et `linux-x64`) pour la version Desktop et CLI lors de la création d'un tag (`v*`).

---

## 🤝 Contribution

Les contributions sont les bienvenues ! Pour proposer une amélioration :
1. Créez un *fork* du projet.
2. Créez une branche dédiée (`git checkout -b feat/nouvelle-fonctionnalite`).
3. Suivez les conventions de code du projet (Architecture Hexagonale, DDD, `dotnet format`).
4. Écrivez des tests unitaires validant votre code dans `CassiniFlow.Tests`.
5. Ouvrez une *Pull Request* détaillée.

---

## 📄 Licence

Ce projet est distribué sous la licence libre et permissive **MIT**. Consultez le fichier [LICENSE](LICENSE) pour plus de détails.
