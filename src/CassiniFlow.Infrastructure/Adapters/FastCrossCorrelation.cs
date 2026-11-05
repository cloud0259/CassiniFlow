using System.Collections.Concurrent;
using CassiniFlow.Domain.Models;
using OpenCvSharp;

namespace CassiniFlow.Infrastructure.Adapters;
/// <summary>
/// Moteur de corrÃ©lation de phase sub-pixel pour le recalage multi-zones et global.
/// </summary>
public static class FastCrossCorrelation
{
    public static (double ShiftX, double ShiftY) ComputeShift(AstroFrame current, AstroFrame reference)
    {
        if (current.Width != reference.Width || current.Height != reference.Height)
        {
            return (0, 0);
        }

        try
        {
            int w = current.Width;
            int h = current.Height;

            using var mat1 = new Mat(h, w, MatType.CV_32FC1);
            using var mat2 = new Mat(h, w, MatType.CV_32FC1);

            FillFloatLuminanceMat(current, mat1);
            FillFloatLuminanceMat(reference, mat2);

            using var hanning = new Mat();
            Cv2.CreateHanningWindow(hanning, new Size(w, h), MatType.CV_32F);
            var shift = Cv2.PhaseCorrelate(mat1, mat2, hanning, out _);
            return (shift.X, shift.Y);
        }
        catch
        {
            // Fallback sur la diffÃ©rence de centroÃ¯des
            if (current.DetectedCentroidX > 0 && reference.DetectedCentroidX > 0)
            {
                return (reference.DetectedCentroidX - current.DetectedCentroidX, reference.DetectedCentroidY - current.DetectedCentroidY);
            }
            return (0, 0);
        }
    }

    public static (double ShiftX, double ShiftY) ComputeTileShift(
        AstroFrame current,
        AstroFrame reference,
        int centerX,
        int centerY,
        int size,
        double globalShiftX = 0,
        double globalShiftY = 0)
    {
        try
        {
            int half = size / 2;
            int refX0 = Math.Max(0, centerX - half);
            int refY0 = Math.Max(0, centerY - half);
            int refX1 = Math.Min(reference.Width, centerX + half);
            int refY1 = Math.Min(reference.Height, centerY + half);
            int tw = refX1 - refX0;
            int th = refY1 - refY0;

            if (tw < 8 || th < 8) return (globalShiftX, globalShiftY);

            // Dans l'image courante, la structure est dÃ©calÃ©e d'environ (-globalShiftX, -globalShiftY)
            int curCenterX = (int)Math.Round(centerX - globalShiftX);
            int curCenterY = (int)Math.Round(centerY - globalShiftY);
            int curX0 = Math.Clamp(curCenterX - half, 0, Math.Max(0, current.Width - tw));
            int curY0 = Math.Clamp(curCenterY - half, 0, Math.Max(0, current.Height - th));

            using var tile1 = new Mat(th, tw, MatType.CV_32FC1);
            using var tile2 = new Mat(th, tw, MatType.CV_32FC1);

            FillFloatSubRegion(current, tile1, curX0, curY0, tw, th);
            FillFloatSubRegion(reference, tile2, refX0, refY0, tw, th);

            using var hanning = new Mat();
            Cv2.CreateHanningWindow(hanning, new Size(tw, th), MatType.CV_32F);
            var residualShift = Cv2.PhaseCorrelate(tile1, tile2, hanning, out _);

            // Si le dÃ©calage rÃ©siduel est cohÃ©rent (turbulence locale < demi-tuile)
            if (Math.Abs(residualShift.X) <= half && Math.Abs(residualShift.Y) <= half)
            {
                double totalShiftX = (refX0 - curX0) + residualShift.X;
                double totalShiftY = (refY0 - curY0) + residualShift.Y;
                return (totalShiftX, totalShiftY);
            }

            return (globalShiftX, globalShiftY);
        }
        catch
        {
            return (globalShiftX, globalShiftY);
        }
    }

    internal static unsafe void FillFloatLuminanceMat(AstroFrame frame, Mat mat)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        ushort[] data = frame.Data;

