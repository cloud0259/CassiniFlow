using System;
using System.Collections.Generic;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class CelestialTargetClassifierTests
{
    [Fact]
    public void Classify_CompactDiskOnBlackBackground_ClassifiesAsPlanet()
    {
        // 800x600, disk radius 40 (diameter 80px), black background
        int w = 800, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 400, cy = 300, r = 40;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 45000;
                    frame.Data[idx + 1] = 45000;
                    frame.Data[idx + 2] = 45000;
                }
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 80.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal(1, result.RecommendedPresetIndex);
        Assert.Contains("Planète", result.DisplayNameFr);
    }

    [Fact]
    public void Classify_LargeDiskOnBlackBackground_ClassifiesAsFullDisc()
    {
        // 1000x1000, disk radius 350 (diameter 700px), black corners
        int w = 1000, h = 1000;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 500, cy = 500, r = 350;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 40000;
                    frame.Data[idx + 1] = 40000;
                    frame.Data[idx + 2] = 40000;
                }
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 700.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.MoonOrSunFullDisc, result.TargetType);
        Assert.Equal(3, result.RecommendedPresetIndex);
        Assert.Contains("disque entier", result.DisplayNameFr);
    }

    [Fact]
    public void Classify_ExtendedSurfaceAcrossWholeFrame_ClassifiesAsSurfaceCloseup()
    {
        // 800x600, bright surface covering entire frame including corners
        int w = 800, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);

        for (int i = 0; i < w * h * 3; i++)
        {
            frame.Data[i] = 30000; // ~45% luminance uniformly across entire frame
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: null, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.MoonOrSunSurface, result.TargetType);
        Assert.Equal(2, result.RecommendedPresetIndex);
        Assert.Contains("Surface", result.DisplayNameFr);
    }

    [Fact]
    public void Classify_HAlphaSolarSurface_IdentifiesHAlpha()
    {
        // High red channel, low green and blue
        int w = 400, h = 400;
        var frame = new AstroFrame(w, h, 3, 16);

        for (int i = 0; i < w * h; i++)
        {
            frame.Data[i * 3] = 45000;     // Red
            frame.Data[i * 3 + 1] = 12000; // Green
            frame.Data[i * 3 + 2] = 5000;  // Blue
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: null, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.MoonOrSunSurface, result.TargetType);
        Assert.Equal(2, result.RecommendedPresetIndex);
        Assert.Contains("Soleil", result.DisplayNameFr);
        Assert.Contains("H-Alpha", result.DisplayNameFr);
    }

    [Fact]
    public void Classify_CompletelyBlackFrame_ReturnsUnknown()
    {
        int w = 200, h = 200;
        var frame = new AstroFrame(w, h, 3, 16);
        // All pixels 0

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: null, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Unknown, result.TargetType);
        Assert.Equal(0, result.RecommendedPresetIndex);
    }

    [Fact]
    public void Classify_ElongatedPlanetDisk_IdentifiesSaturnWithRings()
    {
        int w = 500, h = 500;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 250, cy = 250;
        // Oblong shape: rx = 40, ry = 22 -> aspect ratio ~ 1.8
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double dx = (x - cx) / 40.0;
                double dy = (y - cy) / 22.0;
                if (dx * dx + dy * dy <= 1.0)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 35000;
                    frame.Data[idx + 1] = 32000;
                    frame.Data[idx + 2] = 22000;
                }
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 55.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Saturne", result.SpecificBodyNameFr);
        Assert.Equal("Saturn", result.SpecificBodyNameEn);
        Assert.Contains("Saturne", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void Classify_RedTintedPlanetDisk_IdentifiesMars()
    {
        int w = 400, h = 400;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 200, cy = 200, r = 20;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 48000;     // Strong Red
                    frame.Data[idx + 1] = 28000; // Moderate Green
                    frame.Data[idx + 2] = 16000; // Low Blue
                }
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 40.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Mars", result.SpecificBodyNameFr);
        Assert.Equal("Mars", result.SpecificBodyNameEn);
        Assert.Equal("Mars", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void Classify_LargeCircularPlanetDisk_IdentifiesJupiter()
    {
        int w = 600, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 300, cy = 300, r = 30; // Jupiter disk

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 38000;
                    frame.Data[idx + 1] = 36000;
                    frame.Data[idx + 2] = 32000;
                }
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 60.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal("Jupiter", result.SpecificBodyNameEn);
        Assert.Equal(0, result.DetectedMoonsCount);
        Assert.Equal("Jupiter", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void Classify_PlanetWithThreeGalileanMoons_IdentifiesJupiterAndCountsMoons()
    {
        int w = 600, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 300, cy = 300, r = 25;

        // Disque de Jupiter
        PaintDisk(frame, cx, cy, r, 40000, 38000, 34000);

        // 3 lunes alignées sur l'axe équatorial (3x3 px chacune)
        int[] moonOffsetsX = [-85, 55, 130];
        foreach (int ox in moonOffsetsX)
        {
            PaintMoonSpot(frame, cx + ox, cy, 26000);
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 50.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal(3, result.DetectedMoonsCount);
        Assert.Equal("Jupiter (3 lunes détectées)", result.ObjectSummary(isFrench: true));
        Assert.Equal("Jupiter (3 moons detected)", result.ObjectSummary(isFrench: false));
    }

    [Fact]
    public void Classify_MoreThanFourCollinearSatellites_DoesNotCapAtFour()
    {
        // Vérifie qu'il n'y a aucune limite artificielle à 4 satellites (ex: 6 satellites alignés)
        int w = 800, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 400, cy = 300, r = 24;

        PaintDisk(frame, cx, cy, r, 42000, 40000, 36000);

        int[] moonOffsetsX = [-220, -145, -75, 65, 135, 240];
        foreach (int ox in moonOffsetsX)
        {
            PaintMoonSpot(frame, cx + ox, cy, 28000);
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 48.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal(6, result.DetectedMoonsCount);
        Assert.Equal("Jupiter (6 lunes détectées)", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void Classify_NoisyBackgroundAndDebayeredHotPixels_DetectsZeroMoons()
    {
        // Test critique : zéro faux positif sur fond bruité à fort gain avec pixels chauds mono-pixel
        // ET clusters 3x3 de pixels chauds débayérisés monochromatiques (purement rouges ou bleus)
        int w = 500, h = 500;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 250, cy = 250, r = 28;
        var rng = new Random(42);

        // Fond bruité à fort gain
        for (int i = 0; i < frame.Data.Length; i++)
        {
            frame.Data[i] = (ushort)rng.Next(600, 2100);
        }

        // Disque de Jupiter
        PaintDisk(frame, cx, cy, r, 42000, 39000, 35000);

        // 25 pixels chauds isolés (mono-pixel) très brillants
        for (int i = 0; i < 25; i++)
        {
            int hx = rng.Next(20, w - 20);
            int hy = rng.Next(20, h - 20);
            if ((hx - cx) * (hx - cx) + (hy - cy) * (hy - cy) < (r + 10) * (r + 10)) continue;
            int idx = (hy * w + hx) * 3;
            frame.Data[idx] = 55000;
            frame.Data[idx + 1] = 55000;
            frame.Data[idx + 2] = 55000;
        }

        // 3 pixels chauds débayérisés 3x3 monochromatiques (canal R seul ou canal B seul)
        int[] hotClusterX = [cx - 90, cx + 75, cx + 140];
        for (int c = 0; c < hotClusterX.Length; c++)
        {
            int hx = hotClusterX[c];
            int hy = cy;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int idx = ((hy + dy) * w + (hx + dx)) * 3;
                    frame.Data[idx] = 45000;     // Canal rouge saturé
                    frame.Data[idx + 1] = 1400;  // Canal vert au niveau du bruit
                    frame.Data[idx + 2] = 1200;  // Canal bleu au niveau du bruit
                }
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 56.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal(0, result.DetectedMoonsCount);
        Assert.DoesNotContain("lune", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void ClassifyVideo_MoonsFluctuatingAcrossFrames_DetectsAllThreeMoons()
    {
        // Simulation d'une vidéo de 5 frames où 3 lunes sont présentes,
        // mais avec une visibilitée intermittente due au seeing atmosphérique.
        int w = 600, h = 600;
        int cx = 300, cy = 300, r = 28;
        int[] moonPositions = [-110, -60, 85];

        bool[][] moonMask =
        [
            [false, true, false],
            [true, true, false],
            [true, true, true],
            [false, true, true],
            [true, false, true]
        ];

        var frames = new List<AstroFrame>();
        for (int f = 0; f < 5; f++)
        {
            var frame = new AstroFrame(w, h, 3, 16);
            PaintDisk(frame, cx, cy, r, 38000, 36000, 32000);

            for (int m = 0; m < 3; m++)
            {
                if (!moonMask[f][m]) continue;
                PaintMoonSpot(frame, cx + moonPositions[m], cy, 28000);
            }
            frames.Add(frame);
        }

        var result = CelestialTargetClassifier.ClassifyVideo(
            filePath: "jupiter_fluctuating.ser",
            frameCount: 5,
            measuredDiameterPx: 56.0,
            frameLoader: (path, idx, maxDim) => (frames[idx], w, h));

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal(3, result.DetectedMoonsCount);
        Assert.Contains("3", result.ObjectSummary(isFrench: true));
        Assert.Contains("lunes", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void ClassifyVideo_DriftingPlanetWhereOuterMoonEntersFieldLate_DetectsAllThreeMoons()
    {
        // Reproduit le cas réel d'une vidéo sans suivi (dérive Dobson/Alt-Az) où Jupiter dérive de X=410 à X=130
        // dans une frame 540x960 (preview d'une vidéo 1080x1920), de sorte que la 3e lune lointaine (RelX=+348)
        // est hors du bord droit de l'image pendant les deux premiers tiers de la vidéo et n'entre dans le champ
        // que sur les dernières frames échantillonnées.
        int w = 540, h = 960;
        int origW = 1080, origH = 1920;
        int r = 28; // rayon dans la preview 540x960 (diamètre 112 px en 1080x1920)
        int frameCount = 9;

        (int RelX, int RelY)[] moons = [(-130, 44), (215, -73), (349, -119)];
        var frames = new List<AstroFrame>();

        for (int f = 0; f < frameCount; f++)
        {
            var frame = new AstroFrame(w, h, 3, 16);
            int cx = 412 - f * 35; // dérive de 412 vers 132
            int cy = 450;

            PaintDisk(frame, cx, cy, r, 45000, 42000, 38000);

            foreach (var (rx, ry) in moons)
            {
                int mx = cx + rx;
                int my = cy + ry;
                if (mx >= 4 && mx < w - 4 && my >= 4 && my < h - 4)
                {
                    PaintMoonSpot(frame, mx, my, 15000);
                }
            }

            frames.Add(frame);
        }

        var result = CelestialTargetClassifier.ClassifyVideo(
            filePath: "jupiter_drift.mp4",
            frameCount: frameCount,
            measuredDiameterPx: 112.0,
            originalWidth: origW,
            originalHeight: origH,
            frameLoader: (path, idx, maxDim) => (frames[idx], origW, origH));

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal(3, result.DetectedMoonsCount);
        Assert.Equal("Jupiter (3 lunes détectées)", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void ClassifyVideo_NoiseOnlyVideo_DetectsZeroMoons()
    {
        // 5 frames avec bruit aléatoire et aucun corps céleste externe
        int w = 400, h = 400;
        int cx = 200, cy = 200, r = 25;
        var rng = new Random(12345);

        var frames = new List<AstroFrame>();
        for (int f = 0; f < 5; f++)
        {
            var frame = new AstroFrame(w, h, 3, 16);
            PaintDisk(frame, cx, cy, r, 42000, 39000, 35000);

            // Bruit éphémère différent sur chaque frame
            for (int i = 0; i < 20; i++)
            {
                int rx = rng.Next(10, w - 10);
                int ry = rng.Next(10, h - 10);
                int idx = (ry * w + rx) * 3;
                frame.Data[idx] = (ushort)rng.Next(2500, 25000);
                frame.Data[idx + 1] = (ushort)rng.Next(2500, 25000);
                frame.Data[idx + 2] = (ushort)rng.Next(2500, 25000);
            }
            frames.Add(frame);
        }

        var result = CelestialTargetClassifier.ClassifyVideo(
            filePath: "jupiter_noisy.ser",
            frameCount: 5,
            measuredDiameterPx: 50.0,
            frameLoader: (path, idx, maxDim) => (frames[idx], w, h));

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal(0, result.DetectedMoonsCount);
        Assert.DoesNotContain("lune", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void Classify_WhenDetectSatellitesDisabled_ReturnsZeroMoons()
    {
        int w = 600, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 300, cy = 300, r = 25;

        PaintDisk(frame, cx, cy, r, 40000, 38000, 34000);
        PaintMoonSpot(frame, cx - 80, cy, 28000);
        PaintMoonSpot(frame, cx + 95, cy, 28000);

        var result = CelestialTargetClassifier.Classify(
            frame,
            measuredDiameterPx: 50.0,
            originalWidth: w,
            originalHeight: h,
            detectSatellites: false);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal(0, result.DetectedMoonsCount);
        Assert.Equal("Jupiter", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void Classify_SaturnWithTiltedRingsAndTitan_DetectsAllFourMoons()
    {
        // Saturne avec anneaux inclinés et Titan situé hors de la droite stricte des anneaux
        // (inclinaison orbitale ~27° vue de la Terre)
        int w = 600, h = 400;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 300, cy = 200;

        // Disque saturnien (teinte jaune/dorée) et anneaux allongés (aspect ratio >= 1.28)
        PaintSaturnRings(frame, cx, cy, rx: 40, ry: 14, 38000, 35000, 24000);
        PaintDisk(frame, cx, cy, r: 18, 46000, 42000, 31000);

        // 3 lunes alignées le long du plan des anneaux (axe quasi horizontal)
        PaintMoonSpot(frame, cx - 180, cy + 2, 26000);
        PaintMoonSpot(frame, cx - 120, cy + 1, 26000);
        PaintMoonSpot(frame, cx + 200, cy - 2, 26000);

        // Titan : éloigné et décalé vers le haut d'environ 60px du plan des anneaux
        PaintMoonSpot(frame, cx - 140, cy - 60, 32000);

        var result = CelestialTargetClassifier.Classify(
            frame,
            measuredDiameterPx: 50.0,
            originalWidth: w,
            originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Saturne", result.SpecificBodyNameFr);
        Assert.Equal(4, result.DetectedMoonsCount);
        Assert.Contains("4 lunes détectées", result.ObjectSummary(isFrench: true));
    }

    private static void PaintSaturnRings(AstroFrame frame, int cx, int cy, int rx, int ry, ushort red, ushort green, ushort blue)
    {
        int w = frame.Width;
        int h = frame.Height;
        for (int y = Math.Max(0, cy - ry); y <= Math.Min(h - 1, cy + ry); y++)
        {
            for (int x = Math.Max(0, cx - rx); x <= Math.Min(w - 1, cx + rx); x++)
            {
                double dx = (double)(x - cx) / rx;
                double dy = (double)(y - cy) / ry;
                if (dx * dx + dy * dy <= 1.0)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = red;
                    frame.Data[idx + 1] = green;
                    frame.Data[idx + 2] = blue;
                }
            }
        }
    }

    private static void PaintDisk(AstroFrame frame, int cx, int cy, int r, ushort red, ushort green, ushort blue)
    {
        int w = frame.Width;
        int h = frame.Height;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(h - 1, cy + r); y++)
        {
            for (int x = Math.Max(0, cx - r); x <= Math.Min(w - 1, cx + r); x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = red;
                    frame.Data[idx + 1] = green;
                    frame.Data[idx + 2] = blue;
                }
            }
        }
    }

    private static void PaintMoonSpot(AstroFrame frame, int mx, int my, ushort peakLum)
    {
        int w = frame.Width;
        int h = frame.Height;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int px = mx + dx;
                int py = my + dy;
                if (px < 0 || px >= w || py < 0 || py >= h) continue;
                double falloff = (dx == 0 && dy == 0) ? 1.0 : 0.65;
                ushort val = (ushort)(peakLum * falloff);
                int idx = (py * w + px) * 3;
                frame.Data[idx] = val;
                frame.Data[idx + 1] = val;
                frame.Data[idx + 2] = val;
            }
        }
    }
}
