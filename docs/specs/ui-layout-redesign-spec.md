# SPÉCIFICATION D'ARCHITECTURE UI/UX — NOUVELLE DISPOSITION GRAPHIQUE (ISSUE #9)

> **Document de Spécification & Maquette**  
> **Ticket GitHub** : [#9 — UI/UX - Doc/Spec - Revoir la disposition graphique](https://github.com/cloud0259/CassiniFlow/issues/9)  
> **Statut** : Proposition & Spécification détaillée (Pas de modification de code source dans cette étape)  
> **Cible** : `CassiniFlow.Desktop` (Avalonia UI 11.2 / MVVM / .NET 10)

---

## 1. Contexte & Motivation

### 1.1 Constat sur la disposition actuelle
L'interface actuelle de `CassiniFlow.Desktop` repose sur une division asymétrique à deux panneaux horizontaux :
- **À gauche (58% de l'écran)** : 7 onglets séquentiels (`SourceFiles`, `InputOptions`, `ProcessingOptions`, `QualityOptions`, `AnimationOptions`, `OutputOptions`, `DoProcessing`).
- **À droite (42% de l'écran)** : La visionneuse de frame (`FrameViewerView`) intégrant `AstroViewerControl`.

**Limites identifiées :**
1. **Viewer relégué sur le côté** : En imagerie planétaire (*lucky imaging*), l'observation visuelle de la frame, de la turbulence atmosphérique (seeing), du limbe de la planète et du masque de seuil est le cœur de métier de l'astronome. Un viewer déporté à droite réduit la surface visuelle utile et force des allers-retours oculaires constants.
2. **Navigation de frames comprimée** : Les curseurs temporels, boutons de transport et statistiques de pixels sont tassés au pied du volet droit.
3. **Paramétrage morcelé** : Devoir naviguer à travers 7 onglets pour vérifier des paramètres interconnectés (ex: voir le diamètre planétaire détecté dans l'onglet 1 tout en ajustant la boîte de recadrage dans l'onglet 3) engendre des frictions.

### 1.2 Vision Cible : Le Viewer au Centre de la Scène
Replacer **le Viewer au centre absolu de l'écran**, encadré de manière logique et fluide :
- **À gauche** : La gestion des données d'entrée (file d'attente des fichiers SER/FITS/vidéos, métadonnées télescope/caméra).
- **Au centre** : La grande zone d'inspection planétaire (`AstroViewerControl`) surmontant une barre de scrubbing temporelle riche (timeline, curseur de frames, courbe de qualité de seeing).
- **À droite** : Le panneau des paramètres du pipeline unifié, articulé en cartes pliables ordonnées selon le flux astrophysique (Calibration -> Centrage -> Recadrage -> Sélection Qualité -> Export WinJUPOS).
- **En bas** : Une barre d'état et de télémétrie discrète avec dock escamotable pour les logs et la progression du traitement.

---

## 2. Maquette Visuelle & Design System

### 2.1 Maquette Conceptuelle Haute Définition
La disposition adopte le thème signature **Deep Space Dark** (`#0F141C` / `#18202C`, accents cyan `#38BDF8` et bleu spatial `#2563EB`) défini dans `cassiniflow-theme-palette.json` :

```
+----------------------------------------------------------------------------------------------------+
| CassiniFlow  [File]  [Preview]  [Processing]  [Settings]  [Help]            [+ Ajouter] [▶ Traiter]|
+-----------------------+----------------------------------------------------+-----------------------+
| 📁 SESSION & FICHIERS | 🔭 VIEWER CENTRAL HAUTE RÉSOLUTION                 | ⚙️ PIPELINE DE CALCUL  |
|                       |                                                    |                       |
| [Add] [Measure] [Sort]|  +-----------------------------------------------+ | ▼ [1] CALIBRATION     |
|                       |  | Zoom: 100%   [Fit]   [Reticle]   [Threshold]  | | Débayérisation: HQ    |
| • Jupiter_001.ser     |  |                                               | | Matrice: RGGB       |
|   3456 fr | 18.2 GB   |  |                  ..........                   | | Soustraction Dark   |
|   Diam: 42.4 px       |  |              .::::::::::::::::.               | |                     |
|                       |  |            .::::::::::::::::::::.             | | ▼ [2] STABILISATION   |
| • Jupiter_002.fits    |  |           ::::::::::::::::::::::::            | | Mode: Objet (Planète)|
|   1920 fr | 9.8 GB    |  |     ------+-----------+------------+-----     | | Seuil Auto: [X]     |
|   Diam: 42.1 px       |  |           ::::::::::::::::::::::::            | | Recadrage: 512x512  |
|                       |  |            .::::::::::::::::::::.             | | Centrage CoG: [X]   |
| • Saturn_001.ser      |  |              .::::::::::::::::.               | |                     |
|   2880 fr | 15.1 GB   |  |                  ..........                   | | ▼ [3] QUALITÉ SEEING|
|                       |  |                                               | | Algo: Gradient Sharp|
|-----------------------|  +-----------------------------------------------+ | Top Best: 20 %        |
| ℹ️ MÉTADONNÉES        |                                                    | Retenu: 691 / 3456    |
| Dim: 1920x1080 Mono16 | ⏱️ NAVIGATION & COURBE DE QUALITÉ                  |                       |
| Date: 2026-10-27 UTC  | [|<<] [<] [▶ Play] [>] [>>|]   Frame: 1248 / 3456  | ▼ [4] EXPORT WINJUPOS |
| Exp: 15.2 ms | 65 FPS | Quality: 92.4% | ADU Max: 58240                    | Format: SER 16-bit    |
| Gain: 240             | ───/\/\/\_/\───[====|====]───────/\/\/\─────────── | Naming: Auto WinJUPOS |
+-----------------------+----------------------------------------------------+-----------------------+
| 🟢 Prêt | Frame: 1248/3456 | RAM: 420 Mo | CPU: 12% | Rétine: Adaptée               [🌙 Thème] [FR]|
+----------------------------------------------------------------------------------------------------+
```

---

## 3. Détail Ergonomique des 4 Zones

### 3.1 Zone 1 : Volet Gauche — Fichiers & Contexte de Session (Largeur : 22% - 26%)
Le panneau latéral gauche rassemble tout ce qui concerne **l'approvisionnement et la sélection des données brutes** :
1. **Barre d'outils rapide** :
   - Bouton d'ajout de fichiers/dossiers vidéo (`.SER`, `.FITS`, `.AVI`, `.MP4`).
   - Bouton de mesure automatique du diamètre apparent planétaire (`PlanetSizeAnalyzer`).
   - Boutons de tri par taille/diamètre (croissant/décroissant) pour l'organisation des sessions d'animation.
2. **File d'attente interactive (`DataGrid` ou `ListBox` stylisée)** :
   - Cartouche par fichier affichant : Nom, badge de format coloré (`SER` cyan, `FITS` violet, `AVI` vert), nombre de trames, taille sur disque, diamètre apparent estimé.
   - Case à cocher par fichier pour le traitement par lot (*Batch Mode* ou *Join Mode*).
3. **Inspecteur de Métadonnées (au bas de la colonne gauche)** :
   - Carte compacte affichant les en-têtes FITS/SER du fichier sélectionné : Dimensions du capteur, profondeur d'échantillonnage (8-bit / 16-bit), temps d'exposition, horodatage UTC, télescope / observateur.

### 3.2 Zone 2 : Volet Central — Le Cœur Visuel (Largeur : 48% - 54%)
Le centre est dédié à l'analyse visuelle et au contrôle temps réel :
1. **Barre d'outils d'affichage (HUD intégré)** :
   - Zoom interactif : Boutons `100% (1:1)`, `Ajuster (Fit)`, slider de zoom continu avec roulette de souris.
   - Réticules & Aides de visée : Réticule circulaire de centrage, boîte de recadrage dynamique (jaune pointillé), cadre AFB (*Area of Further Bounds*).
   - Masque de détection : Bascule d'affichage des pixels au-dessus du seuil en surbrillance rouge contrastée.
2. **Scène d'affichage principale (`AstroViewerControl`)** :
   - Grand viewport avec fond ultra-sombre (`#06090E`) pour faire ressortir les faibles contrastes des formations nuageuses planétaires (Jupiter, Saturne, Mars) ou les détails solaires/lunaires.
   - Panning fluide au clic milieu ou clic droit glissé.
3. **Barre de Navigation & Contrôle Temporel** :
   - Boutons de transport ergonomiques : Première trame, précédente, lecture continue (prévisualisation temps réel à cadence paramétrable), suivante, dernière trame.
   - Slider de scrubbing large facilitant le repérage précis d'une turbulence ou d'un artefact.
4. **Graphique de Profil Qualité (Sparkline / Courbe de Seeing)** :
   - Intégration sous la timeline d'un mini-graphe affichant le score de netteté (*Gradient Sharpness / Laplacian*) de chaque trame de la séquence.
   - Surbrillance visuelle de la zone ou des trames sélectionnées pour l'empilement (ex: les 20% meilleures trames).

### 3.3 Zone 3 : Volet Droit — Paramètres & Pipeline de Traitement (Largeur : 24% - 28%)
Plutôt que d'éparpiller les réglages dans des onglets isolés, les paramètres sont présentés sous forme de **cartes verticales séquentielles accordéons**, reflétant l'ordre exact du pipeline de traitement :

1. **Carte 1 : Calibration & Débayérisation**
   - Mode Débayérisation (Auto, HQ Linear, Malvar-He-Cutler).
   - Fichiers maîtres : Master Dark, Master Flat, seuil de détection des pixels chauds (*Hot Pixels*).
2. **Carte 2 : Stabilisation & Centrage**
   - Mode de suivi : *Planète / Disque entier (CoG)* ou *Détail de Surface (Anchor)*.
   - Seuil de détection du limbe (Automatique avec calcul histogramme ou réglage manuel avec retour visuel immédiat).
   - Taille minimale de l'objet détecté (rejet des bruits ou trames vides).
3. **Carte 3 : Recadrage (Cropping)**
   - Mode : Dynamique centré sur la cible ou Taille fixe (presets 256, 512, 640, 800, 1024, Custom).
   - Recadrage automatique basé sur le diamètre calculé + marge de sécurité configurable.
4. **Carte 4 : Évaluation & Tri Qualité (Seeing)**
   - Algorithme de classement : Énergie de gradient (*Sobel/Gradient*), Variance Laplacienne.
   - Critère de sélection : Pourcentage des meilleures poses (*Top X%*) ou Nombre absolu de trames.
   - Statistique temps réel : Nombre exact de trames retenues vs rejetées.
5. **Carte 5 : Export & Format de Sortie**
   - Répertoire de destination avec bouton de sélection et bouton d'ouverture dans l'explorateur.
   - Format d'encodage : SER (8/16-bit), FITS 16-bit, AVI non compressé.
   - Modèle de nommage WinJUPOS automatique (`YYYY-MM-DD-HHMM_S-System`).

### 3.4 Zone 4 : Barre Inférieure & Console d'Exécution
1. **État au repos** :
   - Télémétrie claire : Valeur pixel max sous le curseur (ADU), coordonnées X/Y, zoom actuel, statut de la calibration, bascule rapide du thème nocturne.
2. **En cours de traitement (Mode Actif)** :
   - Un tiroir rétractable ou une barre de progression s'anime :
     - Étape actuelle affichée clairement (ex: `Passe 1/2 : Stabilisation & Mesure de Qualité (Frame 1420/3456)`).
     - Loader fluide (Ellipsis Bounce ou barre de progression pulsée).
     - Débit de traitement mesuré (FPS effectif, Mo/s lus).
     - Bouton d'annulation proéminent mais sécurisé.

---

## 4. Architecture Technique Avalonia 11.2 (Feuille de Route)

### 4.1 Découpage des Composants XAML
La nouvelle organisation permet un découpage modulaire exemplaire conforme à l'architecture hexagonale et MVVM :

```
src/CassiniFlow.Desktop/
├── Views/
│   ├── MainWindow.axaml                      # Grid principale (3 colonnes + header + status bar)
│   ├── Panels/
│   │   ├── FileQueuePanel.axaml             # Volet gauche : Liste des fichiers et métadonnées
│   │   ├── CentralViewerStage.axaml         # Volet central : Viewer + HUD + Timeline + QualityCurve
│   │   └── PipelineParametersPanel.axaml    # Volet droit : Cartes de paramètres (Accordion/Expander)
│   ├── Controls/
│   │   ├── AstroViewerControl.cs            # Maintien du contrôle graphique haute performance existant
│   │   ├── QualityCurveSparkline.cs         # Nouveau contrôle de visualisation du seeing
│   │   └── TimelineScrubber.axaml           # Contrôle de défilement temporel unifié
```

### 4.2 Gestion de la Flexibilité & Redimensionnement
- Utilisation de deux `GridSplitter` verticaux :
  - `GridSplitter 1` entre le volet Fichiers (gauche) et le Viewer central.
  - `GridSplitter 2` entre le Viewer central et le volet Paramètres (droite).
- Contraintes de redimensionnement :
  - `FileQueuePanel` : MinWidth = 240px, MaxWidth = 450px.
  - `CentralViewerStage` : MinWidth = 480px, Width = `*` (prend tout l'espace disponible).
  - `PipelineParametersPanel` : MinWidth = 280px, MaxWidth = 480px.
- En cas de fenêtre compacte (ex: PC portable 1366x768 ou 1280x720) :
  - Le volet de paramètres et le volet fichiers peuvent être réduits ou basculer en mode volet escamotable (*Flyout / Drawer*).

### 4.3 Prise en Charge du Thème & Accessibilité
- Harmonisation avec les styles de `cassiniflow-theme-palette.json` :
  - **Deep Space Dark** (Défaut)
  - **Clean Lab Light** (Bureautique / Jour)
  - **Night Vision Red** (Astro de terrain au télescope, préservant la rhodopsine rétinienne).
- Textes 100% bilingues via le service de localisation existant (`{loc:Tr ...}`).

---

## 5. Synthèse des Bénéfices Utilisateur

| Critère | Disposition Actuelle (Onglets à gauche / Viewer à droite) | Nouvelle Disposition Cible (Viewer Centré) |
| :--- | :--- | :--- |
| **Ergonomie Visuelle** | Viewer comprimé sur le côté droit (42% largeur) | Viewer roi au centre (50-55% largeur, hauteur maximale) |
| **Workflow d'Inspection** | Allers-retours permanents entre 7 onglets pour ajuster un paramètre | Vision continue de l'image pendant le réglage des paramètres |
| **Navigation Temporelle** | Simple slider étroit en bas du viewer | Timeline scrubbable avec indicateurs visuels et courbe de seeing |
| **Lisibilité du Pipeline** | Étapes morcelées dans des onglets isolés | Séquence claire et unifiée suivant le flux astrophysique naturel |
| **Look & Feel** | Classique utilitaire | Moderne, professionnel, proche de DaVinci Resolve / Lightroom |

---

*Spécification rédigée pour validation et préparation de l'implémentation dans le cadre de l'Issue #9.*
