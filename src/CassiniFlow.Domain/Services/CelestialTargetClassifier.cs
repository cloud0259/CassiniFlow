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
    string? SpecificBodyNameEn = null,
    int DetectedMoonsCount = 0
)
{
    public string DisplayName(bool isFrench) => isFrench ? DisplayNameFr : DisplayNameEn;

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
    /// <summary>
    /// Analyse une frame et un diamètre mesuré pour classer l'objet céleste
    /// (Planète, Lune/Soleil disque entier, ou Lune/Soleil surface/gros plan),
    /// identifier précisément le corps céleste (Jupiter avec lunes, Saturne avec anneaux, Mars, Lune, Soleil)
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

        // 2. Si un diamètre est mesuré
        if (measuredDiameterPx.HasValue && measuredDiameterPx.Value > 3.0)
        {
            double d = measuredDiameterPx.Value;
            double relRatio = (double)d / Math.Max(1, minDim);

            // Planète : objet compact entouré d'un fond de ciel noir (ratio < 0.35 et d < 320px)
            if (relRatio < 0.35 && d < 320.0)
            {
                // Analyse morphologique du disque de la planète (barycentre et bounding box)
                int pMinX = w, pMaxX = 0, pMinY = h, pMaxY = 0;
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
                                ushort val = ch == 1 ? p[row + x] : Math.Max(p[(row + x) * 3], Math.Max(p[(row + x) * 3 + 1], p[(row + x) * 3 + 2]));
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

                double pcx = pWeight > 0 ? (double)pSumX / pWeight : w / 2.0;
                double pcy = pWeight > 0 ? (double)pSumY / pWeight : h / 2.0;
                double maxDiskR2 = (d * 0.75) * (d * 0.75);
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

                                ushort val = ch == 1 ? p[row + x] : Math.Max(p[(row + x) * 3], Math.Max(p[(row + x) * 3 + 1], p[(row + x) * 3 + 2]));
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

                int moons = DetectMoonsAroundPlanet(frame, pcx, pcy, d / 2.0, cornerMeanLum);

                string bodyFr = "Planète";
                string bodyEn = "Planet";
                string details;

                // 1. Saturne : forme aplatie / allongée due aux anneaux (aspectRatio >= 1.28 et d >= 18)
                if (aspectRatio >= 1.28 && d >= 18.0)
                {
                    bodyFr = "Saturne";
                    bodyEn = "Saturn";
                    details = $"Saturne identifiée : disque aplati / anneaux détectés (ratio {aspectRatio:F2}, diamètre apparent {d:F0} px)";
                }
                // 2. Mars : teinte rougeâtre ocre (red > 1.2 * blue et red > 1.1 * green)
                else if (ch >= 3 && redSum > 1.22 * blueSum && redSum > 1.12 * greenSum && d <= 85.0)
                {
                    bodyFr = "Mars";
                    bodyEn = "Mars";
                    details = $"Mars identifiée : disque ocre rougeâtre détecté (diamètre apparent {d:F0} px)";
                }
                // 3. Uranus / Neptune : teinte cyan / bleue distincte (blue > red et green > red)
                else if (ch >= 3 && blueSum > 1.15 * redSum && greenSum > 1.05 * redSum && d <= 45.0)
                {
                    bodyFr = "Uranus ou Neptune";
                    bodyEn = "Uranus or Neptune";
                    details = $"Uranus/Neptune identifiée : disque bleuté compact (diamètre apparent {d:F0} px)";
                }
                // 4. Vénus : disque très brillant (albedo très fort) sans lune
                else if (normMeanLum > 0.40 && moons == 0 && (d >= 20.0 || aspectRatio > 1.15))
                {
                    bodyFr = "Vénus";
                    bodyEn = "Venus";
                    details = $"Vénus identifiée : disque à très haut albédo (diamètre apparent {d:F0} px)";
                }
                // 5. Jupiter : grand disque quasi-circulaire ou lunes galiléennes détectées
                else if (moons > 0 || (d >= 28.0 && aspectRatio < 1.25))
                {
                    bodyFr = "Jupiter";
                    bodyEn = "Jupiter";
                    details = moons > 0
                        ? $"Jupiter identifiée avec {moons} satellite{(moons > 1 ? "s" : "")} galiléen{(moons > 1 ? "s" : "")} (diamètre {d:F0} px)"
                        : $"Jupiter identifiée : grand disque circulaire (diamètre apparent {d:F0} px)";
                }
                else
                {
                    details = $"Disque planétaire compact détecté : diamètre apparent {d:F0} px ({relRatio * 100.0:F1} % du champ)";
                }

                return new CelestialTargetClassification(
                    CelestialTargetType.Planet,
                    "Planète",
                    "Planet",
                    RecommendedPresetIndex: 1, // PresetPlanetary
                    Confidence: Math.Clamp(1.0 - (relRatio / 0.35) * 0.3, 0.70, 0.98),
                    Details: details,
                    SpecificBodyNameFr: bodyFr,
                    SpecificBodyNameEn: bodyEn,
                    DetectedMoonsCount: moons);
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
                Details: $"Grand disque détecté : diamètre apparent {d:F0} px ({relRatio * 100.0:F1} % du champ)",
                SpecificBodyNameFr: discBodyFr,
                SpecificBodyNameEn: discBodyEn);
        }

        // 3. Pas de diamètre de disque isolé mesuré : surface lunaire/solaire ou champ vide
        if (normMeanLum >= 0.08 && normCornerLum >= 0.05)
        {
            // Les coins et l'ensemble de l'image sont illuminés : champ étendu / surface
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
                Details: $"Texture de surface étendue sans contour de ciel noir (luminosité moyenne {normMeanLum * 100:F0} %)",
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
    /// Détecte les lunes / satellites ponctuels autour d'une planète (ex: satellites galiléens de Jupiter)
    /// dans une zone annulaire autour du disque planétaire.
    /// </summary>
    public static int DetectMoonsAroundPlanet(AstroFrame frame, double cx, double cy, double radiusPx, double backgroundThreshold16)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        if (w <= 0 || h <= 0 || radiusPx < 5.0) return 0;

        double innerR = radiusPx * 1.35;
        double outerR = Math.Min(radiusPx * 6.5, Math.Min(w, h) * 0.48);
        if (outerR <= innerR) return 0;

        double innerR2 = innerR * innerR;
        double outerR2 = outerR * outerR;

        ushort moonThreshold = (ushort)Math.Clamp(backgroundThreshold16 * 1.40 + 1500, 2000, 60000);

        List<(int X, int Y, int PixelCount)> candidates = new();
        bool[,] visited = new bool[w, h];

        unsafe
        {
            fixed (ushort* p = frame.Data)
            {
                int minSearchX = Math.Max(1, (int)(cx - outerR));
                int maxSearchX = Math.Min(w - 2, (int)(cx + outerR));
                int minSearchY = Math.Max(1, (int)(cy - outerR));
                int maxSearchY = Math.Min(h - 2, (int)(cy + outerR));

                for (int y = minSearchY; y <= maxSearchY; y++)
                {
                    int row = y * w;
                    double dy = y - cy;
                    double dy2 = dy * dy;

                    for (int x = minSearchX; x <= maxSearchX; x++)
                    {
                        if (visited[x, y]) continue;

                        double dx = x - cx;
                        double dist2 = dx * dx + dy2;
                        if (dist2 < innerR2 || dist2 > outerR2) continue;

                        ushort lum;
                        if (ch == 1)
                        {
                            lum = p[row + x];
                        }
                        else
                        {
                            int idx = (row + x) * 3;
                            lum = Math.Max(p[idx], Math.Max(p[idx + 1], p[idx + 2]));
                        }

                        if (lum >= moonThreshold)
                        {
                            int count = 0;
                            long clusterSumX = 0, clusterSumY = 0;
                            Queue<(int X, int Y)> queue = new();
                            queue.Enqueue((x, y));
                            visited[x, y] = true;

                            while (queue.Count > 0 && count < 60)
                            {
                                var (qx, qy) = queue.Dequeue();
                                count++;
                                clusterSumX += qx;
                                clusterSumY += qy;

                                for (int ny = Math.Max(0, qy - 1); ny <= Math.Min(h - 1, qy + 1); ny++)
                                {
                                    int nRow = ny * w;
                                    for (int nx = Math.Max(0, qx - 1); nx <= Math.Min(w - 1, qx + 1); nx++)
                                    {
                                        if (visited[nx, ny]) continue;
                                        ushort nLum = ch == 1 ? p[nRow + nx] : Math.Max(p[(nRow + nx) * 3], Math.Max(p[(nRow + nx) * 3 + 1], p[(nRow + nx) * 3 + 2]));
                                        if (nLum >= moonThreshold)
                                        {
                                            visited[nx, ny] = true;
                                            queue.Enqueue((nx, ny));
                                        }
                                    }
                                }
                            }

                            // Les lunes sont de petites sources ponctuelles compactes (1 à 40 pixels)
                            if (count >= 1 && count <= 40)
                            {
                                int candX = (int)(clusterSumX / count);
                                int candY = (int)(clusterSumY / count);
                                bool duplicate = false;
                                foreach (var c in candidates)
                                {
                                    int cdx = c.X - candX;
                                    int cdy = c.Y - candY;
                                    if (cdx * cdx + cdy * cdy < 36)
                                    {
                                        duplicate = true;
                                        break;
                                    }
                                }
                                if (!duplicate)
                                {
                                    candidates.Add((candX, candY, count));
                                }
                            }
                        }
                    }
                }
            }
        }

        return Math.Clamp(candidates.Count, 0, 8);
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
