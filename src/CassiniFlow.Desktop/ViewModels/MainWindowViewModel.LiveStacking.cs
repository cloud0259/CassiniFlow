using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private ILiveStackingUseCase? _liveStackingUseCase;

    public ObservableCollection<CameraDeviceInfo> AvailableCameras { get; } = [];

    [ObservableProperty] private CameraDeviceInfo? _selectedCamera;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectCameraButtonText))]
    [NotifyPropertyChangedFor(nameof(CanStartLiveStacking))]
    private bool _isLiveCameraConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLiveStackingButtonText))]
    private bool _isLiveStackingActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveRecordSerButtonText))]
    private bool _isLiveRecordingSer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveRecordSerButtonText))]
    private int _liveRecordedSerFrames;

    // 1. Contrôles d'Acquisition Caméra
    [ObservableProperty] private double _liveExposureMs = 10.0;
    [ObservableProperty] private int _liveGain = 250;
    [ObservableProperty] private int _liveOffset = 10;
    [ObservableProperty] private int _liveSelectedRoiIndex = 0; // 0: 480x360, 1: 640x480, 2: 800x600, 3: 1024x768, 4: Max
    [ObservableProperty] private int _liveSelectedBitDepthIndex = 0; // 0: 8-bit, 1: 16-bit
    [ObservableProperty] private int _liveWhiteBalanceR = 52;
    [ObservableProperty] private int _liveWhiteBalanceB = 95;
    [ObservableProperty] private int _liveUsbBandwidth = 80;
    [ObservableProperty] private bool _liveHighSpeedMode = true;

    // 2. Paramètres du Buffer Glissant Top-K & Alignement
    [ObservableProperty] private int _liveRollingBufferSize = 200;
    [ObservableProperty] private bool _liveQualityWeighting = true;
    [ObservableProperty] private bool _liveNormalizeBackground = true;
    [ObservableProperty] private int _liveSelectedAlignmentMethodIndex = 0; // 0: AutoHybrid, 1: PlanetCog, 2: Surface
    [ObservableProperty] private int _liveMaxDriftPixels = 120;
    [ObservableProperty] private bool _liveRejectClipped = true;

    // 3. Filtrage Qualité & Rejet Turbulence / Nuages
    [ObservableProperty] private bool _liveEnableQualityGate = true;
    [ObservableProperty] private int _liveKeepBestPercent = 50;
    [ObservableProperty] private bool _liveEnableCloudFilter = true;
    [ObservableProperty] private int _liveMaxBrightnessDropPercent = 35;

    // 4. Accentuation en Direct (Toggleable) & ADC Numérique
    [ObservableProperty] private bool _liveEnableSharpening = true;
    [ObservableProperty] private int _liveSharpeningBlendPercent = 30;
    [ObservableProperty] private double _liveUnsharpRadius = 1.5;
    [ObservableProperty] private double _liveUnsharpAmount = 1.2;
    [ObservableProperty] private bool _liveEnableDigitalAdc = true;
    [ObservableProperty] private bool _liveAutoDigitalAdc = true;
    [ObservableProperty] private double _liveDisplayGamma = 1.0;

    // 5. Enregistrement & Instantanés (Snapshots & SER)
    [ObservableProperty] private string _liveFilePrefix = "Jupiter_Live";
    [ObservableProperty] private int _liveSelectedSnapshotFormatIndex = 0; // 0: FITS 16-bit, 1: TIFF 16-bit

    // 6. Télémétrie HUD
    [ObservableProperty] private string _liveHudCaptureFpsText = "Capture : 0.0 fps";
    [ObservableProperty] private string _liveHudStackFpsText = "Stack : 0.0 fps";
    [ObservableProperty] private string _liveHudBufferText = "Buffer Top-K : 0 / 200";
    [ObservableProperty] private string _liveHudAcceptedRejectedText = "Retenues : 0 | Rejetées : 0";
    [ObservableProperty] private string _liveHudSeeingText = "Seeing : —";
    [ObservableProperty] private string _liveHudSensorTempText = "Capteur : —";

    // Sections d'accordéon Live Stacking
    [ObservableProperty] private bool _isLiveSection1Expanded = true;
    [ObservableProperty] private bool _isLiveSection2Expanded = false;
    [ObservableProperty] private bool _isLiveSection3Expanded = true;
    [ObservableProperty] private bool _isLiveSection4Expanded = false;

    public bool IsWorkflowLiveStacking => WorkflowMode == StackingWorkflowMode.LiveStacking;
    public bool IsFileWorkflowMode => WorkflowMode != StackingWorkflowMode.LiveStacking;
    public bool CanStartLiveStacking => IsLiveCameraConnected;

    public string ConnectCameraButtonText => IsLiveCameraConnected
        ? Loc.Get("LiveStacking.Disconnect")
        : Loc.Get("LiveStacking.Connect");

    public string StartStopLiveStackingButtonText => IsLiveStackingActive
        ? Loc.Get("LiveStacking.StopLive")
        : Loc.Get("LiveStacking.StartLive");

    public string LiveRecordSerButtonText => IsLiveRecordingSer
        ? Loc.Format("LiveStacking.StopSerRecord", LiveRecordedSerFrames)
        : Loc.Get("LiveStacking.StartSerRecord");

    internal void InitializeLiveStacking(ILiveStackingUseCase? liveStackingUseCase)
    {
        _liveStackingUseCase = liveStackingUseCase;
        if (_liveStackingUseCase is not null)
        {
            _liveStackingUseCase.FrameStacked += OnLiveFrameStacked;
            RefreshLiveCameras();
        }
    }

    [RelayCommand]
    public void RefreshLiveCameras()
    {
        if (_liveStackingUseCase is null) return;

        var discovered = _liveStackingUseCase.DiscoverCameras();
        string? prevId = SelectedCamera?.Id;

        AvailableCameras.Clear();
        foreach (var cam in discovered)
        {
            AvailableCameras.Add(cam);
        }

        SelectedCamera = AvailableCameras.FirstOrDefault(c => c.Id == prevId)
                      ?? AvailableCameras.FirstOrDefault();
    }

    [RelayCommand]
    public void ToggleConnectCamera()
    {
        if (_liveStackingUseCase is null) return;

        if (IsLiveCameraConnected)
        {
            _liveStackingUseCase.DisconnectCamera();
            IsLiveStackingActive = false;
            IsLiveRecordingSer = false;
            IsLiveCameraConnected = false;
            ViewerStatusText = Loc.Get("LiveStacking.StatusDisconnected");
            return;
        }

        if (SelectedCamera is null)
        {
            RefreshLiveCameras();
        }

        if (SelectedCamera is { } cam && _liveStackingUseCase.ConnectCamera(cam.Id))
        {
            IsLiveCameraConnected = true;
            ViewerTitle = cam.Name;
            ViewerStatusText = Loc.Format("LiveStacking.StatusConnected", cam.Name);
        }
    }

    [RelayCommand]
    public void ToggleStartLiveStacking()
    {
        if (_liveStackingUseCase is null) return;

        if (IsLiveStackingActive)
        {
            _liveStackingUseCase.StopSession();
            IsLiveStackingActive = false;
            IsLiveRecordingSer = false;
            return;
        }

        if (!IsLiveCameraConnected)
        {
            ToggleConnectCamera();
            if (!IsLiveCameraConnected) return;
        }

        var streamCfg = BuildCameraStreamConfig();
        var stackOpts = BuildLiveStackingOptions();

        _liveStackingUseCase.StartSession(streamCfg, stackOpts);
        IsLiveStackingActive = true;
    }

    [RelayCommand]
    public void ResetLiveStack()
    {
        _liveStackingUseCase?.ResetStack();
    }

    [RelayCommand]
    public void TakeLiveSnapshot()
    {
        if (_liveStackingUseCase is null) return;

        string outDir = GetEffectiveLiveOutputDirectory();
        bool preferFits = LiveSelectedSnapshotFormatIndex == 0;
        var res = _liveStackingUseCase.TakeSnapshot(outDir, LiveFilePrefix, preferFits);

        if (res.Success)
        {
            string fileName = Path.GetFileName(res.RawFitsOrTiffPath);
            ViewerStatusText = Loc.Format("LiveStacking.SnapshotSaved", fileName);
            ConsoleLogText += $"{Environment.NewLine}{ViewerStatusText}";
        }
        else if (!string.IsNullOrEmpty(res.ErrorMessage))
        {
            ViewerStatusText = res.ErrorMessage;
        }
    }

    [RelayCommand]
    public void ToggleLiveSerRecording()
    {
        if (_liveStackingUseCase is null || !IsLiveStackingActive) return;

        if (IsLiveRecordingSer)
        {
            string? path = _liveStackingUseCase.StopBackgroundSerRecording();
            IsLiveRecordingSer = false;
            if (!string.IsNullOrEmpty(path))
            {
                ViewerStatusText = $"✔ SER : {Path.GetFileName(path)} ({LiveRecordedSerFrames} frames)";
            }
        }
        else
        {
            string outDir = GetEffectiveLiveOutputDirectory();
            string path = _liveStackingUseCase.StartBackgroundSerRecording(outDir, LiveFilePrefix);
            LiveRecordedSerFrames = 0;
            IsLiveRecordingSer = true;
            ViewerStatusText = $"● REC SER : {Path.GetFileName(path)}";
        }
    }

    private string GetEffectiveLiveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CustomOutputDirectory))
        {
            return CustomOutputDirectory;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CassiniFlow_Live");
    }

    private CameraStreamConfig BuildCameraStreamConfig()
    {
        (int w, int h) = LiveSelectedRoiIndex switch
        {
            0 => (480, 360),
            1 => (640, 480),
            2 => (800, 600),
            3 => (1024, 768),
            _ => (1280, 960)
        };

        return new CameraStreamConfig
        {
            Width = w,
            Height = h,
            BitDepth = LiveSelectedBitDepthIndex == 1 ? 16 : 8,
            ExposureMs = Math.Clamp(LiveExposureMs, 0.2, 2000.0),
            Gain = Math.Clamp(LiveGain, 0, 600),
            Offset = Math.Clamp(LiveOffset, 0, 255),
            WhiteBalanceR = Math.Clamp(LiveWhiteBalanceR, 1, 100),
            WhiteBalanceB = Math.Clamp(LiveWhiteBalanceB, 1, 100),
            UsbBandwidth = Math.Clamp(LiveUsbBandwidth, 40, 100),
            HighSpeedMode = LiveHighSpeedMode,
            DebayerInLive = true
        };
    }

    public LiveStackingOptions BuildLiveStackingOptions()
    {
        return new LiveStackingOptions
        {
            RollingBufferSize = Math.Clamp(LiveRollingBufferSize, 5, 1000),
            EnableQualityWeighting = LiveQualityWeighting,
            NormalizeBackground = LiveNormalizeBackground,
            AlignmentMethod = LiveSelectedAlignmentMethodIndex switch
            {
                1 => LiveAlignmentMethod.PlanetCog,
                2 => LiveAlignmentMethod.SurfacePhaseCorrelation,
                _ => LiveAlignmentMethod.AutoHybrid
            },
            MaxDriftPixels = Math.Clamp(LiveMaxDriftPixels, 10, 500),
            RejectEdgeClippedFrames = LiveRejectClipped,
            EnableQualityGate = LiveEnableQualityGate,
            KeepBestPercent = Math.Clamp(LiveKeepBestPercent, 5, 100),
            EnableCloudBrightnessFilter = LiveEnableCloudFilter,
            MaxBrightnessDropPercent = Math.Clamp(LiveMaxBrightnessDropPercent, 5, 95),
            EnableLiveSharpening = LiveEnableSharpening,
            SharpeningBlendPercent = Math.Clamp(LiveSharpeningBlendPercent, 0, 100),
            UnsharpRadius = Math.Clamp(LiveUnsharpRadius, 0.5, 4.0),
            UnsharpAmount = Math.Clamp(LiveUnsharpAmount, 0.0, 4.0),
            EnableDigitalAdc = LiveEnableDigitalAdc,
            AutoDigitalAdc = LiveAutoDigitalAdc,
            DisplayGamma = Math.Clamp(LiveDisplayGamma, 0.2, 3.0)
        };
    }

    private void PushLiveStackingOptionsToEngine()
    {
        _liveStackingUseCase?.UpdateStackingOptions(BuildLiveStackingOptions());
    }

    private void PushLiveCameraControlsToDriver()
    {
        _liveStackingUseCase?.UpdateCameraControls(
            LiveExposureMs,
            LiveGain,
            LiveOffset,
            LiveWhiteBalanceR,
            LiveWhiteBalanceB);
    }

    partial void OnLiveExposureMsChanged(double value) => PushLiveCameraControlsToDriver();
    partial void OnLiveGainChanged(int value) => PushLiveCameraControlsToDriver();
    partial void OnLiveOffsetChanged(int value) => PushLiveCameraControlsToDriver();
    partial void OnLiveWhiteBalanceRChanged(int value) => PushLiveCameraControlsToDriver();
    partial void OnLiveWhiteBalanceBChanged(int value) => PushLiveCameraControlsToDriver();

    partial void OnLiveRollingBufferSizeChanged(int value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveQualityWeightingChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveNormalizeBackgroundChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveSelectedAlignmentMethodIndexChanged(int value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveMaxDriftPixelsChanged(int value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveRejectClippedChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveEnableQualityGateChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveKeepBestPercentChanged(int value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveEnableCloudFilterChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveMaxBrightnessDropPercentChanged(int value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveEnableSharpeningChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveSharpeningBlendPercentChanged(int value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveUnsharpRadiusChanged(double value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveUnsharpAmountChanged(double value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveEnableDigitalAdcChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveAutoDigitalAdcChanged(bool value) => PushLiveStackingOptionsToEngine();
    partial void OnLiveDisplayGammaChanged(double value) => PushLiveStackingOptionsToEngine();

    private void OnLiveFrameStacked(AstroFrame displayFrame, LiveStackingTelemetry telemetry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsWorkflowLiveStacking) return;

            FrameDimensionsText = $"{displayFrame.Width}×{displayFrame.Height} ({(displayFrame.Channels == 3 ? "RGB" : "Mono")})";
            LiveHudCaptureFpsText = Loc.Format("LiveStacking.HudCaptureFps", telemetry.CaptureFps);
            LiveHudStackFpsText = Loc.Format("LiveStacking.HudStackFps", telemetry.StackingFps);
            LiveHudBufferText = Loc.Format("LiveStacking.HudBuffer", telemetry.CurrentBufferCount, telemetry.MaxBufferCapacity);
            int totalRejected = telemetry.FramesRejectedQuality + telemetry.FramesRejectedDriftOrCloud;
            LiveHudAcceptedRejectedText = Loc.Format("LiveStacking.HudAcceptedRejected", telemetry.FramesAccepted, totalRejected);
            LiveHudSeeingText = Loc.Format("LiveStacking.HudSeeing", telemetry.CurrentQualityScore, telemetry.BestQualityScore);
            LiveHudSensorTempText = Loc.Format("LiveStacking.HudSensorTemp", telemetry.SensorTemperatureC);

            IsLiveRecordingSer = telemetry.IsRecordingSer;
            LiveRecordedSerFrames = telemetry.RecordedSerFrames;

            RaiseLivePreviewFrame(displayFrame);
        }, DispatcherPriority.Render);
    }
}
