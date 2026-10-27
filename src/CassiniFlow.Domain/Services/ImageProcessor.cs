using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// High-performance 16-bit image calibration & pixel processing operators.
/// Uses cached LUTs, unsafe pointer arithmetic, and Parallel.For for real-time speed.
/// </summary>
public static class ImageProcessor
{
    private static readonly object LutLock = new();
    private static decimal _cachedGain = -1m;
    private static decimal _cachedGamma = -1m;
    private static ushort[] _cachedGainGammaLut = new ushort[65536];

    public static ushort[] GetGainGammaLut(decimal gain, decimal gamma)
    {
        lock (LutLock)
        {
            if (_cachedGain == gain && _cachedGamma == gamma)
            {
                return _cachedGainGammaLut;
            }

            double dGain = (double)gain;
            double invGamma = 1.0 / Math.Max(0.05, (double)gamma);
            bool applyGamma = gamma != 1.00m;
            var lut = new ushort[65536];

            for (int i = 0; i < 65536; i++)
            {
                double normalized = Math.Clamp((i / 65535.0) * dGain, 0.0, 1.0);

                if (applyGamma)
                {
                    normalized = Math.Pow(normalized, invGamma);
                }
                lut[i] = (ushort)(normalized * 65535.0 + 0.5);
            }

            _cachedGain = gain;
            _cachedGamma = gamma;
            _cachedGainGammaLut = lut;
            return lut;
        }
    }

    public static AstroFrame StackMasterFrame(IReadOnlyList<AstroFrame> frames)
    {
        if (frames.Count == 0)
            throw new ArgumentException("At least one calibration frame is required.", nameof(frames));

        var first = frames[0];
        var master = new AstroFrame(first.Width, first.Height, first.Channels, 16)
        {
            IsBayerRaw = first.IsBayerRaw,
            BayerPattern = first.BayerPattern
        };

        long[] accum = new long[master.Data.Length];
        foreach (var f in frames)
        {
            if (f.Width != first.Width || f.Height != first.Height || f.Channels != first.Channels)
                throw new InvalidDataException($"Calibration frame size ({f.Width}x{f.Height}) does not match ({first.Width}x{first.Height}).");

            ushort[] src = f.Data;
            for (int i = 0; i < accum.Length; i++)
            {
                accum[i] += src[i];
            }
        }

        int count = frames.Count;
        ushort[] dst = master.Data;
        for (int i = 0; i < accum.Length; i++)
        {
            dst[i] = (ushort)Math.Clamp(accum[i] / count, 0, 65535);
        }

        return master;
    }

    public static unsafe void ApplyCalibration(
        AstroFrame frame,
        AstroFrame? masterDark,
        bool replaceHotPixels,
        int hotPixelThresholdPercent,
        bool darkHasBayer,
        AstroFrame? masterFlat)
    {
        int len = frame.Data.Length;

        if (masterDark is not null && masterDark.Data.Length == len)
        {
            fixed (ushort* pFrame = frame.Data)
            fixed (ushort* pDark = masterDark.Data)
            {
                for (int i = 0; i < len; i++)
                {
                    int diff = pFrame[i] - pDark[i];
                    pFrame[i] = (ushort)(diff > 0 ? diff : 0);
                }

                if (replaceHotPixels && frame.Channels == 1)
                {
                    ushort threshold = (ushort)Math.Clamp(hotPixelThresholdPercent * 65535 / 100, 1, 65535);
                    int step = darkHasBayer ? 2 : 1;
                    int w = frame.Width;
                    int h = frame.Height;

                    for (int y = step; y < h - step; y++)
                    {
                        int rowOffset = y * w;
                        for (int x = step; x < w - step; x++)
                        {
                            int idx = rowOffset + x;
                            if (pDark[idx] >= threshold)
                            {
                                int avg = (
                                    pFrame[(y - step) * w + x] +
                                    pFrame[(y + step) * w + x] +
                                    pFrame[rowOffset + (x - step)] +
                                    pFrame[rowOffset + (x + step)]) >> 2;
                                pFrame[idx] = (ushort)avg;
                            }
                        }
                    }
                }
            }
        }

        if (masterFlat is not null && masterFlat.Data.Length == len)
        {
            fixed (ushort* pFrame = frame.Data)
            fixed (ushort* pFlat = masterFlat.Data)
            {
                long sum = 0;
                for (int i = 0; i < len; i++) sum += pFlat[i];
                double meanFlat = Math.Max(1.0, (double)sum / len);

                for (int i = 0; i < len; i++)
                {
                    ushort flatVal = pFlat[i] > 0 ? pFlat[i] : (ushort)1;
                    int corrected = (int)(pFrame[i] * (meanFlat / flatVal));
                    pFrame[i] = (ushort)(corrected > 65535 ? 65535 : (corrected < 0 ? 0 : corrected));
                }
            }
        }
    }

