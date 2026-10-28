using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Serialization;
using CassiniFlow.Infrastructure.WinJupos;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.Theme;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class SourceFileItem : ObservableObject
{
    [ObservableProperty] private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanetSizeText))]
    private double? _planetDiameterPx;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanetSizeText))]
    private bool _isMeasuring;

    public void RefreshLocalizedText() => OnPropertyChanged(nameof(PlanetSizeText));

    public string PlanetSizeText => IsMeasuring ? Loc.Pick("mesure...", "measuring...") : PlanetDiameterPx is double d ? $"{d:F0} px" : "—";
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string FormatBadge { get; init; } = string.Empty;
    public int Frames { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Resolution => $"{Width} × {Height}";
    public double Fps { get; init; }
    public string FpsInfo => $"{Fps:F1} fps";
    public string FileSize { get; init; } = string.Empty;
    public string DetailedReport { get; init; } = string.Empty;

    public AstroMediaMetadata Metadata { get; init; } = AstroMediaMetadata.Empty;
    public string ExposureInfo => Metadata.EffectiveExposureText;
    public string ShutterSpeedInfo => Metadata.EffectiveShutterSpeedText;
    public string IsoOrGainInfo => Metadata.IsoOrGainText;
    public string CameraInfo => Metadata.Camera ?? "u{2014}";
    public bool HasAstroDetails => Metadata.ExposureTimeSeconds.HasValue || Metadata.Iso.HasValue || Metadata.Gain.HasValue;
}

public partial class MainWindowViewModel : ObservableObject
{
    public ProcessingOptions Options { get; private set; } = new();

    public ObservableCollection<SourceFileItem> SourceFiles { get; } = [];

    private UniversalPreviewFrame? _cachedPreviewResult;
    private string? _cachedRawFilePath;
    private int _cachedRawFrameIndex = -1;

    // Conflated single-worker preview state (prevents queue backlog when dragging timeline slider on 4K MP4 videos)
    private readonly object _previewSync = new();
    private bool _previewWorkerRunning;
    private bool _previewPendingWork;
    private bool _previewPendingDiskReload;

    private CancellationTokenSource? _processingCts;

    [ObservableProperty] private SourceFileItem? _selectedSourceFile;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _hasLoadedFiles;

    // Viewer state
    [ObservableProperty] private int _currentFrameIndex = 1;
    [ObservableProperty] private int _maxFrameCount = 1;
    [ObservableProperty] private string _viewerTitle = Loc.Tr("Aucun fichier sélectionné", "No file selected");
    [ObservableProperty] private string _viewerStatusText = Loc.Get("Viewer.LoadVideoPrompt");
    [ObservableProperty] private string _frameDimensionsText = "—";
    [ObservableProperty] private string _maxPixelStatsText = "Max Pixel : —";
    [ObservableProperty] private string _qualityScoreText = Loc.Tr("Qualité : -", "Quality: -");
    [ObservableProperty] private bool _previewCroppedOutput;
    [ObservableProperty] private bool _showOverlayBoxes = true;
    [ObservableProperty] private bool _showDetectedPixelsRed;

    // 1. Source Files & Preset ("Optimise Options For")
    [ObservableProperty] private bool _isBatchMode = true;
    [ObservableProperty] private bool _isJoinMode;
    [ObservableProperty] private bool _limitFrameRange;
    [ObservableProperty] private int _startFrame = 1;
    [ObservableProperty] private int _endFrame = 1000;
    [ObservableProperty] private int _selectedPresetIndex = 0;
    private bool _isAutoDetectingPreset;
    private bool _userManuallySelectedPreset;
    [ObservableProperty] private int _maxCpuUsagePercent = 80;
    [ObservableProperty] private int _cpuWarningThresholdPercent = 85;
    [ObservableProperty] private bool _isCpuWarningActive;
    [ObservableProperty] private string _cpuWarningTitle = string.Empty;
    [ObservableProperty] private string _cpuWarningMessage = string.Empty;
    [ObservableProperty] private string _activeCpuThreadsBadge = string.Empty;
    [ObservableProperty] private string _activeCpuThreadsText = string.Empty;
    [ObservableProperty] private int _totalLogicalCores = Math.Max(1, Environment.ProcessorCount);
    [ObservableProperty] private string _totalCoresDetectedText = string.Empty;
    [ObservableProperty] private int _selectedThemeIndex = 0;
    [ObservableProperty] private int _selectedLanguageIndex = 0;
    [ObservableProperty] private bool _isSettingsSavedFeedbackVisible;

