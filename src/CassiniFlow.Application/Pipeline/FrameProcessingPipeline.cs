using System.Collections.Generic;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Application.Pipeline;

public sealed class FrameProcessingPipeline
{
    private readonly List<IFramePipelineStep> _steps = [];

    public FrameProcessingPipeline AddStep(IFramePipelineStep step)
    {
        _steps.Add(step);
        return this;
    }

    public AstroFrame Process(AstroFrame input, FramePipelineContext context)
    {
        AstroFrame current = input;
        foreach (var step in _steps)
        {
            current = step.Execute(current, context);
        }
        return current;
    }

    public static FrameProcessingPipeline CreateStandard()
    {
        return new FrameProcessingPipeline()
            .AddStep(new CalibrationStep())
            .AddStep(new DebayerStep())
            .AddStep(new CenteringAndDetectionStep())
            .AddStep(new CroppingStep())
            .AddStep(new EnhancementsStep());
    }
}
