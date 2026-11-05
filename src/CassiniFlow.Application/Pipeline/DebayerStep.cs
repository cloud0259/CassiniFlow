using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.Pipeline;

public sealed class DebayerStep : IFramePipelineStep
{
    public AstroFrame Execute(AstroFrame frame, FramePipelineContext context)
    {
        var opt = context.Options;
        if (opt.ProtectBayerPattern || !opt.DebayerEnabled)
        {
            return frame;
        }

        if (frame.Channels == 1)
        {
            return ImageProcessor.Debayer(frame, opt.DebayerPattern, opt.DebayerType);
        }

        if (frame.Channels == 3 && ImageProcessor.IsMonochrome(frame))
        {
            var mono = ImageProcessor.ConvertToMonochrome(frame, MonochromeConversionMode.UseRgbChannels);
            return ImageProcessor.Debayer(mono, opt.DebayerPattern, opt.DebayerType);
        }

        return frame;
    }
}

