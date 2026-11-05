using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.Pipeline;

public sealed class CroppingStep : IFramePipelineStep
{
    public AstroFrame Execute(AstroFrame frame, FramePipelineContext context)
    {
        var opt = context.Options;
        bool centre = opt.CentreObjectInFrame && opt.StabilisationMode == StabilisationMode.ObjectPlanetary;
        if (!opt.EnableCropping && !centre) return frame;

        int cx = centre && context.LastDetection is { Accepted: true }
            ? context.LastDetection.CentroidX
            : frame.Width / 2;
        int cy = centre && context.LastDetection is { Accepted: true }
            ? context.LastDetection.CentroidY
            : frame.Height / 2;

        return ImageProcessor.CropAroundPoint(
            frame,
            cx,
            cy,
            opt.EnableCropping ? opt.CropWidth : frame.Width,
            opt.EnableCropping ? opt.CropHeight : frame.Height,
            opt.EnableCropping ? opt.CropOffsetX : 0,
            opt.EnableCropping ? opt.CropOffsetY : 0,
            opt.ProtectBayerPattern);
    }
}

