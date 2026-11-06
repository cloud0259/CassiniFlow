using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Moteur d'empilement haute résolution multi-zones avec lissage des coutures (cosine/Gaussian feathering).
/// Combine les meilleures trames locales en accumulant avec une dynamique 64-bit pour éliminer le bruit.
/// Utilise une table de correspondances précalculée O(1) et des boucles unsafe parallélisées pour garantir zéro allocation.
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
    private readonly double[] _effShiftX;
    private readonly double[] _effShiftY;
    private int _stackedFramesCount;

    public int StackedFramesCount => _stackedFramesCount;

    /// <summary>
    /// Tampon d'accumulation thread-local permettant d'empiler plusieurs trames en parallèle
    /// sur tous les cœurs CPU sans verrou (lock-free), puis de fusionner en O(N) à la fin d'un palier.
    /// </summary>
    public sealed class LocalAccumulatorBuffer
    {
        public readonly double[] Accumulator;
        public readonly double[] WeightMap;
        public int FramesCount;

        public LocalAccumulatorBuffer(int totalPixels, int channels)
        {
            Accumulator = new double[totalPixels * channels];
            WeightMap = new double[totalPixels];
        }
    }

    public LocalAccumulatorBuffer CreateLocalBuffer()
        => new(_width * _height, _channels);

    public unsafe void MergeLocalBuffer(LocalAccumulatorBuffer local)
    {
        if (local.FramesCount <= 0) return;

        int accLen = _accumulator.Length;
        int wLen = _weightMap.Length;

        fixed (double* pDstAcc = _accumulator)
        fixed (double* pSrcAcc = local.Accumulator)
        fixed (double* pDstW = _weightMap)
        fixed (double* pSrcW = local.WeightMap)
        {
            for (int i = 0; i < accLen; i++)
            {
                pDstAcc[i] += pSrcAcc[i];
            }
            for (int i = 0; i < wLen; i++)
            {
                pDstW[i] += pSrcW[i];
            }
        }

        _stackedFramesCount += local.FramesCount;
    }

    public unsafe void AccumulateFrameIntoBuffer(
        AstroFrame frame,
        int shiftX,
        int shiftY,
        ReadOnlySpan<double> apShiftX,
        ReadOnlySpan<double> apShiftY,
        LocalAccumulatorBuffer buffer)
    {
        if (frame.Width != _width || frame.Height != _height || frame.Channels != _channels)
        {
            return;
        }

        ushort[] data = frame.Data;
        int w = _width;
        int h = _height;
        int ch = _channels;

        if (_activePoints.Count > 0 && _options.FeatheringEnabled && _blendMap is not null &&
            apShiftX.Length >= _activePoints.Count && apShiftY.Length >= _activePoints.Count)
        {
            int maxValidX = w - 1;
            int maxValidY = h - 1;

            fixed (ushort* pData = data)
            fixed (double* pAcc = buffer.Accumulator)
            fixed (double* pWeight = buffer.WeightMap)
            fixed (ApBlendPoint* pBlend = _blendMap)
            fixed (double* pSx = apShiftX)
            fixed (double* pSy = apShiftY)
            {
                for (int y = 0; y < h; y++)
                {
                    AccumulateRowUnsafe(y, w, ch, maxValidX, maxValidY, pData, pAcc, pWeight, pBlend, pSx, pSy);
                }
            }
        }
        else
        {
            fixed (ushort* pData = data)
            fixed (double* pAcc = buffer.Accumulator)
            fixed (double* pWeight = buffer.WeightMap)
            {
                for (int y = 0; y < h; y++)
                {
                    int srcY = y - shiftY;
                    if ((uint)srcY >= (uint)h) continue;

                    int dstRow = y * w * ch;
                    int srcRow = srcY * w * ch;
                    int wRow = y * w;

                    for (int x = 0; x < w; x++)
                    {
                        int srcX = x - shiftX;
                        if ((uint)srcX >= (uint)w) continue;

                        int dstPixel = dstRow + x * ch;
                        int srcPixel = srcRow + srcX * ch;

                        if (ch == 3)
                        {
                            pAcc[dstPixel] += pData[srcPixel];
                            pAcc[dstPixel + 1] += pData[srcPixel + 1];
                            pAcc[dstPixel + 2] += pData[srcPixel + 2];
                        }
                        else
                        {
                            for (int c = 0; c < ch; c++)
                            {
                                pAcc[dstPixel + c] += pData[srcPixel + c];
                            }
                        }

                        pWeight[wRow + x] += 1.0;
                    }
                }
            }
        }

        buffer.FramesCount++;
    }

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
        _effShiftX = new double[Math.Max(1, _activePoints.Count)];
        _effShiftY = new double[Math.Max(1, _activePoints.Count)];

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
                Span<(int Idx, double DistSq)> topK = stackalloc (int, double)[5];
                Span<double> weights = stackalloc double[4];

                for (int x = 0; x < _width; x++)
                {
                    int searchK = Math.Min(5, apCount);
                    int kCount = Math.Min(4, apCount);
                    for (int k = 0; k < searchK; k++)
                    {
                        topK[k] = (-1, double.MaxValue);
                    }

                    for (int i = 0; i < apCount; i++)
                    {
                        double dx = x - _activePoints[i].X;
                        double dy = y - _activePoints[i].Y;
                        double dsq = dx * dx + dy * dy;

                        for (int k = 0; k < searchK; k++)
                        {
                            if (dsq < topK[k].DistSq)
                            {
                                for (int m = searchK - 1; m > k; m--)
                                {
                                    topK[m] = topK[m - 1];
                                }
                                topK[k] = (i, dsq);
                                break;
                            }
                        }
                    }

                    // Support compact C1 : lorsque apCount >= 5, le poids du 4e AP (topK[3]) s'annule
                    // exactement (valeur 0 et dérivée 0) sur la frontière de Voronoï où d_3 == d_4 (topK[4]),
                    // éliminant toute discontinuité géométrique (triangles/polygones) entre zones d'AP.
                    bool hasCompactCutoff = searchK == 5 && topK[4].DistSq > 1e-6 && topK[4].DistSq < double.MaxValue * 0.5;
                    double invCutoffDistSq = hasCompactCutoff ? (1.0 / topK[4].DistSq) : 0.0;

                    double wSum = 0;
                    for (int k = 0; k < kCount; k++)
                    {
                        double w = Math.Exp(-topK[k].DistSq * invTwoSigmaSq);
                        if (hasCompactCutoff)
                        {
                            double t = 1.0 - topK[k].DistSq * invCutoffDistSq;
                            w *= t > 0.0 ? (t * t) : 0.0;
                        }
                        weights[k] = w;
                        wSum += w;
                    }

                    if (wSum <= 1e-12)
                    {
                        wSum = 0;
                        for (int k = 0; k < kCount; k++)
                        {
                            double w = Math.Exp(-topK[k].DistSq * invTwoSigmaSq);
                            weights[k] = w;
                            wSum += w;
                        }
                        if (wSum <= 0.0) wSum = 1.0;
                    }
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
            AccumulateWithGrid(data);
        }
        else
        {
            // Accumulation globale simple avec translation (shiftX, shiftY)
            AccumulateGlobal(data, shiftX, shiftY);
        }

        _stackedFramesCount++;
    }

    private unsafe void AccumulateGlobal(ushort[] data, int shiftX, int shiftY)
    {
        int w = _width;
        int h = _height;
        int ch = _channels;

        fixed (ushort* pData = data)
        fixed (double* pAcc = _accumulator)
        fixed (double* pWeight = _weightMap)
        {
            for (int y = 0; y < h; y++)
            {
                int srcY = y - shiftY;
                if ((uint)srcY >= (uint)h) continue;

                int dstRow = y * w * ch;
                int srcRow = srcY * w * ch;
                int wRow = y * w;

                for (int x = 0; x < w; x++)
                {
                    int srcX = x - shiftX;
                    if ((uint)srcX >= (uint)w) continue;

                    int dstPixel = dstRow + x * ch;
                    int srcPixel = srcRow + srcX * ch;

                    if (ch == 3)
                    {
                        pAcc[dstPixel] += pData[srcPixel];
                        pAcc[dstPixel + 1] += pData[srcPixel + 1];
                        pAcc[dstPixel + 2] += pData[srcPixel + 2];
                    }
                    else
                    {
                        for (int c = 0; c < ch; c++)
                        {
                            pAcc[dstPixel + c] += pData[srcPixel + c];
                        }
                    }

                    pWeight[wRow + x] += 1.0;
                }
            }
        }
    }

    private unsafe void AccumulateWithGrid(ushort[] data)
    {
        int apCount = _activePoints.Count;
        for (int i = 0; i < apCount; i++)
        {
            _effShiftX[i] = _activePoints[i].ShiftX;
            _effShiftY[i] = _activePoints[i].ShiftY;
        }

        int w = _width;
        int h = _height;
        int ch = _channels;
        int maxValidX = w - 1;
        int maxValidY = h - 1;

        fixed (ushort* pData = data)
        fixed (double* pAcc = _accumulator)
        fixed (double* pWeight = _weightMap)
        fixed (ApBlendPoint* pBlend = _blendMap)
        fixed (double* pShiftX = _effShiftX)
        fixed (double* pShiftY = _effShiftY)
        {
            ushort* ptrData = pData;
            double* ptrAcc = pAcc;
            double* ptrWeight = pWeight;
            ApBlendPoint* ptrBlend = pBlend;
            double* ptrSx = pShiftX;
            double* ptrSy = pShiftY;

            if (h >= 128)
            {
                Parallel.For(0, h, y =>
                {
                    AccumulateRowUnsafe(y, w, ch, maxValidX, maxValidY, ptrData, ptrAcc, ptrWeight, ptrBlend, ptrSx, ptrSy);
                });
            }
            else
            {
                for (int y = 0; y < h; y++)
                {
                    AccumulateRowUnsafe(y, w, ch, maxValidX, maxValidY, ptrData, ptrAcc, ptrWeight, ptrBlend, ptrSx, ptrSy);
                }
            }
        }
    }

    private static unsafe void AccumulateRowUnsafe(
        int y,
        int w,
        int ch,
        int maxValidX,
        int maxValidY,
        ushort* pData,
        double* pAcc,
        double* pWeight,
        ApBlendPoint* pBlend,
        double* pShiftX,
        double* pShiftY)
    {
        int rowOffset = y * w;
        int dstRow = rowOffset * ch;

        for (int x = 0; x < w; x++)
        {
            ApBlendPoint bp = pBlend[rowOffset + x];
            double sx = bp.W0 * pShiftX[bp.Ap0] +
                        bp.W1 * pShiftX[bp.Ap1] +
                        bp.W2 * pShiftX[bp.Ap2] +
                        bp.W3 * pShiftX[bp.Ap3];

            double sy = bp.W0 * pShiftY[bp.Ap0] +
                        bp.W1 * pShiftY[bp.Ap1] +
                        bp.W2 * pShiftY[bp.Ap2] +
                        bp.W3 * pShiftY[bp.Ap3];

            double fx = x - sx;
            double fy = y - sy;

            if (fx < 0.0 || fy < 0.0) continue;

            int x0 = (int)fx;
            int y0 = (int)fy;

            if (x0 < maxValidX && y0 < maxValidY)
            {
                double wx1 = fx - x0;
                double wx0 = 1.0 - wx1;
                double wy1 = fy - y0;
                double wy0 = 1.0 - wy1;

                double w00 = wx0 * wy0;
                double w10 = wx1 * wy0;
                double w01 = wx0 * wy1;
                double w11 = wx1 * wy1;

                int row0 = y0 * w;
                int row1 = row0 + w;

                int dstPixel = dstRow + x * ch;

                if (ch == 3)
                {
                    int idx00 = (row0 + x0) * 3;
                    int idx10 = idx00 + 3;
                    int idx01 = (row1 + x0) * 3;
                    int idx11 = idx01 + 3;

                    pAcc[dstPixel] += pData[idx00] * w00 + pData[idx10] * w10 + pData[idx01] * w01 + pData[idx11] * w11;
                    pAcc[dstPixel + 1] += pData[idx00 + 1] * w00 + pData[idx10 + 1] * w10 + pData[idx01 + 1] * w01 + pData[idx11 + 1] * w11;
                    pAcc[dstPixel + 2] += pData[idx00 + 2] * w00 + pData[idx10 + 2] * w10 + pData[idx01 + 2] * w01 + pData[idx11 + 2] * w11;
                }
                else
                {
                    int idx00 = (row0 + x0) * ch;
                    int idx10 = idx00 + ch;
                    int idx01 = (row1 + x0) * ch;
                    int idx11 = idx01 + ch;

                    for (int c = 0; c < ch; c++)
                    {
                        pAcc[dstPixel + c] += pData[idx00 + c] * w00 +
                                              pData[idx10 + c] * w10 +
                                              pData[idx01 + c] * w01 +
                                              pData[idx11 + c] * w11;
                    }
                }

                pWeight[rowOffset + x] += 1.0;
            }
        }
    }

    /// <summary>
    /// Finalise et normalise l'image empilée jusqu'ici (snapshot non-destructif), avec correction RVB et accentuation optionnelle.
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

        // 2. Correction de dispersion atmosphérique (alignement sous-pixel R et B sur G)
        if (_options.CorrectAtmosphericDispersion && _channels >= 3)
        {
            output = AtmosphericDispersionCorrector.AlignChannels(output);
        }

        // 3. Accentuation haute fréquence (Convolution Laplacian non-destructif)
        // Appliquée avant normalisation pour bénéficier du headroom naturel et éviter de brûler les hautes lumières
        if (_options.SharpeningEnabled)
        {
            output = ConvolutionSharpeningFilter.ApplySharpening(output, _options.SharpeningBlendPercent);
        }

        // 4. Normalisation de la brillance maximale contrôlée
        if (_options.NormalizeBrightness)
        {
            NormalizeMasterBrightness(output, _options.NormalizeTargetPercent);
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
            double v = data[i] * factor;
            // Soft-knee au-delà de 92% pour éviter tout écrêtage dur / brûlure des hautes lumières
            if (v > 60000.0)
            {
                double excess = v - 60000.0;
                v = 60000.0 + (5535.0 * (1.0 - Math.Exp(-excess / 8000.0)));
            }
            data[i] = (ushort)Math.Clamp(Math.Round(v), 0, 65535);
        }
    }
}
