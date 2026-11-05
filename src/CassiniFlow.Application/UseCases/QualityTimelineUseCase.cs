using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Ports.Outbound;

namespace CassiniFlow.Application.UseCases;

public sealed class QualityTimelineUseCase : IQualityTimelineUseCase
{
    private readonly IQualityTimelineScanner _scanner;
    private readonly ConcurrentDictionary<string, QualityTimelineDataSet> _cache = new(StringComparer.OrdinalIgnoreCase);

    public QualityTimelineUseCase(IQualityTimelineScanner scanner)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public async Task<QualityTimelineDataSet> AnalyzeTimelineAsync(
        string filePath,
        QualityAlgorithm algorithm,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<QualityAlgorithm>? companionAlgorithms = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);

        string cacheKey = BuildCacheKey(filePath);
        _cache.TryGetValue(cacheKey, out var cachedSet);

        // Algorithmes manquants (principal + compagnons) à calculer en une seule lecture du fichier
        var missing = new List<QualityAlgorithm>();
        if (cachedSet == null || !cachedSet.HasScoresFor(algorithm)) missing.Add(algorithm);
        if (companionAlgorithms != null)
        {
            foreach (var algo in companionAlgorithms)
            {
                if (!missing.Contains(algo) && (cachedSet == null || !cachedSet.HasScoresFor(algo))) missing.Add(algo);
            }
        }

        if (missing.Count == 0 && cachedSet != null)
        {
            cachedSet.PrimaryAlgorithm = algorithm;
            cachedSet.SetAlgorithmScores(algorithm, cachedSet.GetScores(algorithm)!, updateTurbulence: true);
            progress?.Report(100.0);
            return cachedSet;
        }

        var scans = await _scanner.ScanFileMultiScoresAsync(filePath, missing, options, progress, cancellationToken).ConfigureAwait(false);

        int totalFrames = scans.TryGetValue(algorithm, out var primaryRaw) ? primaryRaw.Count : cachedSet?.TotalFrames ?? 0;
        var dataSet = cachedSet ?? new QualityTimelineDataSet(filePath, totalFrames, algorithm);
        dataSet.PrimaryAlgorithm = algorithm;

        foreach (var (algo, raw) in scans)
        {
            if (algo == algorithm) continue;
            dataSet.SetAlgorithmScores(algo, QualityTimelineAnalyzer.NormalizeAndRank(raw), updateTurbulence: false);
        }

        // L'algorithme principal en dernier : il détermine meilleure trame et zones de turbulence
        var primaryRecords = primaryRaw != null
            ? QualityTimelineAnalyzer.NormalizeAndRank(primaryRaw)
            : dataSet.GetScores(algorithm) ?? Array.Empty<FrameQualityRecord>();
        dataSet.SetAlgorithmScores(algorithm, primaryRecords, updateTurbulence: true);

        _cache[cacheKey] = dataSet;
        progress?.Report(100.0);
        return dataSet;
    }

    public async Task ComputeAdditionalAlgorithmAsync(
        QualityTimelineDataSet dataset,
        QualityAlgorithm algorithm,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(options);

        if (dataset.HasScoresFor(algorithm))
        {
            progress?.Report(100.0);
            return;
        }

        var rawScores = await _scanner.ScanFileScoresAsync(dataset.FilePath, algorithm, options, progress, cancellationToken).ConfigureAwait(false);
        var rankedRecords = QualityTimelineAnalyzer.NormalizeAndRank(rawScores);

        dataset.SetAlgorithmScores(algorithm, rankedRecords, updateTurbulence: false);
        progress?.Report(100.0);
    }

    private static string BuildCacheKey(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return $"{filePath}_{info.Length}_{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return filePath;
        }
    }
}

