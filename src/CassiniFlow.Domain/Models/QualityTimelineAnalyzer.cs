using System;
using System.Collections.Generic;

namespace CassiniFlow.Domain.Models;

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
