using System.Buffers.Binary;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Codecs;

namespace CassiniFlow.Infrastructure.Metadata;

public static class AstroMetadataExtractor
{
    /// <summary>
    /// Extrait les métadonnées astronomiques approfondies (exposition, ISO, vitesse d'obturation, gain, instrument)
    /// depuis le fichier média, son en-tête natif (FITS/SER/EXIF) et d'éventuels fichiers compagnons (FireCapture / SharpCap).
    /// </summary>
    public static AstroMediaMetadata Extract(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return AstroMediaMetadata.Empty;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        // 1. Extraction spécifique au format
        AstroMediaMetadata meta = ext switch
        {
            ".fit" or ".fits" or ".fts" => ExtractFromFits(filePath),
            ".ser" => ExtractFromSer(filePath),
            ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".png" or ".dng" => ExtractFromExifOrImage(filePath),
            _ => AstroMediaMetadata.Empty
        };

        // 2. Recherche et fusion des fichiers de capture compagnons (SharpCap, FireCapture, AstroDMx)
        var sidecarMeta = ExtractFromSidecarFile(filePath);
        if (sidecarMeta != AstroMediaMetadata.Empty)
        {
            meta = Merge(meta, sidecarMeta);
        }

        return meta;
    }

    public static AstroMediaMetadata ExtractFromFits(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fitsMeta = FitsFile.ReadMetadata(fs);
            return ExtractFromFitsCards(fitsMeta.Cards);
        }
        catch
        {
            return AstroMediaMetadata.Empty;
        }
    }

    public static AstroMediaMetadata ExtractFromFitsCards(IEnumerable<string> cards)
    {
        double? expTime = null;
        int? iso = null;
        double? gain = null;
        string? camera = null;
        string? telescope = null;
        string? filter = null;
        DateTime? timestamp = null;

        foreach (string card in cards)
        {
            int eqIdx = card.IndexOf('=');
            if (eqIdx < 0) continue;

            string key = card[..Math.Min(8, eqIdx)].Trim().ToUpperInvariant();
            string valPart = card[(eqIdx + 1)..];
            int slashIdx = valPart.IndexOf('/');
            if (slashIdx >= 0) valPart = valPart[..slashIdx];
            valPart = valPart.Trim().Trim('\'').Trim();

            switch (key)
            {
                case "EXPTIME" or "EXPOSURE" or "TELAPSE" or "ELAPSED":
                    if (double.TryParse(valPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double exp) && exp > 0)
                        expTime ??= exp;
                    break;
                case "ISO" or "ISOSPEED":
                    if (int.TryParse(valPart, out int isoVal) && isoVal > 0)
                        iso ??= isoVal;
                    break;
                case "GAIN" or "EGAIN":
                    if (double.TryParse(valPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double gVal))
                        gain ??= gVal;
                    break;
                case "INSTRUME" or "CAMERA":
                    if (!string.IsNullOrWhiteSpace(valPart))
                        camera ??= valPart;
                    break;
                case "TELESCOP":
                    if (!string.IsNullOrWhiteSpace(valPart))
                        telescope ??= valPart;
                    break;
                case "FILTER":
                    if (!string.IsNullOrWhiteSpace(valPart))
                        filter ??= valPart;
                    break;
                case "DATE-OBS":
                    if (DateTime.TryParse(valPart, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dt))
                        timestamp ??= dt;
                    break;
            }
        }

        string? shutter = expTime.HasValue ? AstroMediaMetadata.FormatShutterSpeed(expTime.Value) : null;
        string? expText = expTime.HasValue ? AstroMediaMetadata.FormatExposure(expTime.Value) : null;

        return new AstroMediaMetadata(
            ExposureTimeSeconds: expTime,
            ExposureText: expText,
            ShutterSpeedText: shutter,
            Iso: iso,
            Gain: gain,
            Camera: camera,
            Telescope: telescope,
            Filter: filter,
            TimestampUtc: timestamp);
    }

    public static AstroMediaMetadata ExtractFromSer(string filePath)
    {
        try
        {
            using var ser = new SerFile(filePath);
            var h = ser.Header;

            string? camera = string.IsNullOrWhiteSpace(h.Instrument) || h.Instrument == "AstroCam" ? null : h.Instrument;
            string? telescope = string.IsNullOrWhiteSpace(h.Telescope) || h.Telescope == "Telescope" ? null : h.Telescope;
            DateTime? dt = h.DateTimeUtcTicks > 0 ? SerHeader.SerTimestampToDateTime(h.DateTimeUtcTicks) : null;

            double? framePeriod = null;
            if (h.HasTrailerTimestamps && h.FrameCount > 1)
            {
                // Lecture des premiers timestamps pour estimer la cadence d'obturation maximale
                try
                {
                    long t0 = ser.GetRawTimestamp(0);
                    long t1 = ser.GetRawTimestamp(1);
                    if (t1 > t0)
                    {
                        framePeriod = (t1 - t0) / 10_000_000.0;
                    }
                }
                catch { }
            }

            return new AstroMediaMetadata(
                ExposureTimeSeconds: framePeriod,
                ExposureText: framePeriod.HasValue ? AstroMediaMetadata.FormatExposure(framePeriod.Value) : null,
                ShutterSpeedText: framePeriod.HasValue ? AstroMediaMetadata.FormatShutterSpeed(framePeriod.Value) : null,
                Camera: camera,
                Telescope: telescope,
                TimestampUtc: dt);
        }
        catch
        {
            return AstroMediaMetadata.Empty;
        }
    }

    public static AstroMediaMetadata ExtractFromSidecarFile(string mediaFilePath)
    {
        string dir = Path.GetDirectoryName(mediaFilePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(mediaFilePath);
        string fullName = Path.GetFileName(mediaFilePath);

        string[] potentialSidecars =
        [
            Path.Combine(dir, $"{baseName}.CameraSettings.txt"),
            Path.Combine(dir, $"{fullName}.CameraSettings.txt"),
            Path.Combine(dir, $"{baseName}.txt"),
            Path.Combine(dir, $"{fullName}.txt"),
            Path.Combine(dir, $"{baseName}_info.txt"),
            Path.Combine(dir, $"{baseName}.log")
        ];

        foreach (string sc in potentialSidecars)
        {
            if (File.Exists(sc))
            {
                try
                {
                    var parsed = ParseSidecarContent(File.ReadAllText(sc));
                    if (parsed != AstroMediaMetadata.Empty)
                        return parsed;
                }
                catch { }
            }
        }

        return AstroMediaMetadata.Empty;
    }

    public static AstroMediaMetadata ParseSidecarContent(string text)
    {
        double? expTime = null;
        string? expText = null;
        string? shutterText = null;
        int? iso = null;
        double? gain = null;
        string? camera = null;
        string? telescope = null;
        string? filter = null;
        DateTime? timestamp = null;

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            int sep = line.IndexOfAny(['=', ':']);
            if (sep <= 0) continue;

            string key = line[..sep].Trim().ToLowerInvariant();
            string val = line[(sep + 1)..].Trim();
            if (string.IsNullOrEmpty(val)) continue;

            if (key.Contains("exp") || key.Contains("exposure") || key.Contains("shutter"))
            {
                shutterText ??= val;
                expText ??= val;
                expTime ??= ParseTimeToSeconds(val);
            }
            else if (key == "iso" || key.StartsWith("iso "))
            {
                var match = Regex.Match(val, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int iv))
                    iso ??= iv;
            }
            else if (key.Contains("gain"))
            {
                var match = Regex.Match(val, @"[-+]?[0-9]*\.?[0-9]+");
                if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gv))
                    gain ??= gv;
            }
            else if (key.Contains("camera") || key.Contains("sensor") || key.Contains("instrument"))
            {
                camera ??= val;
            }
            else if (key.Contains("telescope") || key.Contains("scope") || key.Contains("focal"))
            {
                telescope ??= val;
            }
            else if (key.Contains("filter"))
            {
                filter ??= val;
            }
            else if (key.Contains("date") || key.Contains("time") || key.Contains("start"))
            {
                if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    timestamp ??= dt;
            }
        }

        if (expTime.HasValue)
        {
            shutterText ??= AstroMediaMetadata.FormatShutterSpeed(expTime.Value);
            expText ??= AstroMediaMetadata.FormatExposure(expTime.Value);
        }

        return new AstroMediaMetadata(
            ExposureTimeSeconds: expTime,
            ExposureText: expText,
            ShutterSpeedText: shutterText,
            Iso: iso,
            Gain: gain,
            Camera: camera,
            Telescope: telescope,
            Filter: filter,
            TimestampUtc: timestamp);
    }

    private static double? ParseTimeToSeconds(string val)
    {
        val = val.Trim().ToLowerInvariant();

        // 1/250s or 1/250
        var fracMatch = Regex.Match(val, @"^1\s*/\s*([0-9]+(\.[0-9]+)?)\s*s?$");
        if (fracMatch.Success && double.TryParse(fracMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double denom) && denom > 0)
        {
            return 1.0 / denom;
        }

        // 15ms or 15.5 ms
        var msMatch = Regex.Match(val, @"^([0-9]+(\.[0-9]+)?)\s*ms$");
        if (msMatch.Success && double.TryParse(msMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
        {
            return ms / 1000.0;
        }

        // 200us or 200 µs
        var usMatch = Regex.Match(val, @"^([0-9]+(\.[0-9]+)?)\s*(us|µs)$");
        if (usMatch.Success && double.TryParse(usMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double us))
        {
            return us / 1_000_000.0;
        }

        // 2.5s or 2.5
        var secMatch = Regex.Match(val, @"^([0-9]+(\.[0-9]+)?)\s*s?$");
        if (secMatch.Success && double.TryParse(secMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
        {
            return s;
        }

        return null;
    }

    public static AstroMediaMetadata ExtractFromExifOrImage(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadExifTags(fs);
        }
        catch
        {
            return AstroMediaMetadata.Empty;
        }
    }

    private static AstroMediaMetadata ReadExifTags(Stream stream)
    {
        if (stream.Length < 16) return AstroMediaMetadata.Empty;

        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) < 4) return AstroMediaMetadata.Empty;

        // TIFF (II = Little Endian, MM = Big Endian)
        if ((magic[0] == 0x49 && magic[1] == 0x49 && magic[2] == 0x2A && magic[3] == 0x00) ||
            (magic[0] == 0x4D && magic[1] == 0x4D && magic[2] == 0x00 && magic[3] == 0x2A))
        {
            return ParseTiffExifStream(stream, 0, stream.Length);
        }

        // JPEG check (SOI marker 0xFFD8)
        if (magic[0] == 0xFF && magic[1] == 0xD8)
        {
            long pos = 2;
            byte[] markerBuf = new byte[4];

            while (pos < stream.Length - 4)
            {
                stream.Seek(pos, SeekOrigin.Begin);
                if (stream.Read(markerBuf, 0, 4) < 4) break;
                if (markerBuf[0] != 0xFF) break;

                byte marker = markerBuf[1];
                int len = (markerBuf[2] << 8) | markerBuf[3];

                if (marker == 0xE1) // APP1 marker
                {
                    byte[] exifSig = new byte[6];
                    if (stream.Read(exifSig, 0, 6) == 6 &&
                        exifSig[0] == 0x45 && exifSig[1] == 0x78 && exifSig[2] == 0x69 && exifSig[3] == 0x66 &&
                        exifSig[4] == 0x00 && exifSig[5] == 0x00)
                    {
                        long tiffStart = pos + 10;
                        long tiffLen = len - 8;
                        return ParseTiffExifStream(stream, tiffStart, tiffLen);
                    }
                }

                if (marker == 0xDA) break; // SOS marker, image data begins

                pos += 2 + len;
            }
        }

        return AstroMediaMetadata.Empty;
    }

    private static AstroMediaMetadata ParseTiffExifStream(Stream stream, long tiffStart, long tiffLength)
    {
        if (tiffLength < 8 || !stream.CanSeek) return AstroMediaMetadata.Empty;

        stream.Seek(tiffStart, SeekOrigin.Begin);
        byte[] hdr = new byte[8];
        if (stream.Read(hdr, 0, 8) < 8) return AstroMediaMetadata.Empty;

        bool littleEndian = hdr[0] == 0x49 && hdr[1] == 0x49;
        bool bigEndian = hdr[0] == 0x4D && hdr[1] == 0x4D;
        if (!littleEndian && !bigEndian) return AstroMediaMetadata.Empty;

        ushort magic = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(2, 2));
        if (magic != 42) return AstroMediaMetadata.Empty;

        uint ifdOffset = littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(4, 4));

        if (ifdOffset == 0 || ifdOffset >= tiffLength) return AstroMediaMetadata.Empty;

        double? expTime = null;
        int? iso = null;
        string? make = null;
        string? model = null;
        string? camera = null;
        string? telescope = null;
        DateTime? timestamp = null;
        double? fNumber = null;
        double? focalLength = null;
        int? focalLength35 = null;

        var visited = new HashSet<uint>();

        void ReadIfd(uint relOffset, int depth)
        {
            if (depth > 10 || relOffset == 0 || relOffset + 2 >= tiffLength || !visited.Add(relOffset))
                return;

            long absOffset = tiffStart + relOffset;
            stream.Seek(absOffset, SeekOrigin.Begin);

            byte[] countBuf = new byte[2];
            if (stream.Read(countBuf, 0, 2) < 2) return;
            ushort count = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(countBuf)
                : BinaryPrimitives.ReadUInt16BigEndian(countBuf);
            if (count == 0 || count > 500) return;

            byte[] entriesBuf = new byte[count * 12];
            if (stream.Read(entriesBuf, 0, entriesBuf.Length) < entriesBuf.Length) return;

            var subIfdsToFollow = new List<uint>();

            for (int i = 0; i < count; i++)
            {
                var entry = entriesBuf.AsSpan(i * 12, 12);
                ushort tag = littleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(entry[..2])
                    : BinaryPrimitives.ReadUInt16BigEndian(entry[..2]);
                ushort type = littleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(entry[2..4])
                    : BinaryPrimitives.ReadUInt16BigEndian(entry[2..4]);
                uint valCount = littleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(entry[4..8])
                    : BinaryPrimitives.ReadUInt32BigEndian(entry[4..8]);
                uint valOrOffset = littleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12])
                    : BinaryPrimitives.ReadUInt32BigEndian(entry[8..12]);

                switch (tag)
                {
                    case 0x010F: // Make (ASCII)
                        make ??= ReadStringAt(stream, tiffStart, valOrOffset, valCount);
                        break;
                    case 0x0110: // Model (ASCII)
                        model ??= ReadStringAt(stream, tiffStart, valOrOffset, valCount);
                        break;
                    case 0x0132: // DateTime (ASCII)
                        if (timestamp == null)
                        {
                            string? dtStr = ReadStringAt(stream, tiffStart, valOrOffset, valCount);
                            timestamp = ParseExifDateTime(dtStr);
                        }
                        break;
                    case 0x829A: // ExposureTime (Rational)
                        if (expTime == null)
                        {
                            var (num, den) = ReadRationalAt(stream, tiffStart, valOrOffset, littleEndian);
                            if (den > 0) expTime = (double)num / den;
                        }
                        break;
                    case 0x829D: // FNumber (Rational)
                        if (fNumber == null)
                        {
                            var (num, den) = ReadRationalAt(stream, tiffStart, valOrOffset, littleEndian);
                            if (den > 0) fNumber = (double)num / den;
                        }
                        break;
                    case 0x8827: // ISOSpeedRatings (Short/Long)
                        iso ??= (int)(valOrOffset & 0xFFFF);
                        break;
                    case 0x9003: // DateTimeOriginal (ASCII)
                    case 0x9004: // DateTimeDigitized (ASCII)
                        if (timestamp == null)
                        {
                            string? dtStr = ReadStringAt(stream, tiffStart, valOrOffset, valCount);
                            timestamp = ParseExifDateTime(dtStr);
                        }
                        break;
                    case 0x920A: // FocalLength (Rational)
                        if (focalLength == null)
                        {
                            var (num, den) = ReadRationalAt(stream, tiffStart, valOrOffset, littleEndian);
                            if (den > 0) focalLength = (double)num / den;
                        }
                        break;
                    case 0xA405: // FocalLengthIn35mmFilm (Short)
                        focalLength35 ??= (int)(valOrOffset & 0xFFFF);
                        break;
                    case 0x8769: // ExifIFDPointer
                    case 0x014A: // SubIFDs
                        subIfdsToFollow.Add(valOrOffset);
                        break;
                }
            }

            foreach (var sub in subIfdsToFollow)
            {
                ReadIfd(sub, depth + 1);
            }
        }

        ReadIfd(ifdOffset, 0);

        if (!string.IsNullOrWhiteSpace(model))
        {
            if (!string.IsNullOrWhiteSpace(make) && !model.Contains(make, StringComparison.OrdinalIgnoreCase))
                camera = $"{make} {model}".Trim();
            else
                camera = model.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(make))
        {
            camera = make.Trim();
        }

        if (fNumber.HasValue && focalLength.HasValue)
        {
            telescope = focalLength35.HasValue && focalLength35.Value > 0
                ? $"f/{fNumber.Value:F1}, {focalLength.Value:F1} mm (équiv. {focalLength35.Value} mm)"
                : $"f/{fNumber.Value:F1}, {focalLength.Value:F1} mm";
        }
        else if (focalLength.HasValue)
        {
            telescope = focalLength35.HasValue && focalLength35.Value > 0
                ? $"{focalLength.Value:F1} mm (équiv. {focalLength35.Value} mm)"
                : $"{focalLength.Value:F1} mm";
        }

        string? shutter = expTime.HasValue ? AstroMediaMetadata.FormatShutterSpeed(expTime.Value) : null;
        string? expText = expTime.HasValue ? AstroMediaMetadata.FormatExposure(expTime.Value) : null;

        return new AstroMediaMetadata(
            ExposureTimeSeconds: expTime,
            ExposureText: expText,
            ShutterSpeedText: shutter,
            Iso: iso,
            Camera: camera,
            Telescope: telescope,
            TimestampUtc: timestamp);
    }

    private static (uint Num, uint Den) ReadRationalAt(Stream stream, long tiffStart, uint valOrOffset, bool littleEndian)
    {
        long pos = stream.Position;
        try
        {
            stream.Seek(tiffStart + valOrOffset, SeekOrigin.Begin);
            byte[] buf = new byte[8];
            if (stream.Read(buf, 0, 8) == 8)
            {
                uint num = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4)) : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0, 4));
                uint den = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)) : BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));
                return (num, den);
            }
            return (0, 0);
        }
        finally
        {
            stream.Seek(pos, SeekOrigin.Begin);
        }
    }

    private static string? ReadStringAt(Stream stream, long tiffStart, uint valOrOffset, uint count)
    {
        if (count == 0 || count > 1024) return null;
        long pos = stream.Position;
        try
        {
            stream.Seek(tiffStart + valOrOffset, SeekOrigin.Begin);
            byte[] buf = new byte[count];
            if (stream.Read(buf, 0, buf.Length) == buf.Length)
            {
                return Encoding.ASCII.GetString(buf).Trim('\0', ' ').Trim();
            }
            return null;
        }
        finally
        {
            stream.Seek(pos, SeekOrigin.Begin);
        }
    }

    private static DateTime? ParseExifDateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt2))
            return dt2;
        return null;
    }

    private static AstroMediaMetadata Merge(AstroMediaMetadata primary, AstroMediaMetadata secondary)
    {
        return new AstroMediaMetadata(
            ExposureTimeSeconds: primary.ExposureTimeSeconds ?? secondary.ExposureTimeSeconds,
            ExposureText: primary.ExposureText ?? secondary.ExposureText,
            ShutterSpeedText: primary.ShutterSpeedText ?? secondary.ShutterSpeedText,
            Iso: primary.Iso ?? secondary.Iso,
            Gain: primary.Gain ?? secondary.Gain,
            Camera: primary.Camera ?? secondary.Camera,
            Telescope: primary.Telescope ?? secondary.Telescope,
            Filter: primary.Filter ?? secondary.Filter,
            TimestampUtc: primary.TimestampUtc ?? secondary.TimestampUtc);
    }
}