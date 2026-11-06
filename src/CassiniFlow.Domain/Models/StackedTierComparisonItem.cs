namespace CassiniFlow.Domain.Models;

public sealed class StackedTierComparisonItem
{
    public double Percentage { get; init; }
    public int FramesStacked { get; init; }
    public int TotalValidFrames { get; init; }
    public int ActiveApCount { get; init; }
    public int TileSize { get; init; }
    public string OutputFilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public double QualityScore { get; set; }
    public bool IsBest { get; set; }
    public string TierLabel { get; init; } = string.Empty;
    public string SummaryText { get; init; } = string.Empty;
    public string BadgeText { get; set; } = string.Empty;
}
