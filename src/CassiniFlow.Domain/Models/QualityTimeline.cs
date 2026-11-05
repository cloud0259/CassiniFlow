using System;
using System.Collections.Generic;
using System.Linq;

namespace CassiniFlow.Domain.Models;

/// <summary>
/// Turbulence severity level evaluated from seeing and score variance.
/// </summary>
public enum TurbulenceLevel
{
    Low = 0,      // Calm seeing, optimal sharpness (green)
    Moderate = 1, // Medium stability (yellow/orange)
    High = 2      // Severe turbulence, blurry or drifting (red)
}

/// <summary>
/// Represents a contiguous sequence of frames exhibiting a common turbulence level.
/// </summary>
public readonly record struct TurbulenceZone(int StartFrame, int EndFrame, TurbulenceLevel Level);

/// <summary>
/// Individual quality score and rank for a single frame.
/// </summary>
public readonly record struct FrameQualityRecord(
    int FrameIndex,
    double RawScore,
    double NormalizedScore, // 0.0 to 100.0%
    int Rank                // 1-based rank (1 = best frame in recording)
);

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

/// <summary>
/// Domain service providing calculations for score normalization, moving average seeing curves,
/// and turbulence segment detection.
/// </summary>
public static class QualityTimelineAnalyzer
{
    /// <summary>
    /// Normalizes raw scores to 0-100% and computes 1-based ranks.
    /// </summary>
    public static FrameQualityRecord[] NormalizeAndRank(IReadOnlyList<(int FrameIndex, double RawScore)> rawRecords)
    {
        if (rawRecords == null || rawRecords.Count == 0)
        {
            return Array.Empty<FrameQualityRecord>();
        }

        int count = rawRecords.Count;
        double minScore = double.MaxValue;
        double maxScore = double.MinValue;

        for (int i = 0; i < count; i++)
        {
            double s = rawRecords[i].RawScore;
            if (s < minScore) minScore = s;
            if (s > maxScore) maxScore = s;
        }

        double range = maxScore - minScore;
        if (range <= 1e-9) range = 1.0;

        // Temporary array for ranking
        var sortable = new (int OriginalIndex, int FrameIndex, double Raw, double Normalized)[count];
        for (int i = 0; i < count; i++)
        {
            var item = rawRecords[i];
            double norm = Math.Clamp((item.RawScore - minScore) / range * 100.0, 0.0, 100.0);
            sortable[i] = (i, item.FrameIndex, item.RawScore, norm);
        }

        // Sort descending by raw score to determine ranks
        Array.Sort(sortable, static (a, b) => b.Raw.CompareTo(a.Raw));

        var results = new FrameQualityRecord[count];
        for (int rank = 0; rank < count; rank++)
        {
            var item = sortable[rank];
            results[item.OriginalIndex] = new FrameQualityRecord(
                FrameIndex: item.FrameIndex,
                RawScore: item.Raw,
                NormalizedScore: item.Normalized,
                Rank: rank + 1
            );
        }

        return results;
    }

    /// <summary>
    /// Computes a centered moving average over the normalized scores.
    /// </summary>
    public static double[] ComputeMovingAverage(FrameQualityRecord[] records, int windowSize = 11)
    {
        if (records == null || records.Length == 0) return Array.Empty<double>();

        int n = records.Length;
        var smoothed = new double[n];
        int half = Math.Max(1, windowSize / 2);

        for (int i = 0; i < n; i++)
        {
            int start = Math.Max(0, i - half);
            int end = Math.Min(n - 1, i + half);
            double sum = 0.0;
            int count = end - start + 1;
            for (int k = start; k <= end; k++)
            {
                sum += records[k].NormalizedScore;
            }
            smoothed[i] = sum / count;
        }

        return smoothed;
    }

    /// <summary>
    /// Detects contiguous zones of calm seeing, moderate seeing, and high turbulence.
    /// Uses local score levels and score variance over chunks of frames.
    /// </summary>
    public static List<TurbulenceZone> DetectTurbulenceZones(FrameQualityRecord[] records, int chunkDuration = 20)
    {
        var zones = new List<TurbulenceZone>();
        if (records == null || records.Length == 0) return zones;

        int total = records.Length;
        int step = Math.Max(5, Math.Min(chunkDuration, total / 20));

        TurbulenceLevel currentLevel = TurbulenceLevel.Moderate;
        int zoneStart = 0;

        for (int i = 0; i < total; i += step)
        {
            int end = Math.Min(total, i + step);
            double sumNorm = 0.0;
            for (int k = i; k < end; k++)
            {
                sumNorm += records[k].NormalizedScore;
            }
            double avgNorm = sumNorm / (end - i);

            // Classification thresholds:
            // > 65% normalized sharpness = Low turbulence (calm seeing)
            // < 40% normalized sharpness = High turbulence
            // in between = Moderate
            TurbulenceLevel level = avgNorm switch
            {
                >= 65.0 => TurbulenceLevel.Low,
                <= 40.0 => TurbulenceLevel.High,
                _ => TurbulenceLevel.Moderate
            };

            if (i == 0)
            {
                currentLevel = level;
                zoneStart = 0;
            }
            else if (level != currentLevel)
            {
                zones.Add(new TurbulenceZone(zoneStart, i - 1, currentLevel));
                currentLevel = level;
                zoneStart = i;
            }
        }

        zones.Add(new TurbulenceZone(zoneStart, total - 1, currentLevel));
        return zones;
    }
}