    public MainWindowViewModel()
    {
        _maxCpuUsagePercent = UserSettingsService.Current.MaxCpuUsagePercent;
        _cpuWarningThresholdPercent = UserSettingsService.Current.CpuWarningThresholdPercent;
        _selectedThemeIndex = UserSettingsService.Current.Theme?.ToLowerInvariant() switch
        {
            "light" => 1,
            "night_vision" or "nightvision" or "red" => 2,
            _ => 0
        };
        _selectedLanguageIndex = string.Equals(UserSettingsService.Current.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        Loc.LanguageChanged += RefreshLocalizedTexts;
        ThemeManager.ThemeChanged += OnThemeChanged;

        UpdateCpuStatus();
    }

    partial void OnMaxCpuUsagePercentChanged(int value)
    {
        UpdateCpuStatus();
        UserSettingsService.UpdateCpuSettings(value, CpuWarningThresholdPercent);
    }

    partial void OnCpuWarningThresholdPercentChanged(int value)
    {
        UpdateCpuStatus();
        UserSettingsService.UpdateCpuSettings(MaxCpuUsagePercent, value);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            1 => AppTheme.CleanLabLight,
            2 => AppTheme.NightVision,
            _ => AppTheme.DeepSpaceDark
        };
        ThemeManager.ApplyTheme(theme, save: true);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var lang = value == 1 ? AppLanguage.English : AppLanguage.French;
        Loc.SetLanguage(lang, save: true);
    }

    private void OnThemeChanged()
    {
        int targetIdx = ThemeManager.CurrentTheme switch
        {
            AppTheme.CleanLabLight => 1,
            AppTheme.NightVision => 2,
            _ => 0
        };
        if (SelectedThemeIndex != targetIdx)
        {
            SelectedThemeIndex = targetIdx;
        }
    }

    private void UpdateCpuStatus()
    {
        int total = Math.Max(1, Environment.ProcessorCount);
        TotalLogicalCores = total;
        TotalCoresDetectedText = Loc.Format("Settings.TotalCoresDetected", total);

        var tmp = new ProcessingOptions { MaxCpuUsagePercent = MaxCpuUsagePercent };
        int used = PipelineRunner.GetSafeWorkerThreadCount(tmp);

        IsCpuWarningActive = MaxCpuUsagePercent >= CpuWarningThresholdPercent;

        ActiveCpuThreadsBadge = $"{used} / {total} threads ({MaxCpuUsagePercent} %)";

        ActiveCpuThreadsText = used == total
            ? Loc.Pick($"{used} threads alloués sur {total} cœurs (Pleine puissance)", $"{used} threads allocated on {total} cores (Full power)")
            : Loc.Pick($"{used} threads alloués ({total - used} cœur(s) réservé(s) pour le système)", $"{used} threads allocated ({total - used} core(s) reserved for OS)");

        CpuWarningTitle = Loc.Get("Settings.CpuWarningTitle");

        CpuWarningMessage = Loc.Pick(
            $"L'allocation de {MaxCpuUsagePercent}% ({used} threads) atteint ou dépasse votre seuil d'avertissement ({CpuWarningThresholdPercent}%). Les autres applications ouvertes risquent de ralentir pendant le traitement.",
            $"Allocating {MaxCpuUsagePercent}% ({used} threads) reaches or exceeds your alert threshold ({CpuWarningThresholdPercent}%). Background applications may become less responsive during processing.");
    }

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

    // 6. Output Options (Default to index 0 = AVI compatible with VLC & AutoStakkert)
    [ObservableProperty] private int _selectedOutputFormatIndex = 0;
    [ObservableProperty] private bool _matchInputFps = true;
    [ObservableProperty] private int _outputFps = 25;
    [ObservableProperty] private string _outputSuffix = "_cassiniflow";
    [ObservableProperty] private bool _winJuposNaming = false;
    [ObservableProperty] private string _customOutputDirectory = string.Empty;

    // 7. Do Processing
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private int _processingProgress;
    [ObservableProperty] private string _processingStageText = Loc.Tr("Prêt", "Ready");
    [ObservableProperty] private string _lastOutputFilePath = string.Empty;
    [ObservableProperty] private string _lastOutputDirectory = string.Empty;
    [ObservableProperty] private bool _hasCompletedOutput;
    [ObservableProperty] private string _summaryStatsText = Loc.Tr("Aucun traitement exécuté.", "No processing run yet.");
    [ObservableProperty] private string _consoleLogText = Loc.Tr("Ajoutez un ou plusieurs fichiers vidéos/images dans l'onglet '1. Source Files' pour commencer.", "Add one or more video/image files in the '1. Source Files' tab to get started.");

    public event Action<AstroFrame?, DetectionResult?, double>? PreviewReady;

