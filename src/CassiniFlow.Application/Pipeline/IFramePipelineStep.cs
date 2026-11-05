using CassiniFlow.Domain.Models;

namespace CassiniFlow.Application.Pipeline;

public interface IFramePipelineStep
{
    AstroFrame Execute(AstroFrame frame, FramePipelineContext context);
}
