using System.Collections.Generic;

namespace CassiniFlow.Domain.Services;

public sealed record CelestialTargetClassification(
    CelestialTargetType TargetType,
    string DisplayNameFr,
    string DisplayNameEn,
    int RecommendedPresetIndex,
    double Confidence,
    string Details,
    string? SpecificBodyNameFr = null,
    string? SpecificBodyNameEn = null,
    int DetectedMoonsCount = 0,
    IReadOnlyList<DetectedSatellite>? DetectedSatellites = null,
    string? DetailsEn = null
)
{
    public string DisplayName(bool isFrench) => isFrench
        ? (!string.IsNullOrEmpty(DisplayNameFr) ? DisplayNameFr : TargetType.ToString())
        : (!string.IsNullOrEmpty(DisplayNameEn) ? DisplayNameEn : TargetType.ToString());

    public string GetDetails(bool isFrench) => isFrench ? Details : (DetailsEn ?? Details);

    public string ObjectSummary(bool isFrench)
    {
        string name = isFrench ? (SpecificBodyNameFr ?? DisplayNameFr) : (SpecificBodyNameEn ?? DisplayNameEn);
        if (DetectedMoonsCount > 0)
        {
            string moons = isFrench
                ? (DetectedMoonsCount == 1 ? "1 lune détectée" : $"{DetectedMoonsCount} lunes détectées")
                : (DetectedMoonsCount == 1 ? "1 moon detected" : $"{DetectedMoonsCount} moons detected");
            return $"{name} ({moons})";
        }
        return name;
    }
}
