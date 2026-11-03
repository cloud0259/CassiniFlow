using System;
using System.Collections.Generic;
using System.Linq;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Domain.Services;

public enum CelestialTargetType
{
    Unknown = 0,
    Planet = 1,
    MoonOrSunSurface = 2,
    MoonOrSunFullDisc = 3
}

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

public static class CelestialTargetClassifier
{
    public static Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? PreviewFrameLoader
    {
        get => _previewFrameLoader ?? PlanetSizeAnalyzer.PreviewFrameLoader;
        set => _previewFrameLoader = value;
    }
    private static Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? _previewFrameLoader;

    /// <summary>
    /// Active ou désactive par défaut la détection des satellites naturels lors de la classification.
    /// </summary>
    public static bool DefaultDetectSatellites { get; set; } = true;

    /// <summary>
    /// Sensibilité par défaut de détection des satellites naturels (1.0 = Normal).
    /// </summary>
    public static double DefaultSatelliteSensitivity { get; set; } = 1.0;

    /// <summary>
    /// Analyse une vidéo ou séquence en échantillonnant plusieurs frames réparties dans le fichier
    /// pour classifier l'objet céleste (Planète, Lune ou Soleil) et détecter ses satellites naturels
    /// avec persistance temporelle multi-frame.
    /// </summary>
    public static CelestialTargetClassification ClassifyVideo(
        string filePath,
        int frameCount,
        double? measuredDiameterPx = null,
        int originalWidth = 0,
        int originalHeight = 0,
        Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? frameLoader = null,
        bool? detectSatellites = null,
        double? satelliteSensitivity = null,
        IAstroLocalization? localization = null)
    {
        var loc = localization ?? new AstroLocalizationService();
        var loader = frameLoader ?? PreviewFrameLoader;
        if (loader == null || string.IsNullOrWhiteSpace(filePath))
        {
            var frDictInit = loc.GetAll(DomainLanguage.French);
            var enDictInit = loc.GetAll(DomainLanguage.English);
            return new CelestialTargetClassification(
                CelestialTargetType.Unknown,
                frDictInit["Celestial.Unknown"],
                enDictInit["Celestial.Unknown"],
                RecommendedPresetIndex: 0,
                Confidence: 0.0,
                Details: frDictInit["Celestial.PreviewLoaderNotInit"],
                DetailsEn: enDictInit["Celestial.PreviewLoaderNotInit"]);
        }

        bool useSatellites = detectSatellites ?? DefaultDetectSatellites;
        double useSensitivity = satelliteSensitivity ?? DefaultSatelliteSensitivity;

        if (frameCount <= 1)
        {
            try
            {
                var preview = loader(filePath, 0, 960);
                if (preview.Frame != null)
                {
                    return Classify(
                        preview.Frame,
                        measuredDiameterPx,
                        preview.OriginalWidth,
                        preview.OriginalHeight,
                        detectSatellites: useSatellites,
                        satelliteSensitivity: useSensitivity,
                        localization: loc);
                }
            }
            catch { }
            var frDictErr = loc.GetAll(DomainLanguage.French);
            var enDictErr = loc.GetAll(DomainLanguage.English);
            return new CelestialTargetClassification(
                CelestialTargetType.Unknown,
                frDictErr["Celestial.Unknown"],
                enDictErr["Celestial.Unknown"],
                0,
                0.0,
                frDictErr["Celestial.CannotReadFrame"],
                DetailsEn: enDictErr["Celestial.CannotReadFrame"]);
        }

        // Échantillonnage de plusieurs frames réparties dans la vidéo (jusqu'à 9 frames de 5% à 95%)
        int samples = Math.Clamp(frameCount, 2, 9);
        var loadedFrames = new List<(AstroFrame Frame, int OrigW, int OrigH, int FrameIndex)>();

        for (int i = 0; i < samples; i++)
        {
            double t = 0.05 + 0.90 * i / (samples - 1);
            int idx = Math.Clamp((int)(t * (frameCount - 1)), 0, frameCount - 1);
            try
            {
                var preview = loader(filePath, idx, 960);
                if (preview.Frame != null)
                {
                    loadedFrames.Add((preview.Frame, preview.OriginalWidth, preview.OriginalHeight, idx));
                }
            }
            catch { }
        }

        if (loadedFrames.Count == 0)
        {
            var frDictEmpty = loc.GetAll(DomainLanguage.French);
            var enDictEmpty = loc.GetAll(DomainLanguage.English);
            return new CelestialTargetClassification(
                CelestialTargetType.Unknown,
                frDictEmpty["Celestial.Unknown"],
                enDictEmpty["Celestial.Unknown"],
                0,
                0.0,
                frDictEmpty["Celestial.NoReadableFrames"],
                DetailsEn: enDictEmpty["Celestial.NoReadableFrames"]);
        }

        if (loadedFrames.Count == 1)
        {
            var single = loadedFrames[0];
            return Classify(
                single.Frame,
                measuredDiameterPx,
                single.OrigW,
                single.OrigH,
                detectSatellites: useSatellites,
                satelliteSensitivity: useSensitivity,
                localization: loc);
        }

        IReadOnlyList<DetectedSatellite> confirmedSatellites = [];
        if (useSatellites)
        {
            var first = loadedFrames[0];
            double previewScale = first.OrigW > 0 ? (double)first.Frame.Width / first.OrigW : 1.0;
            if (previewScale <= 0) previewScale = 1.0;
            double? scaledDiameterPx = measuredDiameterPx.HasValue ? measuredDiameterPx.Value * previewScale : null;

            var framePairs = loadedFrames.Select(lf => (lf.Frame, lf.FrameIndex)).ToList();
            confirmedSatellites = NaturalSatelliteDetector.DetectAcrossFrames(
                framePairs,
                scaledDiameterPx,
                new SatelliteDetectionOptions(Enabled: true, Sensitivity: useSensitivity));
        }

        // Sélection de la frame médiane pour la classification globale avec les satellites validés en multi-frame
        var midFrame = loadedFrames[loadedFrames.Count / 2];
        return Classify(
            midFrame.Frame,
            measuredDiameterPx,
            midFrame.OrigW,
            midFrame.OrigH,
            detectSatellites: useSatellites,
            satelliteSensitivity: useSensitivity,
            precomputedSatellites: confirmedSatellites,
            localization: loc);
    }

