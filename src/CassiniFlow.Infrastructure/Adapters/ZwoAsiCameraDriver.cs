using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Outbound;
using CassiniFlow.Infrastructure.Codecs;
using CassiniFlow.Infrastructure.Synthetic;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Adaptateur de pilote caméra pour les caméras planétaires ZWO ASI (via ASICamera2 SDK)
/// avec simulateurs matériels temps réel intégrés (Flux SER réel & synthétique) en secours.
/// </summary>
public sealed class ZwoAsiCameraDriver : ICameraDriver
{
    public const string SimJupiterCameraId = "sim-zwo-asi224mc-jupiter";
    public const string SimLunarCameraId = "sim-zwo-asi290mm-lunar";
    public const string SimRealSerCameraId = "sim-zwo-real-ser";

    private readonly object _stateLock = new();
    private List<CameraDeviceInfo> _knownCameras = [];
    private CameraDeviceInfo? _connectedCamera;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    // Paramètres dynamiques mis à jour à chaud
    private volatile int _currentGain = 250;
    private volatile int _currentOffset = 10;
    private volatile int _currentWbR = 52;
    private volatile int _currentWbB = 95;
    private double _currentExposureMs = 10.0;
    private int _droppedFrames;

    public bool IsConnected => _connectedCamera is not null;
    public CameraDeviceInfo? ConnectedCamera => _connectedCamera;
    public bool IsStreaming => _streamTask is { IsCompleted: false };

    public IReadOnlyList<CameraDeviceInfo> DiscoverCameras()
    {
        var list = new List<CameraDeviceInfo>();

        // 1. Tentative de découverte matérielle via le SDK ZWO ASI natif
        try
        {
            ZwoAsiNative.EnsureResolverRegistered();
            int count = ZwoAsiNative.ASIGetNumOfConnectedCameras();
            for (int i = 0; i < count; i++)
            {
                if (ZwoAsiNative.ASIGetCameraProperty(out var prop, i) == ZwoAsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    BayerPattern bayer = prop.BayerPattern switch
                    {
                        ZwoAsiNative.ASI_BAYER_PATTERN.ASI_BAYER_BG => BayerPattern.Bggr,
                        ZwoAsiNative.ASI_BAYER_PATTERN.ASI_BAYER_GR => BayerPattern.Grbg,
                        ZwoAsiNative.ASI_BAYER_PATTERN.ASI_BAYER_GB => BayerPattern.Gbrg,
                        _ => BayerPattern.Rggb
                    };

                    list.Add(new CameraDeviceInfo(
                        Id: $"zwo-hw-{prop.CameraID}",
                        Name: prop.GetName(),
                        CameraIndex: prop.CameraID,
                        MaxWidth: (int)prop.MaxWidth.Value,
                        MaxHeight: (int)prop.MaxHeight.Value,
                        IsColor: prop.IsColorCam != 0,
                        BayerPattern: bayer,
                        MaxBitDepth: prop.BitDepth > 8 ? 16 : 8,
                        PixelSizeMicrons: prop.PixelSize,
                        IsUsb3: prop.IsUSB3Camera != 0,
                        IsCoolerCam: prop.IsCoolerCam != 0,
                        IsSimulated: false));
                }
            }
        }
        catch (DllNotFoundException)
        {
            // ASICamera2.dll non présente sur le système : bascule transparente sur les simulateurs
        }
        catch (Exception)
        {
            // Ignorer les erreurs d'énumération USB bas niveau
        }

        // 2. Simulateur temps réel basé sur un flux vidéo SER réel (Saturne haute fidélité)
        string? realSerPath = FindAvailableRealSerPath();
        if (!string.IsNullOrEmpty(realSerPath))
        {
            try
            {
                using var ser = new SerFile(realSerPath);
                var hdr = ser.Header;
                list.Add(new CameraDeviceInfo(
                    Id: SimRealSerCameraId,
                    Name: $"ZWO ASI (Simulateur SER - Saturne {hdr.ImageWidth}×{hdr.ImageHeight})",
                    CameraIndex: -99,
                    MaxWidth: hdr.ImageWidth,
                    MaxHeight: hdr.ImageHeight,
                    IsColor: hdr.IsBayer || hdr.Planes == 3,
                    BayerPattern: hdr.ToBayerPattern(),
                    MaxBitDepth: hdr.PixelDepthPerPlane > 8 ? 16 : 8,
                    PixelSizeMicrons: 3.75,
                    IsUsb3: true,
                    IsCoolerCam: false,
                    IsSimulated: true));
            }
            catch
            {
                // Ignorer si fichier SER non lisible
            }
        }

        lock (_stateLock)
        {
            _knownCameras = list;
        }

        return list;
    }

