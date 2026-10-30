using System;
using System.Collections.Generic;
using CassiniFlow.Domain.Models;

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
    string? SpecificBodyNameEn = null
)
{
    public string DisplayName(bool isFrench) => isFrench ? DisplayNameFr : DisplayNameEn;

    public string ObjectSummary(bool isFrench) => isFrench
        ? (SpecificBodyNameFr ?? DisplayNameFr)
        : (SpecificBodyNameEn ?? DisplayNameEn);
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
    /// Analyse une vid\u00e9o ou s\u00e9quence en \u00e9chantillonnant plusieurs frames r\u00e9parties dans le fichier
    /// pour classifier l'objet c\u00e9leste (Plan\u00e8te, Lune ou Soleil).
    /// </summary>
    public static CelestialTargetClassification ClassifyVideo(
        string filePath,
        int frameCount,
        double? measuredDiameterPx = null,
        int originalWidth = 0,
        int originalHeight = 0,
        Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? frameLoader = null)
    {
        var loader = frameLoader ?? PreviewFrameLoader;
        if (loader == null || string.IsNullOrWhiteSpace(filePath))
        {
            return new CelestialTargetClassification(
                CelestialTargetType.Unknown,
                "Inconnu",
                "Unknown",
                RecommendedPresetIndex: 0,
                Confidence: 0.0,
                Details: "Chargeur de preview non initialis\u00e9");
        }

        if (frameCount <= 1)
        {
            try
            {
                var preview = loader(filePath, 0, 960);
                if (preview.Frame != null)
                {
                    return Classify(preview.Frame, measuredDiameterPx, preview.OriginalWidth, preview.OriginalHeight);
                }
            }
            catch { }
            return new CelestialTargetClassification(CelestialTargetType.Unknown, "Inconnu", "Unknown", 0, 0.0, "Impossible de lire la frame");
        }

        // \u00c9chantillonnage de plusieurs frames r\u00e9parties dans la vid\u00e9o pour choisir la plus repr\u00e9sentative
        int samples = Math.Clamp(frameCount, 2, 5);
        var loadedFrames = new List<(AstroFrame Frame, int OrigW, int OrigH)>();

        for (int i = 0; i < samples; i++)
        {
            double t = 0.15 + 0.70 * i / (samples - 1);
            int idx = Math.Clamp((int)(t * (frameCount - 1)), 0, frameCount - 1);
            try
            {
                var preview = loader(filePath, idx, 960);
                if (preview.Frame != null)
                {
                    loadedFrames.Add((preview.Frame, preview.OriginalWidth, preview.OriginalHeight));
                }
            }
            catch { }
        }

        if (loadedFrames.Count == 0)
        {
            return new CelestialTargetClassification(CelestialTargetType.Unknown, "Inconnu", "Unknown", 0, 0.0, "Aucune frame lisible");
        }

        // S\u00e9lection de la frame m\u00e9diane pour r\u00e9duire l'impact des turbulences extr\u00eames de d\u00e9but de s\u00e9quence
        var midFrame = loadedFrames[loadedFrames.Count / 2];
        return Classify(midFrame.Frame, measuredDiameterPx, midFrame.OrigW, midFrame.OrigH);
    }

    /// <summary>
    /// Analyse une frame et un diam\u00e8tre mesur\u00e9 pour classer l'objet c\u00e9leste
    /// (Plan\u00e8te, Lune/Soleil disque entier, ou Lune/Soleil surface/gros plan),
    /// identifier pr\u00e9cis\u00e9ment le corps c\u00e9leste (Jupiter, Saturne avec anneaux, Mars, Lune, Soleil)
    /// et recommander le preset ad\u00e9quat.
    /// </summary>
    public static CelestialTargetClassification Classify(
        AstroFrame frame,
        double? measuredDiameterPx = null,
        int originalWidth = 0,
        int originalHeight = 0)
    {
        int origW = originalWidth > 0 ? originalWidth : frame.Width;
        int origH = originalHeight > 0 ? originalHeight : frame.Height;
        int minDim = Math.Min(origW, origH);

        int w = frame.Width;
        int h = frame.Height;
        if (w <= 0 || h <= 0)
        {
            return new CelestialTargetClassification(
                CelestialTargetType.Unknown,
                "Inconnu",
                "Unknown",
                RecommendedPresetIndex: 0,
                Confidence: 0.0,
                Details: "Dimensions invalides");
        }

        // 1. Analyse de la luminosit\u00e9 globale et chrominance
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

        // \u00c9chantillonnage des 4 coins (fond de ciel)
        double cornerMeanLum = SampleCornersMeanLuminance(frame);
        double normCornerLum = cornerMeanLum / 65535.0;

        // D\u00e9tection dominante H-Alpha (forte dominance rouge)
        bool isHAlphaRed = ch >= 3 && redSum > 1.35 * greenSum && redSum > 1.8 * blueSum;

        // 2. Si un diam\u00e8tre est mesur\u00e9
        if (measuredDiameterPx.HasValue && measuredDiameterPx.Value > 3.0)
        {
            double d = measuredDiameterPx.Value;
            double relRatio = (double)d / Math.Max(1, minDim);

            // Plan\u00e8te : objet compact entour\u00e9 d'un fond de ciel noir (ratio < 0.35 et d < 320px)
            if (relRatio < 0.35 && d < 320.0)
            {
                var disk = AnalyzePlanetDisk(frame, d, cornerMeanLum);
                double aspectRatio = disk.AspectRatio;

                string bodyFr = "Plan\u00e8te";
                string bodyEn = "Planet";
                string details;

                // 1. Saturne : forme aplatie / allong\u00e9e due aux anneaux (aspectRatio >= 1.28 et d >= 18)
                if (aspectRatio >= 1.28 && d >= 18.0)
                {
                    bodyFr = "Saturne";
                    bodyEn = "Saturn";
                    details = $"Saturne identifi\u00e9e : disque aplati / anneaux d\u00e9tect\u00e9s (ratio {aspectRatio:F2}, diam\u00e8tre apparent {d:F0} px)";
                }
                // 2. Mars : teinte rouge\u00e2tre ocre (red > 1.2 * blue et red > 1.1 * green)
                else if (ch >= 3 && redSum > 1.22 * blueSum && redSum > 1.12 * greenSum && d <= 85.0)
                {
                    bodyFr = "Mars";
                    bodyEn = "Mars";
                    details = $"Mars identifi\u00e9e : disque ocre rouge\u00e2tre d\u00e9tect\u00e9 (diam\u00e8tre apparent {d:F0} px)";
                }
                // 3. Uranus / Neptune : teinte cyan / bleue distincte (blue > red et green > red)
                else if (ch >= 3 && blueSum > 1.15 * redSum && greenSum > 1.05 * redSum && d <= 45.0)
                {
                    bodyFr = "Uranus ou Neptune";
                    bodyEn = "Uranus or Neptune";
                    details = $"Uranus/Neptune identifi\u00e9e : disque bleut\u00e9 compact (diam\u00e8tre apparent {d:F0} px)";
                }
                // 4. V\u00e9nus : disque tr\u00e8s brillant (albedo tr\u00e8s fort)
                else if (normMeanLum > 0.40 && (d >= 20.0 || aspectRatio > 1.15))
                {
                    bodyFr = "V\u00e9nus";
                    bodyEn = "Venus";
                    details = $"V\u00e9nus identifi\u00e9e : disque \u00e0 tr\u00e8s haut alb\u00e9do (diam\u00e8tre apparent {d:F0} px)";
                }
                // 5. Jupiter : grand disque quasi-circulaire
                else if (d >= 26.0 && aspectRatio < 1.25)
                {
                    bodyFr = "Jupiter";
                    bodyEn = "Jupiter";
                    details = $"Jupiter identifi\u00e9e : grand disque circulaire (diam\u00e8tre apparent {d:F0} px)";
                }
                else
                {
                    details = $"Disque plan\u00e9taire compact d\u00e9tect\u00e9 : diam\u00e8tre apparent {d:F0} px ({relRatio * 100.0:F1} % du champ)";
                }

                return new CelestialTargetClassification(
                    CelestialTargetType.Planet,
                    "Plan\u00e8te",
                    "Planet",
                    RecommendedPresetIndex: 1, // PresetPlanetary
                    Confidence: Math.Clamp(1.0 - (relRatio / 0.35) * 0.3, 0.70, 0.98),
                    Details: details,
                    SpecificBodyNameFr: bodyFr,
                    SpecificBodyNameEn: bodyEn);
            }

            // Disque entier de la Lune ou du Soleil (remplit une part significative du champ)
            string discBodyFr;
            string discBodyEn;
            string nameFr;
            string nameEn;

            if (isHAlphaRed)
            {
                discBodyFr = "Soleil";
                discBodyEn = "Sun";
                nameFr = "Soleil (H-Alpha disque entier)";
                nameEn = "Sun (H-Alpha full disc)";
            }
            else if (ch >= 3 && Math.Abs(redSum - greenSum) < 0.15 * redSum && Math.Abs(greenSum - blueSum) < 0.15 * greenSum)
            {
                discBodyFr = "Lune";
                discBodyEn = "Moon";
                nameFr = "Lune ou Soleil (disque entier)";
                nameEn = "Moon or Sun (full disc)";
            }
            else
            {
                discBodyFr = "Lune ou Soleil";
                discBodyEn = "Moon or Sun";
                nameFr = "Lune ou Soleil (disque entier)";
                nameEn = "Moon or Sun (full disc)";
            }

            return new CelestialTargetClassification(
                CelestialTargetType.MoonOrSunFullDisc,
                nameFr,
                nameEn,
                RecommendedPresetIndex: 3, // PresetSolarLunarFullDisc
                Confidence: 0.92,
                Details: $"Grand disque d\u00e9tect\u00e9 : diam\u00e8tre apparent {d:F0} px ({relRatio * 100.0:F1} % du champ)",
                SpecificBodyNameFr: discBodyFr,
                SpecificBodyNameEn: discBodyEn);
        }

        // 3. Pas de diam\u00e8tre de disque isol\u00e9 mesur\u00e9 : surface lunaire/solaire ou champ vide
        if (normMeanLum >= 0.08 && normCornerLum >= 0.05)
        {
            string surfBodyFr;
            string surfBodyEn;
            string nameFr;
            string nameEn;

            if (isHAlphaRed)
            {
                surfBodyFr = "Soleil";
                surfBodyEn = "Sun";
                nameFr = "Soleil (Surface / Gros plan H-Alpha)";
                nameEn = "Sun (Surface / Close-up H-Alpha)";
            }
            else if (ch >= 3 && Math.Abs(redSum - greenSum) < 0.15 * redSum && Math.Abs(greenSum - blueSum) < 0.15 * greenSum)
            {
                surfBodyFr = "Lune";
                surfBodyEn = "Moon";
                nameFr = "Lune ou Soleil (Surface / Gros plan)";
                nameEn = "Moon or Sun (Surface / Close-up)";
            }
            else
            {
                surfBodyFr = "Lune ou Soleil";
                surfBodyEn = "Moon or Sun";
                nameFr = "Lune ou Soleil (Surface / Gros plan)";
                nameEn = "Moon or Sun (Surface / Close-up)";
            }

            return new CelestialTargetClassification(
                CelestialTargetType.MoonOrSunSurface,
                nameFr,
                nameEn,
                RecommendedPresetIndex: 2, // PresetSolarLunarCloseup
                Confidence: 0.88,
                Details: $"Texture de surface \u00e9tendue sans contour de ciel noir (luminosit\u00e9 moyenne {normMeanLum * 100:F0} %)",
                SpecificBodyNameFr: surfBodyFr,
                SpecificBodyNameEn: surfBodyEn);
        }

        return new CelestialTargetClassification(
            CelestialTargetType.Unknown,
            "Non reconnu (Manuel)",
            "Unrecognized (Manual)",
            RecommendedPresetIndex: 0, // PresetManual
            Confidence: 0.20,
            Details: "Signal trop faible ou objet non identifiable");
    }

    /// <summary>
    /// Analyse morphologique du disque d'une plan\u00e8te (centre, rayon, allongement).
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
        double maxDiskR2 = (measuredDiameter * 0.75) * (measuredDiameter * 0.75);

        int pMinX = w, pMaxX = 0, pMinY = h, pMaxY = 0;
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
                        }
                    }
                }
            }
        }

        int boxW = Math.Max(1, pMaxX - pMinX + 1);
        int boxH = Math.Max(1, pMaxY - pMinY + 1);
        double aspectRatio = (double)Math.Max(boxW, boxH) / Math.Min(boxW, boxH);

        return (pcx, pcy, measuredDiameter / 2.0, aspectRatio, true);
    }

    public static double SampleCornersMeanLuminance(AstroFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        int box = Math.Min(16, Math.Min(w / 4, h / 4));
        if (box <= 0) return 0.0;

        long cornerTotal = 0;
        int count = 0;

        unsafe
        {
            fixed (ushort* p = frame.Data)
            {
                int[] xs = [0, w - box];
                int[] ys = [0, h - box];

                foreach (int cornerY in ys)
                {
                    for (int dy = 0; dy < box; dy++)
                    {
                        int row = (cornerY + dy) * w;
                        foreach (int cornerX in xs)
                        {
                            for (int dx = 0; dx < box; dx++)
                            {
                                int px = cornerX + dx;
                                ushort lum;
                                if (ch == 1)
                                {
                                    lum = p[row + px];
                                }
                                else
                                {
                                    int idx = (row + px) * 3;
                                    lum = Math.Max(p[idx], Math.Max(p[idx + 1], p[idx + 2]));
                                }
                                cornerTotal += lum;
                                count++;
                            }
                        }
                    }
                }
            }
        }

        return count > 0 ? (double)cornerTotal / count : 0.0;
    }
}
