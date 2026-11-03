using System.Collections.Generic;
using System.Threading;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Ports.Inbound;

public interface ICalibrationFrameProvider
{
    AstroFrame? BuildMasterDark(
        IReadOnlyList<string> darkFilePaths,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    AstroFrame? BuildMasterFlat(
        IReadOnlyList<string> flatFilePaths,
        AstroFrame? masterFlatDark = null,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default);

    (AstroFrame? MasterDark, AstroFrame? MasterFlat) PrepareCalibrationMasters(
        ProcessingOptions options,
        string? outputDirectory = null,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default);
}

