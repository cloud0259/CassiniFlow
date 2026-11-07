using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Moteur de domaine pur pour le Live Stacking planétaire, lunaire et solaire haute cadence.
/// Implémente un buffer glissant des N meilleures trames (Rolling Top-K), un accumulateur incrémental O(1),
/// un alignement hybride (CoG sub-pixel / Corrélation de phase surface) et un post-traitement d'affichage commutable.
/// </summary>
public sealed class LiveStackingEngine
{
    private sealed class BufferedFrameEntry
    {
        public required AstroFrame AlignedFrame { get; init; }
        public required double QualityScore { get; init; }
        public required double Weight { get; init; }
        public required long SequenceNumber { get; init; }
    }

    private readonly object _sync = new();
    private readonly Func<AstroFrame, AstroFrame, (double ShiftX, double ShiftY)>? _surfaceCorrelator;
    private readonly List<BufferedFrameEntry> _rollingBuffer = [];
    private readonly Queue<double> _recentScoresWindow = new(128);
    private readonly Queue<double> _recentBrightnessWindow = new(64);

    private double[] _accumulator = [];
    private double _totalWeight;
    private int _width;
    private int _height;
    private int _channels;
    private int _bitDepth = 8;

    private AstroFrame? _referenceFrame;
    private double _lastValidCenterX = -1;
    private double _lastValidCenterY = -1;
    private long _sequenceCounter;

    // Compteurs de télémétrie
    private int _totalFramesReceived;
    private int _framesAccepted;
    private int _framesRejectedQuality;
    private int _framesRejectedDriftOrCloud;
    private double _currentQualityScore;
    private double _bestQualityScore;
    private double _thresholdQualityScore;
    private double _currentShiftX;
    private double _currentShiftY;
    private string _activeAlignmentMode = "Auto (CoG)";
    private string? _lastRejectReason;

    public LiveStackingEngine(Func<AstroFrame, AstroFrame, (double ShiftX, double ShiftY)>? surfaceCorrelator = null)
    {
        _surfaceCorrelator = surfaceCorrelator;
    }

    /// <summary>
    /// Réinitialise complètement le buffer glissant, l'accumulateur et les compteurs de session.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _rollingBuffer.Clear();
            _recentScoresWindow.Clear();
            _recentBrightnessWindow.Clear();
            if (_accumulator.Length > 0)
            {
                Array.Clear(_accumulator, 0, _accumulator.Length);
            }
            _totalWeight = 0;
            _referenceFrame = null;
            _lastValidCenterX = -1;
            _lastValidCenterY = -1;
            _sequenceCounter = 0;

