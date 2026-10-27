# RÃˆGLES D'ARCHITECTURE : HEXAGONALE, DDD & DESIGN PATTERNS

## 1. Principes Fondamentaux
L'application doit adhÃ©rer rigoureusement aux principes de l'**Architecture Hexagonale (Ports et Adaptateurs)**, du **Domain-Driven Design (DDD)** et aux **Design Patterns** logiciels appropriÃ©s.

---

## 2. DÃ©coupage en Couches Hexagonales

```
+------------------------------------------------------------------------+
|                      DRIVING ADAPTERS (ENTRÃ‰ES)                        |
|   CassiniFlow.Desktop (Avalonia MVVM)  |   CassiniFlow.Cli (Console CLI)       |
+------------------------------------+-----------------------------------+
                                  â”‚
                                  â–¼ (Appelle les Inbound Ports)
+------------------------------------------------------------------------+
|                     APPLICATION (CAS D'UTILISATION)                    |
|   Use Cases: RunPipelineUseCase, MeasurePlanetUseCase, ParsePofUseCase  |
|   DTOs, Commandes, Orchestrateurs de traitement                        |
|   Ports Entrants (Inbound Interfaces)                                  |
|   Ports Sortants (Outbound Interfaces)                                 |
+------------------------------------------------------------------------+
                                  â”‚
                                  â–¼ (Utilise le Domaine)
+------------------------------------------------------------------------+
|                          DOMAINE MÃ‰TIER (DDD)                          |
|   EntitÃ©s & AgrÃ©gats: ProcessingSession, AstroFrame, CalibrationSet   |
|   Objets Valeurs (Value Objects): Centroid, Dimensions, QualityScore   |
|   Services Domaine: Algorithmes purs (Debayer, Centering, Quality)     |
|   Invariants mÃ©tier & SpÃ©cifications                                   |
+------------------------------------------------------------------------+
                                  â–² (ImplÃ©mente les Outbound Ports)
                                  â”‚
+------------------------------------------------------------------------+
|                     DRIVEN ADAPTERS (INFRASTRUCTURE)                   |
|   Codecs & Fichiers: SerFileReader/Writer, FitsFileReader/Writer       |
|   Media universel: OpenCvVideoReader, FfmpegCliBackend                 |
|   SÃ©rialisation: PofSerializer, JsonConfig                             |
|   SystÃ¨me d'exploitation & Horloge: FileSystemAdapter, SystemClock    |
+------------------------------------------------------------------------+
```

