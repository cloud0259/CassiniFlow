using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.Pipeline;

public sealed class CenteringAndDetectionStep : IFramePipelineStep
{
    public AstroFrame Execute(AstroFrame frame, FramePipelineContext context)
    {
        var opt = context.Options;
        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            frame,
            checkObject: opt.CheckPlanetObjectPresent,
            autoThreshold: opt.AutoDetectionThreshold,
            manualThreshold8Bit: opt.ManualDetectionThreshold,
            minObjectSizePixels: opt.MinimumObjectSizePixels,
            shadowEdge: opt.EdgeInShadow,
            checkOverexposed: opt.RejectOverexposedFrames,
            maxOverexposedPixels: opt.MaxOverexposedPixels);

        context.LastDetection = det;
        frame.DetectedCentroidX = det.CentroidX;
        frame.DetectedCentroidY = det.CentroidY;
        return frame;
    }
}
