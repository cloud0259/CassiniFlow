using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Codecs;
using CassiniFlow.Infrastructure.Synthetic;
using Xunit;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class LiveStackingTests
{
    [Fact]
    public void LiveStackingEngine_CentersDriftedPlanetAndAccumulatesTopK()
    {
        var engine = new LiveStackingEngine();
        var options = new LiveStackingOptions
        {
            RollingBufferSize = 5,
            EnableQualityGate = false,
            AlignmentMethod = LiveAlignmentMethod.PlanetCog,
            EnableLiveSharpening = false,
            EnableDigitalAdc = false
        };

        var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 8, width: 240, height: 180);
        // Trier de la moins nette à la plus nette afin que chaque nouvelle trame améliore le Top-K
        frames.Reverse();

        int accepted = 0;
        foreach (var f in frames)
        {
            if (engine.ProcessFrame(f, options))
            {
                accepted++;
            }
        }

        Assert.True(accepted >= 5);

        var telemetry = engine.GetTelemetry(60, 60, 20.0, false, 0, options.RollingBufferSize);
        Assert.Equal(5, telemetry.CurrentBufferCount);
        Assert.Equal(8, telemetry.TotalFramesReceived);

        var rawStack = engine.GetRawStackedFrame();
        Assert.NotNull(rawStack);
        Assert.Equal(240, rawStack!.Width);
        Assert.Equal(180, rawStack.Height);

        // Vérifier que la planète empilée est bien centrée au milieu du canevas (120, 90)
        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            rawStack, checkObject: true, autoThreshold: true, manualThreshold8Bit: 30,
            minObjectSizePixels: 36, shadowEdge: LunarShadowEdge.None, checkOverexposed: false, maxOverexposedPixels: 1000);

        Assert.True(det.Accepted);
        Assert.InRange(det.CentroidX, 116, 124);
        Assert.InRange(det.CentroidY, 86, 94);
    }

    [Fact]
    public void LiveStackingEngine_CloudBrightnessDropFilter_RejectsDarkenedFrame()
    {
        var engine = new LiveStackingEngine();
        var options = new LiveStackingOptions
        {
            RollingBufferSize = 20,
            EnableQualityGate = false,
            EnableCloudBrightnessFilter = true,
            MaxBrightnessDropPercent = 30
        };

        var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 10, width: 200, height: 160);
        foreach (var f in frames)
        {
            Assert.True(engine.ProcessFrame(f, options));
        }

        // Simuler le passage d'un nuage épais (chute de 80% de la luminosité)
        var cloudyFrame = frames[0].Clone();
        for (int i = 0; i < cloudyFrame.Data.Length; i++)
        {
            cloudyFrame.Data[i] = (ushort)(cloudyFrame.Data[i] / 5);
        }

        bool cloudAccepted = engine.ProcessFrame(cloudyFrame, options);
        Assert.False(cloudAccepted);

        var telemetry = engine.GetTelemetry(30, 30, 20, false, 0, 20);
        Assert.Equal(1, telemetry.FramesRejectedDriftOrCloud);
    }

    [Fact]
    public void LiveStackingEngine_SharpeningToggle_IsNonDestructiveOnRawStack()
    {
        var engine = new LiveStackingEngine();
        var options = new LiveStackingOptions
        {
            RollingBufferSize = 10,
            EnableQualityGate = false,
            EnableLiveSharpening = true,
            SharpeningBlendPercent = 60,
            UnsharpRadius = 1.5,
            UnsharpAmount = 2.0
        };

        var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 4, width: 200, height: 160);
        foreach (var f in frames)
        {
            engine.ProcessFrame(f, options);
        }

        var rawBefore = engine.GetRawStackedFrame()!;
        var displaySharpened = engine.GetDisplayFrame(options)!;

        options.EnableLiveSharpening = false;
        var displayUnsharpened = engine.GetDisplayFrame(options)!;
        var rawAfter = engine.GetRawStackedFrame()!;

        // Les données scientifiques brutes ne doivent jamais être altérées par le sharpening d'affichage
        Assert.Equal(rawBefore.Data, rawAfter.Data);

        // Le sharpening activé augmente le score de gradient par rapport au rendu non sharpené
        double scoreSharpened = StabilisationAndQualityEngine.ComputeQualityScore(displaySharpened, QualityAlgorithm.GradientSharpness);
        double scoreRaw = StabilisationAndQualityEngine.ComputeQualityScore(displayUnsharpened, QualityAlgorithm.GradientSharpness);
        Assert.True(scoreSharpened > scoreRaw);
    }

    [Fact]
    public async Task LiveStackingSessionService_SimulatedStream_StacksAndSavesSnapshotAndSer()
    {
        using var driver = new ZwoAsiCameraDriver();
        using var session = new LiveStackingSessionService(driver);

        var cameras = session.DiscoverCameras();
        Assert.NotEmpty(cameras);
        Assert.Contains(cameras, c => c.Id == ZwoAsiCameraDriver.SimRealSerCameraId);

        Assert.True(session.ConnectCamera(ZwoAsiCameraDriver.SimRealSerCameraId));
        Assert.True(session.IsConnected);

        var tcs = new TaskCompletionSource<LiveStackingTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FrameStacked += (frame, telemetry) =>
        {
            if (telemetry.FramesAccepted >= 3)
            {
                tcs.TrySetResult(telemetry);
            }
        };

        string tempDir = Path.Combine(Path.GetTempPath(), $"CassiniFlow_LiveTest_{Guid.NewGuid():N}");
        try
        {
            var streamCfg = new CameraStreamConfig
            {
                Width = 240,
                Height = 180,
                ExposureMs = 10.0,
                Gain = 250
            };

            var stackOpts = new LiveStackingOptions
            {
                RollingBufferSize = 20,
                EnableQualityGate = false,
                EnableLiveSharpening = true
            };

            session.StartSession(streamCfg, stackOpts);
            string serPath = session.StartBackgroundSerRecording(tempDir, "TestJupiter");
            Assert.True(session.IsRecordingSer);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Same(tcs.Task, completedTask);

            var snapshot = session.TakeSnapshot(tempDir, "TestSnap", preferFitsOverTiff: true);
            Assert.True(snapshot.Success, snapshot.ErrorMessage);
            Assert.True(File.Exists(snapshot.RawFitsOrTiffPath));
            Assert.True(File.Exists(snapshot.SharpenedPngPath));

            string? finalSer = session.StopBackgroundSerRecording();
            session.StopSession();

            Assert.NotNull(finalSer);
            Assert.True(File.Exists(finalSer!));

            using var ser = new SerFile(finalSer!);
            var serHeader = ser.Header;
            Assert.Equal(240, serHeader.ImageWidth);
            Assert.Equal(180, serHeader.ImageHeight);
            Assert.True(serHeader.FrameCount >= 2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void LiveStackingEngine_ShiftFrameBilinear_PreservesHighBitDepthWithoutInt32Overflow()
    {
        // Régression : vérifie qu'un décalage sub-pixel d'une image lumineuse 16-bit ne subit aucun overflow arithmétique int32
        int w = 64, h = 64;
        var frame = new AstroFrame(w, h, 3, 16);
        ushort highLum = 52000;
        Array.Fill(frame.Data, highLum);

        // Décalage sub-pixel arbitraire (ex: 0.45 px en X, 0.65 px en Y)
        var shifted = LiveStackingEngine.ShiftFrameBilinear(frame, 0.45, 0.65);

        // Le pixel intérieur doit conserver sa luminosité sans être clampé à 0 (trou noir)
        int idx = (32 * w + 32) * 3;
        Assert.InRange(shifted.Data[idx], highLum - 100, highLum + 100);
        Assert.InRange(shifted.Data[idx + 1], highLum - 100, highLum + 100);
        Assert.InRange(shifted.Data[idx + 2], highLum - 100, highLum + 100);
    }
}
