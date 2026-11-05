using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Adapters;

public sealed record MediaFileSummary(
    string FilePath,
    string FormatBadge,
    int FrameCount,
    int Width,
    int Height,
    double Fps,
    long FileSizeBytes,
    string DetailedReport,
    AstroMediaMetadata? Metadata = null);
