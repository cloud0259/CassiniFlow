namespace CassiniFlow.Domain.Services;

/// <summary>
/// Candidat ponctuel extrait sur une frame individuelle avant filtrage multi-frame et colinéarité.
/// </summary>
public sealed record SatelliteSpotCandidate(
    double X,
    double Y,
    double RelX,
    double RelY,
    int PixelCount,
    ushort PeakLuminance,
    double LocalSnr,
    double NeighborRatio,
    int FrameIndex = 0
);