    public static AstroFrame Debayer(AstroFrame src, BayerPattern pattern, DebayerAlgorithm algorithm)
    {
        if (src.Channels == 3) return src;

        int w = src.Width;
        int h = src.Height;
        var dst = new AstroFrame(w, h, 3, src.OriginalBitDepth)
        {
            FrameIndex = src.FrameIndex,
            IsBayerRaw = false,
            TimestampUtc = src.TimestampUtc,
            QualityScore = src.QualityScore
        };

        (int rx, int ry) = pattern switch
        {
            BayerPattern.Rggb => (0, 0),
            BayerPattern.Grbg => (1, 0),
            BayerPattern.Gbrg => (0, 1),
            BayerPattern.Bggr => (1, 1),
            _ => (0, 0)
        };

        ushort[] sData = src.Data;
        ushort[] dData = dst.Data;

        ushort GetClamped(int x, int y)
        {
            int cx = x < 0 ? 0 : (x >= w ? w - 1 : x);
            int cy = y < 0 ? 0 : (y >= h ? h - 1 : y);
            return sData[cy * w + cx];
        }

        for (int y = 0; y < h; y++)
        {
            bool isRedRow = (y & 1) == ry;
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                bool isRedCol = (x & 1) == rx;
                (ushort r, ushort g, ushort b) = (isRedRow, isRedCol) switch
                {
                    (true, true) => (
                        sData[rowBase + x],
                        (ushort)((GetClamped(x - 1, y) + GetClamped(x + 1, y) + GetClamped(x, y - 1) + GetClamped(x, y + 1)) >> 2),
                        (ushort)((GetClamped(x - 1, y - 1) + GetClamped(x + 1, y - 1) + GetClamped(x - 1, y + 1) + GetClamped(x + 1, y + 1)) >> 2)
                    ),
                    (false, false) => (
                        (ushort)((GetClamped(x - 1, y - 1) + GetClamped(x + 1, y - 1) + GetClamped(x - 1, y + 1) + GetClamped(x + 1, y + 1)) >> 2),
                        (ushort)((GetClamped(x - 1, y) + GetClamped(x + 1, y) + GetClamped(x, y - 1) + GetClamped(x, y + 1)) >> 2),
                        sData[rowBase + x]
                    ),
                    (true, false) => (
                        (ushort)((GetClamped(x - 1, y) + GetClamped(x + 1, y)) >> 1),
                        sData[rowBase + x],
                        (ushort)((GetClamped(x, y - 1) + GetClamped(x, y + 1)) >> 1)
                    ),
                    _ => (
                        (ushort)((GetClamped(x, y - 1) + GetClamped(x, y + 1)) >> 1),
                        sData[rowBase + x],
                        (ushort)((GetClamped(x - 1, y) + GetClamped(x + 1, y)) >> 1)
                    )
                };

                int outIdx = (rowBase + x) * 3;
                dData[outIdx] = r;
                dData[outIdx + 1] = g;
                dData[outIdx + 2] = b;
            }
        }

