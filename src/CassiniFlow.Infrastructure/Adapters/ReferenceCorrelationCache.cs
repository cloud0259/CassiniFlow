using System.Collections.Concurrent;
using CassiniFlow.Domain.Models;
using OpenCvSharp;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Cache prÃ©calculÃ© de corrÃ©lation de phase pour la trame de rÃ©fÃ©rence et sa grille de points d'alignement (AP).
/// Ã‰limine des centaines de milliers d'allocations OpenCV Mat et de recalculs de fenÃªtres de Hanning en Passe 2.
/// </summary>
public sealed class ReferenceCorrelationCache : IDisposable
{
    public sealed class CachedApReference
    {
        public required AlignmentPoint Point { get; init; }
        public required int RefX0 { get; init; }
        public required int RefY0 { get; init; }
        public required int Tw { get; init; }
        public required int Th { get; init; }
        public required Mat RefTileMat { get; init; }
        public required Mat HanningWindow { get; init; }
    }

    private static readonly int[] SmoothDftSizes =
    [
        16, 24, 32, 36, 40, 48, 60, 64, 72, 80, 96, 108, 120, 128, 144, 160, 180, 192, 200, 240, 256
    ];

    /// <summary>
    /// Renvoie la dimension paire 5-smooth (2^a * 3^b * 5^c) la plus proche pour garantir
    /// une FFT Radix-2/3/5 O(N log N) ultra-rapide dans OpenCV (Ã©vite les facteurs premiers lents comme 104=8*13 ou 368=16*23).
    /// </summary>
    public static int GetOptimalEvenDftSize(int requestedSize, int maxAllowed)
    {
        int clamped = Math.Min(requestedSize, maxAllowed) & ~1;
        if (clamped < 16) return Math.Max(8, clamped);

        int best = SmoothDftSizes[0];
        int bestDiff = Math.Abs(clamped - best);

        for (int i = 1; i < SmoothDftSizes.Length; i++)
        {
            int candidate = SmoothDftSizes[i];
            if (candidate > maxAllowed) break;

            int diff = Math.Abs(clamped - candidate);
            if (diff < bestDiff)
            {
                best = candidate;
                bestDiff = diff;
            }
        }

        return best;
    }

    private readonly AstroFrame _reference;
    private readonly int _globalX0;
    private readonly int _globalY0;
    private readonly int _globalW;
    private readonly int _globalH;
    private readonly double _refCogX;
    private readonly double _refCogY;
    private readonly bool _refHasIsolatedObject;
    private readonly Mat _refGlobalMat;
    private readonly Mat _globalHanning;
    private readonly Dictionary<(int W, int H), Mat> _hanningBySize = new();
    private readonly List<CachedApReference> _cachedAps = [];
    private readonly float[] _regularizationWeights;
    private readonly ThreadLocal<Dictionary<(int W, int H), Mat>> _threadTileBuffers;
    private readonly ThreadLocal<Mat> _threadGlobalMat;
    private bool _disposed;

    public AstroFrame ReferenceFrame => _reference;
    public IReadOnlyList<CachedApReference> CachedActivePoints => _cachedAps;

