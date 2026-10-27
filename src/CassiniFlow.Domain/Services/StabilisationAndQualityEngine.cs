using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// High-speed unsafe pointer implementation of CassiniFlow's Object/Planetary Detection,
/// Lunar Shadow Edge compensation, Surface Feature Stabilisation (AFB correlation), and the 4 Quality Algorithms.
/// </summary>
public static class StabilisationAndQualityEngine
{
    public static int EstimateAutoThreshold8Bit(AstroFrame frame)
    {
        var (maxGrey, _, _, _) = frame.ComputeMaxPixelValues8Bit();
        return Math.Clamp((int)(maxGrey * 0.14), 8, 180);
    }

    public static unsafe DetectionResult DetectPlanetOrObject(
        AstroFrame frame,
        bool checkObject,
        bool autoThreshold,
        int manualThreshold8Bit,
        int minObjectSizePixels,
        LunarShadowEdge shadowEdge,
        bool checkOverexposed,
        int maxOverexposedPixels,
        bool rejectClippedObjects = false)
    {
        int w = frame.Width;
        int h = frame.Height;
        int threshold8 = autoThreshold ? EstimateAutoThreshold8Bit(frame) : manualThreshold8Bit;
        ushort threshold16 = (ushort)(threshold8 * 257);
        ushort overexposed16 = 254 * 257;

        int overexposedCount = 0;
        int detectedPixels = 0;
        int minX = w, maxX = 0, minY = h, maxY = 0;

        // Use stride=2 on large frames (> 640px) for 4x faster centroid detection while keeping sub-pixel accuracy
        int step = (w > 640 || h > 640) ? 2 : 1;
        int ch = frame.Channels;

        // Marginal projection profiles for robust noise/hot-pixel filtering
        Span<int> rowCounts = (h <= 4096) ? stackalloc int[h] : new int[h];
        Span<int> colCounts = (w <= 4096) ? stackalloc int[w] : new int[w];
        rowCounts.Clear();
        colCounts.Clear();

        long sumX = 0, sumY = 0;
        long totalWeight = 0;

        fixed (ushort* pData = frame.Data)
        {
            for (int y = 0; y < h; y += step)
            {
                int rowOffset = y * w;
                for (int x = 0; x < w; x += step)
                {
                    ushort lum;
                    if (ch == 1)
                    {
                        lum = pData[rowOffset + x];
                        if (lum >= overexposed16) overexposedCount += step * step;
                    }
                    else
                    {
                        int idx = (rowOffset + x) * 3;
                        ushort r = pData[idx], g = pData[idx + 1], b = pData[idx + 2];
                        if (r >= overexposed16 || g >= overexposed16 || b >= overexposed16) overexposedCount += step * step;
                        lum = r >= g ? (r >= b ? r : b) : (g >= b ? g : b);
                    }

                    if (lum >= threshold16)
                    {
                        detectedPixels += step * step;
                        rowCounts[y]++;
                        colCounts[x]++;

                        sumX += x;
                        sumY += y;
                        totalWeight++;

                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        }

        if (checkOverexposed && overexposedCount > maxOverexposedPixels)
        {
            return new DetectionResult(false, "Overexposed pixels exceeded threshold", w / 2, h / 2, detectedPixels, overexposedCount, threshold8);
        }

        if (!checkObject)
        {
            int cx = totalWeight > 0 ? (int)(sumX / totalWeight) : w / 2;
            int cy = totalWeight > 0 ? (int)(sumY / totalWeight) : h / 2;
            return new DetectionResult(true, null, cx, cy, detectedPixels, overexposedCount, threshold8);
        }

        int minArea = (int)(Math.PI * (minObjectSizePixels * 0.5) * (minObjectSizePixels * 0.5));
        if (detectedPixels < Math.Max(4, minArea / 2))
        {
            return new DetectionResult(false, "No planet/object detected above threshold", w / 2, h / 2, detectedPixels, overexposedCount, threshold8);
        }

        // Noise-filtered bounding box via marginal profiles (discards isolated hot pixels/sky noise)
        int minChord = Math.Clamp(minObjectSizePixels / (step * 4), 3, 16);
        int lastStepY = ((h - 1) / step) * step;
        int lastStepX = ((w - 1) / step) * step;
        int cleanMinY = 0; while (cleanMinY <= lastStepY && rowCounts[cleanMinY] < minChord) cleanMinY += step;
        int cleanMaxY = lastStepY; while (cleanMaxY >= 0 && rowCounts[cleanMaxY] < minChord) cleanMaxY -= step;

        int cleanMinX = 0; while (cleanMinX <= lastStepX && colCounts[cleanMinX] < minChord) cleanMinX += step;
        int cleanMaxX = lastStepX; while (cleanMaxX >= 0 && colCounts[cleanMaxX] < minChord) cleanMaxX -= step;

        int finalMinX = (cleanMinX <= cleanMaxX) ? cleanMinX : minX;
        int finalMaxX = (cleanMinX <= cleanMaxX) ? cleanMaxX : maxX;
        int finalMinY = (cleanMinY <= cleanMaxY) ? cleanMinY : minY;
        int finalMaxY = (cleanMinY <= cleanMaxY) ? cleanMaxY : maxY;

        // Only reject border-clipped objects if explicitly enabled and the bright area does not span the whole scene
        bool spansWholeScene = (finalMaxX - finalMinX) > (w * 3 / 4) || (finalMaxY - finalMinY) > (h * 3 / 4);
        if (rejectClippedObjects && !spansWholeScene)
        {
            const int margin = 2;
            bool clippedLeft = finalMinX <= margin && shadowEdge is not (LunarShadowEdge.Left or LunarShadowEdge.TopLeft or LunarShadowEdge.BottomLeft);
            bool clippedRight = finalMaxX >= w - 1 - margin && shadowEdge is not (LunarShadowEdge.Right or LunarShadowEdge.TopRight or LunarShadowEdge.BottomRight);
            bool clippedTop = finalMinY <= margin && shadowEdge is not (LunarShadowEdge.Top or LunarShadowEdge.TopLeft or LunarShadowEdge.TopRight);
            bool clippedBottom = finalMaxY >= h - 1 - margin && shadowEdge is not (LunarShadowEdge.Bottom or LunarShadowEdge.BottomLeft or LunarShadowEdge.BottomRight);

            if (clippedLeft || clippedRight || clippedTop || clippedBottom)
            {
                return new DetectionResult(false, "Partial planet/object clipped at frame edge", w / 2, h / 2, detectedPixels, overexposedCount, threshold8);
            }
        }

        int geomCenterX = (finalMinX + finalMaxX) / 2;
        int geomCenterY = (finalMinY + finalMaxY) / 2;

        int centerX = geomCenterX;
        int centerY = geomCenterY;

        if (shadowEdge != LunarShadowEdge.None)
        {
            int radiusX = (finalMaxX - finalMinX) / 2;
            int radiusY = (finalMaxY - finalMinY) / 2;
            int estRadius = Math.Max(radiusX, radiusY);

            centerX = shadowEdge switch
            {
                LunarShadowEdge.Left or LunarShadowEdge.TopLeft or LunarShadowEdge.BottomLeft => finalMaxX - estRadius,
                LunarShadowEdge.Right or LunarShadowEdge.TopRight or LunarShadowEdge.BottomRight => finalMinX + estRadius,
                _ => centerX
            };
            centerY = shadowEdge switch
            {
                LunarShadowEdge.Top or LunarShadowEdge.TopLeft or LunarShadowEdge.TopRight => finalMaxY - estRadius,
                LunarShadowEdge.Bottom or LunarShadowEdge.BottomLeft or LunarShadowEdge.BottomRight => finalMinY + estRadius,
                _ => centerY
            };
        }

        return new DetectionResult(true, null, centerX, centerY, detectedPixels, overexposedCount, threshold8);
    }

    public static (bool Found, int BestX, int BestY) MatchAnchorFeatureBox(
        AstroFrame referenceFrame,
        int refAfbX,
        int refAfbY,
        int afbWidth,
        int afbHeight,
        AstroFrame targetFrame,
        int searchRadius = 24)
    {
        int w = targetFrame.Width;
        int h = targetFrame.Height;

        if (refAfbX < 0 || refAfbY < 0 || refAfbX + afbWidth >= referenceFrame.Width || refAfbY + afbHeight >= referenceFrame.Height)
            return (false, refAfbX, refAfbY);

        long bestSad = long.MaxValue;
        int bestX = refAfbX;
        int bestY = refAfbY;
        bool foundAny = false;

        int minCandidateX = Math.Max(2, refAfbX - searchRadius);
        int maxCandidateX = Math.Min(w - afbWidth - 2, refAfbX + searchRadius);
        int minCandidateY = Math.Max(2, refAfbY - searchRadius);
        int maxCandidateY = Math.Min(h - afbHeight - 2, refAfbY + searchRadius);

        for (int cy = minCandidateY; cy <= maxCandidateY; cy += 2)
        {
            for (int cx = minCandidateX; cx <= maxCandidateX; cx += 2)
            {
                long sad = ComputePatchSad(referenceFrame, refAfbX, refAfbY, targetFrame, cx, cy, afbWidth, afbHeight, step: 2);
                if (sad < bestSad)
                {
                    bestSad = sad;
                    bestX = cx;
                    bestY = cy;
                    foundAny = true;
                }
            }
        }

        if (foundAny)
        {
            int refineX = bestX, refineY = bestY;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = refineX + dx;
                    int cy = refineY + dy;
                    if (cx >= 1 && cy >= 1 && cx + afbWidth < w && cy + afbHeight < h)
                    {
                        long sad = ComputePatchSad(referenceFrame, refAfbX, refAfbY, targetFrame, cx, cy, afbWidth, afbHeight, step: 1);
                        if (sad < bestSad)
                        {
                            bestSad = sad;
                            bestX = cx;
                            bestY = cy;
                        }
                    }
                }
            }
        }

        return (foundAny, bestX, bestY);
    }

    private static long ComputePatchSad(
        AstroFrame refF, int rx, int ry,
        AstroFrame tgtF, int tx, int ty,
        int bw, int bh, int step)
    {
        long sad = 0;
        for (int y = 0; y < bh; y += step)
        {
            for (int x = 0; x < bw; x += step)
            {
                int rLum = GetLum(refF, rx + x, ry + y);
                int tLum = GetLum(tgtF, tx + x, ty + y);
                sad += Math.Abs(rLum - tLum);
            }
        }
        return sad;
    }

    public static unsafe double ComputeQualityScore(
        AstroFrame frame,
        QualityAlgorithm algorithm,
        int minSubsample = 3,
        int maxSubsample = 5,
        int subsampleInc = 1)
    {
        int w = frame.Width;
        int h = frame.Height;
        if (w < 8 || h < 8) return 0.0;

        int ch = frame.Channels;

        fixed (ushort* pData = frame.Data)
        {
            switch (algorithm)
            {
                case QualityAlgorithm.TotalBrightness:
                {
                    long sum = 0;
                    int len = frame.Data.Length;
                    for (int i = 0; i < len; i++) sum += pData[i];
                    return (double)sum / len;
                }

                case QualityAlgorithm.PeakHistogram:
                {
                    Span<int> hist = stackalloc int[256];
                    for (int y = 0; y < h; y += 2)
                    {
                        int rowOffset = y * w;
                        for (int x = 0; x < w; x += 2)
                        {
                            int lum = ch == 1
                                ? pData[rowOffset + x]
                                : ((pData[(rowOffset + x) * 3] * 77 + pData[(rowOffset + x) * 3 + 1] * 150 + pData[(rowOffset + x) * 3 + 2] * 29) >> 8);
                            hist[lum >> 8]++;
                        }
                    }
                    double weightedHigh = 0;
                    for (int i = 16; i < 256; i++)
                    {
                        weightedHigh += hist[i] * (i * i);
                    }
                    return weightedHigh / (w * h);
                }

                case QualityAlgorithm.OriginalNinox:
                case QualityAlgorithm.GradientSharpness:
                default:
                {
                    int minSub = Math.Max(2, minSubsample);
                    int maxSub = Math.Max(minSub, maxSubsample);
                    int inc = Math.Max(1, subsampleInc);

                    double totalGrad = 0;
                    for (int step = minSub; step <= maxSub; step += inc)
                    {
                        long stepGrad = 0;
                        for (int y = 0; y < h - step; y += step)
                        {
                            int rowOffset = y * w;
                            int downRowOffset = (y + step) * w;
                            for (int x = 0; x < w - step; x += step)
                            {
                                int c, right, down;
                                if (ch == 1)
                                {
                                    c = pData[rowOffset + x];
                                    right = pData[rowOffset + x + step];
                                    down = pData[downRowOffset + x];
                                }
                                else
                                {
                                    int i0 = (rowOffset + x) * 3;
                                    int iR = (rowOffset + x + step) * 3;
                                    int iD = (downRowOffset + x) * 3;
                                    c = pData[i0 + 1]; // Green channel carries highest luminance/sharpness SNR
                                    right = pData[iR + 1];
                                    down = pData[iD + 1];
                                }

                                int dx = c - right;
                                int dy = c - down;
                                if (dx < 0) dx = -dx;
                                if (dy < 0) dy = -dy;
                                stepGrad += dx + dy;
                            }
                        }
                        totalGrad += stepGrad;
                    }
                    return totalGrad / (w * h);
                }
            }
        }
    }

    private static int GetLum(AstroFrame f, int x, int y)
    {
        if (f.Channels == 1) return f.Data[y * f.Width + x];
        int idx = (y * f.Width + x) * 3;
        return (f.Data[idx] * 77 + f.Data[idx + 1] * 150 + f.Data[idx + 2] * 29) >> 8;
    }
}