    /// <summary>
    /// Analyse une frame et un diamètre mesuré pour classer l'objet céleste
    /// (Planète, Lune/Soleil disque entier, ou Lune/Soleil surface/gros plan),
    /// identifier précisément le corps céleste (Jupiter, Saturne avec anneaux, Mars, Lune, Soleil)
    /// ainsi que ses satellites naturels, et recommander le preset adéquat.
    /// </summary>
    public static CelestialTargetClassification Classify(
        AstroFrame frame,
        double? measuredDiameterPx = null,
        int originalWidth = 0,
        int originalHeight = 0,
        bool? detectSatellites = null,
        double? satelliteSensitivity = null,
        IReadOnlyList<DetectedSatellite>? precomputedSatellites = null,
        IAstroLocalization? localization = null)
    {
        var loc = localization ?? new AstroLocalizationService();
        int origW = originalWidth > 0 ? originalWidth : frame.Width;
        int origH = originalHeight > 0 ? originalHeight : frame.Height;
        int minDim = Math.Min(origW, origH);

        int w = frame.Width;
        int h = frame.Height;
        if (w <= 0 || h <= 0)
        {
            var frDictDim = loc.GetAll(DomainLanguage.French);
            var enDictDim = loc.GetAll(DomainLanguage.English);
            return new CelestialTargetClassification(
                CelestialTargetType.Unknown,
                frDictDim["Celestial.Unknown"],
                enDictDim["Celestial.Unknown"],
                RecommendedPresetIndex: 0,
                Confidence: 0.0,
                Details: frDictDim["Celestial.InvalidDimensions"],
                DetailsEn: enDictDim["Celestial.InvalidDimensions"]);
        }

        bool useSatellites = detectSatellites ?? DefaultDetectSatellites;
        double useSensitivity = satelliteSensitivity ?? DefaultSatelliteSensitivity;

        // 1. Analyse de la luminosité globale et chrominance
        long totalLum = 0;
        int step = (w > 640 || h > 640) ? 2 : 1;
        int sampleCount = 0;
        int ch = frame.Channels;

        long redSum = 0, greenSum = 0, blueSum = 0;

        unsafe
        {
            fixed (ushort* p = frame.Data)
            {
                for (int y = 0; y < h; y += step)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x += step)
                    {
                        if (ch == 1)
                        {
                            ushort val = p[row + x];
                            totalLum += val;
                            redSum += val;
                            greenSum += val;
                            blueSum += val;
                        }
                        else
                        {
                            int idx = (row + x) * 3;
                            ushort r = p[idx], g = p[idx + 1], b = p[idx + 2];
                            ushort maxVal = Math.Max(r, Math.Max(g, b));
                            totalLum += maxVal;
                            redSum += r;
                            greenSum += g;
                            blueSum += b;
                        }
                        sampleCount++;
                    }
                }
            }
        }

        double meanLum = sampleCount > 0 ? (double)totalLum / sampleCount : 0.0;
        double normMeanLum = meanLum / 65535.0;

        // Échantillonnage des 4 coins (fond de ciel)
        double cornerMeanLum = SampleCornersMeanLuminance(frame);
        double normCornerLum = cornerMeanLum / 65535.0;

        // Détection dominante H-Alpha (forte dominance rouge)
        bool isHAlphaRed = ch >= 3 && redSum > 1.35 * greenSum && redSum > 1.8 * blueSum;

        var frDict = loc.GetAll(DomainLanguage.French);
        var enDict = loc.GetAll(DomainLanguage.English);

        // 2. Si un diamètre est mesuré
        if (measuredDiameterPx.HasValue && measuredDiameterPx.Value > 3.0)
        {
            double d = measuredDiameterPx.Value;
            double relRatio = (double)d / Math.Max(1, minDim);
            double previewScale = origW > 0 ? (double)w / origW : 1.0;
            if (previewScale <= 0) previewScale = 1.0;
            double frameDiameter = d * previewScale;

            // Planète : objet compact entouré d'un fond de ciel noir (ratio < 0.35 et d < 320px)
            if (relRatio < 0.35 && d < 320.0)
            {
                var disk = AnalyzePlanetDisk(frame, frameDiameter, cornerMeanLum);
                double aspectRatio = disk.AspectRatio;
                bool isSaturn = aspectRatio >= 1.28 && d >= 18.0;

                IReadOnlyList<DetectedSatellite> satellites = [];
                if (useSatellites)
                {
                    satellites = precomputedSatellites
                        ?? NaturalSatelliteDetector.DetectInSingleFrame(
                            frame,
                            disk.Cx,
                            disk.Cy,
                            frameDiameter / 2.0,
                            new SatelliteDetectionOptions(Enabled: true, Sensitivity: useSensitivity),
                            isSaturn: isSaturn);
                }

                int moons = satellites.Count;

                string bodyFr = frDict["Celestial.Planet"];
                string bodyEn = enDict["Celestial.Planet"];
                string details;
                string detailsEn;

                // 1. Saturne : forme aplatie / allongée due aux anneaux (aspectRatio >= 1.28 et d >= 18)
                if (isSaturn)
                {
                    bodyFr = frDict["Celestial.Saturn"];
                    bodyEn = enDict["Celestial.Saturn"];
                    details = moons > 0
                        ? string.Format(frDict["Celestial.SaturnWithMoons"], moons, moons > 1 ? "s" : "", aspectRatio, d)
                        : string.Format(frDict["Celestial.SaturnNoMoons"], aspectRatio, d);
                    detailsEn = moons > 0
                        ? string.Format(enDict["Celestial.SaturnWithMoons"], moons, moons > 1 ? "s" : "", aspectRatio, d)
                        : string.Format(enDict["Celestial.SaturnNoMoons"], aspectRatio, d);
                }
                // 2. Mars : teinte rougeâtre ocre (red > 1.2 * blue et red > 1.1 * green)
                else if (ch >= 3 && redSum > 1.22 * blueSum && redSum > 1.12 * greenSum && d <= 85.0 && moons == 0)
                {
                    bodyFr = frDict["Celestial.Mars"];
                    bodyEn = enDict["Celestial.Mars"];
                    details = string.Format(frDict["Celestial.MarsIdentified"], d);
                    detailsEn = string.Format(enDict["Celestial.MarsIdentified"], d);
                }
                // 3. Uranus / Neptune : teinte cyan / bleue distincte (blue > red et green > red)
                else if (ch >= 3 && blueSum > 1.15 * redSum && greenSum > 1.05 * redSum && d <= 45.0 && moons == 0)
                {
                    bodyFr = frDict["Celestial.UranusNeptune"];
                    bodyEn = enDict["Celestial.UranusNeptune"];
                    details = string.Format(frDict["Celestial.UranusNeptuneIdentified"], d);
                    detailsEn = string.Format(enDict["Celestial.UranusNeptuneIdentified"], d);
                }
                // 4. Vénus : disque très brillant (albedo très fort) sans lune
                else if (normMeanLum > 0.40 && moons == 0 && (d >= 20.0 || aspectRatio > 1.15))
                {
                    bodyFr = frDict["Celestial.Venus"];
                    bodyEn = enDict["Celestial.Venus"];
                    details = string.Format(frDict["Celestial.VenusIdentified"], d);
                    detailsEn = string.Format(enDict["Celestial.VenusIdentified"], d);
                }
                // 5. Jupiter : grand disque quasi-circulaire ou satellites détectés
                else if (moons > 0 || (d >= 26.0 && aspectRatio < 1.25))
                {
                    bodyFr = frDict["Celestial.Jupiter"];
                    bodyEn = enDict["Celestial.Jupiter"];
                    details = moons > 0
                        ? string.Format(frDict["Celestial.JupiterWithMoons"], moons, moons > 1 ? "s" : "", d)
                        : string.Format(frDict["Celestial.JupiterNoMoons"], d);
                    detailsEn = moons > 0
                        ? string.Format(enDict["Celestial.JupiterWithMoons"], moons, moons > 1 ? "s" : "", d)
                        : string.Format(enDict["Celestial.JupiterNoMoons"], d);
                }
                else
                {
                    details = string.Format(frDict["Celestial.CompactPlanetDisc"], d, relRatio * 100.0);
                    detailsEn = string.Format(enDict["Celestial.CompactPlanetDisc"], d, relRatio * 100.0);
                }

                return new CelestialTargetClassification(
                    CelestialTargetType.Planet,
                    frDict["Celestial.Planet"],
                    enDict["Celestial.Planet"],
                    RecommendedPresetIndex: 1, // PresetPlanetary
                    Confidence: Math.Clamp(1.0 - (relRatio / 0.35) * 0.3, 0.70, 0.98),
                    Details: details,
                    SpecificBodyNameFr: bodyFr,
                    SpecificBodyNameEn: bodyEn,
                    DetectedMoonsCount: moons,
                    DetectedSatellites: satellites,
                    DetailsEn: detailsEn);
            }

            // Disque entier de la Lune ou du Soleil (remplit une part significative du champ)
            string discBodyFr;
            string discBodyEn;
            string nameFr;
            string nameEn;

            if (isHAlphaRed)
            {
                discBodyFr = frDict["Celestial.Sun"];
                discBodyEn = enDict["Celestial.Sun"];
                nameFr = frDict["Celestial.SunHAlphaDisc"];
                nameEn = enDict["Celestial.SunHAlphaDisc"];
            }
            else if (ch >= 3 && Math.Abs(redSum - greenSum) < 0.15 * redSum && Math.Abs(greenSum - blueSum) < 0.15 * greenSum)
            {
                discBodyFr = frDict["Celestial.Moon"];
                discBodyEn = enDict["Celestial.Moon"];
                nameFr = frDict["Celestial.MoonOrSunFullDisc"];
                nameEn = enDict["Celestial.MoonOrSunFullDisc"];
            }
            else
            {
                discBodyFr = frDict["Celestial.MoonOrSun"];
                discBodyEn = enDict["Celestial.MoonOrSun"];
                nameFr = frDict["Celestial.MoonOrSunFullDisc"];
                nameEn = enDict["Celestial.MoonOrSunFullDisc"];
            }

            return new CelestialTargetClassification(
                CelestialTargetType.MoonOrSunFullDisc,
                nameFr,
                nameEn,
                RecommendedPresetIndex: 3, // PresetSolarLunarFullDisc
                Confidence: 0.92,
                Details: string.Format(frDict["Celestial.LargeDiscDetected"], d, relRatio * 100.0),
                SpecificBodyNameFr: discBodyFr,
                SpecificBodyNameEn: discBodyEn,
                DetailsEn: string.Format(enDict["Celestial.LargeDiscDetected"], d, relRatio * 100.0));
        }

        // 3. Pas de diamètre de disque isolé mesuré : surface lunaire/solaire ou champ vide
        if (normMeanLum >= 0.08 && normCornerLum >= 0.05)
        {
            string surfBodyFr;
            string surfBodyEn;
            string nameFr;
            string nameEn;

            if (isHAlphaRed)
            {
                surfBodyFr = frDict["Celestial.Sun"];
                surfBodyEn = enDict["Celestial.Sun"];
                nameFr = frDict["Celestial.SunHAlphaSurface"];
                nameEn = enDict["Celestial.SunHAlphaSurface"];
            }
            else if (ch >= 3 && Math.Abs(redSum - greenSum) < 0.15 * redSum && Math.Abs(greenSum - blueSum) < 0.15 * greenSum)
            {
                surfBodyFr = frDict["Celestial.Moon"];
                surfBodyEn = enDict["Celestial.Moon"];
                nameFr = frDict["Celestial.MoonOrSunSurface"];
                nameEn = enDict["Celestial.MoonOrSunSurface"];
            }
            else
            {
                surfBodyFr = frDict["Celestial.MoonOrSun"];
                surfBodyEn = enDict["Celestial.MoonOrSun"];
                nameFr = frDict["Celestial.MoonOrSunSurface"];
                nameEn = enDict["Celestial.MoonOrSunSurface"];
            }

            return new CelestialTargetClassification(
                CelestialTargetType.MoonOrSunSurface,
                nameFr,
                nameEn,
                RecommendedPresetIndex: 2, // PresetSolarLunarCloseup
                Confidence: 0.88,
                Details: string.Format(frDict["Celestial.SurfaceTexture"], normMeanLum * 100.0),
                SpecificBodyNameFr: surfBodyFr,
                SpecificBodyNameEn: surfBodyEn,
                DetailsEn: string.Format(enDict["Celestial.SurfaceTexture"], normMeanLum * 100.0));
        }

        return new CelestialTargetClassification(
            CelestialTargetType.Unknown,
            frDict["Celestial.Unrecognized"],
            enDict["Celestial.Unrecognized"],
            RecommendedPresetIndex: 0, // PresetManual
            Confidence: 0.20,
            Details: frDict["Celestial.SignalTooFaint"],
            DetailsEn: enDict["Celestial.SignalTooFaint"]);
    }

    /// <summary>
    /// Détecte le nombre de satellites naturels autour d'une planète sur une frame unique.
    /// </summary>
    public static int DetectMoonsAroundPlanet(
        AstroFrame frame,
        double cx,
        double cy,
        double radiusPx,
        double sensitivity = 1.0)
    {
        return NaturalSatelliteDetector.DetectInSingleFrame(
            frame,
            cx,
            cy,
            radiusPx,
            new SatelliteDetectionOptions(Enabled: true, Sensitivity: sensitivity)).Count;
    }

    /// <summary>
    /// Analyse morphologique du disque d'une planète (centre, rayon, allongement invariant par rotation).
    /// </summary>
    public static (double Cx, double Cy, double Radius, double AspectRatio, bool Found) AnalyzePlanetDisk(
        AstroFrame frame,
        double measuredDiameter,
        double cornerMeanLum)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        if (w <= 0 || h <= 0 || measuredDiameter <= 3.0) return (0, 0, 0, 1.0, false);

        int step = (w > 640 || h > 640) ? 2 : 1;
        long pSumX = 0, pSumY = 0, pWeight = 0;
        ushort planetThresh = (ushort)Math.Max(cornerMeanLum * 2.0, 3000);

        unsafe
        {
            fixed (ushort* p = frame.Data)
            {
                for (int y = 0; y < h; y += step)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x += step)
                    {
                        ushort val = ch == 1
                            ? p[row + x]
                            : Math.Max(p[(row + x) * 3], Math.Max(p[(row + x) * 3 + 1], p[(row + x) * 3 + 2]));
                        if (val >= planetThresh)
                        {
                            pSumX += x * (long)val;
                            pSumY += y * (long)val;
                            pWeight += val;
                        }
                    }
                }
            }
        }

        if (pWeight == 0) return (w / 2.0, h / 2.0, measuredDiameter / 2.0, 1.0, false);

        double pcx = (double)pSumX / pWeight;
        double pcy = (double)pSumY / pWeight;
        double maxDiskR2 = (measuredDiameter * 0.85) * (measuredDiameter * 0.85);

        int pMinX = w, pMaxX = 0, pMinY = h, pMaxY = 0;
        double m20 = 0.0, m02 = 0.0, m11 = 0.0, m00 = 0.0;

        unsafe
        {
            fixed (ushort* p = frame.Data)
            {
                for (int y = 0; y < h; y += step)
                {
                    int row = y * w;
                    double dy = y - pcy;
                    double dy2 = dy * dy;
                    for (int x = 0; x < w; x += step)
                    {
                        double dx = x - pcx;
                        if (dx * dx + dy2 > maxDiskR2) continue;

                        ushort val = ch == 1
                            ? p[row + x]
                            : Math.Max(p[(row + x) * 3], Math.Max(p[(row + x) * 3 + 1], p[(row + x) * 3 + 2]));
                        if (val >= planetThresh)
                        {
                            if (x < pMinX) pMinX = x;
                            if (x > pMaxX) pMaxX = x;
                            if (y < pMinY) pMinY = y;
                            if (y > pMaxY) pMaxY = y;

                            m00 += 1.0;
                            m20 += dx * dx;
                            m02 += dy2;
                            m11 += dx * dy;
                        }
                    }
                }
            }
        }

        int boxW = Math.Max(1, pMaxX - pMinX + 1);
        int boxH = Math.Max(1, pMaxY - pMinY + 1);
        double boxAspectRatio = (double)Math.Max(boxW, boxH) / Math.Min(boxW, boxH);

        double momentAspectRatio = 1.0;
        if (m00 >= 12.0)
        {
            double diff = m20 - m02;
            double disc = Math.Sqrt(diff * diff + 4.0 * m11 * m11);
            double lambdaMax = (m20 + m02 + disc) * 0.5;
            double lambdaMin = Math.Max(1e-6, (m20 + m02 - disc) * 0.5);
            momentAspectRatio = Math.Sqrt(lambdaMax / lambdaMin);
        }

        double aspectRatio = Math.Max(boxAspectRatio, momentAspectRatio);
        return (pcx, pcy, measuredDiameter / 2.0, aspectRatio, true);
    }

    public static double SampleCornersMeanLuminance(AstroFrame frame)
        => NaturalSatelliteDetector.SampleCornersStats(frame).Mean;
}
