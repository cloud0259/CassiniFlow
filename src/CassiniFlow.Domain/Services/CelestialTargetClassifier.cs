using System;
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
    string Details
)
{
    public string DisplayName(bool isFrench) => isFrench ? DisplayNameFr : DisplayNameEn;
}

public static class CelestialTargetClassifier
{
    /// <summary>
    /// Analyse une frame et un diamètre mesuré pour classer l'objet céleste
    /// (Planète, Lune/Soleil disque entier, ou Lune/Soleil surface/gros plan)
    /// et recommander le preset adéquat.
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

        // 1. Analyse de la luminosité globale et des coins
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

        // Échantillonnage des 4 coins (10x10 pixels dans chaque coin)
        double cornerMeanLum = SampleCornersMeanLuminance(frame);
        double normCornerLum = cornerMeanLum / 65535.0;

        // 2. Si un diamètre est mesuré
        if (measuredDiameterPx.HasValue && measuredDiameterPx.Value > 3.0)
        {
            double d = measuredDiameterPx.Value;
            double relRatio = (double)d / Math.Max(1, minDim);

            // Détection couleur H-Alpha (forte dominance rouge)
            bool isHAlphaRed = ch >= 3 && redSum > 1.35 * greenSum && redSum > 1.8 * blueSum;

            // Planète : objet compact entouré d'un fond de ciel noir (ratio < 0.35 ou d < 280px)
            if (relRatio < 0.35 && d < 320.0)
            {
                return new CelestialTargetClassification(
                    CelestialTargetType.Planet,
                    "Planète",
                    "Planet",
                    RecommendedPresetIndex: 1, // PresetPlanetary
                    Confidence: Math.Clamp(1.0 - (relRatio / 0.35) * 0.3, 0.70, 0.98),
                    Details: $"Disque compact détecté : diamètre apparent {d:F0} px ({relRatio * 100.0:F1} % du champ)");
            }

            // Disque entier de la Lune ou du Soleil (remplit une part significative du champ)
            string nameFr = isHAlphaRed ? "Soleil (H-Alpha disque entier)" : "Lune ou Soleil (disque entier)";
            string nameEn = isHAlphaRed ? "Sun (H-Alpha full disc)" : "Moon or Sun (full disc)";

            return new CelestialTargetClassification(
                CelestialTargetType.MoonOrSunFullDisc,
                nameFr,
                nameEn,
                RecommendedPresetIndex: 3, // PresetSolarLunarFullDisc
                Confidence: 0.92,
                Details: $"Grand disque détecté : diamètre apparent {d:F0} px ({relRatio * 100.0:F1} % du champ)");
        }

        // 3. Pas de diamètre de disque isolé mesuré : surface lunaire/solaire ou champ vide
        if (normMeanLum >= 0.08 && normCornerLum >= 0.05)
        {
            // Les coins et l'ensemble de l'image sont illuminés : champ étendu / surface
            bool isHAlphaRed = ch >= 3 && redSum > 1.35 * greenSum && redSum > 1.8 * blueSum;
            string nameFr = isHAlphaRed ? "Soleil (Surface / Gros plan H-Alpha)" : "Lune ou Soleil (Surface / Gros plan)";
            string nameEn = isHAlphaRed ? "Sun (Surface / Close-up H-Alpha)" : "Moon or Sun (Surface / Close-up)";

            return new CelestialTargetClassification(
                CelestialTargetType.MoonOrSunSurface,
                nameFr,
                nameEn,
                RecommendedPresetIndex: 2, // PresetSolarLunarCloseup
                Confidence: 0.88,
                Details: $"Texture de surface étendue sans contour de ciel noir (luminosité moyenne {normMeanLum * 100:F0} %)");
        }

        return new CelestialTargetClassification(
            CelestialTargetType.Unknown,
            "Non reconnu (Manuel)",
            "Unrecognized (Manual)",
            RecommendedPresetIndex: 0, // PresetManual
            Confidence: 0.20,
            Details: "Signal trop faible ou objet non identifiable");
    }

    private static double SampleCornersMeanLuminance(AstroFrame frame)
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
                // Top-left, Top-right, Bottom-left, Bottom-right
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
