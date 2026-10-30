using System;
using System.Collections.Generic;
using System.Linq;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Paramètres de configuration pour la détection des satellites naturels (lunes de Jupiter / Saturne).
/// </summary>
public sealed record SatelliteDetectionOptions(
    bool Enabled = true,
    double Sensitivity = 1.0
);

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

/// <summary>
/// Candidat ponctuel extrait sur une frame individuelle avant filtrage multi-frame et colinéarité.
/// </summary>
public sealed record SatelliteSpotCandidate(
    double X,
    double Y,
    double RelX,
    double RelY,
    int PixelCount,
    ushort PeakLuminance,
    double LocalSnr,
    double NeighborRatio,
    int FrameIndex = 0
);

/// <summary>
/// Service de domaine dédié à la détection avancée et robuste des satellites naturels
/// (lunes galiléennes de Jupiter, satellites de Saturne, etc.) sans plafond artificiel sur le nombre de lunes.
/// Combine :
/// 1. Analyse du fond de ciel local annulaire (élimine l'effet du halo de diffusion planétaire),
/// 2. Filtrage morphologique de la PSF à mi-hauteur FWHM (rejette les pixels chauds isolés sans pénaliser les lunes brillantes),
/// 3. Vérification d'achromaticité sur les images couleur (rejette les pixels chauds débayérisés R/G/B),
/// 4. Persistance temporelle multi-frame tenant compte de la présence effective dans le champ (dérive Dobson/Alt-Az),
/// 5. Régression colinéaire orbitale passant par le centre planétaire.
/// </summary>
public static class NaturalSatelliteDetector
{
    /// <summary>
    /// Détecte les satellites naturels sur une séquence de frames échantillonnées d'une vidéo.
    /// </summary>
    public static IReadOnlyList<DetectedSatellite> DetectAcrossFrames(
        IReadOnlyList<(AstroFrame Frame, int FrameIndex)> frames,
        double? measuredDiameterPx = null,
        SatelliteDetectionOptions? options = null,
        bool isSaturn = false)
    {
        var opts = options ?? new SatelliteDetectionOptions();
        if (!opts.Enabled || frames == null || frames.Count == 0)
        {
            return [];
        }

        if (frames.Count == 1)
        {
            var single = frames[0].Frame;
            var (cMean, _) = SampleCornersStats(single);
            double d = measuredDiameterPx ?? (single.Width * 0.1);
            var disk = CelestialTargetClassifier.AnalyzePlanetDisk(single, d, cMean);
            if (!disk.Found) return [];
            return DetectInSingleFrame(single, disk.Cx, disk.Cy, disk.Radius, opts);
        }

        double sensitivity = Math.Clamp(opts.Sensitivity, 0.25, 3.0);
        // En multi-frame, on autorise une sensibilité légèrement plus souple par frame individuelle
        // pour capter les lunes atténuées par la turbulence atmosphérique (seeing), puis on valide
        // par persistance temporelle et cohérence médiane.
        var perFrameOpts = new SatelliteDetectionOptions(true, sensitivity * 1.25);

        var allCandidates = new List<SatelliteSpotCandidate>();
        var validFrames = new List<(AstroFrame Frame, double Cx, double Cy, double Radius, double CornerMean, double CornerStd)>();

        foreach (var (frame, frameIdx) in frames)
        {
            if (frame == null || frame.Width <= 0 || frame.Height <= 0) continue;

            var (cMean, cStd) = SampleCornersStats(frame);
            double d = measuredDiameterPx ?? (frame.Width * 0.1);
            var disk = CelestialTargetClassifier.AnalyzePlanetDisk(frame, d, cMean);
            if (!disk.Found || disk.Radius < 4.0) continue;

            validFrames.Add((frame, disk.Cx, disk.Cy, disk.Radius, cMean, cStd));

            var spots = ExtractCandidateSpots(
                frame,
                disk.Cx,
                disk.Cy,
                disk.Radius,
                cMean,
                cStd,
                perFrameOpts.Sensitivity,
                frameIdx);

            allCandidates.AddRange(spots);
        }

        if (validFrames.Count == 0) return [];

        if (validFrames.Count == 1)
        {
            var vf = validFrames[0];
            return DetectInSingleFrame(vf.Frame, vf.Cx, vf.Cy, vf.Radius, opts, isSaturn);
        }

        double avgRadius = validFrames.Average(v => v.Radius);

        // Regroupement spatial dans le référentiel relatif au centre de la planète (rayon de fusion 6.5 px)
        var clusters = new List<List<SatelliteSpotCandidate>>();
        foreach (var cand in allCandidates)
        {
            List<SatelliteSpotCandidate>? bestCluster = null;
            double bestDist2 = 42.25; // 6.5^2

            foreach (var cl in clusters)
            {
                double avgRelX = cl.Average(c => c.RelX);
                double avgRelY = cl.Average(c => c.RelY);
                double dx = cand.RelX - avgRelX;
                double dy = cand.RelY - avgRelY;
                double d2 = dx * dx + dy * dy;
                if (d2 <= bestDist2)
                {
                    bestDist2 = d2;
                    bestCluster = cl;
                }
            }

            if (bestCluster != null)
            {
                bestCluster.Add(cand);
            }
            else
            {
                clusters.Add([cand]);
            }
        }

        double snrScale = 1.0 / sensitivity;
        var persistentCandidates = new List<DetectedSatellite>();

        foreach (var cl in clusters)
        {
            int distinctFrames = cl.Select(c => c.FrameIndex).Distinct().Count();
            double relX = cl.Average(c => c.RelX);
            double relY = cl.Average(c => c.RelY);

            // Nombre de frames où cette position relative (Cx + relX, Cy + relY) est effectivement
            // à l'intérieur des limites du capteur (important lors d'une dérive planétaire en Dobson / Alt-Az
            // où une lune lointaine n'entre dans le champ que sur une partie de la vidéo).
            int inFieldFrames = CountInFieldFrames(validFrames, relX, relY);
            if (inFieldFrames == 0) continue;

            int minRequiredFrames = inFieldFrames <= 1
                ? 1
                : Math.Max(2, (int)Math.Ceiling(inFieldFrames * 0.35));

            if (distinctFrames < minRequiredFrames) continue;

            double distPx = Math.Sqrt(relX * relX + relY * relY);
            double distRadii = avgRadius > 0 ? distPx / avgRadius : 0;

            // Vérification du signal temporel recalé sur les frames où la lune est dans le champ
            double temporalSnr = ComputeTemporalRegisteredSnr(validFrames, relX, relY);
            double maxFrameSnr = cl.Max(c => c.LocalSnr);
            double persistenceRatio = Math.Clamp((double)distinctFrames / Math.Max(1, inFieldFrames), 0.0, 1.0);
            double combinedSnr = Math.Max(temporalSnr, maxFrameSnr * persistenceRatio);

            // Si la lune n'était dans le champ du capteur que sur 1 seule frame échantillonnée (bord extrême),
            // on exige un SNR beaucoup plus strict pour exclure tout bruit éphémère.
            double minSnr = (distinctFrames == 1 ? 10.0 : 4.5) * snrScale;
            if (combinedSnr < minSnr) continue;

            persistentCandidates.Add(new DetectedSatellite(
                RelX: relX,
                RelY: relY,
                DistanceInRadii: distRadii,
                Snr: combinedSnr,
                PixelCount: (int)Math.Round(cl.Average(c => c.PixelCount)),
                PeakLuminance: cl.Max(c => c.PeakLuminance)
            ));
        }

        if (persistentCandidates.Count == 0) return [];

        var collinear = FilterCollinearSatellites(
            persistentCandidates,
            avgRadius,
            snrScale,
            totalFramesCount: validFrames.Count,
            isSaturn: isSaturn);

        return collinear
            .OrderByDescending(s => s.Snr)
            .ToList();
    }