            _totalFramesReceived = 0;
            _framesAccepted = 0;
            _framesRejectedQuality = 0;
            _framesRejectedDriftOrCloud = 0;
            _currentQualityScore = 0;
            _bestQualityScore = 0;
            _thresholdQualityScore = 0;
            _currentShiftX = 0;
            _currentShiftY = 0;
            _lastRejectReason = null;
        }
    }

    /// <summary>
    /// Traite une nouvelle trame entrante, met à jour le buffer glissant Top-K si elle passe les filtres,
    /// et retourne true si l'empilement a été modifié.
    /// </summary>
    public bool ProcessFrame(AstroFrame inputFrame, LiveStackingOptions options, bool debayerIfRaw = true)
    {
        ArgumentNullException.ThrowIfNull(inputFrame);
        ArgumentNullException.ThrowIfNull(options);

        // 1. Débayérisation à la volée si trame RAW couleur
        AstroFrame workingFrame = inputFrame;
        if (debayerIfRaw && inputFrame.IsBayerRaw && inputFrame.Channels == 1)
        {
            workingFrame = ImageProcessor.Debayer(inputFrame, inputFrame.BayerPattern, DebayerAlgorithm.Bilinear);
        }

        lock (_sync)
        {
            _totalFramesReceived++;
            _sequenceCounter++;

            // Si les dimensions ou le nombre de canaux changent (ex: changement de ROI), réinitialiser l'accumulateur
            if (workingFrame.Width != _width || workingFrame.Height != _height || workingFrame.Channels != _channels)
            {
                InitializeDimensions(workingFrame.Width, workingFrame.Height, workingFrame.Channels, workingFrame.OriginalBitDepth);
            }

            // 2. Filtre de luminosité / passage nuageux
            double meanLum = ComputeMeanLuminance8Bit(workingFrame);
            if (options.EnableCloudBrightnessFilter && _recentBrightnessWindow.Count >= 8)
            {
                double avgRecentBrightness = _recentBrightnessWindow.Average();
                if (avgRecentBrightness > 8.0)
                {
                    double dropRatio = (avgRecentBrightness - meanLum) / avgRecentBrightness * 100.0;
                    if (dropRatio > options.MaxBrightnessDropPercent)
                    {
                        _framesRejectedDriftOrCloud++;
                        _lastRejectReason = "Cloud / Brightness drop";
                        return false;
                    }
                }
            }

            // 3. Détection d'objet, vérification de surexposition et alignement hybride
            var detection = StabilisationAndQualityEngine.DetectPlanetOrObject(
                workingFrame,
                checkObject: true,
                autoThreshold: options.AutoDetectionThreshold,
                manualThreshold8Bit: options.ManualDetectionThreshold8Bit,
                minObjectSizePixels: options.MinObjectSizePixels,
                shadowEdge: LunarShadowEdge.None,
                checkOverexposed: options.RejectOverexposed,
                maxOverexposedPixels: options.MaxOverexposedPixels,
                rejectClippedObjects: options.RejectEdgeClippedFrames);

            bool isSurfaceScene = detection.DetectedPixelCount > (_width * _height * 2 / 5);

            // Choix du mode d'alignement effectif
            bool useSurfaceAlignment = options.AlignmentMethod switch
            {
                LiveAlignmentMethod.PlanetCog => false,
                LiveAlignmentMethod.SurfacePhaseCorrelation => true,
                _ => isSurfaceScene || !detection.Accepted
            };

            if (!useSurfaceAlignment && !detection.Accepted)
            {
                _framesRejectedDriftOrCloud++;
                _lastRejectReason = detection.RejectionReason ?? "Object rejected";
                return false;
            }

            double shiftX = 0;
            double shiftY = 0;

            if (!useSurfaceAlignment)
            {
                _activeAlignmentMode = options.AlignmentMethod == LiveAlignmentMethod.AutoHybrid ? "Auto (CoG)" : "Planet CoG";
                var (subCogX, subCogY) = ComputeSubPixelCentroid(workingFrame, detection.CentroidX, detection.CentroidY, detection.Threshold8BitUsed);

                // Vérification du saut maximal (MaxDriftPixels) entre trames consécutives
                if (_lastValidCenterX >= 0 && options.MaxDriftPixels > 0)
                {
                    double jump = Math.Sqrt(
                        (subCogX - _lastValidCenterX) * (subCogX - _lastValidCenterX) +
                        (subCogY - _lastValidCenterY) * (subCogY - _lastValidCenterY));

                    if (jump > options.MaxDriftPixels)
                    {
                        _framesRejectedDriftOrCloud++;
                        _lastRejectReason = $"Drift jump ({jump:F1} px > {options.MaxDriftPixels} px)";
                        return false;
                    }
                }

                _lastValidCenterX = subCogX;
                _lastValidCenterY = subCogY;

                // Décalage requis pour centrer l'astre au milieu du canevas
                shiftX = (_width * 0.5) - subCogX;
                shiftY = (_height * 0.5) - subCogY;
            }
            else
            {
                _activeAlignmentMode = options.AlignmentMethod == LiveAlignmentMethod.AutoHybrid ? "Auto (Surface)" : "Surface";
                if (_referenceFrame is not null)
                {
                    (shiftX, shiftY) = ComputeSurfaceShift(workingFrame, _referenceFrame);
                    double totalDrift = Math.Sqrt(shiftX * shiftX + shiftY * shiftY);
                    if (options.MaxDriftPixels > 0 && totalDrift > options.MaxDriftPixels)
                    {
                        _framesRejectedDriftOrCloud++;
                        _lastRejectReason = $"Surface drift ({totalDrift:F1} px > {options.MaxDriftPixels} px)";
                        return false;
                    }
                }
            }

            _currentShiftX = shiftX;
            _currentShiftY = shiftY;

            // Mémoriser la luminosité valide dans la fenêtre glissante
            if (_recentBrightnessWindow.Count >= 64) _recentBrightnessWindow.Dequeue();
            _recentBrightnessWindow.Enqueue(meanLum);

            // 4. Évaluation de la qualité de seeing
            double quality = StabilisationAndQualityEngine.ComputeQualityScore(
                workingFrame,
                options.QualityAlgorithm,
                noiseRobust: options.NoiseRobust);

            _currentQualityScore = quality;
            if (quality > _bestQualityScore)
            {
                _bestQualityScore = quality;
            }

            if (_recentScoresWindow.Count >= 120) _recentScoresWindow.Dequeue();
            _recentScoresWindow.Enqueue(quality);

            // Calcul du seuil dynamique de qualité (Quality Gate)
            double dynamicThreshold = ComputeDynamicQualityThreshold(options);
            _thresholdQualityScore = dynamicThreshold;

            if (options.EnableQualityGate && _rollingBuffer.Count >= 6 && quality < dynamicThreshold)
            {
                _framesRejectedQuality++;
                _lastRejectReason = "Low seeing score";
                return false;
            }

            // 5. Alignement sub-pixel de la trame retenue
            AstroFrame alignedFrame = (Math.Abs(shiftX) < 0.05 && Math.Abs(shiftY) < 0.05)
                ? workingFrame.Clone()
                : ShiftFrameBilinear(workingFrame, shiftX, shiftY);

            alignedFrame.QualityScore = quality;
            alignedFrame.DetectedCentroidX = _width / 2;
            alignedFrame.DetectedCentroidY = _height / 2;

            if (options.NormalizeBackground)
            {
                SubtractSkyBackgroundPedestal(alignedFrame);
            }

            // Initialiser ou mettre à jour la trame de référence avec la meilleure trame vue
            if (_referenceFrame is null || quality >= _referenceFrame.QualityScore)
            {
                _referenceFrame = alignedFrame;
            }

            // 6. Insertion dans le Buffer Glissant Top-K et mise à jour incrémentale O(1) de l'accumulateur
            int maxCap = Math.Clamp(options.RollingBufferSize, 5, 1000);
            PurgeExcessCapacity(maxCap);

            double weight = options.EnableQualityWeighting ? Math.Max(0.1, quality) : 1.0;

            if (_rollingBuffer.Count >= maxCap)
            {
                // Chercher d'abord une trame expirée (plus ancienne que 2.5x la capacité du buffer) pour garantir le renouvellement temporel
                long maxAgeSeq = _sequenceCounter - (long)(maxCap * 2.5);
                int evictIndex = -1;

                for (int i = 0; i < _rollingBuffer.Count; i++)
                {
                    if (_rollingBuffer[i].SequenceNumber < maxAgeSeq)
                    {
                        evictIndex = i;
                        break;
                    }
                }

                // Sinon, chercher la trame ayant le plus faible score de qualité dans le buffer
                if (evictIndex < 0)
                {
                    double minScore = double.MaxValue;
                    for (int i = 0; i < _rollingBuffer.Count; i++)
                    {
                        if (_rollingBuffer[i].QualityScore < minScore)
                        {
                            minScore = _rollingBuffer[i].QualityScore;
                            evictIndex = i;
                        }
                    }

                    // Si la nouvelle trame est moins bonne que la pire du buffer, on la rejette
                    if (evictIndex >= 0 && quality < minScore)
                    {
                        _framesRejectedQuality++;
                        _lastRejectReason = "Below buffer Top-K";
                        return false;
                    }
                }

                if (evictIndex >= 0)
                {
                    var evicted = _rollingBuffer[evictIndex];
                    SubtractFromAccumulator(evicted.AlignedFrame, evicted.Weight);
                    _rollingBuffer.RemoveAt(evictIndex);
                }
            }

            var newEntry = new BufferedFrameEntry
            {
                AlignedFrame = alignedFrame,
                QualityScore = quality,
                Weight = weight,
                SequenceNumber = _sequenceCounter
            };

            _rollingBuffer.Add(newEntry);
            AddToAccumulator(alignedFrame, weight);
            _framesAccepted++;
            _lastRejectReason = null;

            return true;
        }
    }

    /// <summary>
    /// Ajuste à chaud la capacité du buffer glissant si l'utilisateur réduit RollingBufferSize.
    /// </summary>
    public void ResizeBufferCapacity(int newCapacity)
    {
        lock (_sync)
        {
            PurgeExcessCapacity(Math.Clamp(newCapacity, 5, 1000));
        }
    }

    /// <summary>
    /// Retourne l'empilement linéaire 16-bit brut (sans accentuation ni étirement d'affichage),
    /// idéal pour la sauvegarde scientifique FITS/TIFF 16-bit.
    /// </summary>
    public unsafe AstroFrame? GetRawStackedFrame()
    {
        lock (_sync)
        {
            if (_rollingBuffer.Count == 0 || _totalWeight <= 1e-9 || _width <= 0 || _height <= 0)
            {
                return null;
            }

            var rawStack = new AstroFrame(_width, _height, _channels, 16)
            {
                FrameIndex = _framesAccepted,
                TimestampUtc = DateTime.UtcNow,
                QualityScore = _bestQualityScore,
                DetectedCentroidX = _width / 2,
                DetectedCentroidY = _height / 2
            };

            int len = _accumulator.Length;
            double invWeight = 1.0 / _totalWeight;

            fixed (double* pAcc = _accumulator)
            fixed (ushort* pDst = rawStack.Data)
            {
                for (int i = 0; i < len; i++)
                {
                    int val = (int)(pAcc[i] * invWeight + 0.5);
                    pDst[i] = (ushort)(val < 0 ? 0 : (val > 65535 ? 65535 : val));
                }
            }

            return rawStack;
        }
    }

    /// <summary>
    /// Génère la trame d'affichage temps réel en appliquant de façon non-destructive :
    /// 1) L'ADC numérique (auto ou manuel)
    /// 2) L'accentuation en direct commutable (Live Sharpening : Convolution + Unsharp Mask)
    /// 3) L'étirement d'histogramme / Gamma d'affichage.
    /// </summary>
    public AstroFrame? GetDisplayFrame(LiveStackingOptions options)
    {
        var frame = GetRawStackedFrame();
        if (frame is null)
        {
            return null;
        }

        // 1. Correction de dispersion atmosphérique (ADC numérique)
        if (options.EnableDigitalAdc && frame.Channels == 3)
        {
            if (options.AutoDigitalAdc)
            {
                frame = AtmosphericDispersionCorrector.AlignChannels(frame);
            }
            else if (options.ManualAdcShiftRx != 0 || options.ManualAdcShiftRy != 0 ||
                     options.ManualAdcShiftBx != 0 || options.ManualAdcShiftBy != 0)
            {
                frame = ApplyManualChannelShift(
                    frame,
                    options.ManualAdcShiftRx,
                    options.ManualAdcShiftRy,
                    options.ManualAdcShiftBx,
                    options.ManualAdcShiftBy);
            }
        }

        // 2. Accentuation en direct commutable (Toggle On/Off)
        if (options.EnableLiveSharpening)
        {
            if (options.SharpeningBlendPercent > 0)
            {
                frame = ConvolutionSharpeningFilter.ApplySharpening(frame, options.SharpeningBlendPercent);
            }

            if (options.UnsharpAmount > 0.05)
            {
                frame = ApplyFastUnsharpMask(frame, options.UnsharpRadius, options.UnsharpAmount);
            }
        }

        // 3. Étirement d'affichage (Black Point, White Point, Gamma)
        if (options.DisplayBlackPoint8Bit > 0 || options.DisplayWhitePoint8Bit < 255 || Math.Abs(options.DisplayGamma - 1.0) > 0.01)
        {
            frame = ApplyDisplayTransferFunction(
                frame,
                options.DisplayBlackPoint8Bit,
                options.DisplayWhitePoint8Bit,
                options.DisplayGamma);
        }

        return frame;
    }

    /// <summary>
    /// Construit un instantané immuable de la télémétrie courante.
    /// </summary>
    public LiveStackingTelemetry GetTelemetry(
        double captureFps,
        double stackingFps,
        double sensorTempC,
        bool isRecordingSer,
        int recordedSerFrames,
        int maxBufferCapacity)
    {
        lock (_sync)
        {
            return new LiveStackingTelemetry(
                CaptureFps: captureFps,
                StackingFps: stackingFps,
                TotalFramesReceived: _totalFramesReceived,
                FramesAccepted: _framesAccepted,
                FramesRejectedQuality: _framesRejectedQuality,
                FramesRejectedDriftOrCloud: _framesRejectedDriftOrCloud,
                CurrentBufferCount: _rollingBuffer.Count,
                MaxBufferCapacity: maxBufferCapacity,
                CurrentQualityScore: _currentQualityScore,
                BestQualityScore: _bestQualityScore,
                ThresholdQualityScore: _thresholdQualityScore,
                CurrentShiftX: _currentShiftX,
                CurrentShiftY: _currentShiftY,
                SensorTemperatureC: sensorTempC,
                IsRecordingSer: isRecordingSer,
                RecordedSerFrames: recordedSerFrames,
                ActiveAlignmentMode: _activeAlignmentMode,
                LastRejectReason: _lastRejectReason);
        }
    }

    private void InitializeDimensions(int width, int height, int channels, int bitDepth)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _bitDepth = bitDepth;
        _accumulator = new double[width * height * channels];
        _totalWeight = 0;
        _rollingBuffer.Clear();
        _referenceFrame = null;
        _lastValidCenterX = -1;
        _lastValidCenterY = -1;
    }

    private void PurgeExcessCapacity(int maxCap)
    {
        while (_rollingBuffer.Count > maxCap)
        {
            // Retirer la trame avec le score le plus bas
            int worstIdx = 0;
            double minScore = _rollingBuffer[0].QualityScore;
            for (int i = 1; i < _rollingBuffer.Count; i++)
            {
                if (_rollingBuffer[i].QualityScore < minScore)
                {
                    minScore = _rollingBuffer[i].QualityScore;
                    worstIdx = i;
                }
            }

            var evicted = _rollingBuffer[worstIdx];
            SubtractFromAccumulator(evicted.AlignedFrame, evicted.Weight);
            _rollingBuffer.RemoveAt(worstIdx);
        }
    }

    private unsafe void AddToAccumulator(AstroFrame frame, double weight)
    {
        int len = Math.Min(_accumulator.Length, frame.Data.Length);
        fixed (double* pAcc = _accumulator)
        fixed (ushort* pSrc = frame.Data)
        {
            for (int i = 0; i < len; i++)
            {
                pAcc[i] += pSrc[i] * weight;
            }
        }
        _totalWeight += weight;
    }

    private unsafe void SubtractFromAccumulator(AstroFrame frame, double weight)
    {
        int len = Math.Min(_accumulator.Length, frame.Data.Length);
        fixed (double* pAcc = _accumulator)
        fixed (ushort* pSrc = frame.Data)
        {
            for (int i = 0; i < len; i++)
            {
                double v = pAcc[i] - pSrc[i] * weight;
                pAcc[i] = v > 0 ? v : 0;
            }
        }
        _totalWeight = Math.Max(0, _totalWeight - weight);
    }

    private double ComputeDynamicQualityThreshold(LiveStackingOptions options)
    {
        if (!options.EnableQualityGate || _recentScoresWindow.Count < 4)
        {
            return 0.0;
        }

        double keepRatio = Math.Clamp(options.KeepBestPercent, 5.0, 100.0) / 100.0;
        if (keepRatio >= 0.999)
        {
            return 0.0;
        }

        double[] sorted = [.. _recentScoresWindow];
        Array.Sort(sorted);

        // Indice du fractile (1 - keepRatio)
        int cutoffIndex = (int)Math.Floor((1.0 - keepRatio) * (sorted.Length - 1));
        cutoffIndex = Math.Clamp(cutoffIndex, 0, sorted.Length - 1);
        return sorted[cutoffIndex];
    }

    private static unsafe double ComputeMeanLuminance8Bit(AstroFrame frame)
    {
        if (frame.Data.Length == 0) return 0;
        long sum = 0;
        int len = frame.Data.Length;
        int step = Math.Max(1, len / 4096);
        int count = 0;

        fixed (ushort* pData = frame.Data)
        {
            for (int i = 0; i < len; i += step)
            {
                sum += pData[i] >> 8;
                count++;
            }
        }

        return count > 0 ? (double)sum / count : 0;
    }

    private static unsafe (double Cx, double Cy) ComputeSubPixelCentroid(
        AstroFrame frame,
        int approxX,
        int approxY,
        int threshold8Bit)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        ushort thresh16 = (ushort)(Math.Clamp(threshold8Bit, 4, 240) * 257);

        double sumW = 0;
        double sumX = 0;
        double sumY = 0;

        fixed (ushort* pData = frame.Data)
        {
            for (int y = 0; y < h; y++)
            {
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                {
                    int lum;
                    if (ch == 1)
                    {
                        lum = pData[rowOffset + x];
                    }
                    else
                    {
                        int idx = (rowOffset + x) * 3;
                        lum = (pData[idx] + pData[idx + 1] * 2 + pData[idx + 2]) >> 2;
                    }

                    if (lum > thresh16)
                    {
                        double wt = lum - thresh16;
                        sumW += wt;
                        sumX += x * wt;
                        sumY += y * wt;
                    }
                }
            }
        }

        if (sumW <= 1.0)
        {
            return (approxX, approxY);
        }

        return (sumX / sumW, sumY / sumW);
    }

    private (double ShiftX, double ShiftY) ComputeSurfaceShift(AstroFrame current, AstroFrame reference)
    {
        if (_surfaceCorrelator is not null)
        {
            return _surfaceCorrelator(current, reference);
        }

        // Fallback pur Domaine par corrélation SAD sur boîte d'ancrage centrale (AFB)
        int boxSize = Math.Min(64, Math.Min(current.Width, current.Height) / 2);
        if (boxSize < 16) return (0, 0);

        int refX = (reference.Width - boxSize) / 2;
        int refY = (reference.Height - boxSize) / 2;
        var (found, bestX, bestY) = StabilisationAndQualityEngine.MatchAnchorFeatureBox(
            reference, refX, refY, boxSize, boxSize, current, searchRadius: 24);

        return found ? (refX - bestX, refY - bestY) : (0, 0);
    }

    /// <summary>
    /// Translation sub-pixel par interpolation bilinéaire haute vitesse.
    /// shiftX > 0 déplace l'image vers la droite ; shiftY > 0 déplace l'image vers le bas.
    /// </summary>
    public static unsafe AstroFrame ShiftFrameBilinear(AstroFrame src, double shiftX, double shiftY)
    {
        int w = src.Width;
        int h = src.Height;
        int ch = src.Channels;

        var dst = new AstroFrame(w, h, ch, src.OriginalBitDepth)
        {
            FrameIndex = src.FrameIndex,
            TimestampUtc = src.TimestampUtc,
            QualityScore = src.QualityScore
        };

        fixed (ushort* pSrc = src.Data)
        fixed (ushort* pDst = dst.Data)
        {
            for (int y = 0; y < h; y++)
            {
                double srcY = y - shiftY;
                int y0 = (int)Math.Floor(srcY);
                int y1 = y0 + 1;
                if (y0 < 0 || y1 >= h) continue;

                double fy = srcY - y0;
                int wy1 = (int)(fy * 256.0 + 0.5);
                int wy0 = 256 - wy1;

                int dstRow = y * w * ch;
                int srcRow0 = y0 * w * ch;
                int srcRow1 = y1 * w * ch;

                for (int x = 0; x < w; x++)
                {
                    double srcX = x - shiftX;
                    int x0 = (int)Math.Floor(srcX);
                    int x1 = x0 + 1;
                    if (x0 < 0 || x1 >= w) continue;

                    double fx = srcX - x0;
                    int wx1 = (int)(fx * 256.0 + 0.5);
                    int wx0 = 256 - wx1;

                    int w00 = wx0 * wy0;
                    int w10 = wx1 * wy0;
                    int w01 = wx0 * wy1;
                    int w11 = wx1 * wy1;

                    int i00 = srcRow0 + x0 * ch;
                    int i10 = srcRow0 + x1 * ch;
                    int i01 = srcRow1 + x0 * ch;
                    int i11 = srcRow1 + x1 * ch;
                    int dIdx = dstRow + x * ch;

                    if (ch == 1)
                    {
                        long sum = (long)pSrc[i00] * w00 +
                                   (long)pSrc[i10] * w10 +
                                   (long)pSrc[i01] * w01 +
                                   (long)pSrc[i11] * w11 + 32768L;
                        long v = sum >> 16;
                        pDst[dIdx] = (ushort)(v < 0 ? 0 : (v > 65535 ? 65535 : (ushort)v));
                    }
                    else
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            long sum = (long)pSrc[i00 + c] * w00 +
                                       (long)pSrc[i10 + c] * w10 +
                                       (long)pSrc[i01 + c] * w01 +
                                       (long)pSrc[i11 + c] * w11 + 32768L;
                            long v = sum >> 16;
                            pDst[dIdx + c] = (ushort)(v < 0 ? 0 : (v > 65535 ? 65535 : (ushort)v));
                        }
                    }
                }
            }
        }

        return dst;
    }

    private static unsafe void SubtractSkyBackgroundPedestal(AstroFrame frame)
    {
        int sky8 = StabilisationAndQualityEngine.EstimateSkyBackgroundLevel8Bit(frame);
        if (sky8 <= 2) return;

        ushort pedestal16 = (ushort)Math.Min(16384, (sky8 - 1) * 257);
        int len = frame.Data.Length;

        fixed (ushort* pData = frame.Data)
        {
            for (int i = 0; i < len; i++)
            {
                int v = pData[i] - pedestal16;
                pData[i] = (ushort)(v > 0 ? v : 0);
            }
        }
    }

    private static AstroFrame ApplyManualChannelShift(AstroFrame frame, int rx, int ry, int bx, int by)
    {
        var result = frame.Clone();
        int w = frame.Width;
        int h = frame.Height;
        ushort[] src = frame.Data;
        ushort[] dst = result.Data;

        ShiftSingleChannel(src, dst, w, h, 0, rx, ry);
        ShiftSingleChannel(src, dst, w, h, 2, bx, by);
        return result;
    }

    private static void ShiftSingleChannel(ushort[] src, ushort[] dst, int w, int h, int chIdx, int dx, int dy)
    {
        for (int y = 0; y < h; y++)
        {
            int sy = y - dy;
            int dstRow = y * w * 3;
            if (sy < 0 || sy >= h)
            {
                for (int x = 0; x < w; x++) dst[dstRow + x * 3 + chIdx] = 0;
                continue;
            }

            int srcRow = sy * w * 3;
            for (int x = 0; x < w; x++)
            {
                int sx = x - dx;
                dst[dstRow + x * 3 + chIdx] = (sx >= 0 && sx < w) ? src[srcRow + sx * 3 + chIdx] : (ushort)0;
            }
        }
    }

    /// <summary>
    /// Masque flou séparable (Unsharp Mask) haute vitesse pour le réhaussement fin en direct.
    /// </summary>
    public static unsafe AstroFrame ApplyFastUnsharpMask(AstroFrame frame, double radius, double amount)
    {
        int w = frame.Width;
        int h = frame.Height;
        int ch = frame.Channels;
        if (w < 8 || h < 8 || amount <= 0.01) return frame;

        int r = Math.Clamp((int)Math.Round(radius), 1, 4);
        var dst = frame.Clone();
        var temp = new ushort[frame.Data.Length];

        fixed (ushort* pSrc = frame.Data)
        fixed (ushort* pTmp = temp)
        fixed (ushort* pDst = dst.Data)
        {
            int diam = 2 * r + 1;

            // Passe horizontale (Box blur)
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * w * ch;
                for (int x = r; x < w - r; x++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        int sum = 0;
                        for (int kx = -r; kx <= r; kx++)
                        {
                            sum += pSrc[rowOff + (x + kx) * ch + c];
                        }
                        pTmp[rowOff + x * ch + c] = (ushort)(sum / diam);
                    }
                }
            }

            // Passe verticale + soustraction haute fréquence
            int amtFixed = (int)(amount * 256.0);
            for (int y = r; y < h - r; y++)
            {
                int rowOff = y * w * ch;
                for (int x = r; x < w - r; x++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        int sum = 0;
                        for (int ky = -r; ky <= r; ky++)
                        {
                            sum += pTmp[(y + ky) * w * ch + x * ch + c];
                        }
                        int blurred = sum / diam;
                        int orig = pSrc[rowOff + x * ch + c];
                        int detail = orig - blurred;
                        int sharpened = orig + ((detail * amtFixed) >> 8);
                        pDst[rowOff + x * ch + c] = (ushort)(sharpened < 0 ? 0 : (sharpened > 65535 ? 65535 : sharpened));
                    }
                }
            }
        }

        return dst;
    }

    public static unsafe AstroFrame ApplyDisplayTransferFunction(
        AstroFrame frame,
        int blackPoint8Bit,
        int whitePoint8Bit,
        double gamma)
    {
        var dst = frame.Clone();
        int bp16 = Math.Clamp(blackPoint8Bit, 0, 250) * 257;
        int wp16 = Math.Clamp(whitePoint8Bit, Math.Min(255, blackPoint8Bit + 5), 255) * 257;
        double range = Math.Max(1.0, wp16 - bp16);
        double invGamma = 1.0 / Math.Clamp(gamma, 0.2, 4.0);

        Span<ushort> lut = stackalloc ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int v16 = i * 257;
            if (v16 <= bp16) lut[i] = 0;
            else if (v16 >= wp16) lut[i] = 65535;
            else
            {
                double norm = (v16 - bp16) / range;
                if (Math.Abs(gamma - 1.0) > 0.01)
                {
                    norm = Math.Pow(norm, invGamma);
                }
                lut[i] = (ushort)Math.Clamp((int)(norm * 65535.0 + 0.5), 0, 65535);
            }
        }

        int len = dst.Data.Length;
        fixed (ushort* pData = dst.Data)
        {
            for (int i = 0; i < len; i++)
            {
                pData[i] = lut[pData[i] >> 8];
            }
        }

        return dst;
    }
}
