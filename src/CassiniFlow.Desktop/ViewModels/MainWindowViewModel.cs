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
    public string CameraInfo => Metadata.Camera ?? "-";
    public string TelescopeInfo => Metadata.Telescope ?? "-";
    public bool HasCameraInfo => !string.IsNullOrEmpty(Metadata.Camera);
    public bool HasTelescopeInfo => !string.IsNullOrEmpty(Metadata.Telescope);
    public bool HasAstroDetails => Metadata.ExposureTimeSeconds.HasValue || Metadata.Iso.HasValue || Metadata.Gain.HasValue;
}

public partial class MainWindowViewModel : ObservableObject
{
    public ProcessingOptions Options { get; private set; } = new();

    public ObservableCollection<SourceFileItem> SourceFiles { get; } = [];

    private UniversalPreviewFrame? _cachedPreviewResult;
    private string? _cachedRawFilePath;
    private AstroFrame? _lastProcessedPreviewFrame;
    private DetectionResult? _lastDetection;
    private int _cachedRawFrameIndex = -1;

    // Conflated single-worker preview state (prevents queue backlog when dragging timeline slider on 4K MP4 videos)
    private readonly object _previewSync = new();
    private bool _previewWorkerRunning;
    private bool _previewPendingWork;
    private bool _previewPendingDiskReload;

    private CancellationTokenSource? _processingCts;

