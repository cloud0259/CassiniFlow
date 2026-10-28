using System;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using Xunit;

namespace CassiniFlow.Tests;

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

        for (int i = 0; i < frame.Data.Length; i++)
        {
            frame.Data[i] = 30000;
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
}
