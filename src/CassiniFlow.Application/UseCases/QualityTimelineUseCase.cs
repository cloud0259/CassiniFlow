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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(options);

        string cacheKey = BuildCacheKey(filePath);
        if (_cache.TryGetValue(cacheKey, out var cachedSet))
        {
            if (cachedSet.HasScoresFor(algorithm))
            {
                progress?.Report(100.0);
                return cachedSet;
            }
        }

        var rawScores = await _scanner.ScanFileScoresAsync(filePath, algorithm, options, progress, cancellationToken).ConfigureAwait(false);
        int totalFrames = rawScores.Count;

        var rankedRecords = QualityTimelineAnalyzer.NormalizeAndRank(rawScores);

        var dataSet = cachedSet ?? new QualityTimelineDataSet(filePath, totalFrames, algorithm);
        dataSet.SetAlgorithmScores(algorithm, rankedRecords, updateTurbulence: true);

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

