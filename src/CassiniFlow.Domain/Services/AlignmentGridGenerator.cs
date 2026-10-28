using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Moteur de génération et d'élagage des grilles de points d'alignement (AP Grid).
/// Analyse le contraste et la brillance locale sur chaque tuile pour éliminer le fond de ciel sombre.
/// </summary>
public static class AlignmentGridGenerator
{
    public static AlignmentGrid GenerateGrid(AstroFrame frame, StackingOptions options)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Data.Length == 0)
        {
            return AlignmentGrid.Empty;
        }

        int tileSize = Math.Clamp(options.AlignmentTileSize, 16, 512);
        int step = options.EnableMultiScaleGrid ? Math.Max(8, tileSize / 2) : tileSize;
        int minBright = options.MinBrightnessThreshold;

        var points = new List<AlignmentPoint>();
        int id = 1;

        int width = frame.Width;
        int height = frame.Height;
        int channels = frame.Channels;
        ReadOnlySpan<ushort> data = frame.Data;

        // Détection de l'ancre globale (CoG ou point de contraste maximal)
        int anchorX = frame.DetectedCentroidX > 0 ? frame.DetectedCentroidX : width / 2;
        int anchorY = frame.DetectedCentroidY > 0 ? frame.DetectedCentroidY : height / 2;

        int halfTile = tileSize / 2;

        for (int y = halfTile; y < height - halfTile; y += step)
        {
            for (int x = halfTile; x < width - halfTile; x += step)
            {
                // Évaluation de la brillance moyenne (sur échelle 8-bit 0..255) et de la variance locale
                (double avgBright8Bit, double variance) = ComputeTileMetrics(data, width, height, channels, x, y, halfTile);

                bool isActive = true;
                if (options.EnablePruning)
                {
                    isActive = avgBright8Bit >= minBright && variance > 1.0;
                }

                var ap = new AlignmentPoint(id++, x, y, tileSize, isActive)
                {
                    Brightness = avgBright8Bit,
                    Contrast = variance
                };

                points.Add(ap);
            }
        }

        return new AlignmentGrid(tileSize, minBright, points, (anchorX, anchorY));
    }

    private static (double AvgBrightness, double Variance) ComputeTileMetrics(
        ReadOnlySpan<ushort> data,
        int width,
        int height,
        int channels,
        int centerX,
        int centerY,
        int radius)
    {
        int x0 = Math.Max(0, centerX - radius);
        int x1 = Math.Min(width - 1, centerX + radius);
        int y0 = Math.Max(0, centerY - radius);
        int y1 = Math.Min(height - 1, centerY + radius);

        long sum = 0;
        long sumSq = 0;
        int count = 0;

        for (int y = y0; y <= y1; y++)
        {
            int rowOffset = y * width * channels;
            for (int x = x0; x <= x1; x++)
            {
                // Si couleur, prendre luma approchée : (R + 2G + B) / 4
                int lum;
                if (channels == 3)
                {
                    int idx = rowOffset + x * 3;
                    lum = (data[idx] + (data[idx + 1] << 1) + data[idx + 2]) >> 2;
                }
                else
                {
                    lum = data[rowOffset + x];
                }

                // Normalisation 8-bit pour comparaison avec MinBright (0..255)
                lum >>= 8;

                sum += lum;
                sumSq += (long)lum * lum;
                count++;
            }
        }

        if (count == 0) return (0, 0);

        double mean = (double)sum / count;
        double variance = ((double)sumSq / count) - (mean * mean);
        return (mean, Math.Max(0, variance));
    }
}