    /// <summary>
    /// Détecte les satellites naturels autour d'une planète sur une frame unique.
    /// </summary>
    public static IReadOnlyList<DetectedSatellite> DetectInSingleFrame(
        AstroFrame frame,
        double cx,
        double cy,
        double radiusPx,
        SatelliteDetectionOptions? options = null,
        bool isSaturn = false)
    {
        var opts = options ?? new SatelliteDetectionOptions();
        if (!opts.Enabled || frame == null || frame.Width <= 0 || frame.Height <= 0 || radiusPx < 4.0)
        {
            return [];
        }

        double sensitivity = Math.Clamp(opts.Sensitivity, 0.25, 3.0);
        double snrScale = 1.0 / sensitivity;

        var (cornerMean, cornerStdDev) = SampleCornersStats(frame);
        var spots = ExtractCandidateSpots(frame, cx, cy, radiusPx, cornerMean, cornerStdDev, sensitivity, frameIndex: 0);
        if (spots.Count == 0) return [];

        var candidates = spots
            .Select(s => new DetectedSatellite(
                RelX: s.RelX,
                RelY: s.RelY,
                DistanceInRadii: Math.Sqrt(s.RelX * s.RelX + s.RelY * s.RelY) / radiusPx,
                Snr: s.LocalSnr,
                PixelCount: s.PixelCount,
                PeakLuminance: s.PeakLuminance))
            .ToList();

        var collinear = FilterCollinearSatellites(
            candidates,
            radiusPx,
            snrScale,
            totalFramesCount: 1,
            isSaturn: isSaturn);

        return collinear
            .OrderByDescending(s => s.Snr)
            .ToList();
    }

