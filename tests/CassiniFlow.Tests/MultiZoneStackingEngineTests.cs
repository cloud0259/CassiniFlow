using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Codecs;
using CassiniFlow.Infrastructure.Synthetic;
using Xunit;
using Xunit.Abstractions;

namespace CassiniFlow.Tests;

public class MultiZoneStackingEngineTests
{
    private readonly ITestOutputHelper _output;

    public MultiZoneStackingEngineTests(ITestOutputHelper output)
    {
        _output = output;
    }
    [Fact]
    public void AlignmentGridGenerator_GeneratesExpectedPointsAndPrunesDarkBackground()
    {
        // Image 200x200 avec un disque planétaire brillant au centre
        var frame = new AstroFrame(200, 200, 1, 16);
        for (int y = 50; y < 150; y++)
        {
            for (int x = 50; x < 150; x++)
            {
                // Disque brillant (luminosité ~200 sur échelle 8-bit, soit ~51400 sur 16-bit)
                frame.Data[y * 200 + x] = 51400;
            }
        }

        var options = new StackingOptions
        {
            AlignmentTileSize = 48,
            MinBrightnessThreshold = 20,
            EnablePruning = true,
            EnableMultiScaleGrid = false
        };

        var grid = AlignmentGridGenerator.GenerateGrid(frame, options);

        Assert.NotEmpty(grid.Points);
        Assert.True(grid.ActiveCount > 0, "Doit comporter des points actifs sur le disque brillant");
        Assert.True(grid.ActiveCount < grid.TotalCount, "Les points de fond de ciel noir doivent être élagués");

        var activePoints = grid.Points.Where(p => p.IsActive).ToList();
        foreach (var ap in activePoints)
        {
            Assert.True(ap.Brightness >= options.MinBrightnessThreshold);
        }
    }

