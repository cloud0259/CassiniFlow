namespace CassiniFlow.Domain.Services;

/// <summary>
/// Représente un satellite naturel détecté autour d'un disque planétaire.
/// </summary>
public sealed record DetectedSatellite(
    double RelX,
    double RelY,
    double DistanceInRadii,
    double Snr,
    int PixelCount,
    ushort PeakLuminance
);

