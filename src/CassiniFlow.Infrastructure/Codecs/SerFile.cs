using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Codecs;

public enum SerColorId
{
    Mono = 0,
    BayerRggb = 8,
    BayerGrbg = 9,
    BayerGbrg = 10,
    BayerBggr = 11,
    BayerCmyg = 16,
    BayerCygm = 17,
    BayerRgbc = 18,
    BayerBgrg = 19,
    Rgb = 100,
    Bgr = 101
}

public sealed class SerHeader
{
    public const int HeaderSizeBytes = 178;
    public const string MagicSignature = "LUCAM-RECORDER";

    public string FileId { get; set; } = MagicSignature;
    public int LuId { get; set; }
    public SerColorId ColorId { get; set; } = SerColorId.Mono;
    public int LittleEndian { get; set; } = 0; // Per SER spec quirk, 0 = Little Endian in many cameras, 1 = Big/Little depending on software
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public int PixelDepthPerPlane { get; set; } = 8;
    public int FrameCount { get; set; }
    public string Observer { get; set; } = "New-CassiniFlow";
    public string Instrument { get; set; } = "AstroCam";
    public string Telescope { get; set; } = "Telescope";
    public long DateTimeTicks { get; set; }
    public long DateTimeUtcTicks { get; set; }
    public bool HasTrailerTimestamps { get; set; }

    public int Planes => ColorId is SerColorId.Rgb or SerColorId.Bgr ? 3 : 1;
    public int BytesPerPixelPlane => PixelDepthPerPlane <= 8 ? 1 : 2;
    public long BytesPerFrame => (long)ImageWidth * ImageHeight * Planes * BytesPerPixelPlane;

    public bool IsBayer => ColorId is SerColorId.BayerRggb or SerColorId.BayerGrbg or SerColorId.BayerGbrg or SerColorId.BayerBggr;

    public BayerPattern ToBayerPattern() => ColorId switch
    {
        SerColorId.BayerGbrg => BayerPattern.Gbrg,
        SerColorId.BayerBggr => BayerPattern.Bggr,
        SerColorId.BayerGrbg => BayerPattern.Grbg,
        _ => BayerPattern.Rggb
    };

    public string FormatDetailedReport(string filePath, long fileSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SER File Details: {Path.GetFileName(filePath)}");
        sb.AppendLine(new string('=', 56));
        sb.AppendLine($"File Path          : {filePath}");
        sb.AppendLine($"File Size          : {fileSize:N0} bytes ({fileSize / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine($"File ID            : {FileId}");
        sb.AppendLine($"LuID               : {LuId}");
        sb.AppendLine($"Color ID           : {(int)ColorId} ({ColorId})");
        sb.AppendLine($"Little Endian Flag : {LittleEndian}");
        sb.AppendLine($"Image Size         : {ImageWidth} x {ImageHeight}");
        sb.AppendLine($"Pixel Depth        : {PixelDepthPerPlane}-bit ({BytesPerPixelPlane} byte(s) per sample)");
        sb.AppendLine($"Frame Count        : {FrameCount}");
        sb.AppendLine($"Observer           : {Observer}");
        sb.AppendLine($"Instrument         : {Instrument}");
        sb.AppendLine($"Telescope          : {Telescope}");
        if (DateTimeUtcTicks > 0)
        {
            sb.AppendLine($"Start Time (UTC)   : {SerTimestampToDateTime(DateTimeUtcTicks):yyyy-MM-dd HH:mm:ss.fff} UTC");
        }
        sb.AppendLine($"Timestamps Trailer : {(HasTrailerTimestamps ? "Present (64-bit UTC per frame)" : "Not present")}");
        return sb.ToString();
    }

    public static DateTime SerTimestampToDateTime(long serSeptaseconds)
    {
        if (serSeptaseconds <= 0) return DateTime.UnixEpoch;
        return new DateTime(serSeptaseconds, DateTimeKind.Utc);
    }
}

/// <summary>
/// High-performance zero-copy SER v3 reader using MemoryMappedFile.
/// </summary>
public sealed class SerFile : IDisposable
{
    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    public string FilePath { get; }
    public SerHeader Header { get; }
    private readonly long[] _frameTimestamps;

    public SerFile(string filePath)
    {
        FilePath = filePath;
        _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (_fileStream.Length < SerHeader.HeaderSizeBytes)
        {
            throw new InvalidDataException($"File '{filePath}' is too short to contain SER header.");
        }

        Span<byte> hdr = stackalloc byte[SerHeader.HeaderSizeBytes];
        _fileStream.ReadExactly(hdr);

        Header = ParseHeader(hdr, _fileStream.Length);
        _frameTimestamps = new long[Header.FrameCount];

        if (Header.HasTrailerTimestamps && Header.FrameCount > 0)
        {
            long trailerOffset = SerHeader.HeaderSizeBytes + Header.BytesPerFrame * Header.FrameCount;
            _fileStream.Seek(trailerOffset, SeekOrigin.Begin);
            byte[] tsBytes = new byte[Header.FrameCount * 8];
            _fileStream.ReadExactly(tsBytes);
            for (int i = 0; i < Header.FrameCount; i++)
            {
                _frameTimestamps[i] = BinaryPrimitives.ReadInt64LittleEndian(tsBytes.AsSpan(i * 8, 8));
            }
        }

        _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _accessor = _mmf.CreateViewAccessor(0, _fileStream.Length, MemoryMappedFileAccess.Read);
    }

