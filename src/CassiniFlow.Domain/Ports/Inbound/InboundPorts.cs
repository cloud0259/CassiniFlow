using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Ports.Inbound;

public sealed record PipelineProgressReport(
    string Stage,
    int PercentComplete,
    string CurrentFile,
    int ProcessedFrames,
    int TotalFrames,
    string LogMessage);

public sealed record PipelineRunSummary(
    bool Success,
    TimeSpan Elapsed,
    int TotalInputFrames,
    int OutputFramesWritten,
    int DiscardedNoPlanet,
    int DiscardedOverexposed,
    int DiscardedQuality,
    string OutputFilePath);

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
