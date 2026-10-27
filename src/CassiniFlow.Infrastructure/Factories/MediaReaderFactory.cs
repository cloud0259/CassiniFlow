using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Outbound;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Codecs;

namespace CassiniFlow.Infrastructure.Factories;

public sealed class MediaReaderAdapter : IMediaFrameReader
{
    private readonly SerFile? _serFile;
    private readonly string _filePath;

    public MediaInfo Info { get; }

    public MediaReaderAdapter(string filePath, MediaInfo info, SerFile? serFile = null)
    {
        _filePath = filePath;
        Info = info;
        _serFile = serFile;
    }

    public AstroFrame ReadFrame(int frameIndex)
    {
        if (_serFile != null)
        {
            return _serFile.ReadFrame(frameIndex);
        }
        return UniversalMediaReader.ReadSingleFrame(_filePath, frameIndex);
    }

    public void Dispose()
    {
        _serFile?.Dispose();
    }
}

public sealed class MediaReaderFactory : IMediaReaderFactory
{
    public IMediaFrameReader CreateReader(string filePath)
    {
        var info = Inspect(filePath);
        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".ser", StringComparison.OrdinalIgnoreCase))
        {
            var ser = new SerFile(filePath);
            return new MediaReaderAdapter(filePath, info, ser);
        }
        return new MediaReaderAdapter(filePath, info);
    }

    public MediaInfo Inspect(string filePath)
    {
        var summary = UniversalMediaReader.InspectFile(filePath);
        string ext = Path.GetExtension(filePath);
        bool isBayer = false;
        BayerPattern bayer = BayerPattern.Rggb;

        if (ext.Equals(".ser", StringComparison.OrdinalIgnoreCase))
        {
            using var ser = new SerFile(filePath);
            isBayer = ser.Header.IsBayer;
            bayer = ser.Header.ToBayerPattern();
        }

        return new MediaInfo(
            summary.FilePath,
            summary.FormatBadge,
            summary.FrameCount,
            summary.Width,
            summary.Height,
            16,
            summary.Fps,
            isBayer,
            bayer);
    }

    public (AstroFrame Frame, int OriginalWidth, int OriginalHeight) ReadPreviewFrame(string filePath, int frameIndex, int maxDimension)
    {
        var preview = UniversalMediaReader.ReadPreviewFrame(filePath, frameIndex, maxDimension);
        return (preview.Frame, preview.OriginalWidth, preview.OriginalHeight);
    }
}
