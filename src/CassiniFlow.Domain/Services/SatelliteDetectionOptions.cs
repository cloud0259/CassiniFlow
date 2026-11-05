namespace CassiniFlow.Domain.Services;

/// <summary>
/// Paramètres de configuration pour la détection des satellites naturels (lunes de Jupiter / Saturne).
/// </summary>
public sealed record SatelliteDetectionOptions(
    bool Enabled = true,
    double Sensitivity = 1.0
);