    public ReferenceCorrelationCache(AstroFrame reference, AlignmentGrid grid)
    {
        _reference = reference;
        int w = reference.Width;
        int h = reference.Height;

        _globalW = GetOptimalEvenDftSize(Math.Min(w, 256), w);
        _globalH = GetOptimalEvenDftSize(Math.Min(h, 256), h);

        int anchorX = reference.DetectedCentroidX > 0 ? reference.DetectedCentroidX : (grid.AnchorPoint.X > 0 ? grid.AnchorPoint.X : w / 2);
        int anchorY = reference.DetectedCentroidY > 0 ? reference.DetectedCentroidY : (grid.AnchorPoint.Y > 0 ? grid.AnchorPoint.Y : h / 2);
        _globalX0 = Math.Clamp(anchorX - _globalW / 2, 0, Math.Max(0, w - _globalW));
        _globalY0 = Math.Clamp(anchorY - _globalH / 2, 0, Math.Max(0, h - _globalH));

        _refGlobalMat = new Mat(_globalH, _globalW, MatType.CV_32FC1);
        _refHasIsolatedObject = FastCrossCorrelation.FillGlobalRegionAndComputeCog(
            reference,
            _refGlobalMat,
            _globalX0,
            _globalY0,
            _globalW,
            _globalH,
            out _refCogX,
            out _refCogY);

        _globalHanning = new Mat();
        if (_globalW >= 4 && _globalH >= 4)
        {
            Cv2.CreateHanningWindow(_globalHanning, new Size(_globalW, _globalH), MatType.CV_32F);
        }

        _threadGlobalMat = new ThreadLocal<Mat>(
            () => new Mat(_globalH, _globalW, MatType.CV_32FC1),
            trackAllValues: true);

        _threadTileBuffers = new ThreadLocal<Dictionary<(int W, int H), Mat>>(
            () => new Dictionary<(int W, int H), Mat>(),
            trackAllValues: true);

        foreach (var ap in grid.Points)
        {
            if (!ap.IsActive) continue;

            int maxDim = Math.Min(w, h);
            int effTileSize = GetOptimalEvenDftSize(ap.Size, maxDim);
            int half = effTileSize / 2;

            int refX0 = Math.Clamp(ap.X - half, 0, Math.Max(0, w - effTileSize));
            int refY0 = Math.Clamp(ap.Y - half, 0, Math.Max(0, h - effTileSize));
            int tw = Math.Min(effTileSize, w - refX0);
            int th = Math.Min(effTileSize, h - refY0);

            if (tw < 8 || th < 8) continue;

            var key = (tw, th);
            if (!_hanningBySize.TryGetValue(key, out var hanning))
            {
                hanning = new Mat();
                Cv2.CreateHanningWindow(hanning, new Size(tw, th), MatType.CV_32F);
                _hanningBySize[key] = hanning;
            }

            var refTile = new Mat(th, tw, MatType.CV_32FC1);
            FastCrossCorrelation.FillFloatSubRegion(reference, refTile, refX0, refY0, tw, th);
            if (tw >= 16 && th >= 16)
            {
                Cv2.GaussianBlur(refTile, refTile, new Size(3, 3), 0.85);
            }

            _cachedAps.Add(new CachedApReference
            {
                Point = ap,
                RefX0 = refX0,
                RefY0 = refY0,
                Tw = tw,
                Th = th,
                RefTileMat = refTile,
                HanningWindow = hanning
            });
        }

        // PrÃ©calcul de la matrice de rÃ©gularisation spatiale gaussienne entre AP voisins
        int count = _cachedAps.Count;
        _regularizationWeights = new float[count * count];
        if (count > 0)
        {
            double sigmaSpatial = Math.Max(16.0, grid.TileSize * 0.85);
            double invTwoSigmaSq = 1.0 / (2.0 * sigmaSpatial * sigmaSpatial);

            for (int i = 0; i < count; i++)
            {
                var apI = _cachedAps[i].Point;
                double sumW = 0.0;

                for (int j = 0; j < count; j++)
                {
                    var apJ = _cachedAps[j].Point;
                    double dx = apI.X - apJ.X;
                    double dy = apI.Y - apJ.Y;
                    double dsq = dx * dx + dy * dy;

                    // PondÃ©ration par la distance spatiale et par la richesse de contraste du voisin
                    double contrastFactor = Math.Clamp(Math.Sqrt(Math.Max(1.0, apJ.Contrast)) / 18.0, 0.25, 1.0);
                    double weight = Math.Exp(-dsq * invTwoSigmaSq) * contrastFactor;
                    _regularizationWeights[i * count + j] = (float)weight;
                    sumW += weight;
                }

                if (sumW > 0.0)
                {
                    float invSum = (float)(1.0 / sumW);
                    for (int j = 0; j < count; j++)
                    {
                        _regularizationWeights[i * count + j] *= invSum;
                    }
                }
            }
        }
    }