    public static SerHeader ParseHeader(ReadOnlySpan<byte> hdr, long totalFileLength)
    {
        string fileId = Encoding.ASCII.GetString(hdr[..14]).TrimEnd('\0', ' ');
        int luId = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(14, 4));
        int colorId = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(18, 4));
        int littleEndian = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(22, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(26, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(30, 4));
        int depth = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(34, 4));
        int frameCount = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(38, 4));
        string observer = Encoding.ASCII.GetString(hdr.Slice(42, 40)).TrimEnd('\0', ' ');
        string instrument = Encoding.ASCII.GetString(hdr.Slice(82, 40)).TrimEnd('\0', ' ');
        string telescope = Encoding.ASCII.GetString(hdr.Slice(122, 40)).TrimEnd('\0', ' ');
        long dtLocal = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(162, 8));
        long dtUtc = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(170, 8));

        var result = new SerHeader
        {
            FileId = fileId,
            LuId = luId,
            ColorId = (SerColorId)colorId,
            LittleEndian = littleEndian,
            ImageWidth = width,
            ImageHeight = height,
            PixelDepthPerPlane = depth,
            FrameCount = frameCount,
            Observer = observer,
            Instrument = instrument,
            Telescope = telescope,
            DateTimeTicks = dtLocal,
            DateTimeUtcTicks = dtUtc
        };

        long expectedDataEnd = SerHeader.HeaderSizeBytes + result.BytesPerFrame * frameCount;
        long expectedTrailerEnd = expectedDataEnd + (long)frameCount * 8;
        result.HasTrailerTimestamps = frameCount > 0 && totalFileLength >= expectedTrailerEnd;

        return result;
    }

    public AstroFrame ReadFrame(int frameIndex, int serBitDepthOverride = 0)
    {
        if (frameIndex < 0 || frameIndex >= Header.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        int effectiveBitDepth = serBitDepthOverride is >= 8 and <= 16 ? serBitDepthOverride : Header.PixelDepthPerPlane;
        int channels = Header.Planes;
        var frame = new AstroFrame(Header.ImageWidth, Header.ImageHeight, channels, effectiveBitDepth)
        {
            FrameIndex = frameIndex,
            IsBayerRaw = Header.IsBayer,
            BayerPattern = Header.ToBayerPattern(),
            TimestampUtc = Header.HasTrailerTimestamps && _frameTimestamps[frameIndex] > 0
                ? SerHeader.SerTimestampToDateTime(_frameTimestamps[frameIndex])
                : null
        };

        long offset = SerHeader.HeaderSizeBytes + frameIndex * Header.BytesPerFrame;
        int sampleCount = Header.ImageWidth * Header.ImageHeight * channels;

        if (Header.BytesPerPixelPlane == 1)
        {
            byte[] raw = new byte[sampleCount];
            _accessor.ReadArray(offset, raw, 0, sampleCount);
            for (int i = 0; i < sampleCount; i++)
            {
                frame.Data[i] = (ushort)(raw[i] * 257); // Scale 0..255 -> 0..65535
            }
        }
        else
        {
            int shift = Math.Clamp(16 - effectiveBitDepth, 0, 8);
            byte[] raw = new byte[sampleCount * 2];
            _accessor.ReadArray(offset, raw, 0, raw.Length);
            ReadOnlySpan<byte> span = raw;
            for (int i = 0; i < sampleCount; i++)
            {
                ushort val = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(i * 2, 2));
                frame.Data[i] = (ushort)Math.Min(65535, val << shift);
            }
        }

        // Convert BGR to RGB if ColorId == Bgr
        if (Header.ColorId == SerColorId.Bgr)
        {
            for (int i = 0; i < frame.Data.Length; i += 3)
            {
                (frame.Data[i], frame.Data[i + 2]) = (frame.Data[i + 2], frame.Data[i]);
            }
        }

        return frame;
    }

    public long GetRawTimestamp(int frameIndex) =>
        (frameIndex >= 0 && frameIndex < _frameTimestamps.Length) ? _frameTimestamps[frameIndex] : 0;

    public static unsafe void WriteSerFile(string outputPath, SerHeader templateHeader, IReadOnlyList<AstroFrame> frames)
    {
        if (frames.Count == 0) return;

        using var writer = new SerStreamWriter(
            outputPath,
            frames[0].Width,
            frames[0].Height,
            frames[0].Channels,
            templateHeader.PixelDepthPerPlane,
            frames[0].IsBayerRaw,
            frames[0].BayerPattern,
            templateHeader.Observer,
            templateHeader.Instrument,
            templateHeader.Telescope,
            templateHeader.DateTimeTicks,
            templateHeader.DateTimeUtcTicks);

        for (int f = 0; f < frames.Count; f++)
        {
            writer.WriteFrame(frames[f]);
        }
        writer.Finish();
    }

    public static void WriteFixedAscii(Span<byte> dest, string text)
    {
        dest.Clear();
        if (string.IsNullOrEmpty(text)) return;
        int len = Math.Min(dest.Length, text.Length);
        Encoding.ASCII.GetBytes(text.AsSpan(0, len), dest);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
        _fileStream.Dispose();
    }
}

