using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Outbound;
using CassiniFlow.Infrastructure.Codecs;

namespace CassiniFlow.Infrastructure.Adapters;

public sealed class QualityTimelineScanner : IQualityTimelineScanner
{
    public async Task<IReadOnlyList<(int FrameIndex, double RawScore)>> ScanFileScoresAsync(
        string filePath,
        QualityAlgorithm algorithm,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ScanFileMultiScoresAsync(filePath, new[] { algorithm }, options, progress, cancellationToken).ConfigureAwait(false);
        return all.TryGetValue(algorithm, out var scores) ? scores : Array.Empty<(int, double)>();
    }

    public Task<IReadOnlyDictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>> ScanFileMultiScoresAsync(
        string filePath,
        IReadOnlyList<QualityAlgorithm> algorithms,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(algorithms);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Cannot scan quality for non-existent file: {filePath}", filePath);
        }

        var algos = algorithms.Distinct().ToArray();
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext == ".ser"
            ? ScanSerFileAsync(filePath, algos, options.Clone(), progress, cancellationToken)
            : ScanVideoOrImageFileAsync(filePath, algos, options.Clone(), progress, cancellationToken);
    }

    /// <summary>
    /// Scan SER rapide : une seule lecture (partielle) de chaque trame via
    /// <see cref="SerFile.ComputeTimelineMetricsZeroAlloc"/> qui calcule tous les algorithmes à la fois.
    /// </summary>
    private static Task<IReadOnlyDictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>> ScanSerFileAsync(
        string filePath,
        QualityAlgorithm[] algorithms,
        ProcessingOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew<IReadOnlyDictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>>(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            using var ser = new SerFile(filePath);
            int totalFrames = ser.Header.FrameCount;
            var metrics = new SerFile.TimelineFrameMetrics[Math.Max(0, totalFrames)];

            if (totalFrames > 0)
            {
                // Échantillonnage adaptatif pour les fichiers volumineux :
                // La largeur du contrôle timeline n'excède pas 1000 px ; analyser max 250 trames
                // réparties régulièrement avec interpolation linéaire produit une courbe visuellement
                // identique et divise le temps de scan disque par 2 à 10 pour les gros fichiers SER.
                const int maxTimelineSamples = 250;
                int step = totalFrames > maxTimelineSamples
                    ? (int)Math.Ceiling((double)totalFrames / maxTimelineSamples)
                    : 1;

                int sampleCount = (totalFrames + step - 1) / step;
                if ((totalFrames - 1) % step != 0) sampleCount++; // Dernière trame garantie

                var sampledIndices = new int[sampleCount];
                int sIdx = 0;
                for (int i = 0; i < totalFrames; i += step)
                {
                    sampledIndices[sIdx++] = i;
                }
                if (sampledIndices[sIdx - 1] != totalFrames - 1)
                {
                    sampledIndices[sIdx++] = totalFrames - 1;
                }

                int processed = 0;
                int reportStep = Math.Max(2, sIdx / 50);
                int maxDegree = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);

                Parallel.For(
                    0,
                    sIdx,
                    new ParallelOptions { MaxDegreeOfParallelism = maxDegree, CancellationToken = cancellationToken },
                    k =>
                    {
                        int frameIdx = sampledIndices[k];
                        metrics[frameIdx] = ser.ComputeTimelineMetricsZeroAlloc(frameIdx, options, options.SerBitDepthOverride);

                        int done = Interlocked.Increment(ref processed);
                        if (done % reportStep == 0 || done == sIdx)
                        {
                            progress?.Report(Math.Clamp((double)done / sIdx * 100.0, 0.0, 100.0));
                        }
                    });

                // Interpolation linéaire ultra-rapide des trames intermédiaires
                if (step > 1)
                {
                    for (int k = 0; k < sIdx - 1; k++)
                    {
                        int i0 = sampledIndices[k];
                        int i1 = sampledIndices[k + 1];
                        int span = i1 - i0;
                        if (span <= 1) continue;

                        var m0 = metrics[i0];
                        var m1 = metrics[i1];

                        for (int f = i0 + 1; f < i1; f++)
                        {
                            double t = (double)(f - i0) / span;
                            metrics[f] = new SerFile.TimelineFrameMetrics(
                                Gradient: m0.Gradient + t * (m1.Gradient - m0.Gradient),
                                PeakHistogram: m0.PeakHistogram + t * (m1.PeakHistogram - m0.PeakHistogram),
                                TotalBrightness: m0.TotalBrightness + t * (m1.TotalBrightness - m0.TotalBrightness));
                        }
                    }
                }
            }

            var result = new Dictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>(algorithms.Length);
            foreach (var algo in algorithms)
            {
                var scores = new (int FrameIndex, double RawScore)[metrics.Length];
                for (int i = 0; i < metrics.Length; i++)
                {
                    var m = metrics[i];
                    scores[i] = (i, algo switch
                    {
                        QualityAlgorithm.PeakHistogram => m.PeakHistogram,
                        QualityAlgorithm.TotalBrightness => m.TotalBrightness,
                        _ => m.Gradient // GradientSharpness & OriginalNinox partagent la même mesure
                    });
                }
                result[algo] = scores;
            }
            return result;
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static Task<IReadOnlyDictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>> ScanVideoOrImageFileAsync(
        string filePath,
        QualityAlgorithm[] algorithms,
        ProcessingOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew<IReadOnlyDictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>>(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            int algoCount = algorithms.Length;
            var perAlgoOptions = new ProcessingOptions[algoCount];
            for (int a = 0; a < algoCount; a++)
            {
                perAlgoOptions[a] = options.Clone();
                perAlgoOptions[a].QualityAlgorithm = algorithms[a];
            }

            using var cap = UniversalMediaReader.OpenVideoCapture(filePath);
            if (!cap.IsOpened())
            {
                // Single image fallback (e.g. FITS, TIFF, PNG)
                using var mat = Cv2.ImRead(filePath, ImreadModes.Unchanged);
                var single = new Dictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>(algoCount);
                for (int a = 0; a < algoCount; a++)
                {
                    if (mat.Empty())
                    {
                        single[algorithms[a]] = Array.Empty<(int, double)>();
                        continue;
                    }
                    int hx = -1, hy = -1, thresh = -1;
                    var (_, _, _, score) = UniversalMediaReader.AnalyzeMatZeroAlloc(mat, perAlgoOptions[a], ref hx, ref hy, ref thresh);
                    single[algorithms[a]] = new[] { (0, score) };
                }
                progress?.Report(100.0);
                return single;
            }

            int frameCount = cap.FrameCount;
            if (frameCount <= 0) frameCount = 1;

            var results = new (int FrameIndex, double RawScore)[algoCount][];
            for (int a = 0; a < algoCount; a++) results[a] = new (int, double)[frameCount];

            using var rawMat = new Mat();
            var hintX = new int[algoCount];
            var hintY = new int[algoCount];
            var cachedThresh = new int[algoCount];
            var lastScore = new double[algoCount];
            Array.Fill(hintX, -1);
            Array.Fill(hintY, -1);
            Array.Fill(cachedThresh, -1);

            // Échantillonnage ultra-rapide pour vidéos compressées (MP4/AVI/MKV) :
            // au maximum 60 trames décodées réparties avec Grab() + interpolation linéaire.
            // Chaque trame décodée est évaluée pour tous les algorithmes demandés (le décodage domine).
            const int maxDecodedSamples = 60;
            int step = frameCount > maxDecodedSamples ? (int)Math.Ceiling((double)frameCount / maxDecodedSamples) : 1;

            int reportStep = Math.Max(2, frameCount / 50);
            int lastSampledIdx = 0;

            for (int idx = 0; idx < frameCount; idx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool shouldDecode = (idx % step == 0) || (idx == frameCount - 1);

                if (shouldDecode)
                {
                    if (!cap.Read(rawMat) || rawMat.Empty()) break;

                    for (int a = 0; a < algoCount; a++)
                    {
                        if (idx % 60 == 0) cachedThresh[a] = -1;

                        var (_, _, _, score) = UniversalMediaReader.AnalyzeMatZeroAlloc(
                            rawMat,
                            perAlgoOptions[a],
                            ref hintX[a],
                            ref hintY[a],
                            ref cachedThresh[a]);

                        var res = results[a];
                        res[idx] = (idx, score);

                        // Interpolation linéaire des trames sautées par Grab()
                        if (step > 1 && idx > lastSampledIdx)
                        {
                            int span = idx - lastSampledIdx;
                            double scoreStart = lastScore[a];
                            for (int k = lastSampledIdx + 1; k < idx; k++)
                            {
                                double t = (double)(k - lastSampledIdx) / span;
                                res[k] = (k, scoreStart + t * (score - scoreStart));
                            }
                        }
                        lastScore[a] = score;
                    }

                    lastSampledIdx = idx;
                }
                else
                {
                    // Grab ultra-rapide : saute le décodage complet de l'image
                    if (!cap.Grab()) break;
                }

                if (idx % reportStep == 0 || idx == frameCount - 1)
                {
                    progress?.Report(Math.Clamp((double)(idx + 1) / frameCount * 100.0, 0.0, 100.0));
                }
            }

            var dict = new Dictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>(algoCount);
            for (int a = 0; a < algoCount; a++)
            {
                // Si la fin de vidéo est atteinte prématurément, combler avec le dernier score valide
                for (int k = lastSampledIdx + 1; k < frameCount; k++)
                {
                    results[a][k] = (k, lastScore[a]);
                }
                dict[algorithms[a]] = results[a];
            }
            return dict;
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
