using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

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

public interface IFramePipelineStep
{
    AstroFrame Execute(AstroFrame frame, FramePipelineContext context);
}

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

public sealed class DebayerStep : IFramePipelineStep
{
    public AstroFrame Execute(AstroFrame frame, FramePipelineContext context)
    {
        var opt = context.Options;
        if (opt.DebayerEnabled && frame.Channels == 1 && frame.IsBayerRaw)
        {
            return ImageProcessor.Debayer(frame, opt.DebayerPattern, opt.DebayerType);
        }
        return frame;
    }
}

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

public sealed class CroppingStep : IFramePipelineStep
{
    public AstroFrame Execute(AstroFrame frame, FramePipelineContext context)
    {
        var opt = context.Options;
        if (!opt.EnableCropping) return frame;

        int cx = opt.CentreObjectInFrame && context.LastDetection is { Accepted: true }
            ? context.LastDetection.CentroidX
            : frame.Width / 2;
        int cy = opt.CentreObjectInFrame && context.LastDetection is { Accepted: true }
            ? context.LastDetection.CentroidY
            : frame.Height / 2;

        return ImageProcessor.CropAroundPoint(
            frame,
            cx,
            cy,
            opt.CropWidth,
            opt.CropHeight,
            opt.CropOffsetX,
            opt.CropOffsetY,
            opt.ProtectBayerPattern);
    }
}

public sealed class EnhancementsStep : IFramePipelineStep
{
    public AstroFrame Execute(AstroFrame frame, FramePipelineContext context)
    {
        var opt = context.Options;
        if (opt.ConvertToMonochrome)
        {
            frame = ImageProcessor.ConvertToMonochrome(frame, opt.MonochromeConversionType);
        }

        if (opt.InputGain != 1.00m || opt.Gamma != 1.00m)
        {
            ImageProcessor.ApplyGainAndGamma(frame, opt.InputGain, opt.Gamma);
        }

        if (opt.EnableHistogramStretch)
        {
            ImageProcessor.ApplyHistogramStretch(
                frame,
                opt.HistogramStretchWhitePointPercent,
                opt.HistogramStretchBlackPointZero,
                opt.HistogramStretchRgbIndividually);
        }

        if (opt.EnableMedianNoiseFilter && frame.Channels == 1)
        {
            frame = ImageProcessor.ApplyMedianFilter3x3(frame);
        }

        if (opt.FlipHorizontalX || opt.FlipVerticalY || opt.Rotation != RotationAngle.None)
        {
            frame = ImageProcessor.ApplyFlipAndRotation(frame, opt.FlipHorizontalX, opt.FlipVerticalY, opt.Rotation);
        }

        return frame;
    }
}

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
