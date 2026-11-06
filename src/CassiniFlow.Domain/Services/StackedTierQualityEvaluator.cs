using System;
using System.Collections.Generic;
using System.Linq;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Domain.Services;

public static class StackedTierQualityEvaluator
{
    public static double EvaluateScore(AstroFrame frame)
    {
        if (frame.Width < 8 || frame.Height < 8 || frame.Data.Length == 0) return 0.0;

        // 1. High-frequency detail / gradient sharpness score
        double rawGrad = StabilisationAndQualityEngine.ComputeQualityScore(frame, QualityAlgorithm.GradientSharpness, noiseRobust: 2);

        // 2. Contrast & dynamic range
        double contrast = ComputeContrastScore(frame);

        // 3. Combined score
        double combined = (rawGrad * 0.65) + (contrast * 0.35);
        return Math.Round(Math.Clamp(combined, 0.0, 100.0), 1);
    }

    private static double ComputeContrastScore(AstroFrame frame)
    {
        int len = frame.Data.Length;
        if (len == 0) return 0.0;

        long sum = 0;
        for (int i = 0; i < len; i++) sum += frame.Data[i];
        double mean = (double)sum / len;

        double sumSquares = 0;
        for (int i = 0; i < len; i++)
        {
            double diff = frame.Data[i] - mean;
            sumSquares += diff * diff;
        }
        double stdDev = Math.Sqrt(sumSquares / len);

        return Math.Clamp((stdDev / 15000.0) * 100.0, 0.0, 100.0);
    }

    public static List<StackedTierComparisonItem> EvaluateTiers(
        IReadOnlyList<(StackedTierSummary Summary, AstroFrame Frame)> tiers)
    {
        if (tiers == null || tiers.Count == 0) return [];

        var items = new List<StackedTierComparisonItem>(tiers.Count);
        double maxScore = -1.0;
        int bestIdx = 0;

        for (int i = 0; i < tiers.Count; i++)
        {
            var (summary, frame) = tiers[i];
            double score = EvaluateScore(frame);

            var item = new StackedTierComparisonItem
            {
                Percentage = summary.Percentage,
                FramesStacked = summary.FramesStacked,
                TotalValidFrames = summary.TotalValidFrames,
                ActiveApCount = summary.ActiveApCount,
                TileSize = summary.TileSize,
                OutputFilePath = summary.OutputFilePath,
                FileName = System.IO.Path.GetFileName(summary.OutputFilePath),
                QualityScore = score,
                TierLabel = $"{summary.Percentage:F0}%",
                SummaryText = $"{summary.FramesStacked} / {summary.TotalValidFrames} frames ({summary.Percentage:F0}%)"
            };

            items.Add(item);

            if (score > maxScore)
            {
                maxScore = score;
                bestIdx = i;
            }
        }

        if (items.Count > 0)
        {
            items[bestIdx].IsBest = true;
            items[bestIdx].BadgeText = "⭐ MEILLEUR RENDU";
        }

        return items;
    }
}