    public (double ShiftX, double ShiftY) ComputeGlobalShift(AstroFrame current)
    {
        if (current.Width != _reference.Width || current.Height != _reference.Height)
        {
            return (0, 0);
        }

        int coarseShiftX = 0;
        int coarseShiftY = 0;
        if (current.DetectedCentroidX > 0 && _reference.DetectedCentroidX > 0)
        {
            coarseShiftX = _reference.DetectedCentroidX - current.DetectedCentroidX;
            coarseShiftY = _reference.DetectedCentroidY - current.DetectedCentroidY;
        }

        try
        {
            int curX0 = Math.Clamp(_globalX0 - coarseShiftX, 0, Math.Max(0, current.Width - _globalW));
            int curY0 = Math.Clamp(_globalY0 - coarseShiftY, 0, Math.Max(0, current.Height - _globalH));

            var curGlobal = _threadGlobalMat.Value!;
            bool curHasIsolatedObject = FastCrossCorrelation.FillGlobalRegionAndComputeCog(
                current,
                curGlobal,
                curX0,
                curY0,
                _globalW,
                _globalH,
                out double curCogX,
                out double curCogY);

            double cogShiftX = (_refHasIsolatedObject && curHasIsolatedObject) ? (_refCogX - curCogX) : coarseShiftX;
            double cogShiftY = (_refHasIsolatedObject && curHasIsolatedObject) ? (_refCogY - curCogY) : coarseShiftY;

            var shift = Cv2.PhaseCorrelate(curGlobal, _refGlobalMat, _globalHanning, out double response);
            double totalShiftX = (_globalX0 - curX0) + shift.X;
            double totalShiftY = (_globalY0 - curY0) + shift.Y;

            // Si l'objet est isolÃ© sur fond de ciel (planÃ©taire), le barycentre sub-pixel seuillÃ© (CoG)
            // est infaillible Ã  ~0.1px prÃ¨s : on rejette tout saut aberrant de PhaseCorrelate (> 2.5px du CoG).
            if (_refHasIsolatedObject && curHasIsolatedObject)
            {
                if (response < 0.12 ||
                    Math.Abs(totalShiftX - cogShiftX) > 2.5 ||
                    Math.Abs(totalShiftY - cogShiftY) > 2.5)
                {
                    return (cogShiftX, cogShiftY);
                }

                // Combinaison optimale CoG sub-pixel + PhaseCorrelate filtrÃ©
                return (0.5 * totalShiftX + 0.5 * cogShiftX, 0.5 * totalShiftY + 0.5 * cogShiftY);
            }

            if (current.DetectedCentroidX > 0 && _reference.DetectedCentroidX > 0)
            {
                if (response < 0.06 ||
                    Math.Abs(totalShiftX - coarseShiftX) > 8.0 ||
                    Math.Abs(totalShiftY - coarseShiftY) > 8.0)
                {
                    return (coarseShiftX, coarseShiftY);
                }
            }

            return (totalShiftX, totalShiftY);
        }
        catch
        {
            return (coarseShiftX, coarseShiftY);
        }
    }

