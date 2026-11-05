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

