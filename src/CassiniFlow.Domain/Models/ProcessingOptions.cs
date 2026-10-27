namespace CassiniFlow.Domain.Models;

/// <summary>
/// Complete configuration model covering all ~130 CassiniFlow v2.5.9 parameters extracted via REA.
/// Supports full backward compatibility with .pof (CassiniFlow Options File) and presets.
/// </summary>
public sealed class ProcessingOptions
{
    // --- 1. Source Files & Mode ---
    public ProcessingRunMode RunMode { get; set; } = ProcessingRunMode.BatchMode;
    public OptimisationPreset ActivePreset { get; set; } = OptimisationPreset.None;
    public List<string> ImageFiles { get; set; } = [];
    public List<string> DarkFiles { get; set; } = [];
    public List<string> FlatFiles { get; set; } = [];
    public List<string> FlatDarkFiles { get; set; } = [];

    // --- Calibration (Dark / Flat / Flat-Dark / Hot Pixels) ---
    public bool DarkFrameSubtract { get; set; }
    public bool SaveMasterDark { get; set; }
    public bool ReplaceHotPixels { get; set; }
    public int HotPixelThreshold { get; set; } = 35;
    public bool DarkDataHasBayer { get; set; } = true;

    public bool FlatFrameCalibrate { get; set; }
    public bool SaveMasterFlat { get; set; }
    public bool FlatDataHasBayer { get; set; } = true;

    public bool FlatDarkFrameSubtract { get; set; }
    public bool SaveMasterFlatDark { get; set; }

    // --- 2. Input Options ---
    public bool LimitFrameRange { get; set; }
    public FrameRangeType FrameRangeMode { get; set; } = FrameRangeType.StartToEnd;
    public int StartFrame { get; set; } = 1;
    public int EndFrame { get; set; } = 999999;
    public int NumFramesFromCentre { get; set; } = 1000;

    public BinningMode Binning { get; set; } = BinningMode.Disabled;
    public BinningCalculation BinCalculation { get; set; } = BinningCalculation.Average;

    public InputFrameColourMode InputFrameColour { get; set; } = InputFrameColourMode.AutoDetect;
    public int InputFrameDrop { get; set; }

    // Debayering
    public bool DebayerEnabled { get; set; } = true;
    public bool ProtectBayerPattern { get; set; }
    public DebayerAlgorithm DebayerType { get; set; } = DebayerAlgorithm.HqLinear;
    public BayerPattern DebayerPattern { get; set; } = BayerPattern.Rggb;

    // RAW (DCRAW / LibRaw)
    public bool RawHotPixelFilter { get; set; }
    public bool RawDebayerEnabled { get; set; } = true;
    public RawDebayerAlgorithm RawDebayerAlgorithm { get; set; } = RawDebayerAlgorithm.Ahd;
    public RawColourSpace RawOutputColourSpace { get; set; } = RawColourSpace.SrgbD65;
    public int RawHighlightRecovery { get; set; } = 0; // 0=Clip, 1=Unclip, 2=Blend, 3..9=Reconstruct

    // AVI / SER Input Specifics
    public bool TrueY800DecodeStrict { get; set; }
    public int SerBitDepthOverride { get; set; } = 0; // 0 = As SER Header, 8..16 = override

    // Date / Time Extraction (WinJUPOS)
    public bool ExtractDateTimeFromFilename { get; set; } = true;
    public bool WinJuposConvertLocalToUt { get; set; }
    public DateTimeFilenameFormat WinJuposDateTimeFormat { get; set; } = DateTimeFilenameFormat.FireCapture2;
    public string WinJuposCustomMask { get; set; } = "YYYY-MM-DD-hhmm_t";
    public decimal WinJuposInputFps { get; set; } = 60.0m;

    // --- 3. Processing Options ---
    public bool ConvertToMonochrome { get; set; }
    public MonochromeConversionMode MonochromeConversionType { get; set; } = MonochromeConversionMode.UseRgbChannels;

    public decimal InputGain { get; set; } = 1.00m;
    public decimal Gamma { get; set; } = 1.00m;
    public bool EnableMedianNoiseFilter { get; set; }

    public bool EnableHistogramStretch { get; set; }
    public int HistogramStretchWhitePointPercent { get; set; } = 75;
    public bool HistogramStretchBlackPointZero { get; set; }
    public bool HistogramStretchRgbIndividually { get; set; }

    public bool RejectOverexposedFrames { get; set; }
    public int MaxOverexposedPixels { get; set; } = 60;

    // Frame Stabilisation & Object Detection
    public StabilisationMode StabilisationMode { get; set; } = StabilisationMode.ObjectPlanetary;
    public bool CheckPlanetObjectPresent { get; set; } = true;
    public int MinimumObjectSizePixels { get; set; } = 25;
    public LunarShadowEdge EdgeInShadow { get; set; } = LunarShadowEdge.None;
    public bool CentreObjectInFrame { get; set; } = true;
    public bool AutoDetectionThreshold { get; set; } = true;
    public int ManualDetectionThreshold { get; set; } = 40;
    public bool ShowDetectedPixelsRed { get; set; }

