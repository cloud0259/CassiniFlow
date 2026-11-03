using System;
using System.Collections.Generic;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Domain.Ports.Inbound;

public interface ICelestialTargetClassifier
{
    CelestialTargetClassification Classify(
        AstroFrame? frame,
        double? measuredDiameterPx = null,
        int? originalWidth = null,
        int? originalHeight = null,
        bool detectSatellites = true,
        double satelliteSensitivity = 1.0);

    CelestialTargetClassification ClassifyVideo(
        string filePath,
        int frameCount,
        double? measuredDiameterPx = null,
        int originalWidth = 0,
        int originalHeight = 0,
        Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? frameLoader = null,
        bool? detectSatellites = null,
        double? satelliteSensitivity = null);
}

