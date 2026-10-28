using CassiniFlow.Domain.Models;
using OpenCvSharp;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Moteur de corrélation de phase sub-pixel pour le recalage multi-zones.
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
            // Convertir vers float32 mono pour Cv2.PhaseCorrelate
            int w = current.Width;
            int h = current.Height;

            using var mat1 = new Mat(h, w, MatType.CV_32FC1);
            using var mat2 = new Mat(h, w, MatType.CV_32FC1);

            FillFloatLuminanceMat(current, mat1);
            FillFloatLuminanceMat(reference, mat2);

            var shift = Cv2.PhaseCorrelate(mat1, mat2, null!, out _);
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
        int size)
    {
        try
        {
            int half = size / 2;
            int x0 = Math.Max(0, centerX - half);
            int y0 = Math.Max(0, centerY - half);
            int x1 = Math.Min(current.Width, centerX + half);
            int y1 = Math.Min(current.Height, centerY + half);
            int tw = x1 - x0;
            int th = y1 - y0;

            if (tw < 8 || th < 8) return (0, 0);

            using var tile1 = new Mat(th, tw, MatType.CV_32FC1);
            using var tile2 = new Mat(th, tw, MatType.CV_32FC1);

            FillFloatSubRegion(current, tile1, x0, y0, tw, th);
            FillFloatSubRegion(reference, tile2, x0, y0, tw, th);

            var shift = Cv2.PhaseCorrelate(tile1, tile2, null!, out _);
            // Si le décalage calculé est aberrent (> demi-taille de tuile), retourner 0
            if (Math.Abs(shift.X) > half || Math.Abs(shift.Y) > half)
            {
                return (0, 0);
            }

            return (shift.X, shift.Y);
        }
        catch
        {
            return (0, 0);
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
                    ushort* srcRow = pData + y * w * 3;
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
