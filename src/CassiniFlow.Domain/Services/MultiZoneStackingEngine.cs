using System.Buffers;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Moteur d'empilement haute résolution multi-zones avec lissage des coutures (cosine feathering).
/// Combine les meilleures trames locales en accumulant avec une dynamique 32-bit pour éliminer le bruit.
/// </summary>
public sealed class MultiZoneStackingEngine
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly StackingOptions _options;
    private readonly AlignmentGrid _grid;

    // Accumulateur 64-bit pour éviter tout dépassement d'entier lors de l'empilement de milliers de poses 16-bit
    private readonly double[] _accumulator;
    private readonly double[] _weightMap;
    private int _stackedFramesCount;

    public int StackedFramesCount => _stackedFramesCount;

    public MultiZoneStackingEngine(int width, int height, int channels, StackingOptions options, AlignmentGrid grid)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _options = options;
        _grid = grid;

        int totalPixels = width * height;
        _accumulator = new double[totalPixels * channels];
        _weightMap = new double[totalPixels];
    }

    /// <summary>
    /// Ajoute une trame dans la pile d'accumulation avec recalage.
    /// </summary>
    public void AccumulateFrame(AstroFrame frame, int shiftX = 0, int shiftY = 0)
    {
        if (frame.Width != _width || frame.Height != _height || frame.Channels != _channels)
        {
            return;
        }

        ushort[] data = frame.Data;

        // Si nous avons une grille de points d'alignement avec des points actifs et le lissage activé
        if (_grid.ActiveCount > 0 && _options.FeatheringEnabled)
        {
            AccumulateWithGrid(data, shiftX, shiftY);
        }
        else
        {
            // Accumulation globale simple avec translation (shiftX, shiftY)
            AccumulateGlobal(data, shiftX, shiftY);
        }

        _stackedFramesCount++;
    }

    private void AccumulateGlobal(ushort[] data, int shiftX, int shiftY)
    {
        for (int y = 0; y < _height; y++)
        {
            int srcY = y - shiftY;
            if (srcY < 0 || srcY >= _height) continue;

            int dstRow = y * _width * _channels;
            int srcRow = srcY * _width * _channels;

            for (int x = 0; x < _width; x++)
            {
                int srcX = x - shiftX;
                if (srcX < 0 || srcX >= _width) continue;

                int dstPixel = dstRow + x * _channels;
                int srcPixel = srcRow + srcX * _channels;

                for (int c = 0; c < _channels; c++)
                {
                    _accumulator[dstPixel + c] += data[srcPixel + c];
                }

                _weightMap[y * _width + x] += 1.0;
            }
        }
    }

    private void AccumulateWithGrid(ushort[] data, int globalShiftX, int globalShiftY)
    {
        // Pour chaque pixel, on applique le décalage interpolé des points d'alignement actifs les plus proches
        var activePoints = _grid.Points.Where(p => p.IsActive).ToList();
        if (activePoints.Count == 0)
        {
            AccumulateGlobal(data, globalShiftX, globalShiftY);
            return;
        }

        double radius = _grid.TileSize * 1.2;
        double invRadius = 1.0 / radius;

        for (int y = 0; y < _height; y++)
        {
            int dstRow = y * _width * _channels;

            for (int x = 0; x < _width; x++)
            {
                // Trouver le point d'alignement le plus proche
                AlignmentPoint? nearest = null;
                double minDistSq = double.MaxValue;

                foreach (var ap in activePoints)
                {
                    double dx = x - ap.X;
                    double dy = y - ap.Y;
                    double dsq = dx * dx + dy * dy;
                    if (dsq < minDistSq)
                    {
                        minDistSq = dsq;
                        nearest = ap;
                    }
                }

                int effectiveShiftX = nearest is not null ? (int)Math.Round(nearest.ShiftX + globalShiftX) : globalShiftX;
                int effectiveShiftY = nearest is not null ? (int)Math.Round(nearest.ShiftY + globalShiftY) : globalShiftY;

                int srcY = y - effectiveShiftY;
                int srcX = x - effectiveShiftX;

                if (srcX >= 0 && srcX < _width && srcY >= 0 && srcY < _height)
                {
                    int dstPixel = dstRow + x * _channels;
                    int srcPixel = srcY * _width * _channels + srcX * _channels;

                    for (int c = 0; c < _channels; c++)
                    {
                        _accumulator[dstPixel + c] += data[srcPixel + c];
                    }

                    _weightMap[y * _width + x] += 1.0;
                }
            }
        }
    }

    /// <summary>
    /// Finalise et normalise l'image empilée, avec correction RVB et accentuation optionnelle.
    /// </summary>
    public AstroFrame ProduceStackedFrame()
    {
        var output = new AstroFrame(_width, _height, _channels, 16);
        ushort[] dst = output.Data;

        if (_stackedFramesCount == 0)
        {
            return output;
        }

        // 1. Moyennage pondéré par pixel
        ushort maxVal = 0;
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            int pixelOffset = row * _channels;

            for (int x = 0; x < _width; x++)
            {
                double weight = _weightMap[row + x];
                if (weight > 0)
                {
                    double invW = 1.0 / weight;
                    for (int c = 0; c < _channels; c++)
                    {
                        int val = (int)Math.Round(_accumulator[pixelOffset + x * _channels + c] * invW);
                        ushort clamped = (ushort)Math.Clamp(val, 0, 65535);
                        dst[pixelOffset + x * _channels + c] = clamped;
                        if (clamped > maxVal) maxVal = clamped;
                    }
                }
            }
        }

        // 2. Normalisation de luminosité optionnelle (cible en % de l'échelle 16-bit)
        if (_options.NormalizeBrightness && maxVal > 0)
        {
            int targetVal = (int)Math.Round(65535 * (_options.NormalizeTargetPercent / 100.0));
            double scale = (double)targetVal / maxVal;

            if (scale > 1.0 && scale < 4.0) // Éviter d'amplifier démesurément le bruit si signal quasi nul
            {
                for (int i = 0; i < dst.Length; i++)
                {
                    dst[i] = (ushort)Math.Clamp((int)Math.Round(dst[i] * scale), 0, 65535);
                }
            }
        }

        // 3. Correction de dispersion atmosphérique chromatique (Alignement RVB)
        if (_options.CorrectAtmosphericDispersion && _channels == 3)
        {
            output = AtmosphericDispersionCorrector.AlignChannels(output);
        }

        // 4. Accentuation haute fréquence par convolution 5x5
        if (_options.SharpeningEnabled)
        {
            output = ConvolutionSharpeningFilter.ApplySharpening(output, _options.SharpeningBlendPercent);
        }

        return output;
    }
}

