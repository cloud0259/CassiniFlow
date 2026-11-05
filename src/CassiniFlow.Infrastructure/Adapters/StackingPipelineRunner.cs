using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Infrastructure.Adapters;

public static class StackingPipelineRunner
{
    public static Task<PipelineRunSummary> ExecuteAsync(
        IReadOnlyList<string> inputFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var loc = new AstroLocalizationService();
        var calib = new CalibrationFrameProviderService(loc);
        var runner = new MultiZoneStackingPipelineRunner(loc, calib);
        return runner.ExecuteAsync(inputFiles, options, outputDirectory, progress, cancellationToken);
    }
}