    /// <summary>
    /// Extrait les candidats de satellites ponctuels autour d'une planète sur une frame donnée,
    /// en appliquant :
    /// - un seuillage adaptatif au bruit,
    /// - une analyse SNR sur couronne locale annulaire (5..9 px) pour éliminer les gradients de halo,
    /// - une mesure morphologique de la PSF à mi-hauteur (FWHM) pour supporter aussi bien les lunes faibles
    ///   que les lunes très brillantes entourées d'un halo de diffusion,
    /// - un contrôle d'achromaticité sur les trames RGB (rejet des pixels chauds débayérisés).
    /// </summary>
    public static List<SatelliteSpotCandidate> ExtractCandidateSpots(
        AstroFrame frame,
        double cx,
        double cy,
        double radiusPx,
        double cornerMean,
        double cornerStdDev,
        double sensitivity = 1.0,
        int frameIndex = 0)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        if (w <= 0 || h <= 0 || radiusPx < 4.0) return [];

        double sens = Math.Clamp(sensitivity, 0.25, 3.0);
        double snrScale = 1.0 / sens;

        // Zone annulaire de recherche : au-delà du limbe (1.22 R_p) jusqu'aux élongations lointaines (26.5 R_p)
        double innerR = Math.Max(radiusPx * 1.22, radiusPx + 3.5);
        double outerR = Math.Min(Math.Max(w, h), Math.Max(radiusPx * 26.5, 160.0));
        if (outerR <= innerR) return [];

        double innerR2 = innerR * innerR;
        double outerR2 = outerR * outerR;

        double globalSigma = Math.Max(cornerStdDev, 80.0);
        ushort seedThreshold = (ushort)Math.Clamp(
            Math.Max(cornerMean + 3.5 * globalSigma * snrScale + 1200.0 * snrScale, cornerMean * 1.30 + 1000.0 * snrScale),
            1800,
            62000);

        var spots = new List<SatelliteSpotCandidate>();
        bool[,] visited = new bool[w, h];