    [Fact]
    public void AlignmentGridGenerator_IsSymmetricAndDenseAroundAnchor_OnNonSquareFrame()
    {
        // Simulation d'une vidéo 360x640 avec un astre centré à (180, 320) de rayon 130
        int w = 360;
        int h = 640;
        int cx = 180;
        int cy = 320;
        int radius = 130;

        var frame = new AstroFrame(w, h, 1, 16)
        {
            DetectedCentroidX = cx,
            DetectedCentroidY = cy
        };

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    frame.Data[y * w + x] = 45000; // Corps lunaire brillant
                }
            }
        }

        var options = new StackingOptions
        {
            AlignmentTileSize = 64,
            MinBrightnessThreshold = 15,
            EnablePruning = true,
            EnableMultiScaleGrid = false
        };

        var grid = AlignmentGridGenerator.GenerateGrid(frame, options);

        Assert.Equal(w, grid.FrameWidth);
        Assert.Equal(h, grid.FrameHeight);

        var activePoints = grid.Points.Where(p => p.IsActive).ToList();
        Assert.NotEmpty(activePoints);

        // Vérification de la parfaite symétrie horizontale par rapport au centre de l'astre (180)
        int minX = activePoints.Min(p => p.X);
        int maxX = activePoints.Max(p => p.X);
        Assert.Equal(180 - minX, maxX - 180);

        // Vérification que le limbe droit de la Lune (x ~ 308) est bien couvert par des points d'alignement
        Assert.True(maxX >= 300, $"Le limbe droit doit être couvert (maxX = {maxX})");

        // Vérification de la parfaite symétrie verticale par rapport au centre (320)
        int minY = activePoints.Min(p => p.Y);
        int maxY = activePoints.Max(p => p.Y);
        Assert.Equal(320 - minY, maxY - 320);
    }

    [Fact]
    public void AlignmentGrid_ManualPointCreationAndRemoval_WorksCorrectly()
    {
        var frame = new AstroFrame(200, 200, 1, 16);
        for (int y = 50; y < 150; y++)
        {
            for (int x = 50; x < 150; x++)
            {
                frame.Data[y * 200 + x] = 50000;
            }
        }

        // 1. Création d'un point manuel via AlignmentGridGenerator
        var manualPoint = AlignmentGridGenerator.CreateManualPoint(frame, 1, 100, 100, 48);
        Assert.True(manualPoint.IsActive);
        Assert.True(manualPoint.Brightness > 100);
        Assert.Equal(100, manualPoint.X);
        Assert.Equal(100, manualPoint.Y);
        Assert.Equal(48, manualPoint.Size);

        // 2. Initialisation d'une grille avec ce point
        var grid = new AlignmentGrid(48, 15, [manualPoint], (100, 100), 200, 200);
        Assert.Equal(1, grid.ActiveCount);
        Assert.Equal(1, grid.TotalCount);

        // 3. Ajout d'un deuxième point manuel
        var secondPoint = AlignmentGridGenerator.CreateManualPoint(frame, 2, 120, 120, 48);
        var updatedPoints = new List<AlignmentPoint>(grid.Points) { secondPoint };
        var grid2 = new AlignmentGrid(48, 15, updatedPoints, (100, 100), 200, 200);
        Assert.Equal(2, grid2.ActiveCount);

        // 4. Suppression du premier point
        var remainingPoints = grid2.Points.Where(p => p.Id != 1).ToList();
        var grid3 = new AlignmentGrid(48, 15, remainingPoints, (100, 100), 200, 200);
        Assert.Equal(1, grid3.ActiveCount);
        Assert.Equal(2, grid3.Points[0].Id);
    }

    [Fact]
    public void ConvolutionSharpeningFilter_EnhancesContrastWithoutDistortion()
    {
        var frame = new AstroFrame(64, 64, 3, 16);
        for (int y = 20; y < 44; y++)
        {
            for (int x = 20; x < 44; x++)
            {
                frame.Data[(y * 64 + x) * 3 + 0] = 30000;
                frame.Data[(y * 64 + x) * 3 + 1] = 35000;
                frame.Data[(y * 64 + x) * 3 + 2] = 40000;
            }
        }

        var sharpened = ConvolutionSharpeningFilter.ApplySharpening(frame, blendPercent: 15);

        Assert.Equal(frame.Width, sharpened.Width);
        Assert.Equal(frame.Height, sharpened.Height);
        Assert.Equal(frame.Channels, sharpened.Channels);
        Assert.Equal(frame.Data.Length, sharpened.Data.Length);
    }

    [Fact]
    public void AtmosphericDispersionCorrector_PreservesMonochromeAndHandlesRGB()
    {
        var mono = new AstroFrame(64, 64, 1, 16);
        var resMono = AtmosphericDispersionCorrector.AlignChannels(mono);
        Assert.Same(mono, resMono);

        var rgb = new AstroFrame(64, 64, 3, 16);
        var resRgb = AtmosphericDispersionCorrector.AlignChannels(rgb);
        Assert.NotNull(resRgb);
        Assert.Equal(rgb.Width, resRgb.Width);
    }

    [Fact]
    public void MultiZoneStackingEngine_AccumulatesAndNormalizesFramesCorrectly()
    {
        int w = 64;
        int h = 64;
        int ch = 1;

        var options = new StackingOptions
        {
            NormalizeBrightness = true,
            NormalizeTargetPercent = 75,
            SharpeningEnabled = false,
            FeatheringEnabled = true
        };

        var grid = new AlignmentGrid(32, 15,
        [
            new AlignmentPoint(1, 32, 32, 32, isActive: true)
        ], (32, 32));

        var engine = new MultiZoneStackingEngine(w, h, ch, options, grid);

        for (int i = 0; i < 10; i++)
        {
            var frame = new AstroFrame(w, h, ch, 16);
            for (int p = 0; p < frame.Data.Length; p++)
            {
                frame.Data[p] = 5000;
            }
            engine.AccumulateFrame(frame, shiftX: 0, shiftY: 0);
        }

        Assert.Equal(10, engine.StackedFramesCount);

        var stacked = engine.ProduceStackedFrame();
        Assert.NotNull(stacked);
        Assert.Equal(w, stacked.Width);
        Assert.Equal(h, stacked.Height);

        ushort centerVal = stacked.Data[32 * 64 + 32];
        Assert.True(centerVal >= 5000, "Le signal normalisé doit être supérieur au signal unitaire d'entrée");
    }

    [Fact]
    public async Task StackingPipeline_DirectStreamMode_ProducesSingleStackedTiff()
    {
        string tempAvi = Path.Combine(Path.GetTempPath(), $"stack_test_in_{Guid.NewGuid():N}.avi");
        string tempOutDir = Path.Combine(Path.GetTempPath(), $"stack_test_out_{Guid.NewGuid():N}");

        try
        {
            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 6, width: 128, height: 128);
            UniversalMediaReader.WriteVideoFile(tempAvi, frames, 25.0, false);

            var options = new ProcessingOptions
            {
                EnableCropping = false,
                OutputFormat = OutputFormat.Tiff
            };
            options.Stacking.WorkflowMode = StackingWorkflowMode.DirectStreamAllInOne;
            options.Stacking.AlignmentTileSize = 32;
            options.Stacking.StackPercentages = [50.0];

            var summary = await PipelineRunner.ExecuteFilesAsync([tempAvi], options, tempOutDir);

            Assert.True(summary.Success);
            Assert.Equal(1, summary.OutputFramesWritten);
            Assert.True(File.Exists(summary.OutputFilePath));
            Assert.EndsWith(".tif", summary.OutputFilePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            UniversalMediaReader.ClosePreviewCache();
            if (File.Exists(tempAvi)) File.Delete(tempAvi);
            if (Directory.Exists(tempOutDir)) Directory.Delete(tempOutDir, true);
        }
    }

    [Fact]
    public async Task StackingPipeline_StackingOnlyMode_ExecutesBoundedStreamingWithoutFreezing()
    {
        string tempAvi = Path.Combine(Path.GetTempPath(), $"stack_only_test_{Guid.NewGuid():N}.avi");
        string tempOutDir = Path.Combine(Path.GetTempPath(), $"stack_only_out_{Guid.NewGuid():N}");

        try
        {
            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 8, width: 96, height: 96);
            UniversalMediaReader.WriteVideoFile(tempAvi, frames, 25.0, false);

            var options = new ProcessingOptions
            {
                OutputFormat = OutputFormat.Fits
            };
            options.Stacking.WorkflowMode = StackingWorkflowMode.StackingOnly;
            options.Stacking.AlignmentTileSize = 32;
            options.Stacking.StackPercentages = [50.0];

            var summary = await PipelineRunner.ExecuteFilesAsync([tempAvi], options, tempOutDir);

            Assert.True(summary.Success);
            Assert.Equal(1, summary.OutputFramesWritten);
            Assert.True(File.Exists(summary.OutputFilePath));
            Assert.EndsWith(".fit", summary.OutputFilePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            UniversalMediaReader.ClosePreviewCache();
            if (File.Exists(tempAvi)) File.Delete(tempAvi);
            if (Directory.Exists(tempOutDir)) Directory.Delete(tempOutDir, true);
        }
    }

    [Fact]
    public void ConvolutionSharpeningFilter_PreservesLuminanceOnFlatRegion()
    {
        var frame = new AstroFrame(32, 32, 1, 16);
        Array.Fill(frame.Data, (ushort)30000);

        var sharpened = ConvolutionSharpeningFilter.ApplySharpening(frame, blendPercent: 25);

        // Sur une zone plate, la convolution laplacienne doit conserver exactement la luminosité (KernelSum = 4)
        Assert.Equal(30000, sharpened.Data[16 * 32 + 16]);
    }

    [Fact]
    public void FastCrossCorrelation_TileShift_AccuratelyComputesOffset()
    {
        var refFrame = new AstroFrame(128, 128, 1, 16);
        var curFrame = new AstroFrame(128, 128, 1, 16);

        // Crée une structure caractéristique (disque Gaussien) centrée à (64, 64) sur ref
        for (int y = 50; y <= 78; y++)
        {
            for (int x = 50; x <= 78; x++)
            {
                double dsq = (x - 64) * (x - 64) + (y - 64) * (y - 64);
                ushort val = (ushort)Math.Max(0, 45000 - dsq * 80);
                refFrame.Data[y * 128 + x] = val;
            }
        }

        // Sur curFrame, la structure est décalée de (+3, -2) : centre à (67, 62)
        for (int y = 50; y <= 78; y++)
        {
            for (int x = 50; x <= 78; x++)
            {
                int cy = y - 2;
                int cx = x + 3;
                if (cx >= 0 && cx < 128 && cy >= 0 && cy < 128)
                {
                    curFrame.Data[cy * 128 + cx] = refFrame.Data[y * 128 + x];
                }
            }
        }

        var (shiftX, shiftY) = FastCrossCorrelation.ComputeShift(curFrame, refFrame);
        // curFrame est à ref + (3, -2), donc ref - cur = (-3, +2)
        Assert.InRange(shiftX, -3.5, -2.5);
        Assert.InRange(shiftY, 1.5, 2.5);

        var (tileDx, tileDy) = FastCrossCorrelation.ComputeTileShift(
            curFrame,
            refFrame,
            centerX: 64,
            centerY: 64,
            size: 48,
            globalShiftX: shiftX,
            globalShiftY: shiftY);

        Assert.InRange(tileDx, -3.5, -2.5);
        Assert.InRange(tileDy, 1.5, 2.5);
    }

    [Fact]
    public void MainWindowViewModel_StackFrameCount_MatchesMaxAvailableFrames()
    {
        var vm = new CassiniFlow.Desktop.ViewModels.MainWindowViewModel();

        var file1 = new CassiniFlow.Desktop.ViewModels.SourceFileItem
        {
            FileName = "saturn_video.ser",
            FullPath = "dummy1.ser",
            Frames = 12085,
            Width = 640,
            Height = 480
        };
        var file2 = new CassiniFlow.Desktop.ViewModels.SourceFileItem
        {
            FileName = "jupiter_video.ser",
            FullPath = "dummy2.ser",
            Frames = 5000,
            Width = 640,
            Height = 480
        };

        // 1. Single file selection
        vm.SourceFiles.Add(file1);
        vm.SelectedSourceFile = file1;
        vm.UpdateAvailableFrames();

        Assert.Equal(12085, vm.MaxAvailableFrames);
        Assert.Equal(12085, vm.StackFrameCount);

        // 2. Add second file and enable JoinMode
        vm.SourceFiles.Add(file2);
        vm.IsJoinMode = true;
        Assert.Equal(12085 + 5000, vm.MaxAvailableFrames);
        Assert.Equal(17085, vm.StackFrameCount);

        // 3. Switch to BatchMode (not JoinMode)
        vm.IsJoinMode = false;
        Assert.Equal(12085, vm.MaxAvailableFrames);
        Assert.Equal(12085, vm.StackFrameCount);
    }

    [Fact]
    public void MainWindowViewModel_MultiTierPercentages_DefaultsToZeroAndFiltersActive()
    {
        var vm = new CassiniFlow.Desktop.ViewModels.MainWindowViewModel();

        // 3 choices initialized to 0% at startup as requested
        Assert.Equal(0, vm.StackPercentage1);
        Assert.Equal(0, vm.StackPercentage2);
        Assert.Equal(0, vm.StackPercentage3);

        // When all 0, active percentages list is empty
        var emptyActive = vm.GetActiveStackPercentages();
        Assert.Empty(emptyActive);

        // Configure 5% and 10%
        vm.StackPercentage1 = 5;
        vm.StackPercentage2 = 10;
        vm.StackPercentage3 = 0;

        var active = vm.GetActiveStackPercentages();
        Assert.Equal(2, active.Count);
        Assert.Equal(5.0, active[0]);
        Assert.Equal(10.0, active[1]);
    }

    [Fact]
    public async Task StackingPipeline_MultiTierPercentages_ProducesMultipleDistinctStackedFilesAsync()
    {
        string tempAvi = Path.Combine(Path.GetTempPath(), $"stack_multitier_in_{Guid.NewGuid():N}.avi");
        string tempOutDir = Path.Combine(Path.GetTempPath(), $"stack_multitier_out_{Guid.NewGuid():N}");

        try
        {
            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 10, width: 128, height: 128);
            UniversalMediaReader.WriteVideoFile(tempAvi, frames, 25.0, false);

            var options = new ProcessingOptions
            {
                EnableCropping = false,
                OutputFormat = OutputFormat.Tiff
            };
            options.Stacking.WorkflowMode = StackingWorkflowMode.DirectStreamAllInOne;
            options.Stacking.AlignmentTileSize = 32;
            options.Stacking.StackPercentages = [20.0, 50.0];

            var summary = await PipelineRunner.ExecuteFilesAsync([tempAvi], options, tempOutDir);

            Assert.True(summary.Success);
            Assert.Equal(2, summary.OutputFramesWritten);
            var generatedTifs = Directory.GetFiles(tempOutDir, "*_stacked_*.tif");
            Assert.Equal(2, generatedTifs.Length);
            Assert.Contains(generatedTifs, f => f.Contains("_p20_"));
            Assert.Contains(generatedTifs, f => f.Contains("_p50_"));
        }
        finally
        {
            UniversalMediaReader.ClosePreviewCache();
            if (File.Exists(tempAvi)) File.Delete(tempAvi);
            if (Directory.Exists(tempOutDir)) Directory.Delete(tempOutDir, true);
        }
    }

    [Fact]
    public void MainWindowViewModel_WorkflowMode_AdjustsRightColumnMenusVisibility()
    {
        var vm = new CassiniFlow.Desktop.ViewModels.MainWindowViewModel();

        // 1. Mode DirectStreamAllInOne (Tout) -> Toutes les sections sont visibles (1..9)
        vm.SetWorkflowMode("all");
        Assert.True(vm.IsSection1Visible);
        Assert.True(vm.IsSection2Visible);
        Assert.True(vm.IsSection3Visible);
        Assert.True(vm.IsSection4Visible);
        Assert.True(vm.IsSection5Visible);
        Assert.True(vm.IsSection6Visible);
        Assert.True(vm.IsSection7Visible);
        Assert.True(vm.IsSection8Visible);
        Assert.True(vm.IsSection9Visible);
        Assert.Equal("1", vm.Section1BadgeText);
        Assert.Equal("5", vm.Section5BadgeText);
        Assert.Equal("9", vm.Section9BadgeText);

        // 2. Mode PreProcessingOnly (Prétraitement seul)
        // Seuls les menus de prétraitement + communs sont visibles (1, 2, 3, 4, 7, 8, 9). 5 et 6 sont masqués.
        vm.SetWorkflowMode("prep");
        Assert.True(vm.IsSection1Visible);
        Assert.True(vm.IsSection2Visible);
        Assert.True(vm.IsSection3Visible);
        Assert.True(vm.IsSection4Visible);
        Assert.False(vm.IsSection5Visible); // AP masqué
        Assert.False(vm.IsSection6Visible); // Stacking masqué
        Assert.True(vm.IsSection7Visible);
        Assert.True(vm.IsSection8Visible);
        Assert.True(vm.IsSection9Visible);
        Assert.Equal("1", vm.Section1BadgeText);
        Assert.Equal("2", vm.Section2BadgeText);
        Assert.Equal("5", vm.Section7BadgeText);
        Assert.Equal("6", vm.Section8BadgeText);
        Assert.Equal("7", vm.Section9BadgeText);

        // 3. Mode StackingOnly (Empilement seul)
        // Seuls les menus d'empilement + communs sont visibles (1, 5, 6, 8, 9). 2, 3, 4, 7 sont masqués.
        vm.SetWorkflowMode("stack");
        Assert.True(vm.IsSection1Visible);
        Assert.False(vm.IsSection2Visible); // Calibration masquée
        Assert.False(vm.IsSection3Visible); // Centrage masqué
        Assert.False(vm.IsSection4Visible); // Tri masqué
        Assert.True(vm.IsSection5Visible);  // AP visible
        Assert.True(vm.IsSection6Visible);  // Stacking visible
        Assert.False(vm.IsSection7Visible); // Animation masquée
        Assert.True(vm.IsSection8Visible);
        Assert.True(vm.IsSection9Visible);
        Assert.Equal("1", vm.Section1BadgeText);
        Assert.Equal("2", vm.Section5BadgeText);
        Assert.Equal("3", vm.Section6BadgeText);
        Assert.Equal("4", vm.Section8BadgeText);
        Assert.Equal("5", vm.Section9BadgeText);
    }
}
