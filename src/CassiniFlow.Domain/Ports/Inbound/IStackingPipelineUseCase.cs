using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Ports.Inbound;

public interface IStackingPipelineUseCase
{
    Task<PipelineRunSummary> ExecuteAsync(
        IReadOnlyList<string> inputFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}

