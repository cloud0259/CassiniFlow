using System.Buffers.Binary;
using System.Text;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Metadata;
using OpenCvSharp;

namespace CassiniFlow.Infrastructure.Adapters;

public sealed record DngInfo(
    int DecodedWidth,
    int DecodedHeight,
    int RawSensorWidth,
    int RawSensorHeight,
    int RawBitsPerSample,
    string? CfaPattern,
    string? CameraMake,
    string? CameraModel,
    string? Software);

/// <summary>
/// Robust DNG reader for astronomical and photographic raw files.
/// Handles standard uncompressed DNGs as well as smartphone/camera DNGs with SubIFDs and embedded JPEG streams.
/// Never calls OpenCV's native file loader directly on DNG files to avoid noisy console warnings from libtiff.
/// </summary>
public static class DngReader
{
    private sealed class IfdData
    {
        public uint Offset { get; init; }
        public uint SubfileType { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitsPerSample { get; set; } = 8;
        public ushort Compression { get; set; } = 1;
        public ushort Photometric { get; set; }
        public string? CfaPattern { get; set; }
        public uint JpegOffset { get; set; }
        public uint JpegLength { get; set; }
        public List<uint> StripOffsets { get; } = new();
        public List<uint> StripByteCounts { get; } = new();
        public List<uint> SubIfdOffsets { get; } = new();
    }

