using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Localization;
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

public partial class MainWindowViewModel : ObservableObject
{

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddFiles))]
    private bool _hasLoadedFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddFiles))]
    [NotifyPropertyChangedFor(nameof(IsFileListBusy))]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    private bool _isLoadingFiles;

    [ObservableProperty]
    private double _loadingProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    private string _loadingStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddFiles))]
    [NotifyPropertyChangedFor(nameof(IsFileListBusy))]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    [NotifyPropertyChangedFor(nameof(GlobalStatusText))]
    private bool _isMeasuringPlanetSizes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyStatusText))]
    [NotifyPropertyChangedFor(nameof(GlobalStatusText))]
    private string _measuringStatusText = string.Empty;

    public bool IsFileListBusy => IsLoadingFiles || IsMeasuringPlanetSizes;

    public string BusyStatusText => IsMeasuringPlanetSizes ? MeasuringStatusText : LoadingStatusText;

    public string GlobalStatusText => IsMeasuringPlanetSizes
        ? MeasuringStatusText
        : IsProcessing
            ? ProcessingStageText
            : string.Empty;

    public bool CanAddFiles => !IsProcessing && !IsLoadingFiles && !IsMeasuringPlanetSizes;


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

    private readonly IAstroLocalization _loc;
    private readonly IUserSettingsService _userSettings;
    private readonly IThemeService _themeService;
    private readonly IRunPipelineUseCase _pipelineRunner;
    private readonly ICelestialTargetClassifier _celestialClassifier;
    private readonly IQualityTimelineUseCase? _qualityTimelineUseCase;

    [ObservableProperty] private bool _isUpdateBannerVisible;
    [ObservableProperty] private string _updateBannerText = string.Empty;
    [ObservableProperty] private string _updateBannerUrl = string.Empty;

    [RelayCommand]
    public void OpenUpdateUrl()
    {
        if (!string.IsNullOrWhiteSpace(UpdateBannerUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(UpdateBannerUrl) { UseShellExecute = true });
            }
            catch
            {
                // Fallback silencieux
            }
        }
        IsUpdateBannerVisible = false;
    }

    [RelayCommand]
    public void DismissUpdateBanner()
    {
        IsUpdateBannerVisible = false;
    }

    public MainWindowViewModel(
        IAstroLocalization localization,
        IUserSettingsService userSettings,
        IThemeService themeService,
        IRunPipelineUseCase pipelineRunner,
        ICelestialTargetClassifier celestialClassifier,
        TutorialViewModel tutorialVM,
        IUpdateCheckerService? updateChecker = null,
        IQualityTimelineUseCase? qualityTimelineUseCase = null)
    {
        _loc = localization ?? throw new ArgumentNullException(nameof(localization));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _pipelineRunner = pipelineRunner ?? throw new ArgumentNullException(nameof(pipelineRunner));
        _celestialClassifier = celestialClassifier ?? throw new ArgumentNullException(nameof(celestialClassifier));
        _qualityTimelineUseCase = qualityTimelineUseCase;

        _maxCpuUsagePercent = _userSettings.Current.MaxCpuUsagePercent;
        _cpuWarningThresholdPercent = _userSettings.Current.CpuWarningThresholdPercent;
        _selectedThemeIndex = _userSettings.Current.Theme?.ToLowerInvariant() switch
        {
            "light" => 1,
            "night_vision" or "nightvision" or "red" => 2,
            _ => 0
        };
        _selectedLanguageIndex = string.Equals(_userSettings.Current.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        _winJuposNaming = _userSettings.Current.WinJuposNaming;
        _selectedStackingImageFormatIndex = Math.Clamp(_userSettings.Current.SelectedStackingImageFormatIndex, 0, 2);
        _selectedOutputFormatIndex = Math.Clamp(_userSettings.Current.SelectedOutputFormatIndex, 0, 6);

        // Si nous sommes en mode empilement, aligner le format effectif sur le format d'image d'empilement (TIFF par défaut)
        if (WorkflowMode is not StackingWorkflowMode.PreProcessingOnly)
        {
            _selectedOutputFormatIndex = _selectedStackingImageFormatIndex switch
            {
                1 => 6, // FITS
                2 => 4, // PNG
                _ => 5  // TIFF
            };
        }
        if (!string.IsNullOrEmpty(_userSettings.Current.OutputSuffix))
        {
            _outputSuffix = _userSettings.Current.OutputSuffix;
        }
        _customOutputDirectory = _userSettings.Current.CustomOutputDirectory;
        _autoRotateAlign = _userSettings.Current.AutoRotateAlign;
        if (_userSettings.Current.CropWidth > 0)
        {
            _cropWidth = _userSettings.Current.CropWidth;
        }
        if (_userSettings.Current.CropHeight > 0)
        {
            _cropHeight = _userSettings.Current.CropHeight;
        }
        _enableCropping = _userSettings.Current.EnableCropping;
        _enableQuality = _userSettings.Current.EnableQualityEstimation;
        if (_userSettings.Current.QualityFramesToKeep > 0)
        {
            _qualityKeepValue = _userSettings.Current.QualityFramesToKeep;
        }
        _detectNaturalSatellites = _userSettings.Current.DetectNaturalSatellites;
        _satelliteDetectionSensitivity = Math.Clamp(
            _userSettings.Current.SatelliteDetectionSensitivity > 0 ? _userSettings.Current.SatelliteDetectionSensitivity : 1.0,
            0.40,
            2.50);
        CelestialTargetClassifier.DefaultDetectSatellites = _detectNaturalSatellites;
        CelestialTargetClassifier.DefaultSatelliteSensitivity = _satelliteDetectionSensitivity;

        _stackPercentage1 = Math.Clamp(_userSettings.Current.StackPercentage1, 0, 100);
        _stackPercentage2 = Math.Clamp(_userSettings.Current.StackPercentage2, 0, 100);
        _stackPercentage3 = Math.Clamp(_userSettings.Current.StackPercentage3, 0, 100);
        if (_userSettings.Current.AlignmentTileSize >= 16)
        {
            _alignmentTileSize = _userSettings.Current.AlignmentTileSize;
        }
        if (_userSettings.Current.MinBrightnessThreshold > 0)
        {
            _minBrightnessThreshold = _userSettings.Current.MinBrightnessThreshold;
        }
        _normalizeBrightness = _userSettings.Current.NormalizeBrightness;
        if (_userSettings.Current.NormalizeTargetPercent >= 10)
        {
            _normalizeTargetPercent = _userSettings.Current.NormalizeTargetPercent;
        }
        _correctAtmosphericDispersion = _userSettings.Current.CorrectAtmosphericDispersion;
        _sharpeningEnabled = _userSettings.Current.SharpeningEnabled;
        _sharpeningBlendPercent = Math.Clamp(_userSettings.Current.SharpeningBlendPercent, 0, 100);
        _featheringEnabled = _userSettings.Current.FeatheringEnabled;

        Loc.LanguageChanged += RefreshLocalizedTexts;
        _themeService.ThemeChanged += OnThemeChanged;
        SourceFiles.CollectionChanged += OnSourceFilesCollectionChanged;

        TutorialVM = tutorialVM ?? throw new ArgumentNullException(nameof(tutorialVM));
        TutorialVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) OnPropertyChanged(e.PropertyName);
        };
        TutorialVM.StepChanged += step =>
        {
            switch (step)
            {
                case 0:
                    SelectedTabIndex = 0;
                    IsSection1Expanded = true;
                    break;
                case 1:
                    SelectedTabIndex = 1;
                    IsSection2Expanded = true;
                    break;
                case 2:
                    SelectedTabIndex = 2;
                    IsSection3Expanded = true;
                    break;
                case 3:
                    SelectedTabIndex = 3;
                    IsSection4Expanded = true;
                    break;
                case 4:
                    SelectedTabIndex = 6;
                    IsSection10Expanded = true;
                    break;
            }
        };

        UpdateCpuStatus();

        if (_userSettings.Current.CheckUpdatesOnStartup && updateChecker != null)
        {
            _ = CheckForUpdatesSilentlyAsync(updateChecker);
        }
    }

    private async Task CheckForUpdatesSilentlyAsync(IUpdateCheckerService updateChecker)
    {
        try
        {
            await Task.Delay(2000).ConfigureAwait(false);
            var info = await updateChecker.CheckForUpdateAsync().ConfigureAwait(false);
            if (info.IsUpdateAvailable)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    UpdateBannerText = _loc.Format("Messages.UpdateAvailableBanner", info.LatestVersion);
                    UpdateBannerUrl = info.HtmlUrl;
                    IsUpdateBannerVisible = true;
                });
            }
        }
        catch
        {
            // Échec silencieux
        }
    }

    partial void OnMaxCpuUsagePercentChanged(int value)
    {
        UpdateCpuStatus();
        _userSettings.UpdateCpuSettings(value, CpuWarningThresholdPercent);
    }

    partial void OnCpuWarningThresholdPercentChanged(int value)
    {
        UpdateCpuStatus();
        _userSettings.UpdateCpuSettings(MaxCpuUsagePercent, value);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            1 => AppTheme.CleanLabLight,
            2 => AppTheme.NightVision,
            _ => AppTheme.DeepSpaceDark
        };
        _themeService.ApplyTheme(theme, save: true);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var lang = value == 1 ? AppLanguage.English : AppLanguage.French;
        Loc.SetLanguage(lang, save: true);
    }

    private void OnThemeChanged()
    {
        int targetIdx = _themeService.CurrentTheme switch
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

        ActiveCpuThreadsBadge = Loc.Format("UI.ActiveCpuThreadsBadge", used, total, MaxCpuUsagePercent);

        ActiveCpuThreadsText = used == total
            ? Loc.Format("Messages.ThreadsFullPower", used, total)
            : Loc.Format("Messages.ThreadsReserved", used, total - used);

        CpuWarningTitle = Loc.Get("Settings.CpuWarningTitle");

        CpuWarningMessage = Loc.Format("Messages.CpuWarningOverThreshold", MaxCpuUsagePercent, used, CpuWarningThresholdPercent);
    }

    /// <summary>Retraduit les textes fixes déjà affichés (messages par défaut, état) après un changement de langue.</summary>
    public void RefreshLocalizedTexts()
    {
        ViewerTitle = Loc.Retranslate(ViewerTitle);
        ViewerStatusText = Loc.Retranslate(ViewerStatusText);
        QualityScoreText = Loc.Retranslate(QualityScoreText);
        ProcessingStageText = Loc.Retranslate(ProcessingStageText);
        SummaryStatsText = Loc.Retranslate(SummaryStatsText);
        DarkCountText = Loc.Format("UI.DarkCount", DarkFiles.Count);
        FlatCountText = Loc.Format("UI.FlatCount", FlatFiles.Count);
        FlatDarkCountText = Loc.Format("UI.FlatDarkCount", FlatDarkFiles.Count);
        if (SelectedSourceFile is null)
        {
            ViewerTitle = Loc.Get("UI.NoFileSelected");
        }
        if (_processingLogLines.Count > 0)
        {
            for (int i = 0; i < _processingLogLines.Count; i++)
            {
                _processingLogLines[i] = Loc.Retranslate(_processingLogLines[i]);
            }
            ConsoleLogText = string.Join(Environment.NewLine, _processingLogLines);
        }
        else
        {
            ConsoleLogText = Loc.Retranslate(ConsoleLogText);
        }
        UpdateCpuStatus();
        NotifyStartProcessingValidation();
        foreach (var f in SourceFiles) f.RefreshLocalizedText();
        if (SelectedSourceFile is not null) RequestPreviewUpdate(reloadFromDisk: false);
        if (IsTutorialActive) UpdateTutorialStep();
    }

    // Accordion Sections Expanders State
    [ObservableProperty] private bool _isSection1Expanded = true;
    [ObservableProperty] private bool _isSection2Expanded;
    [ObservableProperty] private bool _isSection3Expanded;
    [ObservableProperty] private bool _isSectionDerotationExpanded;
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
            if (sectionNumber != 11) IsSectionDerotationExpanded = false;
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
    partial void OnIsSectionDerotationExpandedChanged(bool value) { if (value) CollapseOtherSections(11); }
    partial void OnIsSection4ExpandedChanged(bool value) { if (value) CollapseOtherSections(4); }
    partial void OnIsSection5ExpandedChanged(bool value) { if (value) CollapseOtherSections(5); }
    partial void OnIsSection6ExpandedChanged(bool value) { if (value) CollapseOtherSections(6); }
    partial void OnIsSection7ExpandedChanged(bool value) { if (value) CollapseOtherSections(7); }
    partial void OnIsSection8ExpandedChanged(bool value) { if (value) CollapseOtherSections(8); }
    partial void OnIsSection9ExpandedChanged(bool value) { if (value) CollapseOtherSections(9); }
    partial void OnIsSection10ExpandedChanged(bool value) { if (value) CollapseOtherSections(10); }
}
