using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Models.ValueObjects;

namespace CassiniFlow.Domain.Ports.Outbound;

public record MediaInfo(
    string FilePath,
    string FormatDescription,
    int FrameCount,
    int Width,
    int Height,
    int BitDepth,
    double Fps,
    bool IsBayerRaw,
    BayerPattern BayerPattern);

public interface IMediaFrameReader : IDisposable
{
    MediaInfo Info { get; }
    AstroFrame ReadFrame(int frameIndex);
}

public interface IMediaFrameWriter : IDisposable
{
    void WriteFrame(AstroFrame frame);
    void Complete();
}

public interface IMediaReaderFactory
{
    IMediaFrameReader CreateReader(string filePath);
    MediaInfo Inspect(string filePath);
    (AstroFrame Frame, int OriginalWidth, int OriginalHeight) ReadPreviewFrame(string filePath, int frameIndex, int maxDimension);
}

public interface IMediaWriterFactory
{
    IMediaFrameWriter CreateWriter(
        string destinationPath,
        ProcessingOptions options,
        int expectedFrameCount,
        int width,
        int height,
        double fps,
        DateTime? referenceTimestampUtc = null);
}

public interface IProfileSerializer
{
    string SerializeToPof(ProcessingOptions options);
    ProcessingOptions ParseFromPof(string pofText);
    List<string> GenerateCommandLineArguments(ProcessingOptions options);
}

public interface IQualityTimelineScanner
{
    Task<IReadOnlyList<(int FrameIndex, double RawScore)>> ScanFileScoresAsync(
        string filePath,
        QualityAlgorithm algorithm,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Calcule plusieurs algorithmes de qualité en une seule lecture du fichier.
    /// L'implémentation par défaut enchaîne les scans individuels (adaptateurs simples / tests).
    /// </summary>
    async Task<IReadOnlyDictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>> ScanFileMultiScoresAsync(
        string filePath,
        IReadOnlyList<QualityAlgorithm> algorithms,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<QualityAlgorithm, IReadOnlyList<(int FrameIndex, double RawScore)>>();
        foreach (var algo in algorithms.Distinct())
        {
            result[algo] = await ScanFileScoresAsync(filePath, algo, options, progress, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }
}
