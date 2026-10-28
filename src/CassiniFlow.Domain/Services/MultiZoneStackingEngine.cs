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
    private readonly ApBlendPoint[]? _blendMap;

    private readonly struct ApBlendPoint
    {
        public readonly int Ap0;
        public readonly int Ap1;
        public readonly int Ap2;
        public readonly int Ap3;
        public readonly float W0;
        public readonly float W1;
        public readonly float W2;
        public readonly float W3;

        public ApBlendPoint(int ap0, int ap1, int ap2, int ap3, float w0, float w1, float w2, float w3)
        {
            Ap0 = ap0;
            Ap1 = ap1;
            Ap2 = ap2;
            Ap3 = ap3;
            W0 = w0;
            W1 = w1;
            W2 = w2;
            W3 = w3;
        }
    }

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

        // Précalcul de la carte d'interpolation vectorielle continue C1 (élimine tout effet de carré / couture)
        if (_activePoints.Count > 0 && _options.FeatheringEnabled)
        {
            _blendMap = new ApBlendPoint[totalPixels];
            int apCount = _activePoints.Count;
            double sigma = Math.Max(16.0, _options.AlignmentTileSize * 0.9);
            double invTwoSigmaSq = 1.0 / (2.0 * sigma * sigma);

            Parallel.For(0, _height, y =>
            {
                int rowOffset = y * _width;
                Span<(int Idx, double DistSq)> topK = stackalloc (int, double)[4];
                Span<double> weights = stackalloc double[4];

                for (int x = 0; x < _width; x++)
                {
                    int kCount = Math.Min(4, apCount);
                    for (int k = 0; k < kCount; k++)
                    {
                        topK[k] = (-1, double.MaxValue);
                    }

                    for (int i = 0; i < apCount; i++)
                    {
                        double dx = x - _activePoints[i].X;
                        double dy = y - _activePoints[i].Y;
                        double dsq = dx * dx + dy * dy;

                        for (int k = 0; k < kCount; k++)
                        {
                            if (dsq < topK[k].DistSq)
                            {
                                for (int m = kCount - 1; m > k; m--)
                                {
                                    topK[m] = topK[m - 1];
                                }
                                topK[k] = (i, dsq);
                                break;
                            }
                        }
                    }

                    double wSum = 0;
                    for (int k = 0; k < kCount; k++)
                    {
                        double w = Math.Exp(-topK[k].DistSq * invTwoSigmaSq);
                        weights[k] = w;
                        wSum += w;
                    }

                    if (wSum <= 0.0) wSum = 1.0;
                    double invWSum = 1.0 / wSum;

                    _blendMap[rowOffset + x] = new ApBlendPoint(
                        topK[0].Idx,
                        kCount > 1 ? topK[1].Idx : topK[0].Idx,
                        kCount > 2 ? topK[2].Idx : topK[0].Idx,
                        kCount > 3 ? topK[3].Idx : topK[0].Idx,
                        (float)(weights[0] * invWSum),
                        kCount > 1 ? (float)(weights[1] * invWSum) : 0f,
                        kCount > 2 ? (float)(weights[2] * invWSum) : 0f,
                        kCount > 3 ? (float)(weights[3] * invWSum) : 0f);
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
        if (_activePoints.Count > 0 && _options.FeatheringEnabled && _blendMap is not null)
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
        Span<double> effShiftX = apCount <= 1024 ? stackalloc double[apCount] : new double[apCount];
        Span<double> effShiftY = apCount <= 1024 ? stackalloc double[apCount] : new double[apCount];

        for (int i = 0; i < apCount; i++)
        {
            effShiftX[i] = _activePoints[i].ShiftX;
            effShiftY[i] = _activePoints[i].ShiftY;
        }

        int w = _width;
        int h = _height;
        int ch = _channels;

        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * w;
            int dstRow = rowOffset * ch;

            for (int x = 0; x < w; x++)
            {
                ref readonly var bp = ref _blendMap![rowOffset + x];
                double sx = bp.W0 * effShiftX[bp.Ap0] +
                            bp.W1 * effShiftX[bp.Ap1] +
                            bp.W2 * effShiftX[bp.Ap2] +
                            bp.W3 * effShiftX[bp.Ap3];

                double sy = bp.W0 * effShiftY[bp.Ap0] +
                            bp.W1 * effShiftY[bp.Ap1] +
                            bp.W2 * effShiftY[bp.Ap2] +
                            bp.W3 * effShiftY[bp.Ap3];

                double fx = x - sx;
                double fy = y - sy;

                int x0 = (int)Math.Floor(fx);
                int y0 = (int)Math.Floor(fy);

                if ((uint)x0 < (uint)(w - 1) && (uint)y0 < (uint)(h - 1))
                {
                    double wx1 = fx - x0;
                    double wx0 = 1.0 - wx1;
                    double wy1 = fy - y0;
                    double wy0 = 1.0 - wy1;

                    double w00 = wx0 * wy0;
                    double w10 = wx1 * wy0;
                    double w01 = wx0 * wy1;
                    double w11 = wx1 * wy1;

                    int idx00 = (y0 * w + x0) * ch;
                    int idx10 = (y0 * w + x0 + 1) * ch;
                    int idx01 = ((y0 + 1) * w + x0) * ch;
                    int idx11 = ((y0 + 1) * w + x0 + 1) * ch;

                    int dstPixel = dstRow + x * ch;

                    for (int c = 0; c < ch; c++)
                    {
                        double val = data[idx00 + c] * w00 +
                                     data[idx10 + c] * w10 +
                                     data[idx01 + c] * w01 +
                                     data[idx11 + c] * w11;

                        _accumulator[dstPixel + c] += val;
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