    partial void OnSelectedSourceFileChanged(SourceFileItem? value)
    {
        HasLoadedFiles = SourceFiles.Count > 0;
        if (value is null)
        {
            _cachedPreviewResult = null;
            _cachedRawFilePath = null;
            ViewerTitle = Loc.Tr("Aucun fichier sélectionné", "No file selected");
            FrameDimensionsText = "—";
            PreviewReady?.Invoke(null, null, 1.0);
            return;
        }

        MaxFrameCount = Math.Max(1, value.Frames);
        EndFrame = MaxFrameCount;
        CurrentFrameIndex = 1;
        OutputFps = Math.Max(1, (int)Math.Round(value.Fps));
        TargetBoxX = Math.Max(16, value.Width / 2);
        TargetBoxY = Math.Max(16, value.Height / 2);

        // Preserve user/preset crop size (e.g. 448x448 for planetary); only clamp if source video is smaller
        if (CropWidth > value.Width)
        {
            CropWidth = Math.Max(64, value.Width & ~3);
        }
        if (CropHeight > value.Height)
        {
            CropHeight = Math.Max(64, value.Height & ~3);
        }

        RequestPreviewUpdate(reloadFromDisk: true);
    }

    partial void OnSelectedPresetIndexChanged(int value)
    {
        if (!_isAutoDetectingPreset)
        {
            _userManuallySelectedPreset = true;
        }

        int maxW = SelectedSourceFile?.Width ?? 4096;
        int maxH = SelectedSourceFile?.Height ?? 4096;

        switch (value)
        {
            case 1: // Planetary
                SelectedStabilisationModeIndex = 1;
                CheckObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                if (SelectedSourceFile?.PlanetDiameterPx is double planetD && planetD > 5)
                {
                    int pCrop = Math.Clamp((int)Math.Ceiling(planetD * 1.6), 64, Math.Min(maxW, maxH)) & ~7;
                    CropWidth = pCrop;
                    CropHeight = pCrop;
                }
                else
                {
                    CropWidth = Math.Min(448, maxW);
                    CropHeight = Math.Min(448, maxH);
                }
                EnableQuality = true;
                SelectedQualityLimitModeIndex = 1;
                QualityKeepValue = 25;
                break;
            case 2: // Solar/Lunar Close-up
                SelectedStabilisationModeIndex = 2;
                EnableCropping = false;
                EnableQuality = true;
                SelectedQualityLimitModeIndex = 1;
                QualityKeepValue = 20;
                break;
            case 3: // Solar/Lunar Full Disc
                SelectedStabilisationModeIndex = 1;
                CheckObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                int discCrop;
                if (SelectedSourceFile?.PlanetDiameterPx is double d && d > 10)
                {
                    // Encadrer correctement l'objet avec une marge de 15% à 25% autour du diamètre mesuré
                    discCrop = Math.Clamp((int)Math.Ceiling(d * 1.20), 128, Math.Min(maxW, maxH)) & ~7;
                }
                else
                {
                    discCrop = Math.Clamp((int)(Math.Min(maxW, maxH) * 0.90), 256, Math.Min(maxW, maxH)) & ~7;
                }
                CropWidth = discCrop;
                CropHeight = discCrop;
                SelectedQualityAlgoIndex = 2;
                break;
            case 4: // ISS
                SelectedStabilisationModeIndex = 1;
                CheckObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                CropWidth = Math.Min(448, maxW);
                CropHeight = Math.Min(448, maxH);
                SelectedQualityAlgoIndex = 3;
                break;
            case 5: // Planetary Animation
                SelectedStabilisationModeIndex = 1;
                CentreObjectInFrame = true;
                EnableCropping = true;
                CropWidth = Math.Min(448, maxW);
                CropHeight = Math.Min(448, maxH);
                EnableQuality = false;
                SelectedOutputFormatIndex = 1;
                break;
        }
        RequestPreviewUpdate(reloadFromDisk: false);
    }

