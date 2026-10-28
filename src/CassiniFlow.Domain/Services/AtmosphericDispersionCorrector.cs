using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Correction de la dispersion atmosphérique chromatique (alignement sub-pixel des canaux RVB).
/// Supprime les franges rouge et bleue caractéristiques sur les limbes planétaires dues à la réfraction de l'atmosphère.
/// </summary>
public static class AtmosphericDispersionCorrector
{
    public static AstroFrame AlignChannels(AstroFrame frame)
    {
        if (frame.Channels != 3 || frame.Width < 32 || frame.Height < 32)
        {
            return frame;
        }

        int width = frame.Width;
        int height = frame.Height;
        ushort[] src = frame.Data;

        // Calcul du barycentre du canal Vert (référence)
        (double gCx, double gCy) = ComputeChannelCentroid(src, width, height, channelIndex: 1);
        (double rCx, double rCy) = ComputeChannelCentroid(src, width, height, channelIndex: 0);
        (double bCx, double bCy) = ComputeChannelCentroid(src, width, height, channelIndex: 2);

        // Décalages relatifs en pixels par rapport au canal vert
        int shiftRx = (int)Math.Round(gCx - rCx);
        int shiftRy = (int)Math.Round(gCy - rCy);
        int shiftBx = (int)Math.Round(gCx - bCx);
        int shiftBy = (int)Math.Round(gCy - bCy);

        // Si aucun décalage significatif (> 0 et <= 8 pixels), retourner la trame originale
        if ((shiftRx == 0 && shiftRy == 0 && shiftBx == 0 && shiftBy == 0) ||
            Math.Abs(shiftRx) > 8 || Math.Abs(shiftRy) > 8 ||
            Math.Abs(shiftBx) > 8 || Math.Abs(shiftBy) > 8)
        {
            return frame;
        }

        var result = frame.Clone();
        ushort[] dst = result.Data;

        // Décalage du canal Rouge (c = 0)
        ShiftChannel(src, dst, width, height, channelIndex: 0, shiftRx, shiftRy);

        // Décalage du canal Bleu (c = 2)
        ShiftChannel(src, dst, width, height, channelIndex: 2, shiftBx, shiftBy);

        return result;
    }

    private static (double Cx, double Cy) ComputeChannelCentroid(ushort[] data, int width, int height, int channelIndex)
    {
        long sum = 0;
        long sumX = 0;
        long sumY = 0;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 3;
            for (int x = 0; x < width; x++)
            {
                ushort val = data[rowOffset + x * 3 + channelIndex];
                if (val > 1000) // Seuil pour ignorer le bruit de fond
                {
                    sum += val;
                    sumX += (long)val * x;
                    sumY += (long)val * y;
                }
            }
        }

        if (sum == 0) return (width / 2.0, height / 2.0);
        return ((double)sumX / sum, (double)sumY / sum);
    }

    private static void ShiftChannel(ushort[] src, ushort[] dst, int width, int height, int channelIndex, int dx, int dy)
    {
        for (int y = 0; y < height; y++)
        {
            int srcY = y - dy;
            if (srcY < 0 || srcY >= height) continue;

            int dstRow = y * width * 3;
            int srcRow = srcY * width * 3;

            for (int x = 0; x < width; x++)
            {
                int srcX = x - dx;
                if (srcX >= 0 && srcX < width)
                {
                    dst[dstRow + x * 3 + channelIndex] = src[srcRow + srcX * 3 + channelIndex];
                }
            }
        }
    }
}

