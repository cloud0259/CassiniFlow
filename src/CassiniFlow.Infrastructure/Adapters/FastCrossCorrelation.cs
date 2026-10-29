using CassiniFlow.Domain.Models;
using OpenCvSharp;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Moteur de corrélation de phase sub-pixel pour le recalage multi-zones et global.
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
            // Fallback sur la différence de centroïdes
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

            // Dans l'image courante, la structure est décalée d'environ (-globalShiftX, -globalShiftY)
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

            // Si le décalage résiduel est cohérent (turbulence locale < demi-tuile)
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

    private static unsafe void FillFloatLuminanceMat(AstroFrame frame, Mat mat)
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

    private static unsafe void FillFloatSubRegion(AstroFrame frame, Mat mat, int x0, int y0, int tw, int th)
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
}