    partial void OnCurrentFrameIndexChanged(int value) => RequestPreviewUpdate(reloadFromDisk: true);
    partial void OnPreviewCroppedOutputChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnShowOverlayBoxesChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnShowDetectedPixelsRedChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnDebayerEnabledChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnSelectedBayerPatternIndexChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnSelectedDebayerAlgoIndexChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnSelectedBinningIndexChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnSelectedStabilisationModeIndexChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnCheckObjectPresentChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnCentreObjectInFrameChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnAutoThresholdChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnDetectionThresholdChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnSelectedShadowEdgeIndexChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnEnableCroppingChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnCropWidthChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnCropHeightChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnCropOffsetXChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnCropOffsetYChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnInputGainChanged(double value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnGammaChanged(double value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnHistogramStretchEnabledChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnHistogramStretchPercentChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnConvertToMonoChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnMedianFilterEnabledChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnAfbSizeChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnSelectedRotationIndexChanged(int value)
    {
        if (value > 0) PreviewCroppedOutput = true;
        RequestPreviewUpdate(reloadFromDisk: false);
    }
    partial void OnFlipHorizontalChanged(bool value)
    {
        if (value) PreviewCroppedOutput = true;
        RequestPreviewUpdate(reloadFromDisk: false);
    }
    partial void OnFlipVerticalChanged(bool value)
    {
        if (value) PreviewCroppedOutput = true;
        RequestPreviewUpdate(reloadFromDisk: false);
    }
    partial void OnAutoRotateAlignChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);

    public void SetAnchorOrCropTarget(int x, int y)
    {
        switch (SelectedStabilisationModeIndex)
        {
            case 2:
                TargetBoxX = x;
                TargetBoxY = y;
                RequestPreviewUpdate(reloadFromDisk: false);
                break;
            default:
                if (SelectedSourceFile is not null)
                {
                    CropOffsetX = (SelectedSourceFile.Width / 2) - x;
                    CropOffsetY = (SelectedSourceFile.Height / 2) - y;
                    RequestPreviewUpdate(reloadFromDisk: false);
                }
                break;
        }
    }

    public async Task AddPhysicalFilesAsync(IEnumerable<string> filePaths)
    {
        var paths = filePaths.Where(File.Exists).ToList();
        if (paths.Count == 0) return;

        var newItems = new List<SourceFileItem>();

        await Task.Run(() =>
        {
            foreach (string path in paths)
            {
                try
                {
                    var summary = UniversalMediaReader.InspectFile(path);
                    double mb = summary.FileSizeBytes / (1024.0 * 1024.0);
                    string sizeStr = mb >= 1024 ? $"{mb / 1024.0:F2} Go" : $"{mb:F1} Mo";

                    var item = new SourceFileItem
                    {
                        FileName = Path.GetFileName(path),
                        FullPath = path,
                        FormatBadge = summary.FormatBadge,
                        Frames = summary.FrameCount,
                        Width = summary.Width,
                        Height = summary.Height,
                        Fps = summary.Fps,
                        FileSize = sizeStr,
                        DetailedReport = summary.DetailedReport,
                        Metadata = summary.Metadata ?? AstroMediaMetadata.Empty
                    };

                    newItems.Add(item);
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ConsoleLogText += $"\n{Loc.Pick("[Erreur lecture]", "[Read error]")} {Path.GetFileName(path)} : {ex.Message}";
                    });
                }
            }
        });

        if (newItems.Count == 0) return;

        foreach (var item in newItems)
        {
            SourceFiles.Add(item);
        }

        HasLoadedFiles = SourceFiles.Count > 0;
        if (SourceFiles.Count > 1)
        {
            IsJoinMode = true;
            IsBatchMode = false;
        }

        _userManuallySelectedPreset = false;
        var firstNew = newItems[0];
        SelectedSourceFile ??= firstNew;
        ConsoleLogText = Loc.Pick(
            $"{SourceFiles.Count} fichier(s) dans la liste. Sélection : {firstNew.FileName} ({firstNew.Resolution}, {firstNew.Frames} frames à {firstNew.FpsInfo}).",
            $"{SourceFiles.Count} file(s) in the list. Selected: {firstNew.FileName} ({firstNew.Resolution}, {firstNew.Frames} frames at {firstNew.FpsInfo}).");

