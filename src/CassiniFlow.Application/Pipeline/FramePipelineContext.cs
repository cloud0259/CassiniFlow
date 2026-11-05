using CassiniFlow.Domain.Models;

namespace CassiniFlow.Application.Pipeline;

public sealed class FramePipelineContext
{
    public ProcessingOptions Options { get; }
    public AstroFrame? MasterDark { get; set; }
    public AstroFrame? MasterFlat { get; set; }
    public DetectionResult? LastDetection { get; set; }

    public FramePipelineContext(ProcessingOptions options, AstroFrame? masterDark = null, AstroFrame? masterFlat = null)
    {
        Options = options;
        MasterDark = masterDark;
        MasterFlat = masterFlat;
    }
}