    // Surface Stabilisation (AFB & AOI)
    public bool EnableSurfaceStabilisation { get; set; }
    public bool EnableSurfaceFeatureTracking { get; set; } = true;
    public int AfbX { get; set; } = 200;
    public int AfbY { get; set; } = 200;
    public int AfbWidth { get; set; } = 64;
    public int AfbHeight { get; set; } = 64;
    public bool AfbRejectFramesWithoutAnchor { get; set; } = true;

    public bool AoiEnable { get; set; }
    public int AoiX { get; set; } = 100;
    public int AoiY { get; set; } = 100;
    public int AoiWidth { get; set; } = 400;
    public int AoiHeight { get; set; } = 400;
    public bool AoiRejectFramesWithoutCompleteArea { get; set; } = true;
    public bool AoiCropFrames { get; set; } = true;

    // Cropping
    public bool EnableCropping { get; set; } = true;
    public int CropWidth { get; set; } = 448;
    public int CropHeight { get; set; } = 448;
    public int CropOffsetX { get; set; } = 0;
    public int CropOffsetY { get; set; } = 0;

    // Flip, Rotate & Resize
    public bool FlipHorizontalX { get; set; }
    public bool FlipVerticalY { get; set; }
    public RotationAngle Rotation { get; set; } = RotationAngle.None;

    /// <summary>
    /// Détecte l'orientation de l'objet (ex. anneaux de Saturne) dans chaque fichier et fait pivoter
    /// automatiquement les frames pour les aligner sur l'orientation du premier fichier de la liste.
    /// </summary>
    public bool AutoRotateAlign { get; set; }

    public bool EnableResizeFrames { get; set; }
    public bool ResizeKeepAspectRatio { get; set; } = true;
    public int ResizeWidth { get; set; } = 640;
    public int ResizeHeight { get; set; } = 480;

    // --- 4. Quality Options ---
    public bool EnableQualityEstimation { get; set; }
    public bool QualityReorderFrames { get; set; } = true;
    public bool EnableQualityLimiting { get; set; }
    public QualityLimitingType QualityLimitingMode { get; set; } = QualityLimitingType.PercentageOfFrames;
    public int QualityFramesToKeep { get; set; } = 25;
    public QualityAlgorithm QualityAlgorithm { get; set; } = QualityAlgorithm.GradientSharpness;
    public int QualityMinSubsample { get; set; } = 3;
    public int QualityMaxSubsample { get; set; } = 5;
    public int QualitySubsampleIncrement { get; set; } = 1;
    public bool EnableQualityWeighting { get; set; }
    public bool UseRamBuffersForQualitySorting { get; set; } = true;

    // --- 5. Animation Options ---
    public bool AnimationForwardPlay { get; set; } = true;
    public bool AnimationPauseFinalForwardFrame { get; set; }
    public int AnimationPauseForwardCount { get; set; } = 3;

    public bool AnimationReversePlay { get; set; }
    public bool AnimationPauseFinalReverseFrame { get; set; }
    public int AnimationPauseReverseCount { get; set; } = 3;

    public bool AnimationRepeatAllSequence { get; set; }
    public int AnimationRepeatAllCount { get; set; } = 1;
    public bool AnimationDisableReverseInLastRepeat { get; set; }

    // --- 6. Output Options ---
    public bool OutputInSourceDirectory { get; set; } = true;
    public string CustomOutputDirectory { get; set; } = string.Empty;
    public bool CreateOutputSubdirectory { get; set; } = true;
    public string OutputSubdirectoryName { get; set; } = "cassiniflow";
    public bool SubdirectoryAppendDateTime { get; set; } = true;
    public string OutputFilenamePrefix { get; set; } = string.Empty;
    public string OutputFilenameSuffix { get; set; } = "_cassiniflow";

    public OutputFormat OutputFormat { get; set; } = OutputFormat.Ser;
    public bool KeepOriginalImageFilenames { get; set; }
    public bool DisableFrameRenumbering { get; set; }
    public bool AddLeadingZeros { get; set; } = true;
    public bool IncludeQualityValueInFilename { get; set; }
    public bool SplitRgbChannels { get; set; }
    public int OutputFrameDrop { get; set; } = 0;

    // AVI Output Options
    public bool AviFpsSameAsInput { get; set; } = true;
    public int AviFpsValue { get; set; } = 20;
    public AviCodecType AviCodec { get; set; } = AviCodecType.DibUncompressed;
    public int AviCodecQuality { get; set; } = 10;
    public bool AviOldFormat { get; set; } = true;
    public int AviOldFormatMaxSizeGb { get; set; } = 2; // 2 or 4 GB
    public bool AviSafeFrameWidth { get; set; } = true;

