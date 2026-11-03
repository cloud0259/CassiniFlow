using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Codecs;
using CassiniFlow.Infrastructure.Synthetic;
using Xunit;

namespace CassiniFlow.Tests;

public class CenteringValidationTests
{
    [Fact]
    public void DetectPlanetOrObject_WithLuminanceAsymmetryAndHotPixels_RemainsExactlyCentered()
    {
        int w = 800, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);
        int trueCx = 400, trueCy = 300, r = 80;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - trueCx;
                int dy = y - trueCy;
                int idx = (y * w + x) * 3;
                if (dx * dx + dy * dy <= r * r)
                {
                    double normDist = Math.Sqrt(dx * dx + dy * dy) / r;
                    double limb = 1.0 - normDist * 0.45;
                    // Upper hemisphere is 45% brighter than lower
                    double asym = 1.0 - 0.45 * (dy / (double)r);
                    ushort v = (ushort)(48000 * limb * asym);
                    frame.Data[idx] = v;
                    frame.Data[idx + 1] = v;
                    frame.Data[idx + 2] = v;
                }
            }
        }

        // Add 2 isolated hot pixels in opposite corners
        frame.Data[(10 * w + 10) * 3] = 65000;
        frame.Data[(580 * w + 780) * 3] = 65000;

        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            frame,
            checkObject: true,
            autoThreshold: true,
            manualThreshold8Bit: 40,
            minObjectSizePixels: 20,
            shadowEdge: LunarShadowEdge.None,
            checkOverexposed: false,
            maxOverexposedPixels: 0);

        Assert.True(det.Accepted);
        Assert.InRange(det.CentroidX, 399, 401);
        Assert.InRange(det.CentroidY, 299, 301);
    }

    [Fact]
    public void CropAroundPoint_CentersPlanetExactlyAtFrameCenter()
    {
        int w = 800, h = 600;
        var frame = new AstroFrame(w, h, 1, 16);
        int trueCx = 400, trueCy = 300, r = 60;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - trueCx;
                int dy = y - trueCy;
                if (dx * dx + dy * dy <= r * r)
                {
                    frame.Data[y * w + x] = 40000;
                }
            }
        }

        int cropW = 200, cropH = 200;
        var cropped = ImageProcessor.CropAroundPoint(frame, trueCx, trueCy, cropW, cropH);

        Assert.Equal(cropW, cropped.Width);
        Assert.Equal(cropH, cropped.Height);

        // Dans le résultat croppé, le centre doit être exactement à (cropW/2, cropH/2) = (100, 100)
        var croppedDet = StabilisationAndQualityEngine.DetectPlanetOrObject(
            cropped,
            checkObject: true,
            autoThreshold: true,
            manualThreshold8Bit: 40,
            minObjectSizePixels: 20,
            shadowEdge: LunarShadowEdge.None,
            checkOverexposed: false,
            maxOverexposedPixels: 0);

        Assert.True(croppedDet.Accepted);
        Assert.Equal(100, croppedDet.CentroidX);
        Assert.Equal(100, croppedDet.CentroidY);
    }

    [Fact]
    public void CropOffsetXY_SignsAreConsistentAndSymmetric()
    {
        int w = 400, h = 400;
        var frame = new AstroFrame(w, h, 1, 16);
        int cx = 200, cy = 200;
        frame.Data[cy * w + cx] = 60000;

        // Positive offsetX moves object to +X in crop; positive offsetY moves object to +Y in crop
        var cropNormal = ImageProcessor.CropAroundPoint(frame, cx, cy, 100, 100, offsetX: 0, offsetY: 0);
        var cropShifted = ImageProcessor.CropAroundPoint(frame, cx, cy, 100, 100, offsetX: 10, offsetY: 10);

        Assert.Equal(60000, cropNormal.Data[50 * 100 + 50]);
        // Shifted: (50 + 10, 50 + 10) = (60, 60)
        Assert.Equal(60000, cropShifted.Data[60 * 100 + 60]);
    }

    [Fact]
    public void DetectPlanet_WithGalileanMoons_IgnoresMoonsAndLocksOnDiscCenter()
    {
        int w = 1000, h = 800;
        var frame = new AstroFrame(w, h, 3, 16);
        int trueCx = 500, trueCy = 400, planetRadius = 70;

        // Draw planet disc
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - trueCx;
                int dy = y - trueCy;
                int idx = (y * w + x) * 3;
                if (dx * dx + dy * dy <= planetRadius * planetRadius)
                {
                    double normDist = Math.Sqrt(dx * dx + dy * dy) / planetRadius;
                    double limb = 1.0 - normDist * 0.4;
                    ushort v = (ushort)(42000 * limb);
                    frame.Data[idx] = v;
                    frame.Data[idx + 1] = v;
                    frame.Data[idx + 2] = v;
                }
            }
        }

        // Add 3 Galilean moons (e.g. Io at +120px, Europa at -150px, Ganymede at +220px)
        int[] moonOffsetsX = [-150, 120, 220];
        int[] moonOffsetsY = [15, -10, 25];
        for (int m = 0; m < 3; m++)
        {
            int mx = trueCx + moonOffsetsX[m];
            int my = trueCy + moonOffsetsY[m];
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (dx * dx + dy * dy <= 4)
                    {
                        int idx = ((my + dy) * w + (mx + dx)) * 3;
                        frame.Data[idx] = 30000;
                        frame.Data[idx + 1] = 30000;
                        frame.Data[idx + 2] = 30000;
                    }
                }
            }
        }

        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            frame,
            checkObject: true,
            autoThreshold: true,
            manualThreshold8Bit: 40,
            minObjectSizePixels: 20,
            shadowEdge: LunarShadowEdge.None,
            checkOverexposed: false,
            maxOverexposedPixels: 0);

        Assert.True(det.Accepted);
        // Centering must remain locked on the planetary disc center regardless of moons
        Assert.InRange(det.CentroidX, trueCx - 1, trueCx + 1);
        Assert.InRange(det.CentroidY, trueCy - 1, trueCy + 1);
    }

    [Fact]
    public void DetectPlanetOrObject_WithNoisySkyBackground_ComputesRobustThresholdAndCentersPlanet()
    {
        int w = 640, h = 480;
        var frame = new AstroFrame(w, h, 3, 16);
        int trueCx = 250, trueCy = 180, r = 35;

        // Simuler un fond de ciel bruité de vidéo smartphone (luminance 8-bit autour de 20-25)
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 3;
                ushort skyVal = (ushort)((22 + ((x + y) % 5)) * 257);
                frame.Data[idx] = skyVal;
                frame.Data[idx + 1] = skyVal;
                frame.Data[idx + 2] = skyVal;
            }
        }

        // Dessiner la planète Saturne brillante (luminance ~200)
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - trueCx;
                int dy = y - trueCy;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    ushort planetVal = 204 * 257;
                    frame.Data[idx] = planetVal;
                    frame.Data[idx + 1] = planetVal;
                    frame.Data[idx + 2] = planetVal;
                }
            }
        }

        // Le seuil automatique doit monter au-dessus du fond de ciel (ex: > 35) et centrer la planète sur (trueCx, trueCy)
        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            frame,
            checkObject: true,
            autoThreshold: true,
            manualThreshold8Bit: 40,
            minObjectSizePixels: 15,
            shadowEdge: LunarShadowEdge.None,
            checkOverexposed: false,
            maxOverexposedPixels: 0);

        Assert.True(det.Accepted);
        Assert.True(det.Threshold8BitUsed > 28, $"Threshold {det.Threshold8BitUsed} must be higher than noisy sky (~25)");
        Assert.InRange(det.CentroidX, trueCx - 1, trueCx + 1);
        Assert.InRange(det.CentroidY, trueCy - 1, trueCy + 1);
    }

    [Fact]
    public void DetectPlanetOrObject_WhenCheckObjectIsFalse_StillFiltersHotPixelsAndCentersPlanet()
    {
        int w = 640, h = 480;
        var frame = new AstroFrame(w, h, 1, 16);
        int trueCx = 210, trueCy = 150, r = 40;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - trueCx;
                int dy = y - trueCy;
                if (dx * dx + dy * dy <= r * r)
                {
                    frame.Data[y * w + x] = 45000;
                }
            }
        }

        // Add bright hot pixels / moons far from the planet
        for (int dy = -3; dy <= 3; dy++)
        {
            for (int dx = -3; dx <= 3; dx++)
            {
                frame.Data[(420 + dy) * w + (580 + dx)] = 65000;
            }
        }

        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            frame,
            checkObject: false,
            autoThreshold: true,
            manualThreshold8Bit: 40,
            minObjectSizePixels: 20,
            shadowEdge: LunarShadowEdge.None,
            checkOverexposed: false,
            maxOverexposedPixels: 0);

        Assert.True(det.Accepted);
        Assert.InRange(det.CentroidX, trueCx - 1, trueCx + 1);
        Assert.InRange(det.CentroidY, trueCy - 1, trueCy + 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreProcessingOnly_SerInput_CentersMovingPlanet_WithOrWithoutCropping(bool enableCropping)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"cassiniflow_ser_preproc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputSer = Path.Combine(tempDir, "moving_planet.ser");
            int w = 320, h = 240, r = 28;
            (int cx, int cy)[] positions =
            [
                (90, 70),
                (220, 85),
                (140, 165),
                (230, 155)
            ];

            var hdr = new SerHeader
            {
                ImageWidth = w,
                ImageHeight = h,
                PixelDepthPerPlane = 8,
                FrameCount = positions.Length,
                ColorId = SerColorId.Mono
            };

            var inputFrames = new List<AstroFrame>();
            for (int i = 0; i < positions.Length; i++)
            {
                var f = CreatePlanetFrame(w, h, 1, positions[i].cx, positions[i].cy, r);
                f.FrameIndex = i;
                inputFrames.Add(f);
            }
            SerFile.WriteSerFile(inputSer, hdr, inputFrames);

            var loc = new AstroLocalizationService();
            var calib = new CalibrationFrameProviderService(loc);
            var stacking = new MultiZoneStackingPipelineRunner(loc, calib);
            var runner = new PipelineRunner(loc, stacking, calib);

            var options = new ProcessingOptions
            {
                StabilisationMode = StabilisationMode.ObjectPlanetary,
                CheckPlanetObjectPresent = true,
                CentreObjectInFrame = true,
                EnableCropping = enableCropping,
                CropWidth = 128,
                CropHeight = 128,
                EnableQualityEstimation = false,
                OutputFormat = OutputFormat.Ser,
                Stacking = new StackingOptions
                {
                    WorkflowMode = StackingWorkflowMode.PreProcessingOnly
                }
            };

            string outDir = Path.Combine(tempDir, "out");
            var summary = await runner.ExecuteFilesAsync([inputSer], options, outDir, null, CancellationToken.None);

            Assert.True(summary.Success);
            Assert.Equal(positions.Length, summary.OutputFramesWritten);
            Assert.True(File.Exists(summary.OutputFilePath));

            int expectedW = enableCropping ? 128 : w;
            int expectedH = enableCropping ? 128 : h;

            using var outSer = new SerFile(summary.OutputFilePath);
            Assert.Equal(positions.Length, outSer.Header.FrameCount);
            Assert.Equal(expectedW, outSer.Header.ImageWidth);
            Assert.Equal(expectedH, outSer.Header.ImageHeight);

            for (int i = 0; i < outSer.Header.FrameCount; i++)
            {
                var outFrame = outSer.ReadFrame(i);
                var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
                    outFrame,
                    checkObject: true,
                    autoThreshold: true,
                    manualThreshold8Bit: 40,
                    minObjectSizePixels: 15,
                    shadowEdge: LunarShadowEdge.None,
                    checkOverexposed: false,
                    maxOverexposedPixels: 0);

                Assert.True(det.Accepted);
                Assert.InRange(det.CentroidX, expectedW / 2 - 2, expectedW / 2 + 2);
                Assert.InRange(det.CentroidY, expectedH / 2 - 2, expectedH / 2 + 2);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreProcessingOnly_AviInput_CentersMovingPlanet_WithOrWithoutCropping(bool enableCropping)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"cassiniflow_avi_preproc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputAvi = Path.Combine(tempDir, "moving_planet.avi");
            int w = 400, h = 300, r = 55;
            // Include large jumps so local window clipping fallback is exercised
            (int cx, int cy)[] positions =
            [
                (110, 100),
                (155, 130),
                (280, 190),
                (130, 180)
            ];

            var inputFrames = new List<AstroFrame>();
            for (int i = 0; i < positions.Length; i++)
            {
                var f = CreatePlanetFrame(w, h, 3, positions[i].cx, positions[i].cy, r);
                f.FrameIndex = i;
                inputFrames.Add(f);
            }

            UniversalMediaReader.WriteVideoFile(inputAvi, inputFrames, fps: 25);

            var loc = new AstroLocalizationService();
            var calib = new CalibrationFrameProviderService(loc);
            var stacking = new MultiZoneStackingPipelineRunner(loc, calib);
            var runner = new PipelineRunner(loc, stacking, calib);

            var options = new ProcessingOptions
            {
                StabilisationMode = StabilisationMode.ObjectPlanetary,
                CheckPlanetObjectPresent = true,
                CentreObjectInFrame = true,
                EnableCropping = enableCropping,
                CropWidth = 160,
                CropHeight = 160,
                EnableQualityEstimation = false,
                OutputFormat = OutputFormat.Ser,
                Stacking = new StackingOptions
                {
                    WorkflowMode = StackingWorkflowMode.PreProcessingOnly
                }
            };

            string outDir = Path.Combine(tempDir, "out");
            var summary = await runner.ExecuteFilesAsync([inputAvi], options, outDir, null, CancellationToken.None);

            Assert.True(summary.Success);
            Assert.Equal(positions.Length, summary.OutputFramesWritten);
            Assert.True(File.Exists(summary.OutputFilePath));

            int expectedW = enableCropping ? 160 : w;
            int expectedH = enableCropping ? 160 : h;

            using var outSer = new SerFile(summary.OutputFilePath);
            Assert.Equal(positions.Length, outSer.Header.FrameCount);
            Assert.Equal(expectedW, outSer.Header.ImageWidth);
            Assert.Equal(expectedH, outSer.Header.ImageHeight);

            for (int i = 0; i < outSer.Header.FrameCount; i++)
            {
                var outFrame = outSer.ReadFrame(i);
                var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
                    outFrame,
                    checkObject: true,
                    autoThreshold: true,
                    manualThreshold8Bit: 40,
                    minObjectSizePixels: 15,
                    shadowEdge: LunarShadowEdge.None,
                    checkOverexposed: false,
                    maxOverexposedPixels: 0);

                Assert.True(det.Accepted);
                Assert.InRange(det.CentroidX, expectedW / 2 - 2, expectedW / 2 + 2);
                Assert.InRange(det.CentroidY, expectedH / 2 - 2, expectedH / 2 + 2);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DirectStreamAllInOne_Vs_SeparatePreprocessingAndStacking_YieldsCenteringAndSharpnessParity()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"cf_parity_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string inputSer = Path.Combine(tempDir, "moving_planet_parity.ser");
            int w = 240, h = 200, r = 24;
            int frameCount = 12;

            (int cx, int cy)[] positions =
            [
                (120, 100), (123, 102), (118, 98), (122, 101),
                (119, 99), (124, 103), (117, 97), (121, 100),
                (120, 101), (122, 99), (119, 102), (121, 100)
            ];

            var hdr = new SerHeader
            {
                ImageWidth = w,
                ImageHeight = h,
                PixelDepthPerPlane = 16,
                FrameCount = frameCount,
                ColorId = SerColorId.Mono
            };

            var inputFrames = new List<AstroFrame>();
            for (int i = 0; i < frameCount; i++)
            {
                var f = CreatePlanetFrame(w, h, 1, positions[i].cx, positions[i].cy, r);
                f.FrameIndex = i;
                inputFrames.Add(f);
            }
            SerFile.WriteSerFile(inputSer, hdr, inputFrames);

            var loc = new AstroLocalizationService();
            var calib = new CalibrationFrameProviderService(loc);
            var stacking = new MultiZoneStackingPipelineRunner(loc, calib);
            var runner = new PipelineRunner(loc, stacking, calib);

            int cropSize = 96;

            // Mode 1 : DirectStreamAllInOne
            string outDirAll = Path.Combine(tempDir, "out_all");
            var optAll = new ProcessingOptions
            {
                StabilisationMode = StabilisationMode.ObjectPlanetary,
                CheckPlanetObjectPresent = true,
                CentreObjectInFrame = true,
                EnableCropping = true,
                CropWidth = cropSize,
                CropHeight = cropSize,
                OutputFormat = OutputFormat.Tiff
            };
            optAll.Stacking.WorkflowMode = StackingWorkflowMode.DirectStreamAllInOne;
            optAll.Stacking.AlignmentTileSize = 32;
            optAll.Stacking.StackPercentages = [50.0];

            var resAll = await runner.ExecuteFilesAsync([inputSer], optAll, outDirAll);
            Assert.True(resAll.Success);
            Assert.True(File.Exists(resAll.OutputFilePath));

            // Mode 2 : Étape 1 = PreProcessingOnly (SER)
            string outDirPrep = Path.Combine(tempDir, "out_prep");
            var optPrep = new ProcessingOptions
            {
                StabilisationMode = StabilisationMode.ObjectPlanetary,
                CheckPlanetObjectPresent = true,
                CentreObjectInFrame = true,
                EnableCropping = true,
                CropWidth = cropSize,
                CropHeight = cropSize,
                OutputFormat = OutputFormat.Ser
            };
            optPrep.Stacking.WorkflowMode = StackingWorkflowMode.PreProcessingOnly;

            var resPrep = await runner.ExecuteFilesAsync([inputSer], optPrep, outDirPrep);
            Assert.True(resPrep.Success);
            Assert.True(File.Exists(resPrep.OutputFilePath));

            // Mode 2 : Étape 2 = StackingOnly (sur le SER prétraité)
            string outDirStack = Path.Combine(tempDir, "out_stack");
            var optStack = new ProcessingOptions
            {
                OutputFormat = OutputFormat.Tiff
            };
            optStack.Stacking.WorkflowMode = StackingWorkflowMode.StackingOnly;
            optStack.Stacking.AlignmentTileSize = 32;
            optStack.Stacking.StackPercentages = [50.0];

            var resStack = await runner.ExecuteFilesAsync([resPrep.OutputFilePath], optStack, outDirStack);
            Assert.True(resStack.Success);
            Assert.True(File.Exists(resStack.OutputFilePath));

            // Comparaison des deux images résultantes
            var imgAll = UniversalMediaReader.ReadSingleFrame(resAll.OutputFilePath, 0);
            var imgSep = UniversalMediaReader.ReadSingleFrame(resStack.OutputFilePath, 0);

            Assert.Equal(cropSize, imgAll.Width);
            Assert.Equal(cropSize, imgAll.Height);
            Assert.Equal(cropSize, imgSep.Width);
            Assert.Equal(cropSize, imgSep.Height);

            var detAll = StabilisationAndQualityEngine.DetectPlanetOrObject(imgAll, checkObject: true, autoThreshold: true, manualThreshold8Bit: 40, minObjectSizePixels: 15, shadowEdge: LunarShadowEdge.None, checkOverexposed: false, maxOverexposedPixels: 0);
            var detSep = StabilisationAndQualityEngine.DetectPlanetOrObject(imgSep, checkObject: true, autoThreshold: true, manualThreshold8Bit: 40, minObjectSizePixels: 15, shadowEdge: LunarShadowEdge.None, checkOverexposed: false, maxOverexposedPixels: 0);

            Assert.True(detAll.Accepted);
            Assert.True(detSep.Accepted);

            // Vérification du centrage : les deux modes doivent être centrés à (cropSize / 2, cropSize / 2)
            Assert.InRange(detAll.CentroidX, cropSize / 2 - 2, cropSize / 2 + 2);
            Assert.InRange(detAll.CentroidY, cropSize / 2 - 2, cropSize / 2 + 2);

            Assert.InRange(detSep.CentroidX, cropSize / 2 - 2, cropSize / 2 + 2);
            Assert.InRange(detSep.CentroidY, cropSize / 2 - 2, cropSize / 2 + 2);

            // La distance entre les centroïdes des deux modes doit être < 1.5 pixels
            double distCentroids = Math.Sqrt(Math.Pow(detAll.CentroidX - detSep.CentroidX, 2) + Math.Pow(detAll.CentroidY - detSep.CentroidY, 2));
            Assert.True(distCentroids < 1.5, $"L'écart de centroïde entre All ({detAll.CentroidX}, {detAll.CentroidY}) et Séparé ({detSep.CentroidX}, {detSep.CentroidY}) est de {distCentroids:F2}px (doit être < 1.5px)");
        }
        finally
        {
            UniversalMediaReader.ClosePreviewCache();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static AstroFrame CreatePlanetFrame(int w, int h, int channels, int cx, int cy, int r)
    {
        var frame = new AstroFrame(w, h, channels, 16);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    double normDist = Math.Sqrt(dx * dx + dy * dy) / r;
                    ushort v = (ushort)(52000 * (1.0 - 0.35 * normDist));
                    int idx = (y * w + x) * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        frame.Data[idx + c] = v;
                    }
                }
            }
        }
        return frame;
    }
}
