using System;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Domain.Services;

public sealed class CelestialTargetClassifierService : ICelestialTargetClassifier
{
    private readonly IAstroLocalization _loc;

    public CelestialTargetClassifierService(IAstroLocalization localization)
    {
        _loc = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    public CelestialTargetClassification Classify(
        AstroFrame? frame,
        double? measuredDiameterPx = null,
        int? originalWidth = null,
        int? originalHeight = null,
        bool detectSatellites = true,
        double satelliteSensitivity = 1.0)
    {
        if (frame is null)
        {
            var frDict = _loc.GetAll(DomainLanguage.French);
            var enDict = _loc.GetAll(DomainLanguage.English);
            return new CelestialTargetClassification(
                CelestialTargetType.Unknown,
                frDict["Celestial.Unknown"],
                enDict["Celestial.Unknown"],
                RecommendedPresetIndex: 0,
                Confidence: 0.0,
                Details: frDict["Celestial.InvalidDimensions"],
                DetailsEn: enDict["Celestial.InvalidDimensions"]);
        }

        return CelestialTargetClassifier.Classify(
            frame,
            measuredDiameterPx,
            originalWidth ?? 0,
            originalHeight ?? 0,
            detectSatellites,
            satelliteSensitivity,
            localization: _loc);
    }

    public CelestialTargetClassification ClassifyVideo(
        string filePath,
        int frameCount,
        double? measuredDiameterPx = null,
        int originalWidth = 0,
        int originalHeight = 0,
        Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? frameLoader = null,
        bool? detectSatellites = null,
        double? satelliteSensitivity = null)
    {
        return CelestialTargetClassifier.ClassifyVideo(
            filePath,
            frameCount,
            measuredDiameterPx,
            originalWidth,
            originalHeight,
            frameLoader,
            detectSatellites,
            satelliteSensitivity,
            localization: _loc);
    }
}
