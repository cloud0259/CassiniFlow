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
        Assert.Equal("Jupiter", result.ObjectSummary(isFrench: true));
    }

    [Fact]
    public void ClassifyVideo_MultiFrameSampling_IdentifiesJupiter()
    {
        int w = 500, h = 500;
        int cx = 250, cy = 250, r = 25;
        var frame = new AstroFrame(w, h, 3, 16);

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
                    frame.Data[idx + 2] = 33000;
                }
            }
        }

        var result = CelestialTargetClassifier.ClassifyVideo(
            filePath: "jupiter.ser",
            frameCount: 10,
            measuredDiameterPx: 50.0,
            frameLoader: (path, idx, maxDim) => (frame, w, h));

        Assert.Equal(CelestialTargetType.Planet, result.TargetType);
        Assert.Equal("Jupiter", result.SpecificBodyNameFr);
        Assert.Equal("Jupiter", result.ObjectSummary(isFrench: true));
    }
}