        fixed (ushort* pData = data)
        {
            float* pDst = (float*)mat.Data;
            int stride = (int)(mat.Step() / sizeof(float));

            for (int y = 0; y < h; y++)
            {
                float* dstRow = pDst + y * stride;
                if (ch == 1)
                {
                    ushort* srcRow = pData + y * w;
                    for (int x = 0; x < w; x++)
                    {
                        dstRow[x] = srcRow[x] * (1.0f / 65535.0f);
                    }
                }
                else
                {
                    ushort* srcRow = pData + (y * w * 3);
                    for (int x = 0; x < w; x++)
                    {
                        int idx = x * 3;
                        float lum = (srcRow[idx] + (srcRow[idx + 1] * 2) + srcRow[idx + 2]) * (1.0f / (4.0f * 65535.0f));
                        dstRow[x] = lum;
                    }
                }
            }
        }
    }

    internal static unsafe void FillFloatSubRegion(AstroFrame frame, Mat mat, int x0, int y0, int tw, int th)
    {
        int w = frame.Width;
        int ch = frame.Channels;
        ushort[] data = frame.Data;

        fixed (ushort* pData = data)
        {
            float* pDst = (float*)mat.Data;
            int stride = (int)(mat.Step() / sizeof(float));

            for (int y = 0; y < th; y++)
            {
                float* dstRow = pDst + y * stride;
                int srcY = y0 + y;

                if (ch == 1)
                {
                    ushort* srcRow = pData + srcY * w + x0;
                    for (int x = 0; x < tw; x++)
                    {
                        dstRow[x] = srcRow[x] * (1.0f / 65535.0f);
                    }
                }
                else
                {
                    ushort* srcRow = pData + (srcY * w + x0) * 3;
                    for (int x = 0; x < tw; x++)
                    {
                        int idx = x * 3;
                        float lum = (srcRow[idx] + (srcRow[idx + 1] * 2) + srcRow[idx + 2]) * (1.0f / (4.0f * 65535.0f));
                        dstRow[x] = lum;
                    }
                }
            }
        }
    }

    internal static unsafe bool FillGlobalRegionAndComputeCog(
        AstroFrame frame,
        Mat mat,
        int x0,
        int y0,
        int tw,
        int th,
        out double cogX,
        out double cogY)
    {
        FillFloatSubRegion(frame, mat, x0, y0, tw, th);

        float* pDst = (float*)mat.Data;
        int stride = (int)(mat.Step() / sizeof(float));

        float maxLum = 0.0f;
        float borderSum = 0.0f;
        int borderCount = 0;

        for (int y = 0; y < th; y++)
        {
            float* row = pDst + y * stride;
            bool isEdgeY = y < 4 || y >= th - 4;
            for (int x = 0; x < tw; x++)
            {
                float v = row[x];
                if (v > maxLum) maxLum = v;
                if (isEdgeY || x < 4 || x >= tw - 4)
                {
                    borderSum += v;
                    borderCount++;
                }
            }
        }

        float skyMean = borderCount > 0 ? (borderSum / borderCount) : 0.0f;
        float dynamicRange = maxLum - skyMean;

        // VÃ©rifie si la fenÃªtre contient un astre isolÃ© sur fond de ciel sombre (planÃ¨te)
        if (dynamicRange >= 0.04f && skyMean < maxLum * 0.45f)
        {
            float thresh = skyMean + dynamicRange * 0.16f;
            double sumW = 0.0;
            double sumX = 0.0;
            double sumY = 0.0;
            int activeCount = 0;

            for (int y = 0; y < th; y++)
            {
                float* row = pDst + y * stride;
                for (int x = 0; x < tw; x++)
                {
                    float v = row[x];
                    if (v > thresh)
                    {
                        float clean = v - thresh;
                        row[x] = clean;
                        sumW += clean;
                        sumX += (x0 + x) * (double)clean;
                        sumY += (y0 + y) * (double)clean;
                        activeCount++;
                    }
                    else
                    {
                        // Ã‰limination totale du bruit de lecture du fond de ciel avant FFT
                        row[x] = 0.0f;
                    }
                }
            }

            if (tw >= 16 && th >= 16)
            {
                Cv2.GaussianBlur(mat, mat, new Size(5, 5), 1.2);
            }

            if (sumW > 1e-6 && activeCount >= 12 && activeCount < (tw * th * 3) / 4)
            {
                cogX = sumX / sumW;
                cogY = sumY / sumW;
                return true;
            }
        }

        if (tw >= 16 && th >= 16)
        {
            Cv2.GaussianBlur(mat, mat, new Size(3, 3), 0.85);
        }

        cogX = x0 + tw * 0.5;
        cogY = y0 + th * 0.5;
        return false;
    }
}
