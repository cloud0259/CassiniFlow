using System;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Synthetic;
using Xunit;

namespace CassiniFlow.Tests;

public class CenteringValidationTests
{
    [Fact]
    public void DetectPlanetOrObject_WithLuminanceAsymmetryAndHotPixels_RemainsExactlyCentered()
    {
        int w = 800, h = 600;
        var frame = new AstroFrame(w, h, 3, 16);
        int trueCx = 400, trueCy = 300, r = 80;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - trueCx;
                int dy = y - trueCy;
                int idx = (y * w + x) * 3;
                if (dx * dx + dy * dy <= r * r)
                {
                    double normDist = Math.Sqrt(dx * dx + dy * dy) / r;
                    double limb = 1.0 - normDist * 0.45;
                    // Upper hemisphere is 45% brighter than lower
                    double asym = 1.0 - 0.45 * (dy / (double)r);
                    ushort v = (ushort)(48000 * limb * asym);
                    frame.Data[idx] = v;
                    frame.Data[idx + 1] = v;
                    frame.Data[idx + 2] = v;
                }
            }
        }

        // Add 2 isolated hot pixels in opposite corners
        frame.Data[(10 * w + 10) * 3] = 65000;
        frame.Data[(580 * w + 780) * 3] = 65000;

        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            frame,
            checkObject: true,
            autoThreshold: true,
            manualThreshold8Bit: 40,
            minObjectSizePixels: 20,
            shadowEdge: LunarShadowEdge.None,
            checkOverexposed: false,
            maxOverexposedPixels: 0);

        Assert.True(det.Accepted);
        // Centre théorique : X=400, Y=300 -> centré au pixel près !
        Assert.InRange(det.CentroidX, 399, 401);
        Assert.InRange(det.CentroidY, 299, 301);
    }

    [Fact]
    public void CropAroundPoint_CentersPlanetExactlyAtFrameCenter()
    {
        int w = 800, h = 600;
        var frame = new AstroFrame(w, h, 1, 16);
        int trueCx = 400, trueCy = 300, r = 60;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - trueCx;
                int dy = y - trueCy;
                if (dx * dx + dy * dy <= r * r)
                {
                    frame.Data[y * w + x] = 40000;
                }
            }
        }

        int cropW = 200, cropH = 200;
        var cropped = ImageProcessor.CropAroundPoint(frame, trueCx, trueCy, cropW, cropH);

        Assert.Equal(cropW, cropped.Width);
        Assert.Equal(cropH, cropped.Height);

        // Dans le résultat croppé, le centre doit être exactement à (cropW/2, cropH/2) = (100, 100)
        var croppedDet = StabilisationAndQualityEngine.DetectPlanetOrObject(
            cropped,
            checkObject: true,
            autoThreshold: true,
            manualThreshold8Bit: 40,
            minObjectSizePixels: 20,
            shadowEdge: LunarShadowEdge.None,
            checkOverexposed: false,
            maxOverexposedPixels: 0);

        Assert.True(croppedDet.Accepted);
        Assert.Equal(100, croppedDet.CentroidX);
        Assert.Equal(100, croppedDet.CentroidY);
    }

    [Fact]
    public void CropOffsetXY_SignsAreConsistentAndSymmetric()
    {
        int w = 400, h = 400;
        var frame = new AstroFrame(w, h, 1, 16);
        int cx = 200, cy = 200;
        frame.Data[cy * w + cx] = 60000;

        // Positive offsetX moves object to +X in crop; positive offsetY moves object to +Y in crop
        var cropNormal = ImageProcessor.CropAroundPoint(frame, cx, cy, 100, 100, offsetX: 0, offsetY: 0);
        var cropShifted = ImageProcessor.CropAroundPoint(frame, cx, cy, 100, 100, offsetX: 10, offsetY: 10);

        Assert.Equal(60000, cropNormal.Data[50 * 100 + 50]);
        // Shifted: (50 + 10, 50 + 10) = (60, 60)
        Assert.Equal(60000, cropShifted.Data[60 * 100 + 60]);
    }
}
