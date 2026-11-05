using System;
using System.IO;
using System.Text;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Codecs;

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
        sb.AppendLine($"File Size          : {fileSize:N0} bytes ({fileSize / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine($"File ID            : {FileId}");
        sb.AppendLine($"LuID               : {LuId}");
        sb.AppendLine($"Color ID           : {ColorId} ({(int)ColorId})");
        sb.AppendLine($"Little Endian Flag : {LittleEndian}");
        sb.AppendLine($"Dimensions         : {ImageWidth} x {ImageHeight} px");
        sb.AppendLine($"Pixel Depth        : {PixelDepthPerPlane}-bit ({BytesPerPixelPlane} byte(s)/sample)");
        sb.AppendLine($"Frame Count        : {FrameCount:N0}");
        sb.AppendLine($"Bytes Per Frame    : {BytesPerFrame:N0}");
        sb.AppendLine($"Observer           : {Observer}");
        sb.AppendLine($"Instrument         : {Instrument}");
        sb.AppendLine($"Telescope          : {Telescope}");
        if (DateTimeTicks > 0)
            sb.AppendLine($"Start Time (Local) : {SerTimestampToDateTime(DateTimeTicks):yyyy-MM-dd HH:mm:ss.fff}");
        if (DateTimeUtcTicks > 0)
            sb.AppendLine($"Start Time (UTC)   : {SerTimestampToDateTime(DateTimeUtcTicks):yyyy-MM-dd HH:mm:ss.fff} UTC");
        sb.AppendLine($"Timestamps Trailer : {(HasTrailerTimestamps ? "Present (64-bit UTC per frame)" : "Not present")}");
        return sb.ToString();
    }

    public static DateTime SerTimestampToDateTime(long serSeptaseconds)
    {
        if (serSeptaseconds <= 0 || serSeptaseconds > DateTime.MaxValue.Ticks)
            return DateTime.MinValue;
        return new DateTime(serSeptaseconds, DateTimeKind.Utc);
    }
}

