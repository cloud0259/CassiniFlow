using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.Pipeline;

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