    // Animated GIF Output Options
    public bool GifRepeatForever { get; set; } = true;
    public bool GifFpsSameAsInput { get; set; }
    public int GifFpsValue { get; set; } = 10;
    public GifQuantisationAlgorithm GifQuantisation { get; set; } = GifQuantisationAlgorithm.NeuralNetNeuQuant;
    public int GifAdvancedPreset { get; set; } = 0; // 0 = Maximum Quality .. 4 = Smaller File Size
    public bool GifUnchangedBorderEnabled { get; set; }
    public int GifUnchangedBorderTolerance { get; set; } = 4;
    public bool GifTransparentPixelEnabled { get; set; }
    public int GifTransparentPixelTolerance { get; set; } = 4;
    public bool GifBitDepthEnabled { get; set; }
    public int GifBitDepthValue { get; set; } = 8;
    public bool GifLossyCompressionEnabled { get; set; }
    public int GifLossyCompressionLevel { get; set; } = 30;

    // WinJUPOS Filename Generation
    public bool GenerateWinJuposFilenames { get; set; } = true;
    public WinJuposTimeReference WinJuposTimeMode { get; set; } = WinJuposTimeReference.MidPointTime;

    // Global Options
    public bool SaveRejectedFrames { get; set; }
    public bool OpenOutputFolderWhenComplete { get; set; } = true;
    public int MaxCpuUsagePercent { get; set; } = 70; // Default 70% CPU cap so PC never reaches 100% saturation

    /// <summary>
    /// Resets all options to historical CassiniFlow defaults.
    /// </summary>
    public void ResetToDefaults()
    {
        var def = new ProcessingOptions();
        foreach (var prop in typeof(ProcessingOptions).GetProperties())
        {
            if (prop.CanWrite && prop.Name is not (nameof(ImageFiles) or nameof(DarkFiles) or nameof(FlatFiles) or nameof(FlatDarkFiles)))
            {
                prop.SetValue(this, prop.GetValue(def));
            }
        }
    }

    /// <summary>
    /// Applies one of the 7 historical CassiniFlow optimisation presets.
    /// </summary>
    public void ApplyPreset(OptimisationPreset preset)
    {
        ResetToDefaults();
        ActivePreset = preset;

        switch (preset)
        {
            case OptimisationPreset.Planetary:
                StabilisationMode = StabilisationMode.ObjectPlanetary;
                CheckPlanetObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                CropWidth = 448;
                CropHeight = 448;
                EnableQualityEstimation = true;
                QualityReorderFrames = true;
                OutputFormat = OutputFormat.Ser;
                GenerateWinJuposFilenames = true;
                break;

            case OptimisationPreset.SolarLunarFullDisc:
                StabilisationMode = StabilisationMode.ObjectPlanetary;
                CheckPlanetObjectPresent = true;
                MinimumObjectSizePixels = 300;
                CentreObjectInFrame = true;
                EnableCropping = true;
                CropWidth = 1200;
                CropHeight = 1200;
                EnableQualityEstimation = true;
                QualityAlgorithm = QualityAlgorithm.PeakHistogram;
                OutputFormat = OutputFormat.Tiff;
                break;

            case OptimisationPreset.SolarLunarCloseUp:
                StabilisationMode = StabilisationMode.SurfaceFeature;
                EnableSurfaceStabilisation = true;
                EnableSurfaceFeatureTracking = true;
                AoiEnable = false;
                EnableCropping = false;
                EnableQualityEstimation = true;
                QualityAlgorithm = QualityAlgorithm.GradientSharpness;
                OutputFormat = OutputFormat.Ser;
                break;

            case OptimisationPreset.Iss:
                StabilisationMode = StabilisationMode.ObjectPlanetary;
                CheckPlanetObjectPresent = true;
                MinimumObjectSizePixels = 8;
                CentreObjectInFrame = true;
                EnableCropping = true;
                CropWidth = 300;
                CropHeight = 300;
                EnableQualityEstimation = true;
                QualityAlgorithm = QualityAlgorithm.TotalBrightness;
                OutputFormat = OutputFormat.Ser;
                break;

            case OptimisationPreset.PlanetaryAnimationAvi:
                StabilisationMode = StabilisationMode.ObjectPlanetary;
                CheckPlanetObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                EnableQualityEstimation = false;
                OutputFormat = OutputFormat.Avi;
                AviCodec = AviCodecType.UlrgUtVideoLossless;
                AnimationForwardPlay = true;
                AnimationReversePlay = true;
                AnimationRepeatAllSequence = true;
                AnimationRepeatAllCount = 3;
                break;

            case OptimisationPreset.PlanetaryAnimationGif:
                StabilisationMode = StabilisationMode.ObjectPlanetary;
                CheckPlanetObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                EnableQualityEstimation = false;
                OutputFormat = OutputFormat.AnimatedGif;
                GifRepeatForever = true;
                GifFpsSameAsInput = false;
                GifFpsValue = 12;
                break;

            case OptimisationPreset.AviArchive:
                StabilisationMode = StabilisationMode.None;
                CheckPlanetObjectPresent = false;
                CentreObjectInFrame = false;
                EnableCropping = false;
                EnableQualityEstimation = false;
                OutputFormat = OutputFormat.Avi;
                AviCodec = AviCodecType.UlrgUtVideoLossless;
                break;
        }
    }
}
