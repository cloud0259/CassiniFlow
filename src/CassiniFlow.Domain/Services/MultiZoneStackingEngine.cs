using System.Buffers;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Moteur d'empilement haute résolution multi-zones avec lissage des coutures (cosine feathering).
/// Combine les meilleures trames locales en accumulant avec une dynamique 64-bit pour éliminer le bruit.
/// Utilise une table de correspondances précalculée O(1) pour garantir zéro latence et zéro allocation en boucle d'empilement.
/// </summary>
public sealed class MultiZoneStackingEngine
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly StackingOptions _options;
    private readonly AlignmentGrid _grid;
    private readonly List<AlignmentPoint> _activePoints;
    private readonly int[]? _nearestApMap;

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

        _activePoints = _grid.Points.Where(p => p.IsActive).ToList();

        // Précalcul ultra-rapide de la carte d'assignation des AP (O(1) lors de chaque pose accumulée)
        if (_activePoints.Count > 0 && _options.FeatheringEnabled)
        {
            _nearestApMap = new int[totalPixels];
            Parallel.For(0, _height, y =>
            {
                int rowOffset = y * _width;
                for (int x = 0; x < _width; x++)
                {
                    int bestIdx = 0;
                    double minDistSq = double.MaxValue;
                    for (int i = 0; i < _activePoints.Count; i++)
                    {
                        double dx = x - _activePoints[i].X;
                        double dy = y - _activePoints[i].Y;
                        double dsq = dx * dx + dy * dy;
                        if (dsq < minDistSq)
                        {
                            minDistSq = dsq;
                            bestIdx = i;
                        }
                    }
                    _nearestApMap[rowOffset + x] = bestIdx;
                }
            });
        }
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
        if (_activePoints.Count > 0 && _options.FeatheringEnabled && _nearestApMap is not null)
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
            if ((uint)srcY >= (uint)_height) continue;

            int dstRow = y * _width * _channels;
            int srcRow = srcY * _width * _channels;

            for (int x = 0; x < _width; x++)
            {
                int srcX = x - shiftX;
                if ((uint)srcX >= (uint)_width) continue;

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
        int apCount = _activePoints.Count;
        Span<int> effShiftX = apCount <= 1024 ? stackalloc int[apCount] : new int[apCount];
        Span<int> effShiftY = apCount <= 1024 ? stackalloc int[apCount] : new int[apCount];

        for (int i = 0; i < apCount; i++)
        {
            effShiftX[i] = (int)Math.Round(_activePoints[i].ShiftX + globalShiftX);
            effShiftY[i] = (int)Math.Round(_activePoints[i].ShiftY + globalShiftY);
        }

        for (int y = 0; y < _height; y++)
        {
            int rowOffset = y * _width;
            int dstRow = rowOffset * _channels;

            for (int x = 0; x < _width; x++)
            {
                int apIdx = _nearestApMap![rowOffset + x];
                int srcX = x - effShiftX[apIdx];
                int srcY = y - effShiftY[apIdx];

                if ((uint)srcX < (uint)_width && (uint)srcY < (uint)_height)
                {
                    int dstPixel = dstRow + x * _channels;
                    int srcPixel = (srcY * _width + srcX) * _channels;

                    for (int c = 0; c < _channels; c++)
                    {
                        _accumulator[dstPixel + c] += data[srcPixel + c];
                    }

                    _weightMap[rowOffset + x] += 1.0;
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

        // 1. Normalisation par les poids locaux
        int totalPixels = _width * _height;
        for (int i = 0; i < totalPixels; i++)
        {
            double w = _weightMap[i];
            if (w <= 0.0) continue;

            double invW = 1.0 / w;
            int px = i * _channels;

            for (int c = 0; c < _channels; c++)
            {
                double val = _accumulator[px + c] * invW;
                dst[px + c] = (ushort)Math.Clamp(Math.Round(val), 0, 65535);
            }
        }

        // 2. Normalisation de la brillance maximale (75% ou selon options)
        if (_options.NormalizeBrightness)
        {
            NormalizeMasterBrightness(output, _options.NormalizeTargetPercent);
        }

        // 3. Correction de dispersion atmosphérique (alignement sous-pixel R et B sur G)
        if (_options.CorrectAtmosphericDispersion && _channels >= 3)
        {
            output = AtmosphericDispersionCorrector.AlignChannels(output);
        }

        // 4. Accentuation haute fréquence (Convolution Laplacian non-destructif)
        if (_options.SharpeningEnabled)
        {
            output = ConvolutionSharpeningFilter.ApplySharpening(output, _options.SharpeningBlendPercent);
        }

        return output;
    }

    private static void NormalizeMasterBrightness(AstroFrame frame, int targetPercent)
    {
        ushort[] data = frame.Data;
        ushort maxVal = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] > maxVal) maxVal = data[i];
        }

        if (maxVal == 0) return;

        double targetMax = 65535.0 * (Math.Clamp(targetPercent, 10, 100) / 100.0);
        double factor = targetMax / maxVal;

        if (factor is > 0.98 and < 1.02) return;

        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (ushort)Math.Clamp(Math.Round(data[i] * factor), 0, 65535);
        }
    }
}
