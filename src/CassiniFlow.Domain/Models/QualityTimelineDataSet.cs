using System;
using System.Collections.Generic;

namespace CassiniFlow.Domain.Models;

/// <summary>
/// Complete dataset for the quality timeline, holding evaluated scores across multiple algorithms,
/// turbulence zones, and ranking lookups.
/// </summary>
public class QualityTimelineDataSet
{
    public string FilePath { get; }
    public int TotalFrames { get; }
    public QualityAlgorithm PrimaryAlgorithm { get; set; }

    private readonly Dictionary<QualityAlgorithm, FrameQualityRecord[]> _scoresByAlgo = new();
    private readonly Dictionary<QualityAlgorithm, FrameQualityRecord[]> _sortedScoresByAlgo = new();
    private readonly Dictionary<QualityAlgorithm, double[]> _smoothedScoresByAlgo = new();
    private readonly List<TurbulenceZone> _turbulenceZones = new();

    public IReadOnlyList<TurbulenceZone> TurbulenceZones => _turbulenceZones;
    public int BestFrameIndex { get; private set; } = 0;

    public QualityTimelineDataSet(string filePath, int totalFrames, QualityAlgorithm primaryAlgorithm)
    {
        FilePath = filePath ?? string.Empty;
        TotalFrames = Math.Max(0, totalFrames);
        PrimaryAlgorithm = primaryAlgorithm;
    }

    public void SetAlgorithmScores(QualityAlgorithm algorithm, FrameQualityRecord[] records, bool updateTurbulence = false)
    {
        ArgumentNullException.ThrowIfNull(records);
        _scoresByAlgo[algorithm] = records;

        // Precompute sorted by quality descending
        var sorted = new FrameQualityRecord[records.Length];
        Array.Copy(records, sorted, records.Length);
        Array.Sort(sorted, static (a, b) => b.NormalizedScore.CompareTo(a.NormalizedScore));
        _sortedScoresByAlgo[algorithm] = sorted;

        // Precompute smoothed seeing curve (moving average window 11)
        _smoothedScoresByAlgo[algorithm] = QualityTimelineAnalyzer.ComputeMovingAverage(records, windowSize: 11);

        if (algorithm == PrimaryAlgorithm && sorted.Length > 0)
        {
            BestFrameIndex = sorted[0].FrameIndex;
        }

        if (updateTurbulence || algorithm == PrimaryAlgorithm)
        {
            _turbulenceZones.Clear();
            _turbulenceZones.AddRange(QualityTimelineAnalyzer.DetectTurbulenceZones(records));
        }
    }

    public bool HasScoresFor(QualityAlgorithm algorithm) => _scoresByAlgo.ContainsKey(algorithm);

    public FrameQualityRecord[]? GetScores(QualityAlgorithm algorithm) =>
        _scoresByAlgo.TryGetValue(algorithm, out var recs) ? recs : null;

    public FrameQualityRecord[]? GetSortedScores(QualityAlgorithm algorithm) =>
        _sortedScoresByAlgo.TryGetValue(algorithm, out var recs) ? recs : null;

    public double[]? GetSmoothedScores(QualityAlgorithm algorithm) =>
        _smoothedScoresByAlgo.TryGetValue(algorithm, out var smoothed) ? smoothed : null;

    public FrameQualityRecord? GetFrameRecord(QualityAlgorithm algorithm, int frameIndex)
    {
        if (_scoresByAlgo.TryGetValue(algorithm, out var recs) && frameIndex >= 0 && frameIndex < recs.Length)
        {
            return recs[frameIndex];
        }
        return null;
    }
}
