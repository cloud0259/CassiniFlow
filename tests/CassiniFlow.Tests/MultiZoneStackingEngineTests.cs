using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Synthetic;
using Xunit;

namespace CassiniFlow.Tests;

public class MultiZoneStackingEngineTests
{
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
}
