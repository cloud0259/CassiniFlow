using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Adapters;

public sealed record UniversalPreviewFrame(
    AstroFrame Frame,
    int OriginalWidth,
    int OriginalHeight,
    double ScaleFactor);