        await AnalyzePlanetSizes();
    }


    [RelayCommand]
    public void RemoveSelectedFile()
    {
        var checkedItems = SourceFiles.Where(f => f.IsChecked).ToList();
        if (checkedItems.Count == 0)
        {
            if (SelectedSourceFile is null) return;
            checkedItems.Add(SelectedSourceFile);
        }
        UniversalMediaReader.ClosePreviewCache();
        int idx = SelectedSourceFile is null ? 0 : SourceFiles.IndexOf(SelectedSourceFile);
        foreach (var it in checkedItems) SourceFiles.Remove(it);
        HasLoadedFiles = SourceFiles.Count > 0;
        SelectedSourceFile = SourceFiles.Count > 0
            ? SourceFiles[Math.Clamp(idx, 0, SourceFiles.Count - 1)]
            : null;
    }


    [RelayCommand]
    public void ToggleCheckAllFiles()
    {
        bool target = SourceFiles.Any(f => !f.IsChecked);
        foreach (var f in SourceFiles) f.IsChecked = target;
    }


    [RelayCommand]
    public void MoveSelectedFileUp() => MoveSelectedFile(-1);


    [RelayCommand]
    public void MoveSelectedFileDown() => MoveSelectedFile(1);

    private void MoveSelectedFile(int delta)
    {
        var item = SelectedSourceFile;
        if (item is null) return;
        int idx = SourceFiles.IndexOf(item);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= SourceFiles.Count) return;
        SourceFiles.Move(idx, target);
        SelectedSourceFile = item;
    }


    [RelayCommand]
    public async Task AnalyzePlanetSizes()
    {
        var items = SourceFiles.ToList();
        if (items.Count == 0) return;
        foreach (var it in items) it.IsMeasuring = true;

        await Task.Run(() =>
        {
            foreach (var it in items)
            {
                double? d = null;
                try { d = PlanetSizeAnalyzer.MeasureDiameterPixels(it.FullPath, it.Frames); } catch { }
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    it.PlanetDiameterPx = d;
                    it.IsMeasuring = false;
                });
            }
        });

        var sizes = SourceFiles.Where(f => f.PlanetDiameterPx.HasValue).Select(f => f.PlanetDiameterPx!.Value).ToList();
        ConsoleLogText = sizes.Count switch
        {
            0 => Loc.Pick("Taille de l'objet non détectable dans les fichiers (vérifiez le seuil / la présence de la planète).", "Object size could not be detected in the files (check the threshold / that the planet is present)."),
            1 => Loc.Pick($"Taille de l'objet : {sizes[0]:F0} px.", $"Object size: {sizes[0]:F0} px."),
            _ => FormatObjectSizeSpread(sizes)
        };

        // Reconnaissance automatique de l'objet : uniquement si l'utilisateur n'a pas sélectionné manuellement un preset
        var targetFile = SelectedSourceFile;
        if (!_userManuallySelectedPreset && SelectedPresetIndex == 0 && targetFile is not null && PlanetSizeAnalyzer.PreviewFrameLoader is not null)
        {
            try
            {
                var preview = PlanetSizeAnalyzer.PreviewFrameLoader(targetFile.FullPath, 0, 960);
                var classification = CelestialTargetClassifier.Classify(
                    preview.Frame,
                    targetFile.PlanetDiameterPx,
                    preview.OriginalWidth,
                    preview.OriginalHeight);

                if (classification.TargetType != CelestialTargetType.Unknown &&
                    classification.RecommendedPresetIndex > 0)
                {
                    try
                    {
                        _isAutoDetectingPreset = true;
                        SelectedPresetIndex = classification.RecommendedPresetIndex;
                    }
                    finally
                    {
                        _isAutoDetectingPreset = false;
                    }
                    ConsoleLogText += $"\n{Loc.Pick($"🎯 Reconnaissance automatique : {classification.DisplayNameFr} -> Preset « {GetPresetName(classification.RecommendedPresetIndex)} » activé automatiquement.", $"🎯 Automatic recognition: {classification.DisplayNameEn} -> Preset \"{GetPresetName(classification.RecommendedPresetIndex)}\" selected automatically.")}";
                }
            }
            catch { }
        }
    }

    [RelayCommand]
    public void AutoDetectPreset()
    {
        var item = SelectedSourceFile;
        if (item is null || PlanetSizeAnalyzer.PreviewFrameLoader is null) return;

        try
        {
            var preview = PlanetSizeAnalyzer.PreviewFrameLoader(item.FullPath, 0, 960);
            var classification = CelestialTargetClassifier.Classify(
                preview.Frame,
                item.PlanetDiameterPx,
                preview.OriginalWidth,
                preview.OriginalHeight);

            if (classification.TargetType != CelestialTargetType.Unknown &&
                classification.RecommendedPresetIndex > 0)
            {
                try
                {
                    _isAutoDetectingPreset = true;
                    SelectedPresetIndex = classification.RecommendedPresetIndex;
                    _userManuallySelectedPreset = false;
                }
                finally
                {
                    _isAutoDetectingPreset = false;
                }
                ConsoleLogText = Loc.Pick(
                    $"🎯 Reconnaissance automatique : {classification.DisplayNameFr} -> Preset « {GetPresetName(classification.RecommendedPresetIndex)} » activé ({classification.Details}).",
                    $"🎯 Automatic recognition: {classification.DisplayNameEn} -> Preset \"{GetPresetName(classification.RecommendedPresetIndex)}\" activated ({classification.Details}).");
            }
            else
            {
                ConsoleLogText = Loc.Pick(
                    "Objet céleste non reconnu avec certitude. Le mode Manuel est conservé.",
                    "Celestial object could not be identified with certainty. Manual mode is retained.");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogText = Loc.Pick($"Erreur reconnaissance : {ex.Message}", $"Recognition error: {ex.Message}");
        }
    }

    private static string GetPresetName(int index) => index switch
    {
        1 => Loc.Pick("Planétaire", "Planetary"),
        2 => Loc.Pick("Solaire / Lunaire (gros plan)", "Solar / Lunar Close-up"),
        3 => Loc.Pick("Solaire / Lunaire (disque entier)", "Solar / Lunar Full Disc"),
        4 => Loc.Pick("ISS", "ISS"),
        5 => Loc.Pick("Animation planétaire", "Planetary Animation"),
        _ => Loc.Pick("Manuel", "Manual")
    };

    private static string FormatObjectSizeSpread(IReadOnlyList<double> sizes)
    {
        double min = sizes.Min(), max = sizes.Max();
        double spread = (max - min) / max * 100.0;
        return spread > 10.0
            ? Loc.Pick($"⚠ Tailles d'objet différentes : de {min:F0} à {max:F0} px (écart {spread:F0} %). Les images ne seront pas à la même échelle lors de l'alignement.", $"⚠ Different object sizes: from {min:F0} to {max:F0} px ({spread:F0} % spread). Images will not be at the same scale when aligned.")
            : Loc.Pick($"✔ Tailles d'objet homogènes : de {min:F0} à {max:F0} px (écart {spread:F0} %).", $"✔ Consistent object sizes: from {min:F0} to {max:F0} px ({spread:F0} % spread).");
    }


    [RelayCommand]
    public void SortFilesBySizeAscending() => SortFilesBySize(false);


    [RelayCommand]
    public void SortFilesBySizeDescending() => SortFilesBySize(true);

    private void SortFilesBySize(bool descending)
    {
        var selected = SelectedSourceFile;
        var sorted = (descending
            ? SourceFiles.OrderByDescending(f => f.PlanetDiameterPx ?? double.NegativeInfinity)
            : SourceFiles.OrderBy(f => f.PlanetDiameterPx ?? double.PositiveInfinity)).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = SourceFiles.IndexOf(sorted[i]);
            if (cur != i) SourceFiles.Move(cur, i);
        }
        SelectedSourceFile = selected;
    }
    /// <summary>Retraduit les textes fixes déjà affichés (messages par défaut, état) après un changement de langue.</summary>
    public void RefreshLocalizedTexts()
    {
        ViewerTitle = Loc.Retranslate(ViewerTitle);
        ViewerStatusText = Loc.Retranslate(ViewerStatusText);
        QualityScoreText = Loc.Retranslate(QualityScoreText);
        ProcessingStageText = Loc.Retranslate(ProcessingStageText);
        SummaryStatsText = Loc.Retranslate(SummaryStatsText);
        ConsoleLogText = Loc.Retranslate(ConsoleLogText);
        UpdateCpuStatus();
        foreach (var f in SourceFiles) f.RefreshLocalizedText();
        if (SelectedSourceFile is not null) RequestPreviewUpdate(reloadFromDisk: false);
    }

    public event Action? RequestOpenSettings;

    [RelayCommand]
    public void OpenSettings() => RequestOpenSettings?.Invoke();

    [RelayCommand]
    public void OpenSettingsTab() => RequestOpenSettings?.Invoke();

    [RelayCommand]
    public async Task SaveUserSettings()
    {
        UserSettingsService.UpdateCpuSettings(MaxCpuUsagePercent, CpuWarningThresholdPercent);
        IsSettingsSavedFeedbackVisible = true;
        await Task.Delay(2500);
        IsSettingsSavedFeedbackVisible = false;
    }


    [RelayCommand]
    public void ClearAllFiles()    {
        UniversalMediaReader.ClosePreviewCache();
        SourceFiles.Clear();
        HasLoadedFiles = false;
        SelectedSourceFile = null;
    }


    [RelayCommand]
    public void FirstFrame() => CurrentFrameIndex = 1;


    [RelayCommand]
    public void PreviousFrame() => CurrentFrameIndex = Math.Max(1, CurrentFrameIndex - 1);


    [RelayCommand]
    public void NextFrame() => CurrentFrameIndex = Math.Min(MaxFrameCount, CurrentFrameIndex + 1);


    [RelayCommand]
    public void LastFrame() => CurrentFrameIndex = MaxFrameCount;





    /// <summary>
    /// Enqueues a preview update into a single conflated background loop.
    /// If the user drags the timeline slider rapidly across 100 values, intermediate seeks are skipped
    /// and only the most recent slider value is decoded!
    /// </summary>
    public void RequestPreviewUpdate(bool reloadFromDisk)
    {
        lock (_previewSync)
        {
            _previewPendingWork = true;
            if (reloadFromDisk) _previewPendingDiskReload = true;

            if (_previewWorkerRunning) return;
            _previewWorkerRunning = true;
        }

        Task.Run(PreviewWorkerLoop);
    }

    private void PreviewWorkerLoop()
    {
        while (true)
        {
            bool reloadDisk;
            lock (_previewSync)
            {
                if (!_previewPendingWork)
                {
                    _previewWorkerRunning = false;
                    return;
                }
                reloadDisk = _previewPendingDiskReload;
                _previewPendingWork = false;
                _previewPendingDiskReload = false;
            }

            try
            {
                var source = SelectedSourceFile;
                if (source is null || !File.Exists(source.FullPath))
                {
                    PreviewReady?.Invoke(null, null, 1.0);
                    continue;
                }

                int targetFrameIdx = Math.Clamp(CurrentFrameIndex - 1, 0, Math.Max(0, source.Frames - 1));
                bool applyCrop = PreviewCroppedOutput;
                SyncOptionsFromUi();
                var optionsSnapshot = Options;

                if (reloadDisk || _cachedPreviewResult is null || _cachedRawFilePath != source.FullPath || _cachedRawFrameIndex != targetFrameIdx)
                {
                    // Downscales 4K frames to max 640px for fast timeline scrubbing
                    _cachedPreviewResult = UniversalMediaReader.ReadPreviewFrame(source.FullPath, targetFrameIdx, maxPreviewDim: 640, optionsSnapshot.SerBitDepthOverride);
                    _cachedRawFilePath = source.FullPath;
                    _cachedRawFrameIndex = targetFrameIdx;
                }

                var previewRes = _cachedPreviewResult;
                if (previewRes is null) continue;

                double scale = previewRes.ScaleFactor;
                var (processed, det) = PipelineRunner.PreviewSingleFrame(
                    previewRes.Frame,
                    optionsSnapshot,
                    applyCropToOutput: applyCrop,
                    previewScale: scale);

                var (mg, mr, mgreen, mb) = processed.ComputeMaxPixelValues8Bit();
                int fullCentroidX = (int)Math.Round(det.CentroidX / scale);
                int fullCentroidY = (int)Math.Round(det.CentroidY / scale);
                int outDispW = processed.Width;
                int outDispH = processed.Height;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ViewerTitle = $"{source.FileName} - Frame {targetFrameIdx + 1} / {source.Frames}";
                    FrameDimensionsText = $"{outDispW} × {outDispH} px";
                    MaxPixelStatsText = Loc.Pick($"Max: {mg} (R:{mr} G:{mgreen} B:{mb}) | Seuil détection: {det.Threshold8BitUsed}", $"Max: {mg} (R:{mr} G:{mgreen} B:{mb}) | Detection threshold: {det.Threshold8BitUsed}");
                    QualityScoreText = EnableQuality ? Loc.Pick($"Qualité : {processed.QualityScore:F1}", $"Quality: {processed.QualityScore:F1}") : Loc.Tr("Qualité : Désactivée", "Quality: Disabled");
                    ViewerStatusText = SelectedStabilisationModeIndex switch
                    {
                        1 => det.Accepted
                            ? Loc.Pick($"Objet détecté à ({fullCentroidX}, {fullCentroidY}) - Recadrage : {CropWidth}×{CropHeight}", $"Object detected at ({fullCentroidX}, {fullCentroidY}) - Crop: {CropWidth}×{CropHeight}")
                            : Loc.Pick($"Aucun objet isolé détecté au seuil {det.Threshold8BitUsed} (baissez le seuil ou passez en stabilisation 'Aucune' / 'Surface')", $"No isolated object detected at threshold {det.Threshold8BitUsed} (lower the threshold or switch stabilisation to 'None' / 'Surface')"),
                        2 => Loc.Pick($"Ancrage Surface (AFB {AfbSize}×{AfbSize}) à ({TargetBoxX}, {TargetBoxY})", $"Surface anchor (AFB {AfbSize}×{AfbSize}) at ({TargetBoxX}, {TargetBoxY})"),
                        _ => EnableCropping ? Loc.Pick($"Recadrage fixe : {CropWidth}×{CropHeight}", $"Fixed crop: {CropWidth}×{CropHeight}") : Loc.Pick("Aucune stabilisation", "No stabilisation")
                    };
                });

                PreviewReady?.Invoke(processed, det, applyCrop ? 1.0 : scale);
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ViewerStatusText = Loc.Pick($"Erreur aperçu : {ex.Message}", $"Preview error: {ex.Message}");
                });
            }
        }
    }

    public void SyncOptionsFromUi()
    {
        Options.RunMode = IsJoinMode ? ProcessingRunMode.JoinMode : ProcessingRunMode.BatchMode;
        Options.LimitFrameRange = LimitFrameRange;
        Options.StartFrame = Math.Max(1, StartFrame);
        Options.EndFrame = Math.Max(Options.StartFrame, EndFrame);

        Options.DarkFrameSubtract = DarkSubtractEnabled;
        Options.HotPixelThreshold = HotPixelThreshold;
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
        Options.AviFpsSameAsInput = MatchInputFps;
        Options.AviFpsValue = Math.Max(1, OutputFps);
        Options.OutputFilenameSuffix = OutputSuffix;
        Options.GenerateWinJuposFilenames = WinJuposNaming;
        Options.MaxCpuUsagePercent = Math.Clamp(MaxCpuUsagePercent, 10, 100);
    }


    [RelayCommand]
    public async Task RunProcessingAsync()
    {
        if (SourceFiles.Count == 0)
        {
            SelectedTabIndex = 0;
            ConsoleLogText = Loc.Pick("Veuillez d'abord ajouter au moins un fichier vidéo ou image dans l'onglet '1. Source Files'.", "Please add at least one video or image file in the '1. Source Files' tab first.");
            return;
        }

        if (IsProcessing) return;

        SelectedTabIndex = 5;
        IsProcessing = true;
        HasCompletedOutput = false;
        SyncOptionsFromUi();

        _processingCts?.Cancel();
        _processingCts = new CancellationTokenSource();

        string firstPath = (SelectedSourceFile ?? SourceFiles[0]).FullPath;
        string baseDir = !string.IsNullOrWhiteSpace(CustomOutputDirectory) && Directory.Exists(CustomOutputDirectory)
            ? CustomOutputDirectory
            : Path.GetDirectoryName(firstPath) ?? Environment.CurrentDirectory;

        string outDir = Path.Combine(baseDir, $"CassiniFlow_{DateTime.Now:yyyyMMdd_HHmmss}");
        LastOutputDirectory = outDir;

        var progress = new Progress<PipelineProgressReport>(r =>
        {
            ProcessingProgress = r.PercentComplete;
            ProcessingStageText = $"{Loc.TranslateRuntime(r.Stage)} ({r.PercentComplete}%)";
            ConsoleLogText += $"\n[{DateTime.Now:HH:mm:ss}] {Loc.TranslateRuntime(r.LogMessage)}";
        });

        try
        {
            var filesToRun = SourceFiles.Select(f => f.FullPath).ToList();
            string modeLabel = IsJoinMode ? Loc.Pick("Mode Fusion (fusion en 1 fichier)", "Join Mode (merge into 1 file)") : Loc.Pick("Mode Lot (1 fichier de sortie par source)", "Batch Mode (1 output file per source)");
            ConsoleLogText = Loc.Pick($"[{DateTime.Now:HH:mm:ss}] Démarrage du traitement ({filesToRun.Count} fichier(s), {modeLabel})...", $"[{DateTime.Now:HH:mm:ss}] Starting processing ({filesToRun.Count} file(s), {modeLabel})...");

            var summary = await PipelineRunner.ExecuteFilesAsync(
                filesToRun,
                Options,
                outDir,
                progress,
                _processingCts.Token);

            LastOutputFilePath = summary.OutputFilePath;
            HasCompletedOutput = summary.Success && File.Exists(summary.OutputFilePath);

            double fpsSpeed = summary.TotalInputFrames / Math.Max(0.001, summary.Elapsed.TotalSeconds);
            SummaryStatsText = Loc.Pick($"Frames lues : {summary.TotalInputFrames} | Frames écrites : {summary.OutputFramesWritten} | Rejetées : {summary.DiscardedNoPlanet + summary.DiscardedQuality} | Durée : {summary.Elapsed.TotalSeconds:F2}s ({fpsSpeed:F1} fps)", $"Frames read: {summary.TotalInputFrames} | Frames written: {summary.OutputFramesWritten} | Rejected: {summary.DiscardedNoPlanet + summary.DiscardedQuality} | Duration: {summary.Elapsed.TotalSeconds:F2}s ({fpsSpeed:F1} fps)");
            ConsoleLogText += Loc.Pick($"\n[{DateTime.Now:HH:mm:ss}] Traitement terminé.\nDernier fichier généré : {summary.OutputFilePath}", $"\n[{DateTime.Now:HH:mm:ss}] Processing finished.\nLast file generated: {summary.OutputFilePath}");
        }
        catch (OperationCanceledException)
        {
            ProcessingStageText = Loc.Pick("Annulé par l'utilisateur", "Cancelled by user");
            ConsoleLogText += Loc.Pick($"\n[{DateTime.Now:HH:mm:ss}] Traitement annulé.", $"\n[{DateTime.Now:HH:mm:ss}] Processing cancelled.");
        }
        catch (Exception ex)
        {
            ProcessingStageText = Loc.Pick("Erreur", "Error");
            ConsoleLogText += Loc.Pick($"\n[{DateTime.Now:HH:mm:ss}] Erreur : {ex.Message}", $"\n[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }


    [RelayCommand]
    public void CancelProcessing()
    {
        _processingCts?.Cancel();
    }


    [RelayCommand]
    public void OpenOutputFolder()
    {
        string dir = !string.IsNullOrWhiteSpace(LastOutputDirectory) && Directory.Exists(LastOutputDirectory)
            ? LastOutputDirectory
            : (SelectedSourceFile is not null ? Path.GetDirectoryName(SelectedSourceFile.FullPath) ?? string.Empty : string.Empty);

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
    }


    [RelayCommand]
    public void PlayLastOutputVideo()
    {
        if (!string.IsNullOrWhiteSpace(LastOutputFilePath) && File.Exists(LastOutputFilePath))
        {
            Process.Start(new ProcessStartInfo(LastOutputFilePath) { UseShellExecute = true });
        }
    }
}
