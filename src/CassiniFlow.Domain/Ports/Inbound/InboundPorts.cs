using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Ports.Inbound;

public sealed record PipelineProgressReport(
    string Stage,
    int PercentComplete,
    string CurrentFile,
    int ProcessedFrames,
    int TotalFrames,
    string LogMessage);

public sealed record StackedTierSummary(
    double Percentage,
    int FramesStacked,
    int TotalValidFrames,
    int ActiveApCount,
    int TileSize,
    string OutputFilePath);

public sealed record PipelineRunSummary(
    bool Success,
    TimeSpan Elapsed,
    int TotalInputFrames,
    int OutputFramesWritten,
    int DiscardedNoPlanet,
    int DiscardedOverexposed,
    int DiscardedQuality,
    string OutputFilePath,
    string LogFilePath = "",
    IReadOnlyList<StackedTierSummary>? StackedTiers = null);

public sealed record PreviewFrameResult(
    AstroFrame ProcessedFrame,
    DetectionResult Detection,
    int OriginalWidth,
    int OriginalHeight);

public interface IRunPipelineUseCase
{
    Task<PipelineRunSummary> ExecuteFilesAsync(
        IReadOnlyList<string> inputFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPreviewFrameUseCase
{
    PreviewFrameResult GeneratePreview(
        AstroFrame rawInput,
        ProcessingOptions options,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        bool applyCropToOutput = false,
        double previewScale = 1.0);
}

public interface IMeasurePlanetUseCase
{
    Task<double?> MeasureDiameterPixelsAsync(string filePath, int frameCount, int samples = 7);
}

public interface IMeasureOrientationUseCase
{
    Task<(double AngleDegrees, double Elongation)?> MeasureOrientationAsync(string filePath, int frameCount, int samples = 9);
}

public interface IGenerateAlignmentGridUseCase
{
    AlignmentGrid GenerateGrid(AstroFrame frame, StackingOptions options);
}

public interface IQualityTimelineUseCase
{
    Task<QualityTimelineDataSet> AnalyzeTimelineAsync(
        string filePath,
        QualityAlgorithm algorithm,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<QualityAlgorithm>? companionAlgorithms = null);

    Task ComputeAdditionalAlgorithmAsync(
        QualityTimelineDataSet dataset,
        QualityAlgorithm algorithm,
        ProcessingOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
