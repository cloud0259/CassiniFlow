using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ProcessingOptions Options { get; private set; } = new();

    // 2. Input Options
    [ObservableProperty] private bool _debayerEnabled = false;
    [ObservableProperty] private int _selectedBayerPatternIndex = 2;
    [ObservableProperty] private int _selectedDebayerAlgoIndex = 1;
    [ObservableProperty] private int _selectedBinningIndex = 0;
    [ObservableProperty] private bool _darkSubtractEnabled = false;
    [ObservableProperty] private int _hotPixelThreshold = 35;

    // 3. Processing Options
    [ObservableProperty] private int _selectedStabilisationModeIndex = 1;
    [ObservableProperty] private bool _checkObjectPresent = true;
    [ObservableProperty] private bool _centreObjectInFrame = true;
    [ObservableProperty] private bool _autoThreshold = true;
    [ObservableProperty] private int _detectionThreshold = 40;
    [ObservableProperty] private int _minObjectSizePixels = 20;
    [ObservableProperty] private int _selectedShadowEdgeIndex = 0;
    [ObservableProperty] private int _afbSize = 64;
    [ObservableProperty] private int _targetBoxX = 240;
    [ObservableProperty] private int _targetBoxY = 180;
    [ObservableProperty] private bool _detectNaturalSatellites = true;
    [ObservableProperty] private double _satelliteDetectionSensitivity = 1.0;

    // Cropping (Default to 448x448 matching CassiniFlow v2.5.9 Planetary Crop)
    [ObservableProperty] private bool _enableCropping = true;
    [ObservableProperty] private int _cropWidth = 448;
    [ObservableProperty] private int _cropHeight = 448;
    [ObservableProperty] private int _cropOffsetX = 0;
    [ObservableProperty] private int _cropOffsetY = 0;

    // Image Adjustments
    [ObservableProperty] private double _inputGain = 1.00;
    [ObservableProperty] private double _gamma = 1.00;
    [ObservableProperty] private bool _histogramStretchEnabled = false;
    [ObservableProperty] private int _histogramStretchPercent = 80;
    [ObservableProperty] private bool _convertToMono = false;
    [ObservableProperty] private bool _medianFilterEnabled = false;
    [ObservableProperty] private int _selectedRotationIndex = 0;
    [ObservableProperty] private bool _flipHorizontal;
    [ObservableProperty] private bool _flipVertical;
        [ObservableProperty] private bool _autoRotateAlign;

    // 4. Quality Options
    [ObservableProperty] private bool _enableQuality = false;
    [ObservableProperty] private int _selectedQualityAlgoIndex = 0;
    [ObservableProperty] private bool _reorderByQuality = true;
    [ObservableProperty] private bool _limitQualityFrames = true;
    [ObservableProperty] private int _selectedQualityLimitModeIndex = 1;
    [ObservableProperty] private int _qualityKeepValue = 25;
    [ObservableProperty] private bool _enableQualityWeighting = false;

    // 5. Animation Options
    [ObservableProperty] private bool _animationForward = true;
    [ObservableProperty] private bool _animationReverse = false;
    [ObservableProperty] private int _animationRepeatCount = 1;

    // 6. Output Options (Default to index 0 = AVI compatible with planetary processing engines)
    [ObservableProperty] private int _selectedOutputFormatIndex = 0;
    [ObservableProperty] private bool _matchInputFps = true;
    [ObservableProperty] private int _outputFps = 25;
    [ObservableProperty] private string _outputSuffix = "_cassiniflow";
    [ObservableProperty] private bool _winJuposNaming = false;
    [ObservableProperty] private string _customOutputDirectory = string.Empty;


    partial void OnAlignmentTileSizeChanged(int value)
    {
        OnPropertyChanged(nameof(IsTileSize24));
        OnPropertyChanged(nameof(IsTileSize48));
        OnPropertyChanged(nameof(IsTileSize104));
        OnPropertyChanged(nameof(IsTileSize140));
        OnPropertyChanged(nameof(IsTileSize200));
        _userSettings.Current.AlignmentTileSize = Math.Clamp(value, 16, 512);
        _userSettings.Save();
        CalculateAlignmentGrid();
    }

    partial void OnMinBrightnessThresholdChanged(int value)
    {
        _userSettings.Current.MinBrightnessThreshold = Math.Clamp(value, 1, 254);
        _userSettings.Save();
        RecalculateAlignmentGridIfActive();
    }

    partial void OnCurrentAlignmentGridChanged(AlignmentGrid value)
    {
        OnPropertyChanged(nameof(ActivePointsCountText));
        NotifyStartProcessingValidation();
    }

    partial void OnSelectedStackingImageFormatIndexChanged(int value)
    {
        SelectedOutputFormatIndex = value switch
        {
            1 => 6, // FITS
            2 => 4, // PNG
            _ => 5  // TIFF
        };
        _userSettings.Current.SelectedStackingImageFormatIndex = value;
        _userSettings.Save();
    }

    partial void OnSelectedOutputFormatIndexChanged(int value)
    {
        _userSettings.Current.SelectedOutputFormatIndex = value;
        _userSettings.Save();
    }

    partial void OnWinJuposNamingChanged(bool value)
    {
        _userSettings.Current.WinJuposNaming = value;
        _userSettings.Save();
    }

    partial void OnOutputSuffixChanged(string value)
    {
        _userSettings.Current.OutputSuffix = value;
        _userSettings.Save();
    }

    partial void OnCustomOutputDirectoryChanged(string value)
    {
        _userSettings.Current.CustomOutputDirectory = value;
        _userSettings.Save();
    }

    partial void OnDetectNaturalSatellitesChanged(bool value)
    {
        CelestialTargetClassifier.DefaultDetectSatellites = value;
        _userSettings.Current.DetectNaturalSatellites = value;
        _userSettings.Save();
    }

    partial void OnSatelliteDetectionSensitivityChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.40, 2.50);
        CelestialTargetClassifier.DefaultSatelliteSensitivity = clamped;
        _userSettings.Current.SatelliteDetectionSensitivity = clamped;
        _userSettings.Save();
    }


    // --- Mode de Traitement (Workflow) ---
    [ObservableProperty] private StackingWorkflowMode _workflowMode = StackingWorkflowMode.DirectStreamAllInOne;
    [ObservableProperty] private bool _isWorkflowPreProcessingOnly;
    [ObservableProperty] private bool _isWorkflowStackingOnly;
    [ObservableProperty] private bool _isWorkflowDirectStreamAllInOne = true;

    partial void OnIsWorkflowPreProcessingOnlyChanged(bool value)
    {
        if (value && WorkflowMode != StackingWorkflowMode.PreProcessingOnly)
        {
            SetWorkflowMode("prep");
        }
    }

    partial void OnIsWorkflowStackingOnlyChanged(bool value)
    {
        if (value && WorkflowMode != StackingWorkflowMode.StackingOnly)
        {
            SetWorkflowMode("stack");
        }
    }

    partial void OnIsWorkflowDirectStreamAllInOneChanged(bool value)
    {
        if (value && WorkflowMode != StackingWorkflowMode.DirectStreamAllInOne)
        {
            SetWorkflowMode("all");
        }
    }

    public string Section1Title => Loc.Get("Messages.Section1Title");
    public string Section5Title => Loc.Get("Messages.Section5Title");

    public bool IsSection1Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public bool IsSection2Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public bool IsSection3Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public bool IsSectionDerotationVisible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public string SectionDerotationBadgeText => "⟳";
    public bool IsSection4Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public bool IsSection5Visible => WorkflowMode != StackingWorkflowMode.PreProcessingOnly;
    public bool IsSection6Visible => WorkflowMode != StackingWorkflowMode.PreProcessingOnly;
    public bool IsSection7Visible => WorkflowMode != StackingWorkflowMode.PreProcessingOnly;
    public bool IsSection8Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public bool IsSection9Visible => true;
    public bool IsSection10Visible => true;

    public string Section1BadgeText => "1";
    public string Section2BadgeText => "2";
    public string Section3BadgeText => "3";
    public string Section4BadgeText => "4";

    public string Section5BadgeText => WorkflowMode switch
    {
        StackingWorkflowMode.StackingOnly => "1",
        _ => "5"
    };

    public string Section6BadgeText => WorkflowMode switch
    {
        StackingWorkflowMode.StackingOnly => "2",
        _ => "6"
    };

    public string Section7BadgeText => WorkflowMode switch
    {
        StackingWorkflowMode.StackingOnly => "3",
        _ => "7"
    };

    public string Section8BadgeText => WorkflowMode switch
    {
        StackingWorkflowMode.PreProcessingOnly => "5",
        _ => "8"
    };

    public string Section9BadgeText => WorkflowMode switch
    {
        StackingWorkflowMode.StackingOnly => "4",
        StackingWorkflowMode.PreProcessingOnly => "6",
        _ => "9"
    };

    public string Section10BadgeText => WorkflowMode switch
    {
        StackingWorkflowMode.StackingOnly => "5",
        StackingWorkflowMode.PreProcessingOnly => "7",
        _ => "10"
    };

    partial void OnWorkflowModeChanged(StackingWorkflowMode value)
    {
        IsWorkflowPreProcessingOnly = value == StackingWorkflowMode.PreProcessingOnly;
        IsWorkflowStackingOnly = value == StackingWorkflowMode.StackingOnly;
        IsWorkflowDirectStreamAllInOne = value == StackingWorkflowMode.DirectStreamAllInOne;

        OnPropertyChanged(nameof(Section1Title));
        OnPropertyChanged(nameof(Section5Title));
        OnPropertyChanged(nameof(IsSection1Visible));
        OnPropertyChanged(nameof(IsSection2Visible));
        OnPropertyChanged(nameof(IsSection3Visible));
        OnPropertyChanged(nameof(IsSectionDerotationVisible));
        OnPropertyChanged(nameof(SectionDerotationBadgeText));
        OnPropertyChanged(nameof(IsSection4Visible));
        OnPropertyChanged(nameof(IsSection5Visible));
        OnPropertyChanged(nameof(IsSection6Visible));
        OnPropertyChanged(nameof(IsSection7Visible));
        OnPropertyChanged(nameof(IsSection8Visible));
        OnPropertyChanged(nameof(IsSection9Visible));
        OnPropertyChanged(nameof(IsSection10Visible));

        OnPropertyChanged(nameof(Section1BadgeText));
        OnPropertyChanged(nameof(Section2BadgeText));
        OnPropertyChanged(nameof(Section3BadgeText));
        OnPropertyChanged(nameof(Section4BadgeText));
        OnPropertyChanged(nameof(Section5BadgeText));
        OnPropertyChanged(nameof(Section6BadgeText));
        OnPropertyChanged(nameof(Section7BadgeText));
        OnPropertyChanged(nameof(Section8BadgeText));
        OnPropertyChanged(nameof(Section9BadgeText));
        OnPropertyChanged(nameof(Section10BadgeText));

        if (!IsSection1Visible) IsSection1Expanded = false;
        if (!IsSection2Visible) IsSection2Expanded = false;
        if (!IsSection3Visible) IsSection3Expanded = false;
        if (!IsSectionDerotationVisible) IsSectionDerotationExpanded = false;
        if (!IsSection4Visible) IsSection4Expanded = false;
        if (!IsSection5Visible) IsSection5Expanded = false;
        if (!IsSection6Visible) IsSection6Expanded = false;
        if (!IsSection7Visible) IsSection7Expanded = false;
        if (!IsSection8Visible) IsSection8Expanded = false;

        if (!IsAnyVisibleSectionExpanded())
        {
            if (IsSection1Visible) IsSection1Expanded = true;
            else if (IsSection5Visible) IsSection5Expanded = true;
        }

        if (IsWorkflowPreProcessingOnly)
        {
            IsAlignmentGridVisible = false;
        }
        else
        {
            IsAlignmentGridVisible = true;
            if (CurrentAlignmentGrid.Points.Count == 0 && (_lastProcessedPreviewFrame is not null || _cachedPreviewResult is not null))
            {
                CalculateAlignmentGrid();
            }
        }

        NotifyStartProcessingValidation();
    }

    private bool IsAnyVisibleSectionExpanded()
    {
        return (IsSection1Visible && IsSection1Expanded) ||
               (IsSection2Visible && IsSection2Expanded) ||
               (IsSection3Visible && IsSection3Expanded) ||
               (IsSectionDerotationVisible && IsSectionDerotationExpanded) ||
               (IsSection4Visible && IsSection4Expanded) ||
               (IsSection5Visible && IsSection5Expanded) ||
               (IsSection6Visible && IsSection6Expanded) ||
               (IsSection7Visible && IsSection7Expanded) ||
               (IsSection8Visible && IsSection8Expanded) ||
               (IsSection9Visible && IsSection9Expanded) ||
               (IsSection10Visible && IsSection10Expanded);
    }

    // --- Options d'Alignement Global, Qualité & Référence (AutoStakkert style) ---
    [ObservableProperty] private StackingStabilisationMode _stackingStabilisationMode = StackingStabilisationMode.PlanetCog;
    [ObservableProperty] private bool _disableStabilisation = false;
    [ObservableProperty] private StackingCanvasMode _stackingCanvasMode = StackingCanvasMode.Crop;
    [ObservableProperty] private int _noiseRobust = 2;
    [ObservableProperty] private QualityAnalysisScope _qualityScope = QualityAnalysisScope.LocalAp;
    [ObservableProperty] private bool _doubleStackReference = true;
    [ObservableProperty] private ReferenceFrameMode _referenceFrameMode = ReferenceFrameMode.Automatic;
    [ObservableProperty] private int _manualReferenceFrameIndex = 0;

    partial void OnStackingStabilisationModeChanged(StackingStabilisationMode value)
    {
        OnPropertyChanged(nameof(IsStabilisationPlanetCog));
        OnPropertyChanged(nameof(IsStabilisationSurface));
    }

    partial void OnStackingCanvasModeChanged(StackingCanvasMode value)
    {
        OnPropertyChanged(nameof(IsCanvasCrop));
        OnPropertyChanged(nameof(IsCanvasExpand));
    }

    partial void OnNoiseRobustChanged(int value)
    {
        OnPropertyChanged(nameof(NoiseRobustText));
    }

    partial void OnQualityScopeChanged(QualityAnalysisScope value)
    {
        OnPropertyChanged(nameof(IsQualityLocalAp));
        OnPropertyChanged(nameof(IsQualityGlobalFrame));
    }

    partial void OnReferenceFrameModeChanged(ReferenceFrameMode value)
    {
        OnPropertyChanged(nameof(IsReferenceAutomatic));
        OnPropertyChanged(nameof(IsReferenceManual));
    }

    public bool IsStabilisationPlanetCog
    {
        get => StackingStabilisationMode == StackingStabilisationMode.PlanetCog;
        set { if (value) StackingStabilisationMode = StackingStabilisationMode.PlanetCog; }
    }

    public bool IsStabilisationSurface
    {
        get => StackingStabilisationMode == StackingStabilisationMode.Surface;
        set { if (value) StackingStabilisationMode = StackingStabilisationMode.Surface; }
    }

    public bool IsCanvasCrop
    {
        get => StackingCanvasMode == StackingCanvasMode.Crop;
        set { if (value) StackingCanvasMode = StackingCanvasMode.Crop; }
    }

    public bool IsCanvasExpand
    {
        get => StackingCanvasMode == StackingCanvasMode.Expand;
        set { if (value) StackingCanvasMode = StackingCanvasMode.Expand; }
    }

    public bool IsQualityLocalAp
    {
        get => QualityScope == QualityAnalysisScope.LocalAp;
        set { if (value) QualityScope = QualityAnalysisScope.LocalAp; }
    }

    public bool IsQualityGlobalFrame
    {
        get => QualityScope == QualityAnalysisScope.GlobalFrame;
        set { if (value) QualityScope = QualityAnalysisScope.GlobalFrame; }
    }

    public bool IsReferenceAutomatic
    {
        get => ReferenceFrameMode == ReferenceFrameMode.Automatic;
        set { if (value) ReferenceFrameMode = ReferenceFrameMode.Automatic; }
    }

    public bool IsReferenceManual
    {
        get => ReferenceFrameMode == ReferenceFrameMode.Manual;
        set { if (value) ReferenceFrameMode = ReferenceFrameMode.Manual; }
    }

    public string NoiseRobustText => NoiseRobust switch
    {
        1 => Loc.Get("Messages.SnrPreset1"),
        2 => Loc.Get("Messages.SnrPreset2"),
        3 => Loc.Get("Messages.SnrPreset3"),
        4 => Loc.Get("Messages.SnrPreset4"),
        5 => Loc.Get("Messages.SnrPreset5"),
        6 => Loc.Get("Messages.SnrPreset6"),
        7 => Loc.Get("Messages.SnrPreset7"),
        8 => Loc.Get("Messages.SnrPreset8"),
        _ => $"{NoiseRobust}"
    };

    // --- Points d'alignement (AP) ---
    [ObservableProperty] private bool _isAlignmentGridVisible = true;
    [ObservableProperty] private AlignmentGrid _currentAlignmentGrid = AlignmentGrid.Empty;
    [ObservableProperty] private int _alignmentTileSize = 48;
    [ObservableProperty] private int _minBrightnessThreshold = 15;
    [ObservableProperty] private bool _enablePruning = true;
    [ObservableProperty] private bool _enableMultiScaleGrid = true;
    [ObservableProperty] private string _alignmentStatusText = string.Empty;

    public bool IsTileSize24 => AlignmentTileSize == 24;
    public bool IsTileSize48 => AlignmentTileSize == 48;
    public bool IsTileSize104 => AlignmentTileSize == 104;
    public bool IsTileSize140 => AlignmentTileSize == 140;
    public bool IsTileSize200 => AlignmentTileSize == 200;

    public string ActivePointsCountText => CurrentAlignmentGrid.ActiveCount > 0 
        ? $"{CurrentAlignmentGrid.ActiveCount} AP" 
        : "-";

    // --- Format Image Empilee (Sortie unique) ---
    [ObservableProperty] private int _selectedStackingImageFormatIndex = 0;

    // --- Empilement Multi-Zones & Rendu ---
    [ObservableProperty] private int _stackPercentage1 = 25;
    [ObservableProperty] private int _stackPercentage2 = 0;
    [ObservableProperty] private int _stackPercentage3 = 0;

    partial void OnStackPercentage1Changed(int value)
    {
        _userSettings.Current.StackPercentage1 = Math.Clamp(value, 0, 100);
        _userSettings.Save();
        NotifyStartProcessingValidation();
    }

    partial void OnStackPercentage2Changed(int value)
    {
        _userSettings.Current.StackPercentage2 = Math.Clamp(value, 0, 100);
        _userSettings.Save();
        NotifyStartProcessingValidation();
    }

    partial void OnStackPercentage3Changed(int value)
    {
        _userSettings.Current.StackPercentage3 = Math.Clamp(value, 0, 100);
        _userSettings.Save();
        NotifyStartProcessingValidation();
    }

    public int StackPercentage
    {
        get => StackPercentage1 > 0 ? StackPercentage1 : (StackPercentage2 > 0 ? StackPercentage2 : (StackPercentage3 > 0 ? StackPercentage3 : 0));
        set => StackPercentage1 = value;
    }

    public List<double> GetActiveStackPercentages()
    {
        var list = new List<double>();
        if (StackPercentage1 > 0) list.Add(StackPercentage1);
        if (StackPercentage2 > 0) list.Add(StackPercentage2);
        if (StackPercentage3 > 0) list.Add(StackPercentage3);
        return list.Distinct().OrderBy(p => p).ToList();
    }



    public void SyncOptionsFromUi()
    {
        Options.RunMode = IsJoinMode ? ProcessingRunMode.JoinMode : ProcessingRunMode.BatchMode;
        Options.LimitFrameRange = LimitFrameRange;
        Options.StartFrame = Math.Max(1, StartFrame);
        Options.EndFrame = Math.Max(Options.StartFrame, EndFrame);

        Options.FileFrameRanges.Clear();
        foreach (var file in SourceFiles)
        {
            if (file.HasCustomRange)
            {
                Options.FileFrameRanges[file.FullPath] = (file.SelectionIn, file.SelectionOut);
            }
        }

        Options.DarkFiles = DarkFiles.ToList();
        Options.FlatFiles = FlatFiles.ToList();
        Options.FlatDarkFiles = FlatDarkFiles.ToList();
        Options.DarkFrameSubtract = DarkSubtractEnabled || DarkFiles.Count > 0;
        Options.FlatFrameCalibrate = FlatFiles.Count > 0;
        Options.FlatDarkFrameSubtract = FlatDarkFiles.Count > 0;
        Options.HotPixelThreshold = HotPixelThreshold;
        Options.ReplaceHotPixels = DarkSubtractEnabled;
        Options.DebayerEnabled = DebayerEnabled;
        Options.DebayerPattern = (BayerPattern)Math.Clamp(SelectedBayerPatternIndex, 0, 3);
        Options.DebayerType = (DebayerAlgorithm)Math.Clamp(SelectedDebayerAlgoIndex, 0, 2);
        Options.Binning = SelectedBinningIndex == 0 ? BinningMode.Disabled : (BinningMode)(SelectedBinningIndex + 1);

        Options.StabilisationMode = (StabilisationMode)Math.Clamp(SelectedStabilisationModeIndex, 0, 2);
        Options.CheckPlanetObjectPresent = CheckObjectPresent && SelectedStabilisationModeIndex == 1;
        Options.CentreObjectInFrame = CentreObjectInFrame && SelectedStabilisationModeIndex == 1;
        Options.AutoDetectionThreshold = AutoThreshold;
        Options.ManualDetectionThreshold = DetectionThreshold;
        Options.MinimumObjectSizePixels = MinObjectSizePixels;
        Options.ShowDetectedPixelsRed = ShowDetectedPixelsRed;
        Options.EdgeInShadow = (LunarShadowEdge)Math.Clamp(SelectedShadowEdgeIndex, 0, 8);

        Options.EnableSurfaceStabilisation = SelectedStabilisationModeIndex == 2;
        Options.AfbWidth = AfbSize;
        Options.AfbHeight = AfbSize;
        Options.AfbX = Math.Max(4, TargetBoxX - AfbSize / 2);
        Options.AfbY = Math.Max(4, TargetBoxY - AfbSize / 2);

        Options.EnableCropping = EnableCropping;
        Options.CropWidth = Math.Max(16, CropWidth);
        Options.CropHeight = Math.Max(16, CropHeight);
        Options.CropOffsetX = CropOffsetX;
        Options.CropOffsetY = CropOffsetY;

        Options.InputGain = (decimal)InputGain;
        Options.Gamma = (decimal)Gamma;
        Options.EnableHistogramStretch = HistogramStretchEnabled;
        Options.HistogramStretchWhitePointPercent = HistogramStretchPercent;
        Options.ConvertToMonochrome = ConvertToMono;
        Options.EnableMedianNoiseFilter = MedianFilterEnabled;
        Options.Rotation = (RotationAngle)Math.Clamp(SelectedRotationIndex, 0, 3);
        Options.FlipHorizontalX = FlipHorizontal;
        Options.FlipVerticalY = FlipVertical;
                Options.AutoRotateAlign = AutoRotateAlign;
        Options.EnableFieldDerotation = EnableFieldDerotation;
        Options.ObserverLatitudeDegrees = ObserverLatitudeDegrees;
        Options.TargetAltitudeDegrees = TargetAltitudeDegrees;
        Options.TargetAzimuthDegrees = TargetAzimuthDegrees;
        Options.AdmissibleBlurPixels = AdmissibleBlurPixels;

        Options.EnableQualityEstimation = EnableQuality;
        Options.QualityAlgorithm = (QualityAlgorithm)Math.Clamp(SelectedQualityAlgoIndex, 0, 3);
        // En Join Mode, les vidéos sont fusionnées l'une après l'autre (ordre de la liste) : pas de réordonnancement par qualité.
        Options.QualityReorderFrames = ReorderByQuality && !IsJoinMode;
        Options.EnableQualityLimiting = EnableQuality && LimitQualityFrames;
        Options.QualityLimitingMode = SelectedQualityLimitModeIndex == 0 ? QualityLimitingType.NumberOfFrames : QualityLimitingType.PercentageOfFrames;
        Options.QualityFramesToKeep = Math.Max(1, QualityKeepValue);
        Options.EnableQualityWeighting = EnableQualityWeighting;

        Options.AnimationForwardPlay = AnimationForward;
        Options.AnimationReversePlay = AnimationReverse;
        Options.AnimationRepeatAllSequence = AnimationRepeatCount > 1;
        Options.AnimationRepeatAllCount = AnimationRepeatCount;

        if (WorkflowMode is not StackingWorkflowMode.PreProcessingOnly)
        {
            Options.OutputFormat = SelectedStackingImageFormatIndex switch
            {
                1 => OutputFormat.Fits,
                2 => OutputFormat.Png,
                _ => OutputFormat.Tiff
            };
        }
        else
        {
            Options.OutputFormat = SelectedOutputFormatIndex switch
            {
                0 => OutputFormat.Avi,
                1 => OutputFormat.Mp4,
                2 => OutputFormat.Ser,
                3 => OutputFormat.Bmp,
                4 => OutputFormat.Png,
                5 => OutputFormat.Tiff,
                6 => OutputFormat.Fits,
                _ => OutputFormat.Avi
            };
        }
        Options.AviFpsSameAsInput = MatchInputFps;
        Options.AviFpsValue = Math.Max(1, OutputFps);
        Options.OutputFilenameSuffix = OutputSuffix;
        Options.GenerateWinJuposFilenames = WinJuposNaming;
        Options.MaxCpuUsagePercent = Math.Clamp(MaxCpuUsagePercent, 10, 100);

        Options.Stacking.WorkflowMode = WorkflowMode;
        Options.Stacking.StabilisationMode = StackingStabilisationMode;
        Options.Stacking.DisableStabilisation = DisableStabilisation;
        Options.Stacking.CanvasMode = StackingCanvasMode;
        Options.Stacking.NoiseRobust = NoiseRobust;
        Options.Stacking.QualityScope = QualityScope;
        Options.Stacking.DoubleStackReference = DoubleStackReference;
        Options.Stacking.ReferenceMode = ReferenceFrameMode;
        Options.Stacking.ManualReferenceFrameIndex = ManualReferenceFrameIndex;
        Options.Stacking.AlignmentTileSize = AlignmentTileSize;
        Options.Stacking.MinBrightnessThreshold = MinBrightnessThreshold;
        Options.Stacking.EnablePruning = EnablePruning;
        Options.Stacking.EnableMultiScaleGrid = EnableMultiScaleGrid;
        var activePcts = GetActiveStackPercentages();
        Options.Stacking.StackPercentages = activePcts;
        Options.Stacking.CustomAlignmentGrid = (!IsWorkflowPreProcessingOnly && CurrentAlignmentGrid.ActiveCount > 0)
            ? CurrentAlignmentGrid
            : null;
        Options.Stacking.StackFrameCounts = [StackFrameCount];
        Options.Stacking.Drizzle = SelectedDrizzleIndex switch
        {
            1 => DrizzleScale.Drizzle15,
            2 => DrizzleScale.Drizzle30,
            _ => DrizzleScale.None
        };
        Options.Stacking.NormalizeBrightness = NormalizeBrightness;
        Options.Stacking.NormalizeTargetPercent = NormalizeTargetPercent;
        Options.Stacking.CorrectAtmosphericDispersion = CorrectAtmosphericDispersion;
        Options.Stacking.SharpeningEnabled = SharpeningEnabled;
        Options.Stacking.SharpeningBlendPercent = SharpeningBlendPercent;
        Options.Stacking.FeatheringEnabled = FeatheringEnabled;
    }

    [RelayCommand]
    public void SetWorkflowMode(string mode)
    {
        switch (mode.ToLowerInvariant())
        {
            case "prep":
            case "preprocessingonly":
                WorkflowMode = StackingWorkflowMode.PreProcessingOnly;
                IsWorkflowPreProcessingOnly = true;
                IsWorkflowStackingOnly = false;
                IsWorkflowDirectStreamAllInOne = false;
                break;
            case "stack":
            case "stackingonly":
                WorkflowMode = StackingWorkflowMode.StackingOnly;
                IsWorkflowPreProcessingOnly = false;
                IsWorkflowStackingOnly = true;
                if (SelectedOutputFormatIndex is 0 or 1 or 2)
                {
                    SelectedOutputFormatIndex = 5;
                    SelectedStackingImageFormatIndex = 0;
                }
                IsWorkflowDirectStreamAllInOne = false;
                break;
            case "all":
            case "directstreamallinone":
            case "directstream":
            default:
                WorkflowMode = StackingWorkflowMode.DirectStreamAllInOne;
                IsWorkflowPreProcessingOnly = false;
                IsWorkflowStackingOnly = false;
                IsWorkflowDirectStreamAllInOne = true;
                if (SelectedOutputFormatIndex is 0 or 1 or 2)
                {
                    SelectedOutputFormatIndex = 5;
                    SelectedStackingImageFormatIndex = 0;
                }
                break;
        }
    }

    [RelayCommand]
    public void SetAlignmentTileSize(int size)
    {
        AlignmentTileSize = size;
        CalculateAlignmentGrid();
    }

    [RelayCommand]
    public void CalculateAlignmentGrid()
    {
        if (IsWorkflowPreProcessingOnly) return;
        SyncOptionsFromUi();

        AstroFrame? frame = null;

        if (EnableCropping)
        {
            if (PreviewCroppedOutput && _lastProcessedPreviewFrame is not null && 
                _lastProcessedPreviewFrame.Width == CropWidth && _lastProcessedPreviewFrame.Height == CropHeight)
            {
                // La trame déjà prévisualisée est déjà parfaitement recadrée et centrée
                frame = _lastProcessedPreviewFrame;
            }
            else if ((_cachedPreviewResult?.Frame ?? _lastProcessedPreviewFrame) is { } baseFrame)
            {
                int targetX = (CentreObjectInFrame && SelectedStabilisationModeIndex == 1 && _lastDetection is not null)
                    ? _lastDetection.CentroidX
                    : (SelectedStabilisationModeIndex == 2 ? TargetBoxX : baseFrame.Width / 2);
                int targetY = (CentreObjectInFrame && SelectedStabilisationModeIndex == 1 && _lastDetection is not null)
                    ? _lastDetection.CentroidY
                    : (SelectedStabilisationModeIndex == 2 ? TargetBoxY : baseFrame.Height / 2);

                frame = ImageProcessor.CropAroundPoint(
                    baseFrame,
                    targetX,
                    targetY,
                    CropWidth,
                    CropHeight,
                    CropOffsetX,
                    CropOffsetY,
                    protectBayerEvenSteps: Options.ProtectBayerPattern);
            }
        }
        else
        {
            frame = _lastProcessedPreviewFrame ?? _cachedPreviewResult?.Frame;
        }

        if (frame is null) return;

        CurrentAlignmentGrid = AlignmentGridGenerator.GenerateGrid(frame, Options.Stacking);
        AlignmentStatusText = string.Format(Loc.Get("Alignment.PointsActive"), CurrentAlignmentGrid.ActiveCount, CurrentAlignmentGrid.TotalCount);
        OnPropertyChanged(nameof(ActivePointsCountText));
    }

    [RelayCommand]
    public void ClearAlignmentGrid()
    {
        CurrentAlignmentGrid = AlignmentGrid.Empty;
        AlignmentStatusText = string.Empty;
        OnPropertyChanged(nameof(ActivePointsCountText));
    }

    public void AddManualAlignmentPoint(int x, int y)
    {
        if (IsWorkflowPreProcessingOnly) return;

        int fw = CurrentAlignmentGrid.FrameWidth > 0 
            ? CurrentAlignmentGrid.FrameWidth 
            : (EnableCropping ? CropWidth : (_cachedPreviewResult?.Frame.Width ?? 0));
        int fh = CurrentAlignmentGrid.FrameHeight > 0 
            ? CurrentAlignmentGrid.FrameHeight 
            : (EnableCropping ? CropHeight : (_cachedPreviewResult?.Frame.Height ?? 0));

        if (fw > 0 && fh > 0)
        {
            if (x < 0 || x >= fw || y < 0 || y >= fh)
                return;
        }

        AstroFrame? frame = null;
        if (EnableCropping)
        {
            if (PreviewCroppedOutput && _lastProcessedPreviewFrame is not null &&
                _lastProcessedPreviewFrame.Width == fw && _lastProcessedPreviewFrame.Height == fh)
            {
                frame = _lastProcessedPreviewFrame;
            }
            else if ((_cachedPreviewResult?.Frame ?? _lastProcessedPreviewFrame) is { } baseFrame)
            {
                int targetX = (CentreObjectInFrame && SelectedStabilisationModeIndex == 1 && _lastDetection is not null)
                    ? _lastDetection.CentroidX
                    : (SelectedStabilisationModeIndex == 2 ? TargetBoxX : baseFrame.Width / 2);
                int targetY = (CentreObjectInFrame && SelectedStabilisationModeIndex == 1 && _lastDetection is not null)
                    ? _lastDetection.CentroidY
                    : (SelectedStabilisationModeIndex == 2 ? TargetBoxY : baseFrame.Height / 2);

                frame = ImageProcessor.CropAroundPoint(
                    baseFrame,
                    targetX,
                    targetY,
                    fw,
                    fh,
                    CropOffsetX,
                    CropOffsetY,
                    protectBayerEvenSteps: Options.ProtectBayerPattern);
            }
        }
        else
        {
            frame = _lastProcessedPreviewFrame ?? _cachedPreviewResult?.Frame;
        }

        int tileSize = AlignmentTileSize;
        var existing = new List<AlignmentPoint>(CurrentAlignmentGrid.Points);

        // Éviter les doublons trop proches (< 6px)
        if (existing.Any(p => Math.Abs(p.X - x) < 6 && Math.Abs(p.Y - y) < 6))
            return;

        int newId = existing.Count > 0 ? existing.Max(p => p.Id) + 1 : 1;
        var newPoint = AlignmentGridGenerator.CreateManualPoint(frame, newId, x, y, tileSize);
        existing.Add(newPoint);

        var anchor = CurrentAlignmentGrid.AnchorPoint.X >= 0
            ? CurrentAlignmentGrid.AnchorPoint
            : (fw > 0 && fh > 0 ? (fw / 2, fh / 2) : (-1, -1));

        CurrentAlignmentGrid = new AlignmentGrid(
            tileSize,
            MinBrightnessThreshold,
            existing,
            anchor,
            fw,
            fh);

        AlignmentStatusText = string.Format(Loc.Get("Alignment.PointsActive"), CurrentAlignmentGrid.ActiveCount, CurrentAlignmentGrid.TotalCount);
        OnPropertyChanged(nameof(ActivePointsCountText));
    }

    public void RemoveManualAlignmentPoint(int x, int y)
    {
        if (IsWorkflowPreProcessingOnly || CurrentAlignmentGrid.Points.Count == 0)
            return;

        int tileSize = CurrentAlignmentGrid.TileSize > 0 ? CurrentAlignmentGrid.TileSize : AlignmentTileSize;
        int toleranceSq = (tileSize / 2) * (tileSize / 2);

        var matching = CurrentAlignmentGrid.Points
            .Where(p =>
            {
                int dx = p.X - x;
                int dy = p.Y - y;
                return (dx * dx + dy * dy) <= Math.Max(toleranceSq, 16 * 16);
            })
            .ToList();

        if (matching.Count == 0)
            return;

        var closest = matching.OrderBy(p =>
        {
            int dx = p.X - x;
            int dy = p.Y - y;
            return dx * dx + dy * dy;
        }).First();

        var existing = CurrentAlignmentGrid.Points.Where(p => p.Id != closest.Id).ToList();

        CurrentAlignmentGrid = new AlignmentGrid(
            CurrentAlignmentGrid.TileSize,
            CurrentAlignmentGrid.MinBrightness,
            existing,
            CurrentAlignmentGrid.AnchorPoint,
            CurrentAlignmentGrid.FrameWidth,
            CurrentAlignmentGrid.FrameHeight);

        AlignmentStatusText = string.Format(Loc.Get("Alignment.PointsActive"), CurrentAlignmentGrid.ActiveCount, CurrentAlignmentGrid.TotalCount);
        OnPropertyChanged(nameof(ActivePointsCountText));
    }
}
