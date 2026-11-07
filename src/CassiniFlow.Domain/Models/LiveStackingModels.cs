namespace CassiniFlow.Domain.Models;

/// <summary>
/// Informations descriptives d'une caméra astronomique détectée (physique USB ou simulateur).
/// </summary>
public sealed record CameraDeviceInfo(
    string Id,
    string Name,
    int CameraIndex,
    int MaxWidth,
    int MaxHeight,
    bool IsColor,
    BayerPattern BayerPattern,
    int MaxBitDepth,
    double PixelSizeMicrons,
    bool IsUsb3,
    bool IsCoolerCam,
    bool IsSimulated = false);

/// <summary>
/// Configuration du flux vidéo continu pour l'acquisition caméra haute cadence.
/// </summary>
public sealed class CameraStreamConfig
{
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 480;
    public int StartX { get; set; } = 0;
    public int StartY { get; set; } = 0;
    public int Binning { get; set; } = 1;
    public int BitDepth { get; set; } = 8;
    public double ExposureMs { get; set; } = 10.0;
    public int Gain { get; set; } = 250;
    public int Offset { get; set; } = 10;
    public int WhiteBalanceR { get; set; } = 52;
    public int WhiteBalanceB { get; set; } = 95;
    public int UsbBandwidth { get; set; } = 80;
    public bool HighSpeedMode { get; set; } = true;
    public bool AutoExposure { get; set; } = false;
    public bool AutoGain { get; set; } = false;
    public bool DebayerInLive { get; set; } = true;

    public CameraStreamConfig Clone() => new()
    {
        Width = Width,
        Height = Height,
        StartX = StartX,
        StartY = StartY,
        Binning = Binning,
        BitDepth = BitDepth,
        ExposureMs = ExposureMs,
        Gain = Gain,
        Offset = Offset,
        WhiteBalanceR = WhiteBalanceR,
        WhiteBalanceB = WhiteBalanceB,
        UsbBandwidth = UsbBandwidth,
        HighSpeedMode = HighSpeedMode,
        AutoExposure = AutoExposure,
        AutoGain = AutoGain,
        DebayerInLive = DebayerInLive
    };
}

/// <summary>
/// Paramètres complets d'optimisation pour le moteur de Live Stacking planétaire, lunaire et solaire.
/// </summary>
public sealed class LiveStackingOptions
{
    // 1. Buffer glissant & Accumulation
    public int RollingBufferSize { get; set; } = 200;
    public bool EnableQualityWeighting { get; set; } = true;
    public bool NormalizeBackground { get; set; } = true;

    // 2. Alignement & Suivi Temps Réel
    public LiveAlignmentMethod AlignmentMethod { get; set; } = LiveAlignmentMethod.AutoHybrid;
    public bool AutoDetectionThreshold { get; set; } = true;
    public int ManualDetectionThreshold8Bit { get; set; } = 35;
    public int MinObjectSizePixels { get; set; } = 18;
    public int MaxDriftPixels { get; set; } = 120;
    public bool RejectEdgeClippedFrames { get; set; } = true;

    // 3. Filtrage Qualité & Rejet Turbulence / Nuages
    public bool EnableQualityGate { get; set; } = true;
    public double KeepBestPercent { get; set; } = 50.0;
    public QualityAlgorithm QualityAlgorithm { get; set; } = QualityAlgorithm.GradientSharpness;
    public int NoiseRobust { get; set; } = 2;
    public bool EnableCloudBrightnessFilter { get; set; } = true;
    public int MaxBrightnessDropPercent { get; set; } = 35;
    public bool RejectOverexposed { get; set; } = true;
    public int MaxOverexposedPixels { get; set; } = 128;

    // 4. Affichage & Post-Traitement Temps Réel Non-Destructif
    public bool EnableLiveSharpening { get; set; } = true;
    public int SharpeningBlendPercent { get; set; } = 30;
    public double UnsharpRadius { get; set; } = 1.5;
    public double UnsharpAmount { get; set; } = 1.2;
    public bool EnableDigitalAdc { get; set; } = true;
    public bool AutoDigitalAdc { get; set; } = true;
    public int ManualAdcShiftRx { get; set; } = 0;
    public int ManualAdcShiftRy { get; set; } = 0;
    public int ManualAdcShiftBx { get; set; } = 0;
    public int ManualAdcShiftBy { get; set; } = 0;
    public double DisplayGamma { get; set; } = 1.0;
    public int DisplayBlackPoint8Bit { get; set; } = 0;
    public int DisplayWhitePoint8Bit { get; set; } = 255;

    public LiveStackingOptions Clone() => new()
    {
        RollingBufferSize = RollingBufferSize,
        EnableQualityWeighting = EnableQualityWeighting,
        NormalizeBackground = NormalizeBackground,
        AlignmentMethod = AlignmentMethod,
        AutoDetectionThreshold = AutoDetectionThreshold,
        ManualDetectionThreshold8Bit = ManualDetectionThreshold8Bit,
        MinObjectSizePixels = MinObjectSizePixels,
        MaxDriftPixels = MaxDriftPixels,
        RejectEdgeClippedFrames = RejectEdgeClippedFrames,
        EnableQualityGate = EnableQualityGate,
        KeepBestPercent = KeepBestPercent,
        QualityAlgorithm = QualityAlgorithm,
        NoiseRobust = NoiseRobust,
        EnableCloudBrightnessFilter = EnableCloudBrightnessFilter,
        MaxBrightnessDropPercent = MaxBrightnessDropPercent,
        RejectOverexposed = RejectOverexposed,
        MaxOverexposedPixels = MaxOverexposedPixels,
        EnableLiveSharpening = EnableLiveSharpening,
        SharpeningBlendPercent = SharpeningBlendPercent,
        UnsharpRadius = UnsharpRadius,
        UnsharpAmount = UnsharpAmount,
        EnableDigitalAdc = EnableDigitalAdc,
        AutoDigitalAdc = AutoDigitalAdc,
        ManualAdcShiftRx = ManualAdcShiftRx,
        ManualAdcShiftRy = ManualAdcShiftRy,
        ManualAdcShiftBx = ManualAdcShiftBx,
        ManualAdcShiftBy = ManualAdcShiftBy,
        DisplayGamma = DisplayGamma,
        DisplayBlackPoint8Bit = DisplayBlackPoint8Bit,
        DisplayWhitePoint8Bit = DisplayWhitePoint8Bit
    };
}

/// <summary>
/// Télémétrie temps réel de la session de Live Stacking pour l'overlay HUD.
/// </summary>
public sealed record LiveStackingTelemetry(
    double CaptureFps,
    double StackingFps,
    int TotalFramesReceived,
    int FramesAccepted,
    int FramesRejectedQuality,
    int FramesRejectedDriftOrCloud,
    int CurrentBufferCount,
    int MaxBufferCapacity,
    double CurrentQualityScore,
    double BestQualityScore,
    double ThresholdQualityScore,
    double CurrentShiftX,
    double CurrentShiftY,
    double SensorTemperatureC,
    bool IsRecordingSer,
    int RecordedSerFrames,
    string ActiveAlignmentMode,
    string? LastRejectReason = null);

/// <summary>
/// Résultat de la prise d'un instantané (Snapshot) pendant le Live Stacking.
/// </summary>
public sealed record LiveSnapshotResult(
    bool Success,
    string RawFitsOrTiffPath,
    string SharpenedPngPath,
    int StackedFrameCount,
    string? ErrorMessage = null);
