using System.Diagnostics;
using System.Threading.Channels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Ports.Outbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Codecs;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Service orchestrateur d'application/infrastructure implémentant ILiveStackingUseCase.
/// Coordonne l'acquisition caméra non-bloquante, le moteur de Live Stacking Rolling Top-K,
/// l'enregistrement SER continu en arrière-plan et l'export d'instantanés 16-bit / PNG.
/// </summary>
public sealed class LiveStackingSessionService : ILiveStackingUseCase
{
    private readonly ICameraDriver _cameraDriver;
    private readonly LiveStackingEngine _engine;
    private readonly object _sessionLock = new();
    private readonly object _serLock = new();

    private LiveStackingOptions _currentOptions = new();
    private CameraStreamConfig _currentStreamConfig = new();
    private Channel<AstroFrame>? _stackingChannel;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    private SerStreamWriter? _serWriter;
    private string? _activeSerFilePath;
    private int _recordedSerFrames;

    // Mesure des cadences FPS
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    private long _lastFpsMeasureMs;
    private int _captureFramesSinceLastMeasure;
    private int _stackFramesSinceLastMeasure;
    private double _measuredCaptureFps;
    private double _measuredStackingFps;

    public event Action<AstroFrame, LiveStackingTelemetry>? FrameStacked;

    public LiveStackingSessionService(ICameraDriver cameraDriver)
    {
        _cameraDriver = cameraDriver ?? throw new ArgumentNullException(nameof(cameraDriver));
        _engine = new LiveStackingEngine(FastCrossCorrelation.ComputeShift);
    }

    public bool IsConnected => _cameraDriver.IsConnected;
    public bool IsRunning => _cameraDriver.IsStreaming;
    public bool IsRecordingSer
    {
        get
        {
            lock (_serLock)
            {
                return _serWriter is not null;
            }
        }
    }

    public CameraDeviceInfo? ConnectedCamera => _cameraDriver.ConnectedCamera;

    public IReadOnlyList<CameraDeviceInfo> DiscoverCameras() => _cameraDriver.DiscoverCameras();

    public bool ConnectCamera(string cameraId) => _cameraDriver.Connect(cameraId);

    public void DisconnectCamera()
    {
        StopSession();
        _cameraDriver.Disconnect();
    }

    public void StartSession(CameraStreamConfig streamConfig, LiveStackingOptions stackingOptions)
    {
        ArgumentNullException.ThrowIfNull(streamConfig);
        ArgumentNullException.ThrowIfNull(stackingOptions);

        StopSession();

        lock (_sessionLock)
        {
            _currentStreamConfig = streamConfig.Clone();
            _currentOptions = stackingOptions.Clone();
            _engine.Reset();

            _lastFpsMeasureMs = _fpsWatch.ElapsedMilliseconds;
            _captureFramesSinceLastMeasure = 0;
            _stackFramesSinceLastMeasure = 0;
            _measuredCaptureFps = 0;
            _measuredStackingFps = 0;

            _stackingChannel = Channel.CreateBounded<AstroFrame>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            var cts = new CancellationTokenSource();
            _workerCts = cts;
            _workerTask = Task.Run(() => RunStackingWorkerAsync(_stackingChannel.Reader, cts.Token), cts.Token);

            _cameraDriver.StartVideoStream(_currentStreamConfig, OnRawFrameCaptured);
        }
    }

    public void StopSession()
    {
        StopBackgroundSerRecording();
        _cameraDriver.StopVideoStream();

        CancellationTokenSource? cts;
        Task? worker;
        Channel<AstroFrame>? channel;

        lock (_sessionLock)
        {
            cts = _workerCts;
            worker = _workerTask;
            channel = _stackingChannel;
            _workerCts = null;
            _workerTask = null;
            _stackingChannel = null;
        }

        channel?.Writer.TryComplete();
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
                worker?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // Ignorer
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    public void ResetStack()
    {
        _engine.Reset();
    }

    public void UpdateStackingOptions(LiveStackingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var cloned = options.Clone();
        lock (_sessionLock)
        {
            _currentOptions = cloned;
        }

        _engine.ResizeBufferCapacity(cloned.RollingBufferSize);

        // Rafraîchir immédiatement le rendu écran (permet de basculer le Sharpening ou l'ADC à chaud avec effet instantané)
        var displayFrame = _engine.GetDisplayFrame(cloned);
        if (displayFrame is not null)
        {
            var telemetry = BuildCurrentTelemetry(cloned);
            FrameStacked?.Invoke(displayFrame, telemetry);
        }
    }

    public void UpdateCameraControls(
        double exposureMs,
        int gain,
        int offset,
        int wbR,
        int wbB,
        bool autoExp = false,
        bool autoGain = false)
    {
        _cameraDriver.UpdateExposureAndGain(exposureMs, gain, offset, autoExp, autoGain);
        _cameraDriver.UpdateWhiteBalance(wbR, wbB);
    }

    public LiveSnapshotResult TakeSnapshot(string outputDirectory, string filePrefix = "LiveStack", bool preferFitsOverTiff = true)
    {
        try
        {
            var rawStack = _engine.GetRawStackedFrame();
            if (rawStack is null)
            {
                return new LiveSnapshotResult(false, string.Empty, string.Empty, 0, "No stacked frames available yet.");
            }

            LiveStackingOptions opts;
            lock (_sessionLock)
            {
                opts = _currentOptions.Clone();
            }

            var displayFrame = _engine.GetDisplayFrame(opts) ?? rawStack;

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CassiniFlow_Live");
            }
            Directory.CreateDirectory(outputDirectory);

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
            string safePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "LiveStack" : filePrefix.Trim();

            string rawExt = preferFitsOverTiff ? ".fit" : ".tif";
            string rawPath = Path.Combine(outputDirectory, $"{safePrefix}_{timestamp}_raw16{rawExt}");
            string pngPath = Path.Combine(outputDirectory, $"{safePrefix}_{timestamp}_sharpened.png");

            if (preferFitsOverTiff)
            {
                FitsFile.WriteFrame(rawPath, rawStack);
            }
            else
            {
                UniversalMediaReader.WriteSingleImageFile(rawPath, rawStack, is16Bit: true);
            }

            UniversalMediaReader.WriteSingleImageFile(pngPath, displayFrame, is16Bit: false);

            return new LiveSnapshotResult(true, rawPath, pngPath, rawStack.FrameIndex);
        }
        catch (Exception ex)
        {
            return new LiveSnapshotResult(false, string.Empty, string.Empty, 0, ex.Message);
        }
    }

