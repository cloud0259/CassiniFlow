# CONTEXTE DU PROJET — CassiniFlow

## 1. Présentation Générale
**CassiniFlow** est une application moderne, haute performance et multiplateforme (.NET 10 / C# 14 / Avalonia UI / CLI) dédiée au prétraitement des séquences d'imagerie planétaire, lunaire et solaire (*lucky imaging*).

Le projet permet d'ingérer des séquences brutes (vidéos volumineuses, rafales d'images FITS/SER), de détecter, centrer et stabiliser les planètes ou disques solaires/lunaires, d'estimer et de classer la qualité de chaque image (turbulence atmosphérique / seeing), de filtrer les meilleures poses, d'appliquer des calibrations (dark/flat/hot pixels) et d'exporter le flux optimisé vers des logiciels d'empilement (AutoStakkert!, Registax, Siril).

---

## 2. État Actuel & Fonctionnalités Implémentées

### Formats & Codecs (Entrées / Sorties)
- **SER (Lucam Recorder format)** :
  - Lecteur et encodeur haute performance 8-bit et 16-bit (Mono, RGB, Bayer RAW).
  - Gestion des en-têtes complets et des horodatages UTC précis par frame (trailer timestamps).
- **FITS (Flexible Image Transport System)** :
  - Support de lecture et d'écriture de fichiers d'astronomie FITS mono et couleur 16-bit.
- **Flux Vidéos Universels (AVI, MP4, MOV, etc.)** :
  - Décodage haute vitesse via OpenCV (`OpenCvSharp4`).
  - Fallback et transcodage avec le backend FFmpeg CLI.
- **Formats d'image standards** : BMP, séquences d'images.
- **Intégration WinJUPOS** :
  - Génération automatique des noms de fichiers normés WinJUPOS (`YYYY-MM-DD-HHMM_S-System`) pour la dé-rotation planétaire.

### Moteur de Traitement Algorithmique
- **Débayérisation (Démosaïçage RAW)** :
  - Support complet des matrices RGGB, BGGR, GBRG, GRBG.
  - Algorithmes : Bilinéaire rapide, Haute Qualité (HQ Linear), Malvar-He-Cutler (gradient-corrected).
- **Détection & Centrage Planétaire** :
  - Détection automatique du limbe planétaire et estimation du centre de gravité (Center of Gravity - CoG).
  - Algorithme de détection et mesure du diamètre apparent de la planète en pixels (`PlanetSizeAnalyzer`).
  - Centrage dynamique de la cible avec rejet automatique des trames sans planète ou surexposées.
- **Sélection & Tri par Qualité (Seeing Atmospheric Grading)** :
  - Variance laplacienne, énergie de gradient et détection des détails fins (`QualityAlgorithm.GradientSharpness`).
  - Tri haute performance par tas min borné (*Single-Pass Bounded Top-K Min-Heap*) pour limiter la consommation mémoire à moins de 900 Mo même sur des milliers de frames.
- **Recadrage & Stabilisation** :
  - Recadrage centré dynamique ou fixe (ex: 512x512, 800x800).
  - Alignement de rotation automatique (`AutoRotationAligner`).
- **Calibration** :
  - Soustraction de Master Dark, division par Master Flat, élimination des pixels chauds (*Hot Pixels*).
- **Modes d'Exécution** :
  - **Batch Mode** : Traitement individuel fichier par fichier.
  - **Join Mode** : Fusion de plusieurs fichiers d'observation consécutifs en un seul fichier SER/AVI horodaté.
- **Compatibilité Profils / Options** :
  - Sérialisation et désérialisation complète des profils de configuration (`ProfileSerializer`).
  - Support des arguments en ligne de commande dérivés du profil.

### Interfaces Utilisateur
- **CassiniFlow.Desktop (Avalonia UI 11.2)** :
  - Interface moderne réactive sous pattern MVVM (CommunityToolkit.Mvvm).
  - Support natif Windows et Linux (testé WSL2/WSLg Ubuntu 24.04).
  - Contrôle d'affichage temps réel `AstroViewerControl` avec zoom, pan et réticules de centrage.
  - Système de bilinguisme dynamique complet Français / Anglais (`Loc.cs`).
- **CassiniFlow.Cli** :
  - Exécutable CLI pour l'automatisation en script et batch headless.

### Architecture Hexagonale & DDD
- **`CassiniFlow.Domain`** : Entités (`AstroFrame`), Value Objects (`Centroid`, `FrameDimensions`, `QualityScore`, `CropRegion`), Domain Services purs sans framework (`ImageProcessor`, `StabilisationAndQualityEngine`, `PlanetSizeAnalyzer`, `AutoRotationAligner`), Ports Inbound et Outbound.
- **`CassiniFlow.Application`** : Use cases (`RunPipelineUseCase`, `PreviewFrameUseCase`, `MeasurePlanetUseCase`, `MeasureOrientationUseCase`), Pipeline Pattern composable (`FrameProcessingPipeline`).
- **`CassiniFlow.Infrastructure`** : Codecs (`SerFile`, `FitsFile`, `BmpAndAviWriter`), Adapters (`UniversalMediaReader`, `FfmpegCliBackend`, `PipelineRunner`), Factories (`MediaReaderFactory`, `MediaWriterFactory`), `WinJuposHelper`, `ProfileSerializer`.
- **`CassiniFlow.Desktop` & `CassiniFlow.Cli`** : Driving adapters.
- **`CassiniFlow.Tests`** : Tests unitaires xUnit.