### 2.1 Couche Domaine (`CassiniFlow.Domain`)
- **IndÃ©pendance absolue** : ZÃ©ro dÃ©pendance vers des frameworks graphiques (Avalonia) ou d'infrastructure externe I/O directe.
- **Ubiquitous Language** : Termes mÃ©tier de l'astrophotographie (`AstroFrame`, `Centroid`, `BayerPattern`, `SeeingQuality`, `CroppingRegion`, `CalibrationProfile`, `PlanetaryDisk`).
- **Objets Valeurs (Value Objects)** : Immutables (records C# ou readonly structs), Ã©galitÃ© par valeur, validation des invariants Ã  la crÃ©ation :
  - `Dimensions(int Width, int Height)`
  - `Centroid(double X, double Y)`
  - `QualityScore(double Value, QualityAlgorithm Algorithm)`
  - `BayerConfiguration(BayerPattern Pattern, DebayerAlgorithm Algorithm)`
  - `CropRegion(int X, int Y, int Width, int Height)`
- **AgrÃ©gats & EntitÃ©s** :
  - `AstroFrame` : Trame avec pixels normalisÃ©s (0..65535), canaux, mÃ©tadonnÃ©es, horodatage UTC.
  - `ProcessingPipelinePlan` : Plan d'exÃ©cution validÃ© et cohÃ©rent.
- **Services Domaine** :
  - Algorithmes de centrage et dÃ©tection de limbe (`ICentroidDetector`).
  - Algorithmes de dÃ©bayÃ©risation (`IDebayerService`).
  - Algorithmes d'Ã©valuation de qualitÃ© (`IQualityEstimator`).
  - DÃ©tection du diamÃ¨tre planÃ©taire (`IPlanetDiskAnalyzer`).

### 2.2 Couche Application (`CassiniFlow.Application`)
- **RÃ´le** : Orchestrer les cas d'utilisation sans contenir de rÃ¨gles mÃ©tier pures.
- **Ports Entrants (Primary / Inbound Ports)** :
  - `IRunPipelineUseCase` : Lancement du traitement (Batch ou Join).
  - `IMeasurePlanetUseCase` : Mesure rapide du diamÃ¨tre sur des fichiers d'entrÃ©e.
  - `IInspectMediaUseCase` : Extraction des mÃ©tadonnÃ©es d'une trame ou d'un flux.
  - `IPofConfigurationUseCase` : Chargement et sauvegarde de fichiers `.pof`.
- **Ports Sortants (Secondary / Outbound Ports)** :
  - `IMediaFrameReader` : Lecture de frames depuis une source (SER, FITS, VidÃ©o).
  - `IMediaFrameWriter` : Ã‰criture de frames vers une destination (SER, FITS, AVI, BMP).
  - `IMediaReaderFactory` : CrÃ©ation du reader appropriÃ© selon le format.
  - `IMediaWriterFactory` : CrÃ©ation du writer appropriÃ©.
  - `IProgressPublisher` : Notification de l'Ã©tat d'avancement au client appelant.

### 2.3 Couche Infrastructure (`CassiniFlow.Infrastructure`)
- **RÃ´le** : ImplÃ©menter les ports secondaires dÃ©finis par l'Application et le Domaine.
- **Composants** :
  - Codecs spÃ©cifiques : SER (`SerFileReader`, `SerFileWriter`), FITS (`FitsFileReader`, `FitsFileWriter`), AVI/BMP.
  - Wrappers externes : OpenCV (`OpenCvMediaReader`), FFmpeg CLI (`FfmpegBackend`).
  - Persistance : SÃ©rialisation `.pof` (`PofFileSerializer`).

### 2.4 Couche PrÃ©sentation / Adaptateurs Primaires
- `CassiniFlow.Desktop` (Avalonia UI MVVM) :
  - Ne communique qu'avec les ports applicatifs (`IRunPipelineUseCase`, `IMeasurePlanetUseCase`, etc.).
  - Ne dÃ©pend jamais directement de l'Infrastructure bas niveau.
- `CassiniFlow.Cli` :
  - Parse la ligne de commande, instancie les adaptateurs et invoque les Use Cases applicatifs.

---

## 3. Design Patterns Obligatoires

1. **Strategy Pattern** :
   - Algorithmes de dÃ©bayÃ©risation (`IDebayerStrategy` : Bilinear, HqLinear, MalvarHeCutler).
   - MÃ©triques d'estimation de qualitÃ© (`IQualityEstimationStrategy` : Laplacian, Gradient, EdgeEnergy).
   - Algorithmes de centrage (`ICenteringStrategy` : CoG, ThresholdDisk, EdgeFitting).
   - StratÃ©gies de sÃ©lection de frames (`IFrameSelectionStrategy` : TopKPercent, BestNFrames, ThresholdScore).
2. **Factory / Abstract Factory Pattern** :
   - `IMediaReaderFactory` pour instancier le reader appropriÃ© selon le format (SER, FITS, vidÃ©o).
   - `IMediaWriterFactory` pour instancier l'encodeur de sortie appropriÃ©.
3. **Pipeline / Chain of Responsibility Pattern** :
   - Traitement par Ã©tapes modulaires et composables d'une trame :
     `CalibrationStep` -> `DebayerStep` -> `CenteringStep` -> `QualityStep` -> `CropStep` -> `RotationStep`.
4. **Adapter Pattern** :
   - Encapsulation d'OpenCV et de FFmpeg derriÃ¨re des interfaces de streaming de frames propres au domaine.
5. **Observer / Progress Pattern** :
   - Notification thread-safe de la progression via `IProgress<PipelineProgressReport>`.
