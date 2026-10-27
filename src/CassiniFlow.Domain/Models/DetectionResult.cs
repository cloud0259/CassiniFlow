namespace CassiniFlow.Domain.Models;

public sealed record DetectionResult(
    bool Accepted,
    string? RejectionReason,
    int CentroidX,
    int CentroidY,
    int DetectedPixelCount,
    int OverexposedPixelCount,
    int Threshold8BitUsed);