    public static bool TryInspect(string filePath, out MediaFileSummary? summary)
    {
        summary = null;
        if (!File.Exists(filePath)) return false;

        // Parse TIFF/DNG structure first without touching OpenCV's file loader
        try
        {
            var parsed = ParseDngMetadata(filePath);
            if (parsed != null)
            {
                // Try decoding embedded image preview first
                using var decoded = DecodeEmbeddedImage(filePath, parsed);
                if (decoded != null && !decoded.Empty())
                {
                    var fi = new FileInfo(filePath);
                    var meta = AstroMetadataExtractor.Extract(filePath);
                    int w = decoded.Width;
                    int h = decoded.Height;
                    int depth = parsed.RawSensorBits > 0 ? parsed.RawSensorBits : (decoded.ElemSize1() * 8);

                    var sb = new StringBuilder();
                    sb.AppendLine("Format : DNG (Digital Negative - Image Brute)");
                    if (!string.IsNullOrWhiteSpace(meta.Camera))
                    {
                        sb.AppendLine($"Appareil : {meta.Camera}");
                    }
                    else if (!string.IsNullOrWhiteSpace(parsed.CameraMake) || !string.IsNullOrWhiteSpace(parsed.CameraModel))
                    {
                        sb.AppendLine($"Appareil : {parsed.CameraMake} {parsed.CameraModel}".Trim());
                    }

                    if (!string.IsNullOrWhiteSpace(meta.EffectiveShutterSpeedText) && meta.EffectiveShutterSpeedText != "—")
                    {
                        sb.AppendLine($"Temps d'exposition : {meta.EffectiveShutterSpeedText} ({meta.EffectiveExposureText})");
                    }
                    if (!string.IsNullOrWhiteSpace(meta.IsoOrGainText) && meta.IsoOrGainText != "—")
                    {
                        sb.AppendLine($"Sensibilité : {meta.IsoOrGainText}");
                    }
                    if (!string.IsNullOrWhiteSpace(meta.Telescope))
                    {
                        sb.AppendLine($"Optique : {meta.Telescope}");
                    }
                    if (meta.TimestampUtc.HasValue)
                    {
                        sb.AppendLine($"Date de prise de vue : {meta.TimestampUtc.Value:yyyy-MM-dd HH:mm:ss} UTC");
                    }

                    if (!string.IsNullOrWhiteSpace(parsed.Software))
                    {
                        sb.AppendLine($"Logiciel : {parsed.Software}");
                    }
                    sb.AppendLine($"Dimensions décodées : {w} x {h}");
                    if (parsed.RawSensorWidth > 0 && parsed.RawSensorHeight > 0)
                    {
                        sb.AppendLine($"Capteur RAW d'origine : {parsed.RawSensorWidth} x {parsed.RawSensorHeight} ({parsed.RawSensorBits}-bit)");
                    }
                    if (!string.IsNullOrWhiteSpace(parsed.CfaPattern))
                    {
                        sb.AppendLine($"Matrice Bayer CFA : {parsed.CfaPattern}");
                    }
                    sb.AppendLine($"Taille du fichier : {fi.Length:N0} octets");

                    string badge = parsed.RawSensorBits > 0 ? $"DNG {parsed.RawSensorBits}-bit" : $"DNG {depth}-bit";

                    summary = new MediaFileSummary(
                        filePath,
                        badge,
                        1,
                        w,
                        h,
                        1.0,
                        fi.Length,
                        sb.ToString(),
                        meta);
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to OpenCV native fallback if custom parser fails
        }

        // Fallback: uncompressed TIFF DNG handled natively by OpenCV
        try
        {
            using var mat = Cv2.ImRead(filePath, ImreadModes.Unchanged);
            if (!mat.Empty())
            {
                int w = mat.Width;
                int h = mat.Height;
                int depth = mat.ElemSize1() * 8;
                var fi = new FileInfo(filePath);
                var meta = AstroMetadataExtractor.Extract(filePath);

                var sb = new StringBuilder();
                sb.AppendLine("Format : DNG (Digital Negative)");
                if (!string.IsNullOrWhiteSpace(meta.Camera)) sb.AppendLine($"Appareil : {meta.Camera}");
                sb.AppendLine($"Dimensions : {w} x {h}");
                sb.AppendLine($"Profondeur : {depth}-bit");
                sb.AppendLine($"Canaux : {mat.Channels()}");
                sb.AppendLine($"Taille du fichier : {fi.Length:N0} octets");

                summary = new MediaFileSummary(
                    filePath,
                    $"DNG {depth}-bit",
                    1,
                    w,
                    h,
                    1.0,
                    fi.Length,
                    sb.ToString(),
                    meta);
                return true;
            }
        }
        catch
        {
            // Ignore
        }

        return false;
    }

    public static Mat? Decode(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        // Try decoding embedded image stream first (fast and completely silent)
        try
        {
            var parsed = ParseDngMetadata(filePath);
            if (parsed != null)
            {
                var mat = DecodeEmbeddedImage(filePath, parsed);
                if (mat != null && !mat.Empty())
                    return mat;
            }
        }
        catch
        {
            // Ignore and try native fallback
        }

        try
        {
            var mat = Cv2.ImRead(filePath, ImreadModes.Unchanged);
            if (!mat.Empty())
                return mat;
        }
        catch
        {
            // Ignore
        }

        return null;
    }

    private sealed class ParsedDng
    {
        public int RawSensorWidth { get; set; }
        public int RawSensorHeight { get; set; }
        public int RawSensorBits { get; set; }
        public string? CfaPattern { get; set; }
        public string? CameraMake { get; set; }
        public string? CameraModel { get; set; }
        public string? Software { get; set; }
        public List<IfdData> AllIfds { get; } = new();
    }

    private static ParsedDng? ParseDngMetadata(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        if (fs.Length < 16) return null;

        Span<byte> header = stackalloc byte[8];
        if (fs.Read(header) < 8) return null;

        bool isLittle = header[0] == 0x49 && header[1] == 0x49;
        bool isBig = header[0] == 0x4D && header[1] == 0x4D;
        if (!isLittle && !isBig) return null;

        ushort magic = isLittle ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) : BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        if (magic != 42) return null;

        uint firstIfdOffset = isLittle ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);

        var result = new ParsedDng();
        var visited = new HashSet<uint>();
        var ifdQueue = new Queue<uint>();
        ifdQueue.Enqueue(firstIfdOffset);

        while (ifdQueue.Count > 0)
        {
            uint currentOffset = ifdQueue.Dequeue();
            if (currentOffset == 0 || currentOffset >= fs.Length || !visited.Add(currentOffset))
                continue;

            var ifd = ReadIfd(fs, currentOffset, isLittle, result);
            if (ifd != null)
            {
                result.AllIfds.Add(ifd);
                foreach (var sub in ifd.SubIfdOffsets)
                {
                    if (!visited.Contains(sub))
                        ifdQueue.Enqueue(sub);
                }
            }
        }

        // Determine raw sensor dimensions from IFD with SubfileType == 0 or CFA photometric
        var rawIfd = result.AllIfds.FirstOrDefault(x => x.SubfileType == 0 && x.Width > 0)
                     ?? result.AllIfds.FirstOrDefault(x => !string.IsNullOrEmpty(x.CfaPattern) && x.Width > 0)
                     ?? result.AllIfds.FirstOrDefault(x => x.Width > 0);

        if (rawIfd != null)
        {
            result.RawSensorWidth = rawIfd.Width;
            result.RawSensorHeight = rawIfd.Height;
            result.RawSensorBits = rawIfd.BitsPerSample;
            result.CfaPattern ??= rawIfd.CfaPattern;
        }

        return result;
    }

