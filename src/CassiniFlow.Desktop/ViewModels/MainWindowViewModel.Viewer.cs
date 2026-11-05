using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
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

    // Viewer state
    [ObservableProperty] private int _currentFrameIndex = 1;
    [ObservableProperty] private int _maxFrameCount = 1;
    [ObservableProperty] private string _viewerTitle = Loc.Get("UI.NoFileSelected");
    [ObservableProperty] private string _viewerStatusText = Loc.Get("Viewer.LoadVideoPrompt");
    [ObservableProperty] private string _frameDimensionsText = "—";
    [ObservableProperty] private string _maxPixelStatsText = Loc.Get("UI.MaxPixelPlaceholder");
    [ObservableProperty] private string _qualityScoreText = Loc.Get("UI.QualityScorePlaceholder");
    [ObservableProperty] private bool _previewCroppedOutput;
    [ObservableProperty] private bool _showOverlayBoxes = true;
    [ObservableProperty] private bool _showDetectedPixelsRed;

    // Timeline state (Ticket #61)
    [ObservableProperty] private QualityTimelineDataSet? _qualityTimelineData;
    [ObservableProperty] private bool _isTimelineAnalyzing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineProgressPercentText))]
    private double _timelineAnalysisProgress;
    public string TimelineProgressPercentText => $"{Math.Round(TimelineAnalysisProgress)} %";
    [ObservableProperty] private bool _isTimelineChronological = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineExpandIcon))]
    private bool _isTimelineExpanded;
    public string TimelineExpandIcon => IsTimelineExpanded ? "▼" : "▲";
    [ObservableProperty] private double _timelineHeight = 110;
    [ObservableProperty] private bool _timelineShowGradient = true;
    [ObservableProperty] private bool _timelineShowNinox = true;
    [ObservableProperty] private bool _timelineShowPeakHisto;
    [ObservableProperty] private bool _timelineShowTotalLum;
    [ObservableProperty] private int _timelineSelectionIn = 1;
    [ObservableProperty] private int _timelineSelectionOut = 1;
    [ObservableProperty] private int _timelineCutoffPercent = 25;
    [ObservableProperty] private string _currentFrameRankText = "—";
    [ObservableProperty] private string _currentSeeingText = "—";
    [ObservableProperty] private string _retainedFramesSummaryText = "—";
    [ObservableProperty] private int _hoverFrameIndex = -1;
    [ObservableProperty] private double _hoverScore;
    [ObservableProperty] private int _hoverRank = -1;
    [ObservableProperty] private bool _isTimelineHovered;

    private CancellationTokenSource? _timelineCts;

    partial void OnStartFrameChanged(int value)
    {
        if (TimelineSelectionIn != value)
        {
            TimelineSelectionIn = value;
        }
        if (SelectedSourceFile is { } file && file.SelectionIn != value)
        {
            file.SelectionIn = value;
        }
    }

    partial void OnEndFrameChanged(int value)
    {
        if (TimelineSelectionOut != value)
        {
            TimelineSelectionOut = value;
        }
        if (SelectedSourceFile is { } file && file.SelectionOut != value)
        {
            file.SelectionOut = value;
        }
    }

    partial void OnTimelineSelectionInChanged(int value)
    {
        if (StartFrame != value) StartFrame = value;
        if (SelectedSourceFile is { } file && file.SelectionIn != value)
        {
            file.SelectionIn = value;
        }
        UpdateRetainedFramesSummary();
    }

    partial void OnTimelineSelectionOutChanged(int value)
    {
        if (EndFrame != value) EndFrame = value;
        if (SelectedSourceFile is { } file && file.SelectionOut != value)
        {
            file.SelectionOut = value;
        }
        UpdateRetainedFramesSummary();
    }

    [RelayCommand]
    public void SetSelectionInToCurrentFrame()
    {
        int cur = CurrentFrameIndex;
        if (cur >= TimelineSelectionOut)
        {
            TimelineSelectionOut = Math.Min(MaxFrameCount, cur + 1);
        }
        TimelineSelectionIn = cur;
    }

    [RelayCommand]
    public void SetSelectionOutToCurrentFrame()
    {
        int cur = CurrentFrameIndex;
        if (cur <= TimelineSelectionIn)
        {
            TimelineSelectionIn = Math.Max(1, cur - 1);
        }
        TimelineSelectionOut = cur;
    }

    [RelayCommand]
    public void ResetSelection()
    {
        TimelineSelectionIn = 1;
        TimelineSelectionOut = MaxFrameCount;
    }

    partial void OnTimelineCutoffPercentChanged(int value)
    {
        QualityKeepValue = value;
        SelectedQualityLimitModeIndex = 1; // Percentage
        UpdateRetainedFramesSummary();
    }

    partial void OnTimelineShowGradientChanged(bool value)
    {
        if (value) RequestAdditionalAlgorithmIfNeeded(QualityAlgorithm.GradientSharpness);
    }

    partial void OnTimelineShowNinoxChanged(bool value)
    {
        if (value) RequestAdditionalAlgorithmIfNeeded(QualityAlgorithm.OriginalNinox);
    }

    partial void OnTimelineShowPeakHistoChanged(bool value)
    {
        if (value) RequestAdditionalAlgorithmIfNeeded(QualityAlgorithm.PeakHistogram);
    }

    partial void OnTimelineShowTotalLumChanged(bool value)
    {
        if (value) RequestAdditionalAlgorithmIfNeeded(QualityAlgorithm.TotalBrightness);
    }

    [RelayCommand]
    public void ToggleTimelineMode()
    {
        IsTimelineChronological = !IsTimelineChronological;
    }

    [RelayCommand]
    public void ToggleTimelineExpanded()
    {
        IsTimelineExpanded = !IsTimelineExpanded;
        TimelineHeight = IsTimelineExpanded ? 180 : 110;
    }

    [RelayCommand]
    public void GoToBestFrame()
    {
        if (QualityTimelineData is { } data && data.BestFrameIndex >= 0)
        {
            CurrentFrameIndex = data.BestFrameIndex + 1;
        }
    }

    public void TriggerQualityTimelineAnalysis(string filePath)
    {
        _timelineCts?.Cancel();
        _timelineCts?.Dispose();
        _timelineCts = new CancellationTokenSource();
        var token = _timelineCts.Token;

        if (_qualityTimelineUseCase == null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            QualityTimelineData = null;
            IsTimelineAnalyzing = false;
            return;
        }

        IsTimelineAnalyzing = true;
        TimelineAnalysisProgress = 0.0;

        var progress = new Progress<double>(p =>
        {
            TimelineAnalysisProgress = p;
        });

        SyncOptionsFromUi();
        var optionsSnapshot = Options.Clone();

        Task.Run(async () =>
        {
            try
            {
                var dataset = await _qualityTimelineUseCase.AnalyzeTimelineAsync(
                    filePath,
                    optionsSnapshot.QualityAlgorithm,
                    optionsSnapshot,
                    progress,
                    token).ConfigureAwait(false);

                if (!token.IsCancellationRequested)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        QualityTimelineData = dataset;
                        IsTimelineAnalyzing = false;
                        TimelineAnalysisProgress = 100.0;

                        // Conserver le découpage temporel propre à chaque fichier
                        if (SelectedSourceFile is { } curFile && string.Equals(curFile.FullPath, filePath, StringComparison.OrdinalIgnoreCase))
                        {
                            int selIn = curFile.SelectionIn > 0 ? curFile.SelectionIn : 1;
                            int selOut = curFile.SelectionOut > 0 ? curFile.SelectionOut : dataset.TotalFrames;
                            TimelineSelectionIn = Math.Clamp(selIn, 1, dataset.TotalFrames);
                            TimelineSelectionOut = Math.Clamp(selOut, TimelineSelectionIn, dataset.TotalFrames);
                        }

                        UpdateTimelineFrameHud(CurrentFrameIndex);
                        UpdateRetainedFramesSummary();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Cleanly canceled
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsTimelineAnalyzing = false;
                    ViewerStatusText = Loc.Format("Messages.PreviewError", ex.Message);
                });
            }
        }, token);
    }

    private void RequestAdditionalAlgorithmIfNeeded(QualityAlgorithm algo)
    {
        var data = QualityTimelineData;
        if (data == null || data.HasScoresFor(algo) || _qualityTimelineUseCase == null) return;

        SyncOptionsFromUi();
        var optionsSnapshot = Options.Clone();

        Task.Run(async () =>
        {
            try
            {
                await _qualityTimelineUseCase.ComputeAdditionalAlgorithmAsync(
                    data,
                    algo,
                    optionsSnapshot,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Trigger UI re-render
                    QualityTimelineData = null;
                    QualityTimelineData = data;
                });
            }
            catch
            {
                // Silently ignore background comparison failure
            }
        });
    }

    private void UpdateTimelineFrameHud(int frame1Based)
    {
        int total = SelectedSourceFile?.Frames ?? MaxFrameCount;
        if (QualityTimelineData != null)
        {
            var rec = QualityTimelineData.GetFrameRecord(QualityTimelineData.PrimaryAlgorithm, frame1Based - 1);
            if (rec.HasValue)
            {
                double pctRank = (rec.Value.Rank * 100.0) / Math.Max(1, total);
                CurrentFrameRankText = Loc.Format("Viewer.RankFormatted", rec.Value.Rank, total, pctRank.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                CurrentSeeingText = rec.Value.NormalizedScore switch
                {
                    >= 65 => Loc.Get("Viewer.SeeingCalm"),
                    <= 40 => Loc.Get("Viewer.SeeingTurbulent"),
                    _ => Loc.Get("Viewer.SeeingModerate")
                };
                return;
            }
        }
        CurrentFrameRankText = "—";
        CurrentSeeingText = "—";
    }

    private void UpdateRetainedFramesSummary()
    {
        int rangeCount = Math.Max(1, TimelineSelectionOut - TimelineSelectionIn + 1);
        int kept = Math.Clamp((int)Math.Round(rangeCount * (TimelineCutoffPercent / 100.0)), 1, rangeCount);
        RetainedFramesSummaryText = Loc.Format("Viewer.RetainedFrames", kept, TimelineCutoffPercent);
    }

        public event Action<AstroFrame?, DetectionResult?, double>? PreviewReady;

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
    partial void OnEnableCroppingChanged(bool value)
    {
        RequestPreviewUpdate(reloadFromDisk: false);
        RecalculateAlignmentGridIfActive();
    }
    partial void OnCropWidthChanged(int value)
    {
        RequestPreviewUpdate(reloadFromDisk: false);
        RecalculateAlignmentGridIfActive();
    }
    partial void OnCropHeightChanged(int value)
    {
        RequestPreviewUpdate(reloadFromDisk: false);
        RecalculateAlignmentGridIfActive();
    }
    partial void OnCropOffsetXChanged(int value)
    {
        RequestPreviewUpdate(reloadFromDisk: false);
        RecalculateAlignmentGridIfActive();
    }
    partial void OnCropOffsetYChanged(int value)
    {
        RequestPreviewUpdate(reloadFromDisk: false);
        RecalculateAlignmentGridIfActive();
    }

    private void RecalculateAlignmentGridIfActive()
    {
        if (CurrentAlignmentGrid is not null && CurrentAlignmentGrid.Points.Count > 0)
        {
            CalculateAlignmentGrid();
        }
    }
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
        partial void OnAutoRotateAlignChanged(bool value)
    {
        _userSettings.Current.AutoRotateAlign = value;
        _userSettings.Save();
        RequestPreviewUpdate(reloadFromDisk: false);
    }

    partial void OnDarkSubtractEnabledChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnHotPixelThresholdChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnMinObjectSizePixelsChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnEnableQualityChanged(bool value)
    {
        _userSettings.Current.EnableQualityEstimation = value;
        _userSettings.Save();
        NotifyStartProcessingValidation();
        RequestPreviewUpdate(reloadFromDisk: false);
    }
    partial void OnLimitQualityFramesChanged(bool value) => NotifyStartProcessingValidation();
    partial void OnQualityKeepValueChanged(int value)
    {
        if (value > 0)
        {
            _userSettings.Current.QualityFramesToKeep = value;
            _userSettings.Save();
        }
        NotifyStartProcessingValidation();
    }
    partial void OnSelectedQualityAlgoIndexChanged(int value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnObserverLatitudeDegreesChanged(double value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnTargetAltitudeDegreesChanged(double value) => RequestPreviewUpdate(reloadFromDisk: false);
    partial void OnTargetAzimuthDegreesChanged(double value) => RequestPreviewUpdate(reloadFromDisk: false);

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


    [RelayCommand]
    public void FirstFrame()
    {
        StopPlayback();
        CurrentFrameIndex = 1;
    }

    [RelayCommand]
    public void PreviousFrame()
    {
        StopPlayback();
        CurrentFrameIndex = Math.Max(1, CurrentFrameIndex - 1);
    }

    [RelayCommand]
    public void NextFrame()
    {
        StopPlayback();
        CurrentFrameIndex = Math.Min(MaxFrameCount, CurrentFrameIndex + 1);
    }

    [RelayCommand]
    public void LastFrame()
    {
        StopPlayback();
        CurrentFrameIndex = MaxFrameCount;
    }

    [ObservableProperty] private bool _isPlaying;
    public string PlayPauseIcon => IsPlaying ? "⏸" : "▶";

    private Avalonia.Threading.DispatcherTimer? _playbackTimer;

    [RelayCommand]
    public void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    public void StartPlayback()
    {
        if (MaxFrameCount <= 1) return;
        IsPlaying = true;
        OnPropertyChanged(nameof(PlayPauseIcon));

        _playbackTimer?.Stop();
        int fps = Math.Clamp(OutputFps > 0 ? OutputFps : 30, 5, 60);
        _playbackTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / fps)
        };
        _playbackTimer.Tick += (s, e) =>
        {
            int next = CurrentFrameIndex + 1;
            int limitOut = Math.Clamp(TimelineSelectionOut, 1, MaxFrameCount);
            int limitIn = Math.Clamp(TimelineSelectionIn, 1, limitOut);

            if (next > limitOut)
            {
                next = limitIn;
            }
            CurrentFrameIndex = next;
        };
        _playbackTimer.Start();
    }

    public void StopPlayback()
    {
        if (!IsPlaying && _playbackTimer == null) return;
        IsPlaying = false;
        OnPropertyChanged(nameof(PlayPauseIcon));
        _playbackTimer?.Stop();
        _playbackTimer = null;
    }





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
                if (IsPlaying && optionsSnapshot.EnableQualityEstimation)
                {
                    optionsSnapshot = optionsSnapshot.Clone();
                    optionsSnapshot.EnableQualityEstimation = false;
                }

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

                double displayScore = processed.QualityScore;
                if (QualityTimelineData is { } tlData && targetFrameIdx < tlData.TotalFrames)
                {
                    var records = tlData.GetScores(tlData.PrimaryAlgorithm);
                    if (records != null && targetFrameIdx < records.Length)
                    {
                        displayScore = records[targetFrameIdx].RawScore;
                    }
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ViewerTitle = Loc.Format("UI.ViewerFrameTitle", source.FileName, targetFrameIdx + 1, source.Frames);
                    FrameDimensionsText = $"{outDispW} × {outDispH} px";
                    MaxPixelStatsText = Loc.Format("Messages.MaxPixelStats", mg, mr, mgreen, mb, det.Threshold8BitUsed);
                    QualityScoreText = EnableQuality ? Loc.Format("Messages.QualityScoreFormatted", displayScore) : Loc.Get("Messages.QualityDisabled");
                    UpdateTimelineFrameHud(targetFrameIdx + 1);
                    ViewerStatusText = SelectedStabilisationModeIndex switch
                    {
                        1 => det.Accepted
                            ? Loc.Format("Messages.ObjectDetectedCrop", fullCentroidX, fullCentroidY, CropWidth, CropHeight)
                            : Loc.Format("Messages.NoIsolatedObject", det.Threshold8BitUsed),
                        2 => Loc.Format("Messages.SurfaceAnchor", AfbSize, TargetBoxX, TargetBoxY),
                        _ => EnableCropping ? Loc.Format("Messages.FixedCrop", CropWidth, CropHeight) : Loc.Get("Messages.NoStabilisation")
                    };
                });

                _lastProcessedPreviewFrame = processed;
                _lastDetection = det;
                PreviewReady?.Invoke(processed, det, applyCrop ? 1.0 : scale);

                if (!IsWorkflowPreProcessingOnly && !IsPlaying &&
                    (CurrentAlignmentGrid is null || CurrentAlignmentGrid.Points.Count == 0 ||
                     (EnableCropping && (CurrentAlignmentGrid.FrameWidth != CropWidth || CurrentAlignmentGrid.FrameHeight != CropHeight))))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(CalculateAlignmentGrid);
                }
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ViewerStatusText = Loc.Format("Messages.PreviewError", ex.Message);
                });
            }
        }
    }
}
