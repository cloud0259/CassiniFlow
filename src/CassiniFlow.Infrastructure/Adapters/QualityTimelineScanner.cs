using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Cannot scan quality for non-existent file: {filePath}", filePath);
        }

        var opt = options.Clone();
        opt.QualityAlgorithm = algorithm;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".ser" => await ScanSerFileAsync(filePath, opt, progress, cancellationToken).ConfigureAwait(false),
            _ => await ScanVideoOrImageFileAsync(filePath, opt, progress, cancellationToken).ConfigureAwait(false)
        };
    }

    private static Task<IReadOnlyList<(int FrameIndex, double RawScore)>> ScanSerFileAsync(
        string filePath,
        ProcessingOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew<IReadOnlyList<(int FrameIndex, double RawScore)>>(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            using var ser = new SerFile(filePath);
            int totalFrames = (int)ser.Header.FrameCount;
            if (totalFrames <= 0) return Array.Empty<(int, double)>();

            var results = new (int FrameIndex, double RawScore)[totalFrames];
            int processed = 0;
            int reportStep = Math.Max(10, totalFrames / 100);

            // Modéré (max 4 threads) pour préserver les accès disques et le thread UI
            int maxDegree = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

            Parallel.For(
                0,
                totalFrames,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegree,
                    CancellationToken = cancellationToken
                },
                i =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var analysis = ser.AnalyzeFrameZeroAlloc(i, options, options.SerBitDepthOverride);
                    results[i] = (i, analysis.QualityScore);

                    int done = Interlocked.Increment(ref processed);
                    if (done % reportStep == 0 || done == totalFrames)
                    {
                        double pct = Math.Clamp((double)done / totalFrames * 100.0, 0.0, 100.0);
                        progress?.Report(pct);
                    }
                });

            return results;
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static Task<IReadOnlyList<(int FrameIndex, double RawScore)>> ScanVideoOrImageFileAsync(
        string filePath,
        ProcessingOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew<IReadOnlyList<(int FrameIndex, double RawScore)>>(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            using var cap = UniversalMediaReader.OpenVideoCapture(filePath);
            if (!cap.IsOpened())
            {
                // Single image fallback (e.g. FITS, TIFF, PNG)
                using var mat = Cv2.ImRead(filePath, ImreadModes.Unchanged);
                if (mat.Empty()) return Array.Empty<(int, double)>();

                int hx = -1, hy = -1, thresh = -1;
                var (_, _, _, score) = UniversalMediaReader.AnalyzeMatZeroAlloc(mat, options, ref hx, ref hy, ref thresh);
                progress?.Report(100.0);
                return new[] { (0, score) };
            }

            int frameCount = cap.FrameCount;
            if (frameCount <= 0) frameCount = 1;

            var results = new (int FrameIndex, double RawScore)[frameCount];
            using var rawMat = new Mat();
            int hintX = -1, hintY = -1, cachedThresh = -1;

            // Échantillonnage ultra-rapide (< 0.3s) pour vidéos compressées (MP4/AVI/MKV) :
            // On limite le décodage complet d'images à un maximum de 60 trames réparties avec Grab()
            // et interpolation linéaire. Le tracé de la courbe et les zones de seeing sont instantanés !
            const int maxDecodedSamples = 60;
            int step = frameCount > maxDecodedSamples ? (int)Math.Ceiling((double)frameCount / maxDecodedSamples) : 1;

            int reportStep = Math.Max(2, frameCount / 50);
            int lastSampledIdx = 0;
            double lastSampledScore = 0.0;

            for (int idx = 0; idx < frameCount; idx++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool shouldDecode = (idx % step == 0) || (idx == frameCount - 1);

                if (shouldDecode)
                {
                    if (!cap.Read(rawMat) || rawMat.Empty()) break;

                    if (idx % 60 == 0) cachedThresh = -1;

                    var (_, _, _, score) = UniversalMediaReader.AnalyzeMatZeroAlloc(
                        rawMat,
                        options,
                        ref hintX,
                        ref hintY,
                        ref cachedThresh);

                    results[idx] = (idx, score);

                    // Interpolation linéaire des trames sautées par Grab()
                    if (step > 1 && idx > lastSampledIdx)
                    {
                        int span = idx - lastSampledIdx;
                        double scoreStart = lastSampledScore;
                        double scoreEnd = score;
                        for (int k = lastSampledIdx + 1; k < idx; k++)
                        {
                            double t = (double)(k - lastSampledIdx) / span;
                            results[k] = (k, scoreStart + t * (scoreEnd - scoreStart));
                        }
                    }

                    lastSampledIdx = idx;
                    lastSampledScore = score;
                }
                else
                {
                    // Grab ultra-rapide : saute le décodage complet de l'image
                    if (!cap.Grab()) break;
                }

                if (idx % reportStep == 0 || idx == frameCount - 1)
                {
                    double pct = Math.Clamp((double)(idx + 1) / frameCount * 100.0, 0.0, 100.0);
                    progress?.Report(pct);
                }
            }

            // Si la fin de vidéo est atteinte prématurément, combler avec le dernier score valide
            for (int k = lastSampledIdx + 1; k < frameCount; k++)
            {
                results[k] = (k, lastSampledScore);
            }

            return results;
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