    private static IfdData? ReadIfd(FileStream fs, uint offset, bool isLittle, ParsedDng result)
    {
        if (offset + 2 > fs.Length) return null;
        fs.Seek(offset, SeekOrigin.Begin);

        Span<byte> countBuf = stackalloc byte[2];
        if (fs.Read(countBuf) < 2) return null;
        ushort tagCount = isLittle ? BinaryPrimitives.ReadUInt16LittleEndian(countBuf) : BinaryPrimitives.ReadUInt16BigEndian(countBuf);
        if (tagCount == 0 || tagCount > 1000) return null;

        var ifd = new IfdData { Offset = offset };
        byte[] entryBuffer = new byte[tagCount * 12];
        if (fs.Read(entryBuffer, 0, entryBuffer.Length) < entryBuffer.Length) return null;

        for (int i = 0; i < tagCount; i++)
        {
            var span = entryBuffer.AsSpan(i * 12, 12);
            ushort tag = isLittle ? BinaryPrimitives.ReadUInt16LittleEndian(span[0..2]) : BinaryPrimitives.ReadUInt16BigEndian(span[0..2]);
            ushort type = isLittle ? BinaryPrimitives.ReadUInt16LittleEndian(span[2..4]) : BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
            uint count = isLittle ? BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]) : BinaryPrimitives.ReadUInt32BigEndian(span[4..8]);
            uint valOrOffset = isLittle ? BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]) : BinaryPrimitives.ReadUInt32BigEndian(span[8..12]);

            switch (tag)
            {
                case 254: // NewSubfileType
                    ifd.SubfileType = valOrOffset;
                    break;
                case 256: // ImageWidth
                    ifd.Width = (int)valOrOffset;
                    break;
                case 257: // ImageLength (Height)
                    ifd.Height = (int)valOrOffset;
                    break;
                case 258: // BitsPerSample
                    if (count == 1) ifd.BitsPerSample = (int)(valOrOffset & 0xFFFF);
                    else
                    {
                        ushort firstBit = ReadFirstUshort(fs, valOrOffset, isLittle);
                        if (firstBit > 0) ifd.BitsPerSample = firstBit;
                    }
                    break;
                case 259: // Compression
                    ifd.Compression = (ushort)(valOrOffset & 0xFFFF);
                    break;
                case 262: // PhotometricInterpretation
                    ifd.Photometric = (ushort)(valOrOffset & 0xFFFF);
                    break;
                case 271: // Make
                    result.CameraMake ??= ReadTiffString(fs, valOrOffset, count);
                    break;
                case 272: // Model
                    result.CameraModel ??= ReadTiffString(fs, valOrOffset, count);
                    break;
                case 273: // StripOffsets
                    ReadUintArray(fs, valOrOffset, count, type, isLittle, ifd.StripOffsets);
                    break;
                case 279: // StripByteCounts
                    ReadUintArray(fs, valOrOffset, count, type, isLittle, ifd.StripByteCounts);
                    break;
                case 305: // Software
                    result.Software ??= ReadTiffString(fs, valOrOffset, count);
                    break;
                case 330: // SubIFDs
                    ReadUintArray(fs, valOrOffset, count, type, isLittle, ifd.SubIfdOffsets);
                    break;
                case 513: // JPEGInterchangeFormat
                    ifd.JpegOffset = valOrOffset;
                    break;
                case 514: // JPEGInterchangeFormatLength
                    ifd.JpegLength = valOrOffset;
                    break;
                case 33422: // CFAPattern
                    ifd.CfaPattern = ReadCfaPattern(fs, valOrOffset, count);
                    result.CfaPattern ??= ifd.CfaPattern;
                    break;
            }
        }

        // Check for chained next IFD
        Span<byte> nextIfdBuf = stackalloc byte[4];
        if (fs.Read(nextIfdBuf) == 4)
        {
            uint nextOffset = isLittle ? BinaryPrimitives.ReadUInt32LittleEndian(nextIfdBuf) : BinaryPrimitives.ReadUInt32BigEndian(nextIfdBuf);
            if (nextOffset > 0 && nextOffset < fs.Length)
            {
                ifd.SubIfdOffsets.Add(nextOffset);
            }
        }

        return ifd;
    }

    private static void ReadUintArray(FileStream fs, uint valOrOffset, uint count, ushort type, bool isLittle, List<uint> destination)
    {
        if (count == 0) return;
        if (count == 1)
        {
            destination.Add(valOrOffset);
            return;
        }

        long savedPos = fs.Position;
        try
        {
            fs.Seek(valOrOffset, SeekOrigin.Begin);
            if (type == 3) // SHORT (16-bit)
            {
                byte[] buf = new byte[count * 2];
                if (fs.Read(buf, 0, buf.Length) == buf.Length)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ushort v = isLittle ? BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i * 2, 2)) : BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(i * 2, 2));
                        destination.Add(v);
                    }
                }
            }
            else // LONG (32-bit)
            {
                byte[] buf = new byte[count * 4];
                if (fs.Read(buf, 0, buf.Length) == buf.Length)
                {
                    for (int i = 0; i < count; i++)
                    {
                        uint v = isLittle ? BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4, 4)) : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i * 4, 4));
                        destination.Add(v);
                    }
                }
            }
        }
        finally
        {
            fs.Seek(savedPos, SeekOrigin.Begin);
        }
    }

    private static ushort ReadFirstUshort(FileStream fs, uint offset, bool isLittle)
    {
        long savedPos = fs.Position;
        try
        {
            fs.Seek(offset, SeekOrigin.Begin);
            Span<byte> buf = stackalloc byte[2];
            if (fs.Read(buf) == 2)
            {
                return isLittle ? BinaryPrimitives.ReadUInt16LittleEndian(buf) : BinaryPrimitives.ReadUInt16BigEndian(buf);
            }
            return 0;
        }
        finally
        {
            fs.Seek(savedPos, SeekOrigin.Begin);
        }
    }

    private static string? ReadTiffString(FileStream fs, uint offset, uint count)
    {
        if (count == 0 || count > 1024) return null;
        long savedPos = fs.Position;
        try
        {
            fs.Seek(offset, SeekOrigin.Begin);
            byte[] buf = new byte[count];
            if (fs.Read(buf, 0, buf.Length) == buf.Length)
            {
                return Encoding.ASCII.GetString(buf).TrimEnd('\0').Trim();
            }
            return null;
        }
        finally
        {
            fs.Seek(savedPos, SeekOrigin.Begin);
        }
    }

    private static string? ReadCfaPattern(FileStream fs, uint offset, uint count)
    {
        if (count < 4) return null;
        long savedPos = fs.Position;
        try
        {
            fs.Seek(offset, SeekOrigin.Begin);
            Span<byte> buf = stackalloc byte[4];
            if (fs.Read(buf) == 4)
            {
                // 0 = Red, 1 = Green, 2 = Blue
                static char ToColor(byte c) => c switch { 0 => 'R', 1 => 'G', 2 => 'B', _ => '?' };
                return $"{ToColor(buf[0])}{ToColor(buf[1])}{ToColor(buf[2])}{ToColor(buf[3])}";
            }
            return null;
        }
        finally
        {
            fs.Seek(savedPos, SeekOrigin.Begin);
        }
    }

    private static Mat? DecodeEmbeddedImage(string filePath, ParsedDng parsed)
    {
        using var fs = File.OpenRead(filePath);

        // Try JPEG streams first, sorted by largest dimension or largest byte count
        var candidateIfds = parsed.AllIfds
            .OrderByDescending(x => x.Width * x.Height)
            .ToList();

        foreach (var ifd in candidateIfds)
        {
            // Case A: JPEGInterchangeFormat
            if (ifd.JpegOffset > 0 && ifd.JpegLength > 100)
            {
                var mat = TryDecodeJpegStream(fs, ifd.JpegOffset, ifd.JpegLength);
                if (mat != null) return mat;
            }

            // Case B: StripOffsets with JPEG compression (7 or 6)
            if (ifd.StripOffsets.Count > 0 && ifd.StripByteCounts.Count > 0)
            {
                uint offset = ifd.StripOffsets[0];
                uint len = ifd.StripByteCounts[0];
                if (offset > 0 && len > 100)
                {
                    var mat = TryDecodeJpegStream(fs, offset, len);
                    if (mat != null) return mat;
                }
            }
        }

        return null;
    }

    private static Mat? TryDecodeJpegStream(FileStream fs, uint offset, uint length)
    {
        if (offset + length > fs.Length) return null;

        fs.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[length];
        if (fs.Read(buffer, 0, buffer.Length) != buffer.Length) return null;

        // Verify standard JPEG SOI marker (0xFF, 0xD8)
        if (buffer.Length < 4 || buffer[0] != 0xFF || buffer[1] != 0xD8) return null;

        var mat = Cv2.ImDecode(buffer, ImreadModes.Unchanged);
        if (mat.Empty())
        {
            mat.Dispose();
            return null;
        }

        return mat;
    }
}
