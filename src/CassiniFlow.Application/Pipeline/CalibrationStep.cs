using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.Pipeline;

public sealed class CalibrationStep : IFramePipelineStep
{
    public AstroFrame Execute(AstroFrame frame, FramePipelineContext context)
    {
        var opt = context.Options;
        ImageProcessor.ApplyCalibration(
            frame,
            context.MasterDark,
            opt.ReplaceHotPixels,
            opt.HotPixelThreshold,
            opt.DarkDataHasBayer,
            context.MasterFlat);
        return frame;
    }
}