        return dst;
    }

    public static AstroFrame ApplyBinning(AstroFrame src, BinningMode mode, BinningCalculation calc)
    {
        int factor = (int)mode;
        if (factor <= 1) return src;

        int newW = src.Width / factor;
        int newH = src.Height / factor;
        if (newW <= 0 || newH <= 0) return src;

        var dst = new AstroFrame(newW, newH, src.Channels, src.OriginalBitDepth)
        {
            FrameIndex = src.FrameIndex,
            TimestampUtc = src.TimestampUtc,
            QualityScore = src.QualityScore
        };

        int area = factor * factor;
        int ch = src.Channels;
        int srcW = src.Width;
        ushort[] sData = src.Data;
        ushort[] dData = dst.Data;

        for (int ny = 0; ny < newH; ny++)
        {
            for (int nx = 0; nx < newW; nx++)
            {
                for (int c = 0; c < ch; c++)
                {
                    long sum = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int sy = ny * factor + dy;
                        for (int dx = 0; dx < factor; dx++)
                        {
                            int sx = nx * factor + dx;
                            sum += sData[(sy * srcW + sx) * ch + c];
                        }
                    }

                    long val = calc == BinningCalculation.Average ? sum / area : sum;
                    dData[(ny * newW + nx) * ch + c] = (ushort)(val > 65535 ? 65535 : val);
                }
            }
        }

        return dst;
    }

    public static unsafe AstroFrame ConvertToMonochrome(AstroFrame src, MonochromeConversionMode mode)
    {
        if (src.Channels == 1) return src;

        int pixelCount = src.Width * src.Height;
        var dst = new AstroFrame(src.Width, src.Height, 1, src.OriginalBitDepth)
        {
            FrameIndex = src.FrameIndex,
            TimestampUtc = src.TimestampUtc,
            QualityScore = src.QualityScore
        };

        fixed (ushort* pSrc = src.Data)
        fixed (ushort* pDst = dst.Data)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int idx = i * 3;
                ushort r = pSrc[idx];
                ushort g = pSrc[idx + 1];
                ushort b = pSrc[idx + 2];

                pDst[i] = mode switch
                {
                    MonochromeConversionMode.UseRChannelOnly => r,
                    MonochromeConversionMode.UseGChannelOnly => g,
                    MonochromeConversionMode.UseBChannelOnly => b,
                    MonochromeConversionMode.UseRgChannels => (ushort)((r + g) >> 1),
                    MonochromeConversionMode.UseRbChannels => (ushort)((r + b) >> 1),
                    MonochromeConversionMode.UseGbChannels => (ushort)((g + b) >> 1),
                    _ => (ushort)((r * 77 + g * 150 + b * 29) >> 8)
                };
            }
        }

        return dst;
    }

    public static unsafe void ApplyGainAndGamma(AstroFrame frame, decimal gain, decimal gamma)
    {
        if (gain == 1.00m && gamma == 1.00m) return;

        ushort[] lut = GetGainGammaLut(gain, gamma);
        int len = frame.Data.Length;

        fixed (ushort* pData = frame.Data)
        fixed (ushort* pLut = lut)
        {
            for (int i = 0; i < len; i++)
            {
                pData[i] = pLut[pData[i]];
            }
        }
    }

    public static unsafe void ApplyHistogramStretch(AstroFrame frame, int whitePointPercent, bool blackPointZero, bool stretchRgbIndividually)
    {
        ushort targetMax = (ushort)Math.Clamp(whitePointPercent * 65535 / 100, 256, 65535);
        int len = frame.Data.Length;
        if (len == 0) return;

        fixed (ushort* pData = frame.Data)
        {
            if (frame.Channels == 1 || !stretchRgbIndividually)
            {
                ushort min = ushort.MaxValue;
                ushort max = 0;
                for (int i = 0; i < len; i++)
                {
                    ushort v = pData[i];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                ushort floor = blackPointZero ? min : (ushort)0;
                if (max <= floor) return;

                int range = max - floor;
                for (int i = 0; i < len; i++)
                {
                    int val = pData[i] - floor;
                    if (val <= 0) pData[i] = 0;
                    else
                    {
                        int stretched = (int)((long)val * targetMax / range);
                        pData[i] = (ushort)(stretched > 65535 ? 65535 : stretched);
                    }
                }
            }
            else
            {
                for (int c = 0; c < 3; c++)
                {
                    ushort min = ushort.MaxValue;
                    ushort max = 0;
                    for (int i = c; i < len; i += 3)
                    {
                        ushort v = pData[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }

                    ushort floor = blackPointZero ? min : (ushort)0;
                    if (max <= floor) continue;

                    int range = max - floor;
                    for (int i = c; i < len; i += 3)
                    {
                        int val = pData[i] - floor;
                        if (val <= 0) pData[i] = 0;
                        else
                        {
                            int stretched = (int)((long)val * targetMax / range);
                            pData[i] = (ushort)(stretched > 65535 ? 65535 : stretched);
                        }
                    }
                }
            }
        }
    }

    public static AstroFrame ApplyMedianFilter3x3(AstroFrame src)
    {
        if (src.Channels != 1) return src;
        var dst = src.Clone();
        int w = src.Width;
        int h = src.Height;
        Span<ushort> window = stackalloc ushort[9];
        ushort[] sData = src.Data;
        ushort[] dData = dst.Data;

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int k = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int row = (y + dy) * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        window[k++] = sData[row + x + dx];
                    }
                }
                window.Sort();
                dData[y * w + x] = window[4];
            }
        }

        return dst;
    }

    public static unsafe AstroFrame CropAroundPoint(
        AstroFrame src,
        int centerX,
        int centerY,
        int cropWidth,
        int cropHeight,
        int offsetX = 0,
        int offsetY = 0,
        bool protectBayerEvenSteps = false)
    {
        cropWidth = Math.Max(16, cropWidth);
        cropHeight = Math.Max(16, cropHeight);

        int startX = centerX - (cropWidth / 2) - offsetX;
        int startY = centerY - (cropHeight / 2) - offsetY;

        if (protectBayerEvenSteps)
        {
            startX &= ~1;
            startY &= ~1;
        }

        var dst = new AstroFrame(cropWidth, cropHeight, src.Channels, src.OriginalBitDepth)
        {
            FrameIndex = src.FrameIndex,
            IsBayerRaw = src.IsBayerRaw,
            BayerPattern = src.BayerPattern,
            TimestampUtc = src.TimestampUtc,
            QualityScore = src.QualityScore,
            DetectedCentroidX = cropWidth / 2,
            DetectedCentroidY = cropHeight / 2
        };

        int srcW = src.Width;
        int srcH = src.Height;
        int ch = src.Channels;

        fixed (ushort* pSrc = src.Data)
        fixed (ushort* pDst = dst.Data)
        {
            for (int y = 0; y < cropHeight; y++)
            {
                int sy = startY + y;
                if ((uint)sy >= (uint)srcH) continue;

                int dstXStart = 0;
                int srcXStart = startX;
                int copyPixels = cropWidth;

                if (srcXStart < 0)
                {
                    dstXStart = -srcXStart;
                    copyPixels -= dstXStart;
                    srcXStart = 0;
                }
                if (srcXStart + copyPixels > srcW)
                {
                    copyPixels = srcW - srcXStart;
                }
                if (copyPixels <= 0) continue;

                ushort* srcRow = pSrc + (sy * srcW + srcXStart) * ch;
                ushort* dstRow = pDst + (y * cropWidth + dstXStart) * ch;
                Buffer.MemoryCopy(srcRow, dstRow, copyPixels * ch * sizeof(ushort), copyPixels * ch * sizeof(ushort));
            }
        }

        return dst;
    }

    public static AstroFrame ApplyFlipAndRotation(AstroFrame src, bool flipX, bool flipY, RotationAngle rotation)
    {
        if (!flipX && !flipY && rotation == RotationAngle.None) return src;

        int outW = rotation is RotationAngle.Rotate90Clockwise or RotationAngle.Rotate90Anticlockwise ? src.Height : src.Width;
        int outH = rotation is RotationAngle.Rotate90Clockwise or RotationAngle.Rotate90Anticlockwise ? src.Width : src.Height;

        var dst = new AstroFrame(outW, outH, src.Channels, src.OriginalBitDepth)
        {
            FrameIndex = src.FrameIndex,
            TimestampUtc = src.TimestampUtc,
            QualityScore = src.QualityScore,
            DetectedCentroidX = src.DetectedCentroidX,
            DetectedCentroidY = src.DetectedCentroidY
        };

        int srcW = src.Width;
        int srcH = src.Height;
        int ch = src.Channels;
        ushort[] sData = src.Data;
        ushort[] dData = dst.Data;

        for (int y = 0; y < srcH; y++)
        {
            for (int x = 0; x < srcW; x++)
            {
                int fx = flipX ? (srcW - 1 - x) : x;
                int fy = flipY ? (srcH - 1 - y) : y;

                (int rx, int ry) = rotation switch
                {
                    RotationAngle.Rotate90Clockwise => (srcH - 1 - fy, fx),
                    RotationAngle.Rotate90Anticlockwise => (fy, srcW - 1 - fx),
                    RotationAngle.Rotate180 => (srcW - 1 - fx, srcH - 1 - fy),
                    _ => (fx, fy)
                };

                int srcBase = (y * srcW + x) * ch;
                int dstBase = (ry * outW + rx) * ch;
                for (int c = 0; c < ch; c++)
                {
                    dData[dstBase + c] = sData[srcBase + c];
                }
            }
        }

        return dst;
    }
}