    public bool Connect(string cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return false;

        Disconnect();

        List<CameraDeviceInfo> cameras;
        lock (_stateLock)
        {
            if (_knownCameras.Count == 0)
            {
                DiscoverCameras();
            }
            cameras = [.. _knownCameras];
        }

        var target = cameras.FirstOrDefault(c => string.Equals(c.Id, cameraId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;

        if (!target.IsSimulated)
        {
            try
            {
                var openErr = ZwoAsiNative.ASIOpenCamera(target.CameraIndex);
                if (openErr != ZwoAsiNative.ASI_ERROR_CODE.ASI_SUCCESS) return false;

                var initErr = ZwoAsiNative.ASIInitCamera(target.CameraIndex);
                if (initErr != ZwoAsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    ZwoAsiNative.ASICloseCamera(target.CameraIndex);
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        lock (_stateLock)
        {
            _connectedCamera = target;
            _droppedFrames = 0;
        }

        return true;
    }

    public void Disconnect()
    {
        StopVideoStream();

        lock (_stateLock)
        {
            if (_connectedCamera is { IsSimulated: false } hwCam)
            {
                try
                {
                    ZwoAsiNative.ASICloseCamera(hwCam.CameraIndex);
                }
                catch
                {
                    // Ignorer à la fermeture
                }
            }
            _connectedCamera = null;
        }
    }

    public void StartVideoStream(CameraStreamConfig config, Action<AstroFrame> onFrameCaptured)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(onFrameCaptured);

        StopVideoStream();

        CameraDeviceInfo? cam;
        lock (_stateLock)
        {
            cam = _connectedCamera;
        }

        if (cam is null)
        {
            throw new InvalidOperationException("No camera is currently connected.");
        }

        var cfg = config.Clone();
        _currentExposureMs = Math.Clamp(cfg.ExposureMs, 0.1, 5000.0);
        _currentGain = Math.Clamp(cfg.Gain, 0, 600);
        _currentOffset = Math.Clamp(cfg.Offset, 0, 255);
        _currentWbR = Math.Clamp(cfg.WhiteBalanceR, 1, 100);
        _currentWbB = Math.Clamp(cfg.WhiteBalanceB, 1, 100);

        var cts = new CancellationTokenSource();
        _streamCts = cts;

        if (string.Equals(cam.Id, SimRealSerCameraId, StringComparison.OrdinalIgnoreCase))
        {
            string? serPath = FindAvailableRealSerPath();
            if (!string.IsNullOrEmpty(serPath))
            {
                _streamTask = Task.Run(() => RunRealSerCaptureLoop(serPath, cam, cfg, onFrameCaptured, cts.Token), cts.Token);
            }
            else
            {
                _streamTask = Task.Run(() => RunSimulatedCaptureLoop(cam, cfg, onFrameCaptured, cts.Token), cts.Token);
            }
        }
        else if (cam.IsSimulated)
        {
            _streamTask = Task.Run(() => RunSimulatedCaptureLoop(cam, cfg, onFrameCaptured, cts.Token), cts.Token);
        }
        else
        {
            _streamTask = Task.Run(() => RunHardwareCaptureLoop(cam, cfg, onFrameCaptured, cts.Token), cts.Token);
        }
    }

    public void StopVideoStream()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_stateLock)
        {
            cts = _streamCts;
            task = _streamTask;
            _streamCts = null;
            _streamTask = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
                task?.Wait(TimeSpan.FromMilliseconds(800));
            }
            catch
            {
                // Ignorer l'annulation
            }
            finally
            {
                cts.Dispose();
            }
        }
    }

    public void UpdateExposureAndGain(double exposureMs, int gain, int offset = 10, bool autoExposure = false, bool autoGain = false)
    {
        _currentExposureMs = Math.Clamp(exposureMs, 0.1, 5000.0);
        _currentGain = Math.Clamp(gain, 0, 600);
        _currentOffset = Math.Clamp(offset, 0, 255);

        CameraDeviceInfo? cam = _connectedCamera;
        if (cam is { IsSimulated: false })
        {
            try
            {
                int expMicroSec = (int)Math.Round(_currentExposureMs * 1000.0);
                ZwoAsiNative.ASISetControlValue(cam.CameraIndex, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_EXPOSURE, new CLong(expMicroSec), autoExposure ? 1 : 0);
                ZwoAsiNative.ASISetControlValue(cam.CameraIndex, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_GAIN, new CLong(_currentGain), autoGain ? 1 : 0);
                ZwoAsiNative.ASISetControlValue(cam.CameraIndex, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_OFFSET, new CLong(_currentOffset), 0);
            }
            catch
            {
                // Ignorer si occupé
            }
        }
    }

    public void UpdateWhiteBalance(int wbR, int wbB)
    {
        _currentWbR = Math.Clamp(wbR, 1, 100);
        _currentWbB = Math.Clamp(wbB, 1, 100);

        CameraDeviceInfo? cam = _connectedCamera;
        if (cam is { IsSimulated: false, IsColor: true })
        {
            try
            {
                ZwoAsiNative.ASISetControlValue(cam.CameraIndex, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_WB_R, new CLong(_currentWbR), 0);
                ZwoAsiNative.ASISetControlValue(cam.CameraIndex, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_WB_B, new CLong(_currentWbB), 0);
            }
            catch
            {
                // Ignorer
            }
        }
    }

    public double GetSensorTemperatureCelsius()
    {
        CameraDeviceInfo? cam = _connectedCamera;
        if (cam is null) return 0.0;

        if (!cam.IsSimulated)
        {
            try
            {
                if (ZwoAsiNative.ASIGetControlValue(cam.CameraIndex, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_TEMPERATURE, out CLong temp10, out _) == ZwoAsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    return temp10.Value / 10.0;
                }
            }
            catch
            {
                // Fallback
            }
        }

        return 21.4;
    }

    public int GetDroppedFramesCount()
    {
        CameraDeviceInfo? cam = _connectedCamera;
        if (cam is { IsSimulated: false })
        {
            try
            {
                if (ZwoAsiNative.ASIGetDroppedFrames(cam.CameraIndex, out int dropped) == ZwoAsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    return dropped;
                }
            }
            catch
            {
                // Fallback
            }
        }
        return _droppedFrames;
    }

    private unsafe void RunHardwareCaptureLoop(
        CameraDeviceInfo cam,
        CameraStreamConfig cfg,
        Action<AstroFrame> onFrameCaptured,
        CancellationToken ct)
    {
        int camId = cam.CameraIndex;
        int bin = Math.Clamp(cfg.Binning, 1, 4);
        int width = Math.Clamp((cfg.Width / 8) * 8, 64, cam.MaxWidth / bin);
        int height = Math.Clamp((cfg.Height / 2) * 2, 64, cam.MaxHeight / bin);
        bool is16 = cfg.BitDepth > 8;

        var imgType = is16
            ? ZwoAsiNative.ASI_IMG_TYPE.ASI_IMG_RAW16
            : ZwoAsiNative.ASI_IMG_TYPE.ASI_IMG_RAW8;

        ZwoAsiNative.ASISetROIFormat(camId, width, height, bin, imgType);

        int startX = cfg.StartX > 0 ? cfg.StartX : Math.Max(0, (cam.MaxWidth / bin - width) / 2);
        int startY = cfg.StartY > 0 ? cfg.StartY : Math.Max(0, (cam.MaxHeight / bin - height) / 2);
        ZwoAsiNative.ASISetStartPos(camId, startX & ~1, startY & ~1);

        UpdateExposureAndGain(_currentExposureMs, _currentGain, _currentOffset, cfg.AutoExposure, cfg.AutoGain);
        if (cam.IsColor)
        {
            UpdateWhiteBalance(_currentWbR, _currentWbB);
        }

        ZwoAsiNative.ASISetControlValue(camId, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_BANDWIDTHOVERLOAD, new CLong(Math.Clamp(cfg.UsbBandwidth, 40, 100)), 0);
        ZwoAsiNative.ASISetControlValue(camId, ZwoAsiNative.ASI_CONTROL_TYPE.ASI_HIGH_SPEED_MODE, new CLong(cfg.HighSpeedMode ? 1 : 0), 0);

        int bytesPerPixel = is16 ? 2 : 1;
        int bufSize = width * height * bytesPerPixel;
        byte[] rawBuffer = ArrayPool<byte>.Shared.Rent(bufSize);

        try
        {
            if (ZwoAsiNative.ASIBeginVideoCapture(camId) != ZwoAsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
            {
                return;
            }

            int frameIdx = 0;
            while (!ct.IsCancellationRequested)
            {
                int waitMs = Math.Max(200, (int)(_currentExposureMs * 2.5) + 100);
                ZwoAsiNative.ASI_ERROR_CODE status;

                fixed (byte* pBuf = rawBuffer)
                {
                    status = ZwoAsiNative.ASIGetVideoData(camId, pBuf, new CLong(bufSize), waitMs);
                }

                if (status == ZwoAsiNative.ASI_ERROR_CODE.ASI_SUCCESS)
                {
                    frameIdx++;
                    var frame = new AstroFrame(width, height, 1, is16 ? 16 : 8)
                    {
                        FrameIndex = frameIdx,
                        IsBayerRaw = cam.IsColor,
                        BayerPattern = cam.BayerPattern,
                        TimestampUtc = DateTime.UtcNow
                    };

                    int pixelCount = width * height;
                    fixed (byte* pSrc = rawBuffer)
                    fixed (ushort* pDst = frame.Data)
                    {
                        if (is16)
                        {
                            Buffer.MemoryCopy(pSrc, pDst, pixelCount * sizeof(ushort), pixelCount * sizeof(ushort));
                        }
                        else
                        {
                            for (int i = 0; i < pixelCount; i++)
                            {
                                pDst[i] = (ushort)(pSrc[i] * 257);
                            }
                        }
                    }

                    onFrameCaptured(frame);
                }
                else if (status == ZwoAsiNative.ASI_ERROR_CODE.ASI_ERROR_TIMEOUT)
                {
                    continue;
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            try
            {
                ZwoAsiNative.ASIEndVideoCapture(camId);
            }
            catch
            {
                // Ignorer
            }
            ArrayPool<byte>.Shared.Return(rawBuffer);
        }
    }

    private unsafe void RunSimulatedCaptureLoop(
        CameraDeviceInfo cam,
        CameraStreamConfig cfg,
        Action<AstroFrame> onFrameCaptured,
        CancellationToken ct)
    {
        int width = Math.Clamp(cfg.Width, 128, 1280);
        int height = Math.Clamp(cfg.Height, 128, 960);
        bool isLunar = string.Equals(cam.Id, SimLunarCameraId, StringComparison.OrdinalIgnoreCase);

        // Pré-générer un banc de 24 trames synthétiques variées (turbulence + dérive réalistes) pour assurer un débit > 60 FPS sans surcharge CPU
        var subject = isLunar ? SyntheticSubject.LunarClaviusMono : SyntheticSubject.JupiterRggb;
        var baseBank = SyntheticAstroGenerator.GenerateSequence(subject, frameCount: 24, width: width, height: height);

        int frameIdx = 0;
        var sw = Stopwatch.StartNew();
        uint rngState = 0x9E3779B9u;

        while (!ct.IsCancellationRequested)
        {
            long frameStartMs = sw.ElapsedMilliseconds;
            var template = baseBank[frameIdx % baseBank.Count];
            frameIdx++;

            var frame = template.Clone();
            frame.FrameIndex = frameIdx;
            frame.TimestampUtc = DateTime.UtcNow;

            // Appliquer l'effet du Gain, de l'Exposition et du bruit de lecture CMOS en temps réel
            double gainFactor = (_currentGain / 250.0) * (Math.Clamp(_currentExposureMs, 1.0, 200.0) / 10.0);
            gainFactor = Math.Clamp(gainFactor, 0.15, 3.5);
            int offset16 = _currentOffset * 64;
            int noiseAmp = Math.Clamp(180 + _currentGain * 3, 120, 2200);

            int len = frame.Data.Length;
            fixed (ushort* pData = frame.Data)
            {
                for (int i = 0; i < len; i++)
                {
                    // Générateur pseudo-aléatoire Xorshift32 ultra-rapide
                    rngState ^= rngState << 13;
                    rngState ^= rngState >> 17;
                    rngState ^= rngState << 5;
                    int noise = (int)(rngState % (uint)noiseAmp) - (noiseAmp >> 1);

                    int val = (int)(pData[i] * gainFactor) + offset16 + noise;
                    pData[i] = (ushort)(val < 0 ? 0 : (val > 65535 ? 65535 : val));
                }
            }

            onFrameCaptured(frame);

            // Cadencement réaliste selon l'exposition configurée (entre 10 ms / 100 fps et ExposureMs)
            int targetIntervalMs = Math.Clamp((int)Math.Round(_currentExposureMs), 10, 250);
            int elapsedMs = (int)(sw.ElapsedMilliseconds - frameStartMs);
            int sleepMs = targetIntervalMs - elapsedMs;
            if (sleepMs > 1)
            {
                Thread.Sleep(sleepMs);
            }
        }
    }

    private void RunRealSerCaptureLoop(
        string serPath,
        CameraDeviceInfo cam,
        CameraStreamConfig cfg,
        Action<AstroFrame> onFrameCaptured,
        CancellationToken ct)
    {
        try
        {
            using var ser = new SerFile(serPath);
            int totalFrames = ser.Header.FrameCount;
            if (totalFrames <= 0) return;

            int frameIdx = 0;
            var sw = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                long frameStartMs = sw.ElapsedMilliseconds;

                var frame = ser.ReadFrame(frameIdx % totalFrames);
                frame.FrameIndex = frameIdx;
                frame.TimestampUtc = DateTime.UtcNow;

                onFrameCaptured(frame);
                frameIdx++;

                // Cadencement régulier selon l'exposition demandée (par ex. 15 ms ~ 65 fps)
                int targetIntervalMs = Math.Clamp((int)Math.Round(_currentExposureMs), 10, 250);
                int elapsedMs = (int)(sw.ElapsedMilliseconds - frameStartMs);
                int sleepMs = targetIntervalMs - elapsedMs;
                if (sleepMs > 1)
                {
                    Thread.Sleep(sleepMs);
                }
            }
        }
        catch
        {
            // Terminer proprement la boucle sur annulation ou déconnexion
        }
    }

    private static string? FindAvailableRealSerPath()
    {
        string? envPath = Environment.GetEnvironmentVariable("CASSINIFLOW_SIM_SER");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        string knownSaturn = @"D:\Astro\test\2026-10-01-2238_8-Saturn.ser";
        if (File.Exists(knownSaturn))
        {
            return knownSaturn;
        }

        return EnsureFallbackTestSerPath();
    }

    private static string? EnsureFallbackTestSerPath()
    {
        try
        {
            string fallbackPath = Path.Combine(Path.GetTempPath(), "CassiniFlow_Saturn_Fallback.ser");
            if (File.Exists(fallbackPath) && new FileInfo(fallbackPath).Length > 1024)
            {
                return fallbackPath;
            }

            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.SaturnRingsRgb, frameCount: 12, width: 320, height: 240);
            using var writer = new SerStreamWriter(fallbackPath, 320, 240, channels: 3, pixelDepthPerPlane: 16, isBayerRaw: false);
            foreach (var f in frames)
            {
                writer.WriteFrame(f);
            }
            writer.Finish();
            return fallbackPath;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