    public string StartBackgroundSerRecording(string outputDirectory, string filePrefix = "Capture")
    {
        lock (_serLock)
        {
            if (_serWriter is not null && _activeSerFilePath is not null)
            {
                return _activeSerFilePath;
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CassiniFlow_Live");
            }
            Directory.CreateDirectory(outputDirectory);

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
            string safePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "Capture" : filePrefix.Trim();
            string serPath = Path.Combine(outputDirectory, $"{safePrefix}_{timestamp}.ser");

            var cam = _cameraDriver.ConnectedCamera;
            int w = _currentStreamConfig.Width;
            int h = _currentStreamConfig.Height;
            int depth = _currentStreamConfig.BitDepth <= 8 ? 8 : 16;
            bool isBayer = cam?.IsColor ?? true;
            var pattern = cam?.BayerPattern ?? BayerPattern.Rggb;

            // Les simulateurs génèrent des AstroFrame déjà en 3 canaux (Jupiter) ou 1 canal (Lune)
            int ch = (cam is { IsSimulated: true, IsColor: true }) ? 3 : 1;
            if (ch == 3) isBayer = false;

            long nowTicks = DateTime.UtcNow.Ticks;
            _serWriter = new SerStreamWriter(
                outputPath: serPath,
                width: w,
                height: h,
                channels: ch,
                pixelDepthPerPlane: depth,
                isBayerRaw: isBayer,
                bayerPattern: pattern,
                observer: "CassiniFlow Live",
                instrument: cam?.Name ?? "Camera",
                telescope: "Planetary Live",
                dateTimeTicks: nowTicks,
                dateTimeUtcTicks: nowTicks);

            _activeSerFilePath = serPath;
            _recordedSerFrames = 0;
            return serPath;
        }
    }

    public string? StopBackgroundSerRecording()
    {
        lock (_serLock)
        {
            if (_serWriter is null) return null;

            string? path = _activeSerFilePath;
            try
            {
                _serWriter.Finish();
                _serWriter.Dispose();
            }
            catch
            {
                // Ignorer à la fermeture
            }
            finally
            {
                _serWriter = null;
                _activeSerFilePath = null;
            }
            return path;
        }
    }

    private void OnRawFrameCaptured(AstroFrame rawFrame)
    {
        Interlocked.Increment(ref _captureFramesSinceLastMeasure);

        // 1. Enregistrement SER brut en arrière-plan si actif
        lock (_serLock)
        {
            if (_serWriter is not null)
            {
                try
                {
                    _serWriter.WriteFrame(rawFrame);
                    _recordedSerFrames++;
                }
                catch
                {
                    // Éviter d'interrompre le flux live en cas d'erreur disque
                }
            }
        }

        // 2. Envoi non-bloquant vers le worker de Live Stacking
        _stackingChannel?.Writer.TryWrite(rawFrame);
    }

    private async Task RunStackingWorkerAsync(ChannelReader<AstroFrame> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                LiveStackingOptions opts;
                bool debayer;
                lock (_sessionLock)
                {
                    opts = _currentOptions;
                    debayer = _currentStreamConfig.DebayerInLive;
                }

                _engine.ProcessFrame(frame, opts, debayer);
                Interlocked.Increment(ref _stackFramesSinceLastMeasure);

                UpdateFpsMetrics();

                var displayFrame = _engine.GetDisplayFrame(opts) ?? frame;
                var telemetry = BuildCurrentTelemetry(opts);
                FrameStacked?.Invoke(displayFrame, telemetry);
            }
        }
        catch (OperationCanceledException)
        {
            // Fin normale
        }
    }

    private void UpdateFpsMetrics()
    {
        long nowMs = _fpsWatch.ElapsedMilliseconds;
        long elapsedMs = nowMs - _lastFpsMeasureMs;
        if (elapsedMs >= 500)
        {
            int capCount = Interlocked.Exchange(ref _captureFramesSinceLastMeasure, 0);
            int stkCount = Interlocked.Exchange(ref _stackFramesSinceLastMeasure, 0);
            double sec = elapsedMs / 1000.0;
            _measuredCaptureFps = capCount / sec;
            _measuredStackingFps = stkCount / sec;
            _lastFpsMeasureMs = nowMs;
        }
    }

    private LiveStackingTelemetry BuildCurrentTelemetry(LiveStackingOptions opts)
    {
        bool isRec;
        int recFrames;
        lock (_serLock)
        {
            isRec = _serWriter is not null;
            recFrames = _recordedSerFrames;
        }

        return _engine.GetTelemetry(
            captureFps: _measuredCaptureFps,
            stackingFps: _measuredStackingFps,
            sensorTempC: _cameraDriver.GetSensorTemperatureCelsius(),
            isRecordingSer: isRec,
            recordedSerFrames: recFrames,
            maxBufferCapacity: opts.RollingBufferSize);
    }

    public void Dispose()
    {
        StopSession();
        _cameraDriver.Dispose();
    }
}