    /// <summary>
    /// Calcule les dÃ©calages sub-pixel de tous les AP actifs sur le thread courant (sans verrou ni mutation partagÃ©e),
    /// avec bornage physique de la distorsion locale de turbulence et rÃ©gularisation spatiale inter-AP.
    /// </summary>
    public void ComputeAllTileShiftsThreadSafe(
        AstroFrame current,
        double globalShiftX,
        double globalShiftY,
        Span<double> outShiftX,
        Span<double> outShiftY)
    {
        int count = _cachedAps.Count;
        if (count == 0) return;

        var dict = _threadTileBuffers.Value!;
        Span<double> rawDiffX = count <= 256 ? stackalloc double[count] : new double[count];
        Span<double> rawDiffY = count <= 256 ? stackalloc double[count] : new double[count];

        // Amplitude maximale physiquement plausible de la turbulence diffÃ©rentielle locale par rapport au mouvement global
        const double MaxLocalResidualPx = 2.5;

        for (int i = 0; i < count; i++)
        {
            var cached = _cachedAps[i];
            var ap = cached.Point;
            int tw = cached.Tw;
            int th = cached.Th;
            int halfW = tw / 2;
            int halfH = th / 2;

            try
            {
                int curCenterX = (int)Math.Round(ap.X - globalShiftX);
                int curCenterY = (int)Math.Round(ap.Y - globalShiftY);
                int curX0 = Math.Clamp(curCenterX - halfW, 0, Math.Max(0, current.Width - tw));
                int curY0 = Math.Clamp(curCenterY - halfH, 0, Math.Max(0, current.Height - th));

                var key = (tw, th);
                if (!dict.TryGetValue(key, out var curTile))
                {
                    curTile = new Mat(th, tw, MatType.CV_32FC1);
                    dict[key] = curTile;
                }

                FastCrossCorrelation.FillFloatSubRegion(current, curTile, curX0, curY0, tw, th);
                if (tw >= 16 && th >= 16)
                {
                    Cv2.GaussianBlur(curTile, curTile, new Size(3, 3), 0.85);
                }
                var residualShift = Cv2.PhaseCorrelate(curTile, cached.RefTileMat, cached.HanningWindow, out double response);

                double totalShiftX = (cached.RefX0 - curX0) + residualShift.X;
                double totalShiftY = (cached.RefY0 - curY0) + residualShift.Y;
                double diffX = totalShiftX - globalShiftX;
                double diffY = totalShiftY - globalShiftY;

                if (response >= 0.08 &&
                    Math.Abs(diffX) <= MaxLocalResidualPx &&
                    Math.Abs(diffY) <= MaxLocalResidualPx)
                {
                    double respConfidence = Math.Clamp((response - 0.08) / 0.30, 0.15, 1.0);
                    double contrastConfidence = ap.Contrast > 0.0
                        ? Math.Clamp(Math.Sqrt(ap.Contrast) / 18.0, 0.25, 1.0)
                        : 1.0;
                    double damping = respConfidence * contrastConfidence;
                    rawDiffX[i] = diffX * damping;
                    rawDiffY[i] = diffY * damping;
                }
                else
                {
                    rawDiffX[i] = 0.0;
                    rawDiffY[i] = 0.0;
                }
            }
            catch
            {
                rawDiffX[i] = 0.0;
                rawDiffY[i] = 0.0;
            }
        }

        // RÃ©gularisation spatiale gaussienne du champ de vecteurs diffÃ©rentiels entre AP voisins
        for (int i = 0; i < count; i++)
        {
            double smoothDx = 0.0;
            double smoothDy = 0.0;
            int rowBase = i * count;

            for (int j = 0; j < count; j++)
            {
                float w = _regularizationWeights[rowBase + j];
                smoothDx += w * rawDiffX[j];
                smoothDy += w * rawDiffY[j];
            }

            outShiftX[i] = globalShiftX + smoothDx;
            outShiftY[i] = globalShiftY + smoothDy;
        }
    }

    public void ComputeAllTileShiftsParallel(AstroFrame current, double globalShiftX, double globalShiftY)
    {
        int count = _cachedAps.Count;
        if (count == 0) return;

        Span<double> sx = count <= 256 ? stackalloc double[count] : new double[count];
        Span<double> sy = count <= 256 ? stackalloc double[count] : new double[count];
        ComputeAllTileShiftsThreadSafe(current, globalShiftX, globalShiftY, sx, sy);

        for (int i = 0; i < count; i++)
        {
            _cachedAps[i].Point.ShiftX = sx[i];
            _cachedAps[i].Point.ShiftY = sy[i];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _refGlobalMat.Dispose();
        _globalHanning.Dispose();

        foreach (var m in _threadGlobalMat.Values)
        {
            m.Dispose();
        }
        _threadGlobalMat.Dispose();

        foreach (var h in _hanningBySize.Values)
        {
            h.Dispose();
        }
        _hanningBySize.Clear();

        foreach (var ap in _cachedAps)
        {
            ap.RefTileMat.Dispose();
        }
        _cachedAps.Clear();

        foreach (var dict in _threadTileBuffers.Values)
        {
            foreach (var m in dict.Values)
            {
                m.Dispose();
            }
            dict.Clear();
        }
        _threadTileBuffers.Dispose();
    }
}