    [ObservableProperty] private SourceFileItem? _selectedSourceFile;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddFiles))]
    private bool _hasLoadedFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddFiles))]
    private bool _isLoadingFiles;

    [ObservableProperty]
    private double _loadingProgress;

    [ObservableProperty]
    private string _loadingStatusText = string.Empty;

    public bool CanAddFiles => !IsProcessing && !IsLoadingFiles;

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
    partial void OnIsJoinModeChanged(bool value) => UpdateAvailableFrames();
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

    // 6. Output Options (Default to index 0 = AVI compatible with planetary processing engines)
    [ObservableProperty] private int _selectedOutputFormatIndex = 0;
    [ObservableProperty] private bool _matchInputFps = true;
    [ObservableProperty] private int _outputFps = 25;
    [ObservableProperty] private string _outputSuffix = "_cassiniflow";
    [ObservableProperty] private bool _winJuposNaming = false;
    [ObservableProperty] private string _customOutputDirectory = string.Empty;

    // 7. Do Processing
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddFiles))]
    private bool _isProcessing;
    [ObservableProperty] private int _processingProgress;
    [ObservableProperty] private string _processingStageText = Loc.Tr("Prêt", "Ready");
    [ObservableProperty] private string _lastOutputFilePath = string.Empty;
    partial void OnLastOutputFilePathChanged(string value) => OnPropertyChanged(nameof(PlayOrOpenOutputText));

    public string PlayOrOpenOutputText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LastOutputFilePath))
            {
                return Loc.Pick("Ouvrir le résultat", "Open result");
            }
            string ext = Path.GetExtension(LastOutputFilePath).ToLowerInvariant();
            if (ext is ".tif" or ".tiff" or ".png" or ".fit" or ".fits" or ".jpg" or ".jpeg")
            {
                return Loc.Pick("Afficher l'image", "View Image");
            }
            return Loc.Pick("Lire la vidéo", "Play Video");
        }
    }
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
            UpdateAvailableFrames();
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

        UpdateAvailableFrames();
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
                    // Multiplicateur 2.5 pour englober confortablement les anneaux (ex: Saturne) et une marge de sécurité
                    int pCrop = Math.Clamp((int)Math.Ceiling(planetD * 2.5), 96, Math.Min(maxW, maxH)) & ~7;
                    CropWidth = pCrop;
                    CropHeight = pCrop;
                }
                else
                {
                    CropWidth = Math.Min(512, maxW);
                    CropHeight = Math.Min(512, maxH);
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

    partial void OnAlignmentTileSizeChanged(int value)
    {
        OnPropertyChanged(nameof(IsTileSize24));
        OnPropertyChanged(nameof(IsTileSize48));
        OnPropertyChanged(nameof(IsTileSize104));
        OnPropertyChanged(nameof(IsTileSize140));
        OnPropertyChanged(nameof(IsTileSize200));
        CalculateAlignmentGrid();
    }

    partial void OnCurrentAlignmentGridChanged(AlignmentGrid value)
    {
        OnPropertyChanged(nameof(ActivePointsCountText));
    }

    partial void OnSelectedStackingImageFormatIndexChanged(int value)
    {
        SelectedOutputFormatIndex = value switch
        {
            1 => 6, // FITS
            2 => 4, // PNG
            _ => 5  // TIFF
        };
    }
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
        if (IsLoadingFiles || IsProcessing) return;

        var paths = filePaths.Where(File.Exists).ToList();
        if (paths.Count == 0) return;

        IsLoadingFiles = true;
        LoadingProgress = 0;
        LoadingStatusText = string.Format(Loc.Get("SourceFiles.LoadingFiles"), 0, paths.Count);

        var newItems = new SourceFileItem?[paths.Count];
        int processedCount = 0;

        try
        {
            await Task.Run(() =>
            {
                int maxWorkers = Math.Clamp(Environment.ProcessorCount, 1, 16);
                Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers }, i =>
                {
                    string path = paths[i];
                    try
                    {
                        var summary = UniversalMediaReader.InspectFile(path);
                        double mb = summary.FileSizeBytes / (1024.0 * 1024.0);
                        string sizeStr = mb >= 1024 ? $"{mb / 1024.0:F2} Go" : $"{mb:F1} Mo";

                        newItems[i] = new SourceFileItem
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
                    }
                    catch (Exception ex)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            ConsoleLogText += $"\n{Loc.Pick("[Erreur lecture]", "[Read error]")} {Path.GetFileName(path)} : {ex.Message}";
                        });
                    }

                    int done = Interlocked.Increment(ref processedCount);
                    if (paths.Count > 10 && (done % 20 == 0 || done == paths.Count))
                    {
                        double pct = (double)done / paths.Count * 100.0;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            LoadingProgress = pct;
                            LoadingStatusText = string.Format(Loc.Get("SourceFiles.LoadingFiles"), done, paths.Count);
                        });
                    }
                });
            });
        }
        finally
        {
            IsLoadingFiles = false;
            LoadingProgress = 0;
            LoadingStatusText = string.Empty;
        }

        var validItems = newItems.Where(it => it != null).Cast<SourceFileItem>().ToList();
        if (validItems.Count == 0) return;

        foreach (var item in validItems)
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
        var firstNew = validItems[0];
        SelectedSourceFile ??= firstNew;
        if (firstNew.FormatBadge.Contains("Bayer", StringComparison.OrdinalIgnoreCase))
        {
            DebayerEnabled = true;
            if (firstNew.FormatBadge.Contains("BGGR", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 1;
            else if (firstNew.FormatBadge.Contains("RGGB", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 2;
            else if (firstNew.FormatBadge.Contains("GRBG", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 3;
            else if (firstNew.FormatBadge.Contains("GBRG", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 0;
        }
        ConsoleLogText = Loc.Pick(
            $"{SourceFiles.Count} fichier(s) dans la liste. Sélection : {firstNew.FileName} ({firstNew.Resolution}, {firstNew.Frames} frames à {firstNew.FpsInfo}).",
            $"{SourceFiles.Count} file(s) in the list. Selected: {firstNew.FileName} ({firstNew.Resolution}, {firstNew.Frames} frames at {firstNew.FpsInfo}).");

        UpdateAvailableFrames();
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
        UpdateAvailableFrames();
    }


    public bool? AreAllFilesChecked
    {
        get
        {
            if (SourceFiles.Count == 0) return false;
            if (SourceFiles.All(f => f.IsChecked)) return true;
            if (SourceFiles.All(f => !f.IsChecked)) return false;
            return null;
        }
        set
        {
            bool target = value ?? true;
            foreach (var f in SourceFiles) f.IsChecked = target;
            OnPropertyChanged(nameof(AreAllFilesChecked));
        }
    }

    [RelayCommand]
    public void ToggleCheckAllFiles()
    {
        bool target = SourceFiles.Any(f => !f.IsChecked);
        foreach (var f in SourceFiles) f.IsChecked = target;
        OnPropertyChanged(nameof(AreAllFilesChecked));
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
        if (IsTutorialActive) UpdateTutorialStep();
    }

    // Accordion Sections Expanders State
    [ObservableProperty] private bool _isSection1Expanded = true;
    [ObservableProperty] private bool _isSection2Expanded;
    [ObservableProperty] private bool _isSection3Expanded;
    [ObservableProperty] private bool _isSection4Expanded;
    [ObservableProperty] private bool _isSection5Expanded;
    [ObservableProperty] private bool _isSection6Expanded;
    [ObservableProperty] private bool _isSection7Expanded;
    [ObservableProperty] private bool _isSection8Expanded;
    [ObservableProperty] private bool _isSection9Expanded;
    [ObservableProperty] private bool _isSection10Expanded;

    private bool _isTogglingAccordion;

    private void CollapseOtherSections(int sectionNumber)
    {
        if (_isTogglingAccordion) return;
        try
        {
            _isTogglingAccordion = true;
            if (sectionNumber != 1) IsSection1Expanded = false;
            if (sectionNumber != 2) IsSection2Expanded = false;
            if (sectionNumber != 3) IsSection3Expanded = false;
            if (sectionNumber != 4) IsSection4Expanded = false;
            if (sectionNumber != 5) IsSection5Expanded = false;
            if (sectionNumber != 6) IsSection6Expanded = false;
            if (sectionNumber != 7) IsSection7Expanded = false;
            if (sectionNumber != 8) IsSection8Expanded = false;
            if (sectionNumber != 9) IsSection9Expanded = false;
            if (sectionNumber != 10) IsSection10Expanded = false;
        }
        finally
        {
            _isTogglingAccordion = false;
        }
    }

    partial void OnIsSection1ExpandedChanged(bool value) { if (value) CollapseOtherSections(1); }
    partial void OnIsSection2ExpandedChanged(bool value) { if (value) CollapseOtherSections(2); }
    partial void OnIsSection3ExpandedChanged(bool value) { if (value) CollapseOtherSections(3); }
    partial void OnIsSection4ExpandedChanged(bool value) { if (value) CollapseOtherSections(4); }
    partial void OnIsSection5ExpandedChanged(bool value) { if (value) CollapseOtherSections(5); }
    partial void OnIsSection6ExpandedChanged(bool value) { if (value) CollapseOtherSections(6); }
    partial void OnIsSection7ExpandedChanged(bool value) { if (value) CollapseOtherSections(7); }
    partial void OnIsSection8ExpandedChanged(bool value) { if (value) CollapseOtherSections(8); }
    partial void OnIsSection9ExpandedChanged(bool value) { if (value) CollapseOtherSections(9); }
    partial void OnIsSection10ExpandedChanged(bool value) { if (value) CollapseOtherSections(10); }

    // --- Mode de Traitement (Workflow) ---
    [ObservableProperty] private StackingWorkflowMode _workflowMode = StackingWorkflowMode.DirectStreamAllInOne;
    [ObservableProperty] private bool _isWorkflowPreProcessingOnly;
    [ObservableProperty] private bool _isWorkflowStackingOnly;
    [ObservableProperty] private bool _isWorkflowDirectStreamAllInOne = true;

    public string Section1Title => Loc.Pick("Mode & Presets", "Mode & Presets");
    public string Section5Title => Loc.Pick("Stabilisation & Référence", "Stabilisation & Reference");

    public bool IsSection1Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public bool IsSection2Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
    public bool IsSection3Visible => WorkflowMode != StackingWorkflowMode.StackingOnly;
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
    }

    private bool IsAnyVisibleSectionExpanded()
    {
        return (IsSection1Visible && IsSection1Expanded) ||
               (IsSection2Visible && IsSection2Expanded) ||
               (IsSection3Visible && IsSection3Expanded) ||
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
        1 => Loc.Pick("1 — SNR maximal (Lune / Soleil net)", "1 — Max SNR (Sharp Moon/Sun)"),
        2 => Loc.Pick("2 — SNR très élevé (Défaut)", "2 — Very high SNR (Default)"),
        3 => Loc.Pick("3 — SNR élevé (Planétaire standard)", "3 — High SNR (Standard planetary)"),
        4 => Loc.Pick("4 — Bruit modéré (Gain moyen)", "4 — Moderate noise (Medium gain)"),
        5 => Loc.Pick("5 — Bruit moyen (Forte amplification)", "5 — Medium noise (High gain)"),
        6 => Loc.Pick("6 — Faible SNR (Cible sombre / Saturne)", "6 — Low SNR (Dark target / Saturn)"),
        7 => Loc.Pick("7 — Très faible SNR (Turbulence / Faint)", "7 — Very low SNR (Turbulence / Faint)"),
        8 => Loc.Pick("8 — Bruit extrême (Ultra faible luminosité)", "8 — Extreme noise (Ultra faint)"),
        _ => $"{NoiseRobust}"
    };

    // --- Points d'alignement (AP) ---
    [ObservableProperty] private bool _isAlignmentGridVisible = true;
    [ObservableProperty] private AlignmentGrid _currentAlignmentGrid = AlignmentGrid.Empty;
    [ObservableProperty] private int _alignmentTileSize = 104;
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
    [ObservableProperty] private int _stackPercentage1 = 0;
    [ObservableProperty] private int _stackPercentage2 = 0;
    [ObservableProperty] private int _stackPercentage3 = 0;

    public int StackPercentage
    {
        get => StackPercentage1 > 0 ? StackPercentage1 : (StackPercentage2 > 0 ? StackPercentage2 : (StackPercentage3 > 0 ? StackPercentage3 : 25));
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
    [ObservableProperty] private int _stackFrameCount = 1000;
    [ObservableProperty] private int _maxAvailableFrames = 999999;

    public void UpdateAvailableFrames()
    {
        if (SourceFiles.Count == 0)
        {
            MaxAvailableFrames = 999999;
            return;
        }

        int available;
        if (SourceFiles.Count > 1 && IsJoinMode)
        {
            available = Math.Max(1, SourceFiles.Sum(f => Math.Max(1, f.Frames)));
        }
        else if (SelectedSourceFile is not null)
        {
            available = Math.Max(1, SelectedSourceFile.Frames);
        }
        else
        {
            available = Math.Max(1, SourceFiles.Max(f => Math.Max(1, f.Frames)));
        }

        MaxAvailableFrames = Math.Max(1, available);
        StackFrameCount = MaxAvailableFrames;
    }
    [ObservableProperty] private int _selectedDrizzleIndex = 0;
    [ObservableProperty] private bool _normalizeBrightness = true;
    [ObservableProperty] private int _normalizeTargetPercent = 75;
    [ObservableProperty] private bool _correctAtmosphericDispersion = true;
    [ObservableProperty] private bool _sharpeningEnabled = true;
    [ObservableProperty] private int _sharpeningBlendPercent = 10;
    [ObservableProperty] private bool _featheringEnabled = true;

    // Interactive Tutorial Wizard
    [ObservableProperty] private bool _isTutorialActive;
    [ObservableProperty] private int _tutorialStepIndex;
    [ObservableProperty] private string _tutorialStepTitle = string.Empty;
    [ObservableProperty] private string _tutorialStepDescription = string.Empty;
    [ObservableProperty] private string _tutorialStepBadge = string.Empty;
    [ObservableProperty] private string _tutorialTargetBadge = string.Empty;
    [ObservableProperty] private bool _canTutorialGoPrevious;
    [ObservableProperty] private string _tutorialNextButtonText = "Suivant \u25B6";
    [ObservableProperty] private Avalonia.Layout.HorizontalAlignment _tutorialHorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    [ObservableProperty] private Avalonia.Layout.VerticalAlignment _tutorialVerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    [ObservableProperty] private Avalonia.Thickness _tutorialMargin = new(16, 20, 0, 0);

    [RelayCommand]
    public void StartTutorial()
    {
        TutorialStepIndex = 0;
        IsTutorialActive = true;
        UpdateTutorialStep();
    }

    [RelayCommand]
    public void NextTutorialStep()
    {
        if (TutorialStepIndex < 4)
        {
            TutorialStepIndex++;
            UpdateTutorialStep();
        }
        else
        {
            CloseTutorial();
        }
    }

    [RelayCommand]
    public void PreviousTutorialStep()
    {
        if (TutorialStepIndex > 0)
        {
            TutorialStepIndex--;
            UpdateTutorialStep();
        }
    }

    [RelayCommand]
    public void CloseTutorial()
    {
        IsTutorialActive = false;
        UserSettingsService.MarkTutorialSeen();
    }

    public void UpdateTutorialStep()
    {
        CanTutorialGoPrevious = TutorialStepIndex > 0;
        TutorialStepBadge = string.Format(Loc.Get("Tutorial.StepBadge"), TutorialStepIndex + 1, 5);
        TutorialNextButtonText = TutorialStepIndex == 4
            ? Loc.Get("Tutorial.Finish")
            : Loc.Get("Tutorial.Next");

        switch (TutorialStepIndex)
        {
            case 0:
                SelectedTabIndex = 0;
                IsSection1Expanded = true;
                TutorialHorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                TutorialVerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                TutorialMargin = new Avalonia.Thickness(16, 20, 0, 0);
                TutorialTargetBadge = Loc.Pick("\uD83D\uDC48 Volet Fichiers Sources", "\uD83D\uDC48 Source Files Pane");
                TutorialStepTitle = Loc.Get("Tutorial.Step1Title");
                TutorialStepDescription = Loc.Get("Tutorial.Step1Desc");
                break;

            case 1:
                SelectedTabIndex = 1;
                IsSection2Expanded = true;
                TutorialHorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                TutorialVerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                TutorialMargin = new Avalonia.Thickness(0, 30, 16, 0);
                TutorialTargetBadge = Loc.Pick("Options d'Entr\u00E9e \uD83D\uDC49", "Input Options \uD83D\uDC49");
                TutorialStepTitle = Loc.Get("Tutorial.Step2Title");
                TutorialStepDescription = Loc.Get("Tutorial.Step2Desc");
                break;

            case 2:
                SelectedTabIndex = 2;
                IsSection3Expanded = true;
                TutorialHorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                TutorialVerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                TutorialMargin = new Avalonia.Thickness(0, 0, 16, 40);
                TutorialTargetBadge = Loc.Pick("Stabilisation & Centrage \uD83D\uDC49", "Stabilization & Centering \uD83D\uDC49");
                TutorialStepTitle = Loc.Get("Tutorial.Step3Title");
                TutorialStepDescription = Loc.Get("Tutorial.Step3Desc");
                break;

            case 3:
                SelectedTabIndex = 3;
                IsSection4Expanded = true;
                TutorialHorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                TutorialVerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                TutorialMargin = new Avalonia.Thickness(0, 60, 16, 0);
                TutorialTargetBadge = Loc.Pick("Tri par Qualit\u00E9 \uD83D\uDC49", "Quality Sorting \uD83D\uDC49");
                TutorialStepTitle = Loc.Get("Tutorial.Step4Title");
                TutorialStepDescription = Loc.Get("Tutorial.Step4Desc");
                break;

            case 4:
                SelectedTabIndex = 6;
                IsSection10Expanded = true;
                // Execution step
                TutorialHorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                TutorialVerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
                TutorialMargin = new Avalonia.Thickness(0, 0, 16, 20);
                TutorialTargetBadge = Loc.Pick("Sortie & Ex\u00E9cution \uD83D\uDC49", "Output & Execution \uD83D\uDC49");
                TutorialStepTitle = Loc.Get("Tutorial.Step5Title");
                TutorialStepDescription = Loc.Get("Tutorial.Step5Desc");
                break;
        }
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
        UpdateAvailableFrames();
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
                    // Charge en résolution native (jusqu'à 4K) pour préserver la netteté et la cohérence 1:1 du recadrage
                    _cachedPreviewResult = UniversalMediaReader.ReadPreviewFrame(source.FullPath, targetFrameIdx, maxPreviewDim: 4096, optionsSnapshot.SerBitDepthOverride);
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

                _lastProcessedPreviewFrame = processed;
                _lastDetection = det;
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
        if (activePcts.Count == 0) activePcts = [25.0];
        Options.Stacking.StackPercentages = activePcts;
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
    public async Task RunProcessingAsync()
    {
        if (SourceFiles.Count == 0)
        {
            SelectedTabIndex = 0;
            ConsoleLogText = Loc.Pick("Veuillez d'abord ajouter au moins un fichier vidéo ou image dans l'onglet '1. Source Files'.", "Please add at least one video or image file in the '1. Source Files' tab first.");
            return;
        }

        if (IsProcessing) return;

        IsSection10Expanded = true;
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
            UpdateProgressLog(r);
        });

        try
        {
            var filesToRun = SourceFiles.Select(f => f.FullPath).ToList();
            string modeLabel = IsJoinMode ? Loc.Pick("Mode Fusion (fusion en 1 fichier)", "Join Mode (merge into 1 file)") : Loc.Pick("Mode Lot (1 fichier de sortie par source)", "Batch Mode (1 output file per source)");
            InitProcessingLog(filesToRun.Count);

            var summary = await Task.Run(() => PipelineRunner.ExecuteFilesAsync(
                filesToRun,
                Options,
                outDir,
                progress,
                _processingCts.Token));

            LastOutputFilePath = summary.OutputFilePath;
            HasCompletedOutput = summary.Success && File.Exists(summary.OutputFilePath);

            double fpsSpeed = summary.TotalInputFrames / Math.Max(0.001, summary.Elapsed.TotalSeconds);
            SummaryStatsText = Loc.Pick($"Frames lues : {summary.TotalInputFrames} | Frames écrites : {summary.OutputFramesWritten} | Rejetées : {summary.DiscardedNoPlanet + summary.DiscardedQuality} | Durée : {summary.Elapsed.TotalSeconds:F2}s ({fpsSpeed:F1} fps)", $"Frames read: {summary.TotalInputFrames} | Frames written: {summary.OutputFramesWritten} | Rejected: {summary.DiscardedNoPlanet + summary.DiscardedQuality} | Duration: {summary.Elapsed.TotalSeconds:F2}s ({fpsSpeed:F1} fps)");
            FinishProcessingLog(summary);
        }
        catch (OperationCanceledException)
        {
            ProcessingStageText = Loc.Pick("Annulé par l'utilisateur", "Cancelled by user");
            AppendStatusLog(Loc.Pick($"[{DateTime.Now:HH:mm:ss}] Traitement annulé.", $"[{DateTime.Now:HH:mm:ss}] Processing cancelled."));
        }
        catch (Exception ex)
        {
            ProcessingStageText = Loc.Pick("Erreur", "Error");
            AppendStatusLog(Loc.Pick($"[{DateTime.Now:HH:mm:ss}] Erreur : {ex.Message}", $"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}"));
        }
        finally
        {
            IsProcessing = false;
        }
    }


    private readonly List<string> _processingLogLines = [];
    private string _currentProcessingStage = string.Empty;

    private void InitProcessingLog(int fileCount)
    {
        _processingLogLines.Clear();
        _currentProcessingStage = string.Empty;
        string startTime = DateTime.Now.ToString("HH:mm:ss");
        string modeLabel = IsJoinMode
            ? Loc.Pick("Mode Fusion", "Join Mode")
            : Loc.Pick("Mode Lot", "Batch Mode");
        string line1 = Loc.Pick(
            $"Heure de lancement : {startTime} ({fileCount} fichier(s), {modeLabel})",
            $"Launch time: {startTime} ({fileCount} file(s), {modeLabel})");
        _processingLogLines.Add(line1);
        ConsoleLogText = line1;
    }

    private void UpdateProgressLog(PipelineProgressReport r)
    {
        string stage = r.Stage;
        string msg = Loc.TranslateRuntime(r.LogMessage);

        if (string.Equals(stage, _currentProcessingStage, StringComparison.Ordinal) && _processingLogLines.Count > 1)
        {
            _processingLogLines[^1] = msg;
        }
        else
        {
            _currentProcessingStage = stage;
            _processingLogLines.Add(msg);
        }

        ConsoleLogText = string.Join(Environment.NewLine, _processingLogLines);
    }

    private void FinishProcessingLog(PipelineRunSummary summary)
    {
        string finishMsg = Loc.Pick(
            $"[{DateTime.Now:HH:mm:ss}] Traitement terminé avec succès.\nDernier fichier généré : {summary.OutputFilePath}",
            $"[{DateTime.Now:HH:mm:ss}] Processing finished successfully.\nLast file generated: {summary.OutputFilePath}");
        _processingLogLines.Add(finishMsg);
        ConsoleLogText = string.Join(Environment.NewLine, _processingLogLines);
    }

    private void AppendStatusLog(string message)
    {
        _processingLogLines.Add(message);
        ConsoleLogText = string.Join(Environment.NewLine, _processingLogLines);
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
            else if (_cachedPreviewResult?.Frame is not null)
            {
                var baseFrame = _cachedPreviewResult.Frame;
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
            else if (_cachedPreviewResult?.Frame is not null)
            {
                var baseFrame = _cachedPreviewResult.Frame;
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
        if (CurrentAlignmentGrid.Points.Count == 0)
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