        unsafe
        {
            fixed (ushort* p = frame.Data)
            {
                int minSearchX = Math.Max(2, (int)(cx - outerR));
                int maxSearchX = Math.Min(w - 3, (int)(cx + outerR));
                int minSearchY = Math.Max(2, (int)(cy - outerR));
                int maxSearchY = Math.Min(h - 3, (int)(cy + outerR));

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

                        ushort lum = GetPixelMaxLum(p, row + x, ch);
                        if (lum < seedThreshold) continue;

                        // Composante connexe 8-voisinage pour localiser le maximum local
                        int count = 0;
                        ushort peakLum = 0;
                        int peakX = x, peakY = y;

                        Queue<(int X, int Y)> queue = new();
                        queue.Enqueue((x, y));
                        visited[x, y] = true;

                        while (queue.Count > 0 && count < 140)
                        {
                            var (qx, qy) = queue.Dequeue();
                            count++;

                            ushort qLum = GetPixelMaxLum(p, qy * w + qx, ch);
                            if (qLum > peakLum)
                            {
                                peakLum = qLum;
                                peakX = qx;
                                peakY = qy;
                            }

                            for (int ny = Math.Max(1, qy - 1); ny <= Math.Min(h - 2, qy + 1); ny++)
                            {
                                int nRow = ny * w;
                                for (int nx = Math.Max(1, qx - 1); nx <= Math.Min(w - 2, qx + 1); nx++)
                                {
                                    if (visited[nx, ny]) continue;
                                    ushort nLum = GetPixelMaxLum(p, nRow + nx, ch);
                                    if (nLum >= seedThreshold)
                                    {
                                        visited[nx, ny] = true;
                                        queue.Enqueue((nx, ny));
                                    }
                                }
                            }
                        }

                        // Rejet strict des pixels chauds mono-pixel (count < 2) et des grandes régions (count >= 130)
                        if (count < 2 || count >= 130) continue;

                        // Vérification du fond de ciel local dans une couronne annulaire [5..9 px] autour du pic
                        var (localMean, localStd) = SampleLocalAnnulusStats(p, w, h, ch, peakX, peakY, cx, cy, radiusPx * 1.15);
                        double bg = Math.Max(localMean, cornerMean);
                        double netPeak = peakLum - bg;
                        if (netPeak < 1200.0 * snrScale) continue;

                        double effectiveSigma = Math.Max(localStd, Math.Max(globalSigma, 120.0));
                        double localSnr = netPeak / effectiveSigma;
                        if (localSnr < 4.5 * snrScale) continue;

                        // Analyse morphologique à mi-hauteur (FWHM : 50% du pic net au-dessus du fond local)
                        // Permet de mesurer la taille réelle du cœur de la PSF sans être faussé par le halo
                        // périphérique, les blocs de compression vidéo ou un léger filé de bougé/dérive.
                        ushort halfMaxThresh = (ushort)Math.Clamp(bg + 0.50 * netPeak, seedThreshold, peakLum);
                        int fwhmCount = 0;
                        int fMinX = peakX, fMaxX = peakX, fMinY = peakY, fMaxY = peakY;
                        double weightedSumX = 0, weightedSumY = 0, totalWeight = 0;

                        for (int wy = Math.Max(1, peakY - 7); wy <= Math.Min(h - 2, peakY + 7); wy++)
                        {
                            int wRow = wy * w;
                            for (int wx = Math.Max(1, peakX - 7); wx <= Math.Min(w - 2, peakX + 7); wx++)
                            {
                                ushort wLum = GetPixelMaxLum(p, wRow + wx, ch);
                                if (wLum >= halfMaxThresh)
                                {
                                    fwhmCount++;
                                    if (wx < fMinX) fMinX = wx;
                                    if (wx > fMaxX) fMaxX = wx;
                                    if (wy < fMinY) fMinY = wy;
                                    if (wy > fMaxY) fMaxY = wy;

                                    double netW = Math.Max(1.0, wLum - bg);
                                    weightedSumX += wx * netW;
                                    weightedSumY += wy * netW;
                                    totalWeight += netW;
                                }
                            }
                        }

                        if (fwhmCount < 1 || fwhmCount > 65) continue;
                        int fBoxW = fMaxX - fMinX + 1;
                        int fBoxH = fMaxY - fMinY + 1;
                        if (Math.Max(fBoxW, fBoxH) > 14) continue;
                        if (fwhmCount >= 6 && (double)Math.Max(fBoxW, fBoxH) / Math.Max(1, Math.Min(fBoxW, fBoxH)) > 4.2) continue;

                        double candX = totalWeight > 0 ? weightedSumX / totalWeight : peakX;
                        double candY = totalWeight > 0 ? weightedSumY / totalWeight : peakY;

                        // Vérification morphologique de la PSF (étalement radial 3x3 autour du pic)
                        double neighborRatio = EvaluatePsfNeighborRatio(p, w, h, ch, peakX, peakY, bg, netPeak, out int activeNeighbors);
                        if (activeNeighbors < 1 || neighborRatio < 0.18) continue;

                        // Vérification d'achromaticité sur image couleur (rejette les pixels chauds débayérisés purement R, G ou B)
                        if (ch >= 3 && !IsAchromaticSpot(p, w, h, peakX, peakY, cornerMean))
                        {
                            continue;
                        }

                        // Déduplication locale (distance minimale 4.5 px entre deux lunes)
                        int existingIdx = -1;
                        for (int i = 0; i < spots.Count; i++)
                        {
                            double ddx = spots[i].X - candX;
                            double ddy = spots[i].Y - candY;
                            if (ddx * ddx + ddy * ddy < 20.25)
                            {
                                existingIdx = i;
                                break;
                            }
                        }

                        var candidate = new SatelliteSpotCandidate(
                            X: candX,
                            Y: candY,
                            RelX: candX - cx,
                            RelY: candY - cy,
                            PixelCount: fwhmCount,
                            PeakLuminance: peakLum,
                            LocalSnr: localSnr,
                            NeighborRatio: neighborRatio,
                            FrameIndex: frameIndex);

                        if (existingIdx >= 0)
                        {
                            if (candidate.LocalSnr > spots[existingIdx].LocalSnr)
                            {
                                spots[existingIdx] = candidate;
                            }
                        }
                        else
                        {
                            spots.Add(candidate);
                        }
                    }
                }
            }
        }

        return spots;
    }

    private static int CountInFieldFrames(
        IReadOnlyList<(AstroFrame Frame, double Cx, double Cy, double Radius, double CornerMean, double CornerStd)> validFrames,
        double relX,
        double relY)
    {
        int inField = 0;
        foreach (var vf in validFrames)
        {
            double x = vf.Cx + relX;
            double y = vf.Cy + relY;
            if (x >= 2.0 && x < vf.Frame.Width - 2.0 && y >= 2.0 && y < vf.Frame.Height - 2.0)
            {
                inField++;
            }
        }
        return inField;
    }

    /// <summary>
    /// Filtre les satellites candidats par alignement colinéaire avec le centre de la planète (plan orbital équatorial).
    /// Pour Saturne, l'inclinaison apparente des anneaux et de l'orbite de Titan permet une composante hors-axe (ellipse orbitale).
    /// </summary>
    private static List<DetectedSatellite> FilterCollinearSatellites(
        IReadOnlyList<DetectedSatellite> candidates,
        double planetRadius,
        double snrScale,
        int totalFramesCount,
        bool isSaturn = false)
    {
        if (candidates.Count == 0) return [];

        if (candidates.Count == 1)
        {
            double singleMinSnr = (totalFramesCount > 1 ? 5.2 : 6.0) * snrScale;
            return candidates[0].Snr >= singleMinSnr ? [candidates[0]] : [];
        }

        int bestCount = 0;
        double bestErrorSum = double.MaxValue;
        double bestPhi = 0;

        // Recherche de l'axe orbital passant par le centre de la planète (pas de 0.5 degré)
        for (int step = 0; step < 360; step++)
        {
            double rad = step * 0.5 * Math.PI / 180.0;
            double sin = Math.Sin(rad);
            double cos = Math.Cos(rad);
            int count = 0;
            double errorSum = 0;

            foreach (var c in candidates)
            {
                double distFromCenter = Math.Sqrt(c.RelX * c.RelX + c.RelY * c.RelY);
                double maxPerpDist = Math.Max(6.0, planetRadius * 0.18 + distFromCenter * 0.035);
                double perpDist = Math.Abs(-c.RelX * sin + c.RelY * cos);
                if (perpDist <= maxPerpDist)
                {
                    count++;
                    errorSum += perpDist * perpDist;
                }
            }

            if (count > bestCount || (count == bestCount && errorSum < bestErrorSum))
            {
                bestCount = count;
                bestErrorSum = errorSum;
                bestPhi = rad;
            }
        }

        if (bestCount <= 1)
        {
            var strongest = candidates.OrderByDescending(c => c.Snr).First();
            double loneMinSnr = 7.0 * snrScale;
            return strongest.Snr >= loneMinSnr ? [strongest] : [];
        }

        double bestSin = Math.Sin(bestPhi);
        double bestCos = Math.Cos(bestPhi);
        var inliers = new List<DetectedSatellite>();

        foreach (var c in candidates)
        {
            double distFromCenter = Math.Sqrt(c.RelX * c.RelX + c.RelY * c.RelY);
            double maxPerpDist = Math.Max(6.0, planetRadius * 0.18 + distFromCenter * 0.035);
            double perpDist = Math.Abs(-c.RelX * bestSin + c.RelY * bestCos);

            if (perpDist <= maxPerpDist)
            {
                inliers.Add(c);
            }
            else if (isSaturn)
            {
                // Pour Saturne : inclinaison des anneaux jusqu'à ~27° (sin(27°) ≈ 0.45).
                // Les gros satellites comme Titan circulent sur une ellipse orbitale inclinée.
                // Si le candidat présente un SNR très net (>= 8.0 * snrScale) et reste dans
                // l'enveloppe de projection orbitale de Saturne (perpDist <= distFromCenter * 0.55 + 10px),
                // il s'agit d'un satellite légitime hors du plan fin équatorial.
                double maxInclinedPerp = Math.Max(maxPerpDist, distFromCenter * 0.55 + 10.0);
                if (perpDist <= maxInclinedPerp && c.Snr >= 8.0 * snrScale)
                {
                    inliers.Add(c);
                }
            }
        }

        return inliers;
    }

    private static unsafe double ComputeTemporalRegisteredSnr(
        IReadOnlyList<(AstroFrame Frame, double Cx, double Cy, double Radius, double CornerMean, double CornerStd)> validFrames,
        double relX,
        double relY)
    {
        var netSignals = new List<double>(validFrames.Count);
        double avgSigma = 0;

        foreach (var vf in validFrames)
        {
            int w = vf.Frame.Width;
            int h = vf.Frame.Height;
            int ch = vf.Frame.Channels;
            int tx = (int)Math.Round(vf.Cx + relX);
            int ty = (int)Math.Round(vf.Cy + relY);

            if (tx < 2 || tx >= w - 2 || ty < 2 || ty >= h - 2) continue;

            fixed (ushort* p = vf.Frame.Data)
            {
                ushort localPeak = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int row = (ty + dy) * w;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        ushort lum = GetPixelMaxLum(p, row + (tx + dx), ch);
                        if (lum > localPeak) localPeak = lum;
                    }
                }

                var (localMean, localStd) = SampleLocalAnnulusStats(p, w, h, ch, tx, ty, vf.Cx, vf.Cy, vf.Radius * 1.15);
                double bg = Math.Max(localMean, vf.CornerMean);
                double sigma = Math.Max(localStd, Math.Max(vf.CornerStd, 120.0));
                netSignals.Add(Math.Max(0.0, localPeak - bg));
                avgSigma += sigma;
            }
        }

        if (netSignals.Count == 0) return 0.0;
        avgSigma /= netSignals.Count;

        netSignals.Sort();
        int midIdx = netSignals.Count / 2;
        double medianNet = netSignals[midIdx];
        return medianNet / Math.Max(100.0, avgSigma);
    }

    private static unsafe (double Mean, double StdDev) SampleLocalAnnulusStats(
        ushort* p,
        int w,
        int h,
        int ch,
        int cxSpot,
        int cySpot,
        double planetCx,
        double planetCy,
        double minPlanetDist)
    {
        const int rIn2 = 64;   // r_in = 8 px (au-delà des ailes de la PSF / filé de dérive)
        const int rOut2 = 169; // r_out = 13 px
        double minPlanetDist2 = minPlanetDist * minPlanetDist;

        int minX = Math.Max(0, cxSpot - 13);
        int maxX = Math.Min(w - 1, cxSpot + 13);
        int minY = Math.Max(0, cySpot - 13);
        int maxY = Math.Min(h - 1, cySpot + 13);

        long sum = 0;
        double sumSq = 0;
        int count = 0;

        for (int y = minY; y <= maxY; y++)
        {
            int dySpot = y - cySpot;
            int dySpot2 = dySpot * dySpot;
            double dyPlanet = y - planetCy;
            double dyPlanet2 = dyPlanet * dyPlanet;
            int row = y * w;

            for (int x = minX; x <= maxX; x++)
            {
                int dxSpot = x - cxSpot;
                int distSpot2 = dxSpot * dxSpot + dySpot2;
                if (distSpot2 < rIn2 || distSpot2 > rOut2) continue;

                double dxPlanet = x - planetCx;
                if (dxPlanet * dxPlanet + dyPlanet2 < minPlanetDist2) continue;

                ushort lum = GetPixelMaxLum(p, row + x, ch);
                sum += lum;
                sumSq += (double)lum * lum;
                count++;
            }
        }

        if (count < 8) return (0.0, 0.0);
        double mean = (double)sum / count;
        double variance = Math.Max(0.0, (sumSq / count) - (mean * mean));
        return (mean, Math.Sqrt(variance));
    }

    private static unsafe double EvaluatePsfNeighborRatio(
        ushort* p,
        int w,
        int h,
        int ch,
        int peakX,
        int peakY,
        double background,
        double netPeak,
        out int activeNeighbors)
    {
        activeNeighbors = 0;
        if (netPeak <= 0) return 0.0;

        Span<double> neighborNets = stackalloc double[8];
        int idx = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = peakY + dy;
            if (ny < 0 || ny >= h) continue;
            int row = ny * w;
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = peakX + dx;
                if (nx < 0 || nx >= w) continue;

                ushort lum = GetPixelMaxLum(p, row + nx, ch);
                double net = Math.Max(0.0, lum - background);
                neighborNets[idx++] = net;
                if (net >= 0.16 * netPeak)
                {
                    activeNeighbors++;
                }
            }
        }

        if (idx == 0) return 0.0;
        neighborNets[..idx].Sort();

        int take = Math.Min(4, idx);
        double sumTop = 0;
        for (int i = idx - take; i < idx; i++)
        {
            sumTop += neighborNets[i];
        }

        return (sumTop / take) / netPeak;
    }

    private static unsafe bool IsAchromaticSpot(
        ushort* p,
        int w,
        int h,
        int peakX,
        int peakY,
        double cornerMean)
    {
        double sumR = 0, sumG = 0, sumB = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = Math.Clamp(peakY + dy, 0, h - 1);
            int row = ny * w;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = Math.Clamp(peakX + dx, 0, w - 1);
                int idx = (row + nx) * 3;
                sumR += Math.Max(0.0, p[idx] - cornerMean);
                sumG += Math.Max(0.0, p[idx + 1] - cornerMean);
                sumB += Math.Max(0.0, p[idx + 2] - cornerMean);
            }
        }

        double maxCh = Math.Max(sumR, Math.Max(sumG, sumB));
        if (maxCh <= 1.0) return false;

        double minCh = Math.Min(sumR, Math.Min(sumG, sumB));
        double midCh = (sumR + sumG + sumB) - maxCh - minCh;

        // Un pixel chaud capteur (même débayérisé) concentre presque toute son énergie sur 1 seul canal couleur.
        // Une lune réelle éclaire au moins 2 canaux significativement (spectre solaire continu).
        return (midCh / maxCh) >= 0.22;
    }

    private static unsafe ushort GetPixelMaxLum(ushort* p, int pixelIndex, int channels)
    {
        if (channels == 1)
        {
            return p[pixelIndex];
        }
        int idx = pixelIndex * 3;
        return Math.Max(p[idx], Math.Max(p[idx + 1], p[idx + 2]));
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
                                ushort lum = GetPixelMaxLum(p, row + px, ch);
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
        return (mean, Math.Sqrt(variance));
    }
}
