using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Outbound;

namespace CassiniFlow.Tests;

public class QualityTimelineTests
{
    [Fact]
    public void NormalizeAndRank_CalculatesRanksAndPercentageCorrectly()
    {
        var raw = new List<(int FrameIndex, double RawScore)>
        {
            (0, 10.0),
            (1, 50.0),
            (2, 20.0),
            (3, 100.0),
            (4, 5.0)
        };

        var records = QualityTimelineAnalyzer.NormalizeAndRank(raw);

        Assert.Equal(5, records.Length);

        // Frame 3 has the highest score (100.0) -> Rank 1, 100% normalized
        Assert.Equal(1, records[3].Rank);
        Assert.Equal(100.0, records[3].NormalizedScore, precision: 2);

        // Frame 4 has the lowest score (5.0) -> Rank 5, 0% normalized
        Assert.Equal(5, records[4].Rank);
        Assert.Equal(0.0, records[4].NormalizedScore, precision: 2);

        // Frame 1 is second best (50.0) -> Rank 2
        Assert.Equal(2, records[1].Rank);
        Assert.True(records[1].NormalizedScore > records[2].NormalizedScore);
    }

    [Fact]
    public void ComputeMovingAverage_SmoothsHighFrequencySpikes()
    {
        var records = new FrameQualityRecord[10];
        for (int i = 0; i < 10; i++)
        {
            // Alternating scores
            double score = (i % 2 == 0) ? 80.0 : 20.0;
            records[i] = new FrameQualityRecord(i, score, score, i + 1);
        }

        var smoothed = QualityTimelineAnalyzer.ComputeMovingAverage(records, windowSize: 3);

        Assert.Equal(10, smoothed.Length);
        // Middle values should hover around 50%
        for (int i = 1; i < 9; i++)
        {
            Assert.InRange(smoothed[i], 35.0, 65.0);
        }
    }

    [Fact]
    public void DetectTurbulenceZones_IdentifiesCalmAndTurbulentPeriods()
    {
        var records = new FrameQualityRecord[60];
        // 0..19: Very calm seeing (score 85)
        for (int i = 0; i < 20; i++) records[i] = new FrameQualityRecord(i, 85, 85, i + 1);
        // 20..39: Turbulent burst (score 25)
        for (int i = 20; i < 40; i++) records[i] = new FrameQualityRecord(i, 25, 25, i + 1);
        // 40..59: Moderate seeing (score 55)
        for (int i = 40; i < 60; i++) records[i] = new FrameQualityRecord(i, 55, 55, i + 1);

        var zones = QualityTimelineAnalyzer.DetectTurbulenceZones(records, chunkDuration: 10);

        Assert.NotEmpty(zones);
        Assert.Contains(zones, z => z.Level == TurbulenceLevel.Low);
        Assert.Contains(zones, z => z.Level == TurbulenceLevel.High);
    }

    [Fact]
    public void QualityTimelineDataSet_StoresMultipleAlgorithmsAndSorts()
    {
        var dataset = new QualityTimelineDataSet("test.ser", 3, QualityAlgorithm.GradientSharpness);

        var gradRecords = new[]
        {
            new FrameQualityRecord(0, 10.0, 20.0, 3),
            new FrameQualityRecord(1, 50.0, 100.0, 1),
            new FrameQualityRecord(2, 30.0, 60.0, 2)
        };

        dataset.SetAlgorithmScores(QualityAlgorithm.GradientSharpness, gradRecords);

        Assert.True(dataset.HasScoresFor(QualityAlgorithm.GradientSharpness));
        Assert.False(dataset.HasScoresFor(QualityAlgorithm.TotalBrightness));
        Assert.Equal(1, dataset.BestFrameIndex); // Frame 1 is Rank 1

        var sorted = dataset.GetSortedScores(QualityAlgorithm.GradientSharpness);
        Assert.NotNull(sorted);
        Assert.Equal(1, sorted![0].FrameIndex);
        Assert.Equal(2, sorted[1].FrameIndex);
        Assert.Equal(0, sorted[2].FrameIndex);
    }

    private sealed class FakeQualityScanner : IQualityTimelineScanner
    {
        public Task<IReadOnlyList<(int FrameIndex, double RawScore)>> ScanFileScoresAsync(
            string filePath,
            QualityAlgorithm algorithm,
            ProcessingOptions options,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<(int, double)> data = algorithm switch
            {
                QualityAlgorithm.GradientSharpness => new List<(int, double)> { (0, 10.0), (1, 80.0), (2, 40.0) },
                _ => new List<(int, double)> { (0, 100.0), (1, 90.0), (2, 85.0) }
            };
            progress?.Report(100.0);
            return Task.FromResult(data);
        }
    }

    [Fact]
    public async Task QualityTimelineUseCase_CachesAndLoadsAdditionalAlgorithms()
    {
        var scanner = new FakeQualityScanner();
        var useCase = new QualityTimelineUseCase(scanner);
        var options = new ProcessingOptions();

        var dataset = await useCase.AnalyzeTimelineAsync("dummy.ser", QualityAlgorithm.GradientSharpness, options);

        Assert.NotNull(dataset);
        Assert.True(dataset.HasScoresFor(QualityAlgorithm.GradientSharpness));
        Assert.False(dataset.HasScoresFor(QualityAlgorithm.PeakHistogram));

        // On-demand computation of additional algorithm
        await useCase.ComputeAdditionalAlgorithmAsync(dataset, QualityAlgorithm.PeakHistogram, options);

        Assert.True(dataset.HasScoresFor(QualityAlgorithm.PeakHistogram));
    }
}

