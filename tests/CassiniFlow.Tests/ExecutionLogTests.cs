using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using Xunit;

namespace CassiniFlow.Tests;

public class ExecutionLogTests
{
    [Fact]
    public void ExecutionLogGenerator_Generate_ContainsAllEssentialSections()
    {
        // Arrange
        var options = new ProcessingOptions
        {
            StabilisationMode = StabilisationMode.ObjectPlanetary,
            CheckPlanetObjectPresent = true,
            EnableCropping = true,
            CropWidth = 320,
            CropHeight = 240,
            DebayerEnabled = true,
            DebayerPattern = BayerPattern.Rggb,
            EnableQualityEstimation = true,
            QualityAlgorithm = QualityAlgorithm.GradientSharpness,
            QualityFramesToKeep = 50,
            OutputFormat = OutputFormat.Ser,
            GenerateWinJuposFilenames = true
        };

        var summary = new PipelineRunSummary(
            Success: true,
            Elapsed: TimeSpan.FromSeconds(12.345),
            TotalInputFrames: 1000,
            OutputFramesWritten: 500,
            DiscardedNoPlanet: 50,
            DiscardedOverexposed: 10,
            DiscardedQuality: 440,
            OutputFilePath: @"C:\Astro\output\jupiter_pipp.ser",
            LogFilePath: @"C:\Astro\output\jupiter_pipp_log.txt");

        var sourceFiles = new[] { @"C:\Astro\raw\jupiter_001.ser", @"C:\Astro\raw\jupiter_002.ser" };

        // Act
        string log = ExecutionLogGenerator.Generate(
            options,
            summary,
            sourceFiles,
            timestampUtc: new DateTime(2026, 10, 29, 20, 0, 0, DateTimeKind.Utc),
            detectedTarget: "Jupiter");

        // Assert
        Assert.NotNull(log);
        Assert.Contains("CASSINIFLOW EXECUTION LOG", log);
        Assert.Contains("2026-10-29 20:00:00 UTC", log);
        Assert.Contains("SUCCESS", log);
        Assert.Contains("Total Input Frames   : 1000", log);
        Assert.Contains("Output Frames Written: 500", log);
        Assert.Contains("Discarded (No Object): 50", log);
        Assert.Contains("Discarded (Overexp.) : 10", log);
        Assert.Contains("Discarded (Quality)  : 440", log);
        Assert.Contains(@"C:\Astro\raw\jupiter_001.ser", log);
        Assert.Contains(@"C:\Astro\raw\jupiter_002.ser", log);
        Assert.Contains("ObjectPlanetary", log);
        Assert.Contains("Target Object      : Jupiter", log);
        Assert.Contains("Dimensions         : 320 x 240 px", log);
        Assert.Contains("Rggb", log);
        Assert.Contains("GradientSharpness", log);
        Assert.Contains(@"C:\Astro\output\jupiter_pipp.ser", log);
        Assert.Contains(@"C:\Astro\output\jupiter_pipp_log.txt", log);
    }

    [Fact]
    public async Task PipelineRunner_ExecuteAsync_GeneratesLogFileAlongsideOutput()
    {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), "cassiniflow_log_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create dummy test frame
            var frame = new AstroFrame(width: 64, height: 64, channels: 3);

            var options = new ProcessingOptions
            {
                EnableCropping = false,
                CheckPlanetObjectPresent = false,
                EnableQualityEstimation = false,
                OutputFormat = OutputFormat.Ser
            };

            // Act
            var summary = await PipelineRunner.ExecuteAsync(new[] { frame }, options, tempDir);

            // Assert
            Assert.True(summary.Success);
            Assert.True(File.Exists(summary.OutputFilePath), "Output file should exist");
            Assert.False(string.IsNullOrWhiteSpace(summary.LogFilePath), "Log file path should be set in summary");
            Assert.True(File.Exists(summary.LogFilePath), "Log file should exist on disk");
            Assert.EndsWith("_log.txt", summary.LogFilePath);

            string logContent = await File.ReadAllTextAsync(summary.LogFilePath);
            Assert.Contains("CASSINIFLOW EXECUTION LOG", logContent);
            Assert.Contains("Total Input Frames   : 1", logContent);
            Assert.Contains("Output Frames Written: 1", logContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