/// <summary>
/// Streaming SER v3 file writer that writes frames sequentially to disk using a single reused buffer
/// and patches the final FrameCount and optional UTC timestamp trailer upon Finish().
/// </summary>
public sealed class SerStreamWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly int _pixelDepth;
    private readonly byte[] _frameBuffer;
    private readonly List<long> _timestamps = [];
    private bool _hasAnyTimestamps;
    private int _framesWritten;
    private bool _finished;

    public SerStreamWriter(
        string outputPath,
        int width,
        int height,
        int channels,
        int pixelDepthPerPlane = 8,
        bool isBayerRaw = false,
        BayerPattern bayerPattern = BayerPattern.Rggb,
        string observer = "cassiniflow",
        string instrument = "Camera",
        string telescope = "Telescope",
        long dateTimeTicks = 0,
        long dateTimeUtcTicks = 0)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _pixelDepth = pixelDepthPerPlane <= 8 ? 8 : 16;

        int bytesPerSample = _pixelDepth <= 8 ? 1 : 2;
        _frameBuffer = new byte[width * height * channels * bytesPerSample];

        _fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 262144);

        SerColorId outColorId = channels == 3
            ? SerColorId.Rgb
            : isBayerRaw
                ? bayerPattern switch
                {
                    BayerPattern.Gbrg => SerColorId.BayerGbrg,
                    BayerPattern.Bggr => SerColorId.BayerBggr,
                    BayerPattern.Grbg => SerColorId.BayerGrbg,
                    _ => SerColorId.BayerRggb
                }
                : SerColorId.Mono;

        Span<byte> hdr = stackalloc byte[SerHeader.HeaderSizeBytes];
        hdr.Clear();
        SerFile.WriteFixedAscii(hdr[..14], SerHeader.MagicSignature);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(14, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(18, 4), (int)outColorId);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(22, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(26, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(30, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(34, 4), _pixelDepth);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(38, 4), 0); // Patched on Finish()
        SerFile.WriteFixedAscii(hdr.Slice(42, 40), observer);
        SerFile.WriteFixedAscii(hdr.Slice(82, 40), instrument);
        SerFile.WriteFixedAscii(hdr.Slice(122, 40), telescope);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(162, 8), dateTimeTicks);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(170, 8), dateTimeUtcTicks);

        _fs.Write(hdr);
    }

    public unsafe void WriteFrame(AstroFrame frame)
    {
        if (frame.TimestampUtc.HasValue)
        {
            _hasAnyTimestamps = true;
            _timestamps.Add(frame.TimestampUtc.Value.ToUniversalTime().Ticks);
        }
        else
        {
            _timestamps.Add(0);
        }

        int count = Math.Min(frame.Data.Length, _width * _height * _channels);
        fixed (ushort* pSrc = frame.Data)
        fixed (byte* pDst = _frameBuffer)
        {
            if (_pixelDepth <= 8)
            {
                for (int i = 0; i < count; i++)
                {
                    pDst[i] = (byte)(pSrc[i] >> 8);
                }
            }
            else
            {
                Buffer.MemoryCopy(pSrc, pDst, _frameBuffer.Length, count * sizeof(ushort));
            }
        }

        _fs.Write(_frameBuffer);
        _framesWritten++;
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        if (_hasAnyTimestamps && _framesWritten > 0)
        {
            byte[] tsBuf = new byte[_framesWritten * 8];
            for (int i = 0; i < _framesWritten; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(tsBuf.AsSpan(i * 8, 8), _timestamps[i]);
            }
            _fs.Write(tsBuf);
        }

        // Patch FrameCount at offset 38 in SER header
        Span<byte> fcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(fcBytes, _framesWritten);
        _fs.Seek(38, SeekOrigin.Begin);
        _fs.Write(fcBytes);
        _fs.Flush();
    }

    public void Dispose()
    {
        Finish();
        _fs.Dispose();
    }
}
