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
                    ViewerTitle = Loc.Format("UI.ViewerFrameTitle", source.FileName, targetFrameIdx + 1, source.Frames);
                    FrameDimensionsText = $"{outDispW} × {outDispH} px";
                    MaxPixelStatsText = Loc.Format("Messages.MaxPixelStats", mg, mr, mgreen, mb, det.Threshold8BitUsed);
                    QualityScoreText = EnableQuality ? Loc.Format("Messages.QualityScoreFormatted", processed.QualityScore) : Loc.Get("Messages.QualityDisabled");
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

                if (!IsWorkflowPreProcessingOnly &&
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
