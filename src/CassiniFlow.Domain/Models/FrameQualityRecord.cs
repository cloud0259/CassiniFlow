namespace CassiniFlow.Domain.Models;

/// <summary>
/// Individual quality score and rank for a single frame.
/// </summary>
public readonly record struct FrameQualityRecord(
    int FrameIndex,
    double RawScore,
    double NormalizedScore, // 0.0 to 100.0%
    int Rank                // 1-based rank (1 = best frame in recording)
);
