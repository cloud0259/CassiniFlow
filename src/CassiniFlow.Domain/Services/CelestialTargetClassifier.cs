using System;
using System.Collections.Generic;
using System.Linq;
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
                ? (DetectedMoonsCount == 1 ? "1 lune d\u00e9tect\u00e9e" : $"{DetectedMoonsCount} lunes d\u00e9tect\u00e9es")
                : (DetectedMoonsCount == 1 ? "1 moon detected" : $"{DetectedMoonsCount} moons detected");
            return $"{name} ({moons})";
        }
        return name;
    }
}

public sealed record MoonSpot(int X, int Y, int PixelCount, ushort MaxLum);

public sealed record MoonCandidateSpot(double RelX, double RelY, int PixelCount, ushort MaxLum, int FrameIndex);

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
    /// pour classifier l'objet et identifier pr\u00e9cis\u00e9ment les lunes avec persistance temporelle
    /// (\u00e9limine les faux positifs de bruit et d\u00e9tecte les lunes qui fluctuent \u00e0 cause du seeing).
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

        // \u00c9chantillonnage de plusieurs frames r\u00e9parties dans la vid\u00e9o (ex: 7 frames)
        int samples = Math.Clamp(frameCount, 2, 7);
        var loadedFrames = new List<(AstroFrame Frame, int OrigW, int OrigH, int FrameIndex)>();

        for (int i = 0; i < samples; i++)
        {
            double t = 0.08 + 0.84 * i / (samples - 1);
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
            return new CelestialTargetClassification(CelestialTargetType.Unknown, "Inconnu", "Unknown", 0, 0.0, "Aucune frame lisible");
        }

        if (loadedFrames.Count == 1)
        {
            var single = loadedFrames[0];
            return Classify(single.Frame, measuredDiameterPx, single.OrigW, single.OrigH);
        }

        // Multi-frame tracking des lunes autour de la plan\u00e8te
        var allCandidates = new List<MoonCandidateSpot>();
        int validPlanetFrames = 0;
        double avgRadius = 0;

        foreach (var lf in loadedFrames)
        {
            var (cMean, cStdDev) = SampleCornersStats(lf.Frame);
            double d = measuredDiameterPx ?? (lf.Frame.Width * 0.1);
            var disk = AnalyzePlanetDisk(lf.Frame, d, cMean);
            if (!disk.Found) continue;

            validPlanetFrames++;
            avgRadius += disk.Radius;

            var spots = ExtractMoonCandidateSpots(lf.Frame, disk.Cx, disk.Cy, disk.Radius, cMean, cStdDev);
            foreach (var s in spots)
            {
                allCandidates.Add(new MoonCandidateSpot(
                    RelX: s.X - disk.Cx,
                    RelY: s.Y - disk.Cy,
                    PixelCount: s.PixelCount,
                    MaxLum: s.MaxLum,
                    FrameIndex: lf.FrameIndex
                ));
            }
        }

        int confirmedMoons = 0;
        if (validPlanetFrames >= 2 && allCandidates.Count > 0)
        {
            avgRadius /= validPlanetFrames;

            // Grouper les d\u00e9tections similaires (\u00e0 moins de 5.5 px de distance relative)
            var clusters = new List<List<MoonCandidateSpot>>();
            foreach (var cand in allCandidates)
            {
                List<MoonCandidateSpot>? targetCluster = null;
                foreach (var cl in clusters)
                {
                    double avgX = cl.Average(c => c.RelX);
                    double avgY = cl.Average(c => c.RelY);
                    double dx = cand.RelX - avgX;
                    double dy = cand.RelY - avgY;
                    if (dx * dx + dy * dy <= 30.25) // 5.5^2
                    {
                        targetCluster = cl;
                        break;
                    }
                }

                if (targetCluster != null)
                {
                    targetCluster.Add(cand);
                }
                else
                {
                    clusters.Add([cand]);
                }
            }

            // Persistance temporelle : pr\u00e9sent dans au moins 2 frames distinctes
            var persistentClusters = clusters
                .Where(cl => cl.Select(c => c.FrameIndex).Distinct().Count() >= 2)
                .Select(cl => (
                    RelX: cl.Average(c => c.RelX),
                    RelY: cl.Average(c => c.RelY),
                    MaxLum: cl.Max(c => c.MaxLum),
                    AvgCount: cl.Average(c => c.PixelCount),
                    HitCount: cl.Select(c => c.FrameIndex).Distinct().Count()
                ))
                .ToList();

            // Filtrer par colin\u00e9arit\u00e9 avec le centre de Jupiter (0, 0)
            var collinearMoons = FilterCollinearMoons(
                persistentClusters,
                c => (c.RelX, c.RelY),
                cx: 0,
                cy: 0,
                planetRadius: avgRadius);

            // Jupiter a au plus 4 satellites galil\u00e9ens visibles
            confirmedMoons = Math.Clamp(collinearMoons.Count, 0, 4);
        }

        // Classification globale sur la frame m\u00e9diane avec le nombre de lunes confirm\u00e9 par l'analyse multi-frame
        var bestFrameData = loadedFrames[loadedFrames.Count / 2];
        return Classify(bestFrameData.Frame, measuredDiameterPx, bestFrameData.OrigW, bestFrameData.OrigH, forceMoonsCount: confirmedMoons);
    }

    /// <summary>
    /// Analyse une frame et un diam\u00e8tre mesur\u00e9 pour classer l'objet c\u00e9leste
    /// (Plan\u00e8te, Lune/Soleil disque entier, ou Lune/Soleil surface/gros plan),
    /// identifier pr\u00e9cis\u00e9ment le corps c\u00e9leste (Jupiter avec lunes, Saturne avec anneaux, Mars, Lune, Soleil)
    /// et recommander le preset ad\u00e9quat.
    /// </summary>
    public static CelestialTargetClassification Classify(
        AstroFrame frame,
        double? measuredDiameterPx = null,
        int originalWidth = 0,
        int originalHeight = 0,
        int? forceMoonsCount = null)
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
        var (cornerMeanLum, cornerStdDev) = SampleCornersStats(frame);
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
                double pcx = disk.Cx;
                double pcy = disk.Cy;
                double aspectRatio = disk.AspectRatio;

                int moons = forceMoonsCount ?? DetectMoonsAroundPlanet(frame, pcx, pcy, d / 2.0, cornerMeanLum);

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
                // 4. V\u00e9nus : disque tr\u00e8s brillant (albedo tr\u00e8s fort) sans lune
                else if (normMeanLum > 0.40 && moons == 0 && (d >= 20.0 || aspectRatio > 1.15))
                {
                    bodyFr = "V\u00e9nus";
                    bodyEn = "Venus";
                    details = $"V\u00e9nus identifi\u00e9e : disque \u00e0 tr\u00e8s haut alb\u00e9do (diam\u00e8tre apparent {d:F0} px)";
                }
                // 5. Jupiter : grand disque quasi-circulaire ou lunes galil\u00e9ennes d\u00e9tect\u00e9es
                else if (moons > 0 || (d >= 28.0 && aspectRatio < 1.25))
                {
                    bodyFr = "Jupiter";
                    bodyEn = "Jupiter";
                    details = moons > 0
                        ? $"Jupiter identifi\u00e9e avec {moons} satellite{(moons > 1 ? "s" : "")} galil\u00e9en{(moons > 1 ? "s" : "")} (diam\u00e8tre {d:F0} px)"
                        : $"Jupiter identifi\u00e9e : grand disque circulaire (diam\u00e8tre apparent {d:F0} px)";
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

    /// <summary>
    /// Extrait les candidats de lunes ponctuelles autour d'une plan\u00e8te sur une frame unique,
    /// avec \u00e9limination du bruit mono-pixel et adaptation au bruit r\u00e9el de fond de ciel.
    /// </summary>
    public static List<MoonSpot> ExtractMoonCandidateSpots(
        AstroFrame frame,
        double cx,
        double cy,
        double radiusPx,
        double cornerMean,
        double cornerStdDev)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        if (w <= 0 || h <= 0 || radiusPx < 4.0) return [];

        double innerR = radiusPx * 1.20;
        double outerR = Math.Min(Math.Max(w, h), Math.Max(radiusPx * 25.0, 150.0));
        if (outerR <= innerR) return [];

        double innerR2 = innerR * innerR;
        double outerR2 = outerR * outerR;

        // Seuil robuste contre le bruit : moyenne des coins + 3.5 * sigma + marge
        ushort moonThreshold = (ushort)Math.Clamp(
            Math.Max(cornerMean + 3.5 * cornerStdDev + 1500, cornerMean * 1.35 + 1200),
            2200,
            60000);

        List<MoonSpot> spots = [];
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

                        ushort lum = ch == 1
                            ? p[row + x]
                            : Math.Max(p[(row + x) * 3], Math.Max(p[(row + x) * 3 + 1], p[(row + x) * 3 + 2]));

                        if (lum >= moonThreshold)
                        {
                            int count = 0;
                            long clusterSumX = 0, clusterSumY = 0;
                            ushort peakLum = 0;
                            Queue<(int X, int Y)> queue = new();
                            queue.Enqueue((x, y));
                            visited[x, y] = true;

                            while (queue.Count > 0 && count < 60)
                            {
                                var (qx, qy) = queue.Dequeue();
                                count++;
                                clusterSumX += qx;
                                clusterSumY += qy;

                                ushort qLum = ch == 1
                                    ? p[qy * w + qx]
                                    : Math.Max(p[(qy * w + qx) * 3], Math.Max(p[(qy * w + qx) * 3 + 1], p[(qy * w + qx) * 3 + 2]));
                                if (qLum > peakLum) peakLum = qLum;

                                for (int ny = Math.Max(0, qy - 1); ny <= Math.Min(h - 1, qy + 1); ny++)
                                {
                                    int nRow = ny * w;
                                    for (int nx = Math.Max(0, qx - 1); nx <= Math.Min(w - 1, qx + 1); nx++)
                                    {
                                        if (visited[nx, ny]) continue;
                                        ushort nLum = ch == 1
                                            ? p[nRow + nx]
                                            : Math.Max(p[(nRow + nx) * 3], Math.Max(p[(nRow + nx) * 3 + 1], p[(nRow + nx) * 3 + 2]));
                                        if (nLum >= moonThreshold)
                                        {
                                            visited[nx, ny] = true;
                                            queue.Enqueue((nx, ny));
                                        }
                                    }
                                }
                            }

                            // Les lunes sont de petites sources ponctuelles compactes (2 \u00e0 50 pixels)
                            // Les pixels chauds et le bruit isol\u00e9 mono-pixel sont strictement rejet\u00e9s.
                            bool validCluster = (count >= 3 && count <= 50) ||
                                                (count == 2 && peakLum >= moonThreshold * 1.20);

                            if (validCluster)
                            {
                                int candX = (int)(clusterSumX / count);
                                int candY = (int)(clusterSumY / count);
                                bool duplicate = false;
                                foreach (var s in spots)
                                {
                                    int cdx = s.X - candX;
                                    int cdy = s.Y - candY;
                                    if (cdx * cdx + cdy * cdy < 36)
                                    {
                                        duplicate = true;
                                        break;
                                    }
                                }
                                if (!duplicate)
                                {
                                    spots.Add(new MoonSpot(candX, candY, count, peakLum));
                                }
                            }
                        }
                    }
                }
            }
        }

        return spots;
    }

    /// <summary>
    /// Filtre les candidats de lunes pour ne conserver que ceux qui sont approximativement colin\u00e9aires
    /// avec le centre de Jupiter (plan orbital des satellites galil\u00e9ens).
    /// </summary>
    private static List<T> FilterCollinearMoons<T>(
        IReadOnlyList<T> candidates,
        Func<T, (double X, double Y)> getCoords,
        double cx,
        double cy,
        double planetRadius)
    {
        if (candidates.Count <= 1) return candidates.ToList();

        double bestCount = 0;
        double bestPhi = 0;
        double maxDist = Math.Max(6.0, planetRadius * 0.18);

        for (int deg = 0; deg < 180; deg++)
        {
            double rad = deg * Math.PI / 180.0;
            double sin = Math.Sin(rad);
            double cos = Math.Cos(rad);
            int count = 0;

            foreach (var c in candidates)
            {
                var (x, y) = getCoords(c);
                double dx = x - cx;
                double dy = y - cy;
                double dist = Math.Abs(-dx * sin + dy * cos);
                if (dist <= maxDist) count++;
            }

            if (count > bestCount)
            {
                bestCount = count;
                bestPhi = rad;
            }
        }

        double bestSin = Math.Sin(bestPhi);
        double bestCos = Math.Cos(bestPhi);
        var inliers = new List<T>();
        foreach (var c in candidates)
        {
            var (x, y) = getCoords(c);
            double dx = x - cx;
            double dy = y - cy;
            double dist = Math.Abs(-dx * bestSin + dy * bestCos);
            if (dist <= maxDist) inliers.Add(c);
        }

        return inliers;
    }

    /// <summary>
    /// D\u00e9tecte les lunes / satellites ponctuels autour d'une plan\u00e8te (ex: satellites galil\u00e9ens de Jupiter)
    /// dans une zone annulaire autour du disque plan\u00e9taire, avec filtrage de colin\u00e9arit\u00e9.
    /// </summary>
    public static int DetectMoonsAroundPlanet(AstroFrame frame, double cx, double cy, double radiusPx, double backgroundThreshold16)
    {
        var (cornerMean, cornerStdDev) = SampleCornersStats(frame);
        var spots = ExtractMoonCandidateSpots(frame, cx, cy, radiusPx, cornerMean, cornerStdDev);
        if (spots.Count == 0) return 0;

        var collinear = FilterCollinearMoons(spots, s => (s.X, s.Y), cx, cy, radiusPx);
        return Math.Clamp(collinear.Count, 0, 4);
    }

    public static (double Mean, double StdDev) SampleCornersStats(AstroFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        int box = Math.Min(16, Math.Min(w / 4, h / 4));
        if (box <= 0) return (0.0, 0.0);

        long cornerTotal = 0;
        double cornerTotalSq = 0.0;
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
                                cornerTotalSq += (double)lum * lum;
                                count++;
                            }
                        }
                    }
                }
            }
        }

        if (count == 0) return (0.0, 0.0);
        double mean = (double)cornerTotal / count;
        double variance = Math.Max(0.0, (cornerTotalSq / count) - (mean * mean));
        double stdDev = Math.Sqrt(variance);
        return (mean, stdDev);
    }

    private static double SampleCornersMeanLuminance(AstroFrame frame) => SampleCornersStats(frame).Mean;
}
