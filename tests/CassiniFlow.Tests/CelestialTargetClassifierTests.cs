using System;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using Xunit;

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
    public void Classify_PlanetWithMoons_IdentifiesJupiterWithMoons()
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

        // Add 3 small moons outside the planet disk along equatorial axis
        int[] moonXOffsets = [-100, -65, 85];
        foreach (var mx in moonXOffsets)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int px = cx + mx + dx;
                    int py = cy + dy;
                    int idx = (py * w + px) * 3;
                    frame.Data[idx] = 30000;
                    frame.Data[idx + 1] = 30000;
                    frame.Data[idx + 2] = 30000;
                }
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 60.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.True(result.DetectedMoonsCount >= 2, $"Expected at least 2 moons, got {result.DetectedMoonsCount}");
        Assert.Contains("Jupiter", result.ObjectSummary(isFrench: true));
        Assert.Contains("lunes", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void Classify_HighGainNoiseWithoutMoons_DetectsZeroMoons()
    {
        // Disque de Jupiter sans lune, avec 20 pixels chauds / bruit isolÃ©s Ã©parpillÃ©s
        int w = 500, h = 500;
        var frame = new AstroFrame(w, h, 3, 16);
        int cx = 250, cy = 250, r = 25;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 40000;
                    frame.Data[idx + 1] = 38000;
                    frame.Data[idx + 2] = 34000;
                }
            }
        }

        // Bruit de fond alÃ©atoire et pixels chauds isolÃ©s
        var rng = new Random(42);
        for (int i = 0; i < 25; i++)
        {
            int hx = rng.Next(20, w - 20);
            int hy = rng.Next(20, h - 20);
            int dx = hx - cx, dy = hy - cy;
            if (dx * dx + dy * dy > (r * 1.5) * (r * 1.5))
            {
                int idx = (hy * w + hx) * 3;
                frame.Data[idx] = (ushort)rng.Next(3000, 45000); // Hot pixel isolÃ© (mono-pixel)
            }
        }

        var result = CelestialTargetClassifier.Classify(frame, measuredDiameterPx: 50.0, originalWidth: w, originalHeight: h);

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal(0, result.DetectedMoonsCount);
        Assert.Equal("Jupiter", result.ObjectSummary(isFrench: true));
        Assert.DoesNotContain("lune", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void ClassifyVideo_MoonsFluctuatingAcrossFrames_DetectsAllThreeMoons()
    {
        // Simulation d'une vidÃ©o de 5 frames oÃ¹ 3 lunes sont prÃ©sentes,
        // mais avec une visibilitÃ© intermittente due au seeing atmosphÃ©rique.
        int w = 600, h = 600;
        int cx = 300, cy = 300, r = 28;

        // 3 lunes alignÃ©es sur l'Ã©quateur jovien Ã  X = -110, -60, +85
        int[] moonPositions = [-110, -60, 85];

        // PrÃ©sence par frame (5 frames) :
        // Frame 0 : seulement lune 1 (visibilitÃ© partielle sur frame 0)
        // Frame 1 : lunes 0 et 1
        // Frame 2 : lunes 0, 1 et 2 (toutes visibles)
        // Frame 3 : lunes 1 et 2
        // Frame 4 : lunes 0 et 2
        bool[][] moonMask = [
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

            for (int m = 0; m < 3; m++)
            {
                if (!moonMask[f][m]) continue;
                int mx = moonPositions[m];
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int px = cx + mx + dx;
                        int py = cy + dy;
                        int idx = (py * w + px) * 3;
                        frame.Data[idx] = 28000;
                        frame.Data[idx + 1] = 28000;
                        frame.Data[idx + 2] = 28000;
                    }
                }
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
    public void ClassifyVideo_NoiseOnlyVideo_DetectsZeroMoons()
    {
        // 5 frames avec bruit alÃ©atoire et aucun corps cÃ©leste externe
        int w = 400, h = 400;
        int cx = 200, cy = 200, r = 25;
        var rng = new Random(12345);

        var frames = new List<AstroFrame>();
        for (int f = 0; f < 5; f++)
        {
            var frame = new AstroFrame(w, h, 3, 16);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r * r)
                    {
                        int idx = (y * w + x) * 3;
                        frame.Data[idx] = 42000;
                        frame.Data[idx + 1] = 39000;
                        frame.Data[idx + 2] = 35000;
                    }
                }
            }

            // Bruit ephemere different sur chaque frame
            for (int i = 0; i < 15; i++)
            {
                int rx = rng.Next(10, w - 10);
                int ry = rng.Next(10, h - 10);
                int idx = (ry * w + rx) * 3;
                frame.Data[idx] = (ushort)rng.Next(2500, 20000);
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
}
