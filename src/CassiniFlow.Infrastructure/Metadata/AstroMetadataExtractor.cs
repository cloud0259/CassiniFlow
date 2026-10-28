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
        // Lecteur d'en-tête EXIF léger pour JPEG (APP1) et TIFF (IFD0)
        byte[] header = new byte[4096];
        int read = stream.Read(header, 0, header.Length);
        if (read < 16) return AstroMediaMetadata.Empty;

        // TIFF header check (II = Little Endian, MM = Big Endian)
        if ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00) ||
            (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A))
        {
            return ParseTiffExif(header, 0, read);
        }

        // JPEG check (SOI marker 0xFFD8)
        if (header[0] == 0xFF && header[1] == 0xD8)
        {
            int idx = 2;
            while (idx < read - 4)
            {
                if (header[idx] != 0xFF) break;
                byte marker = header[idx + 1];
                int len = (header[idx + 2] << 8) | header[idx + 3];

                if (marker == 0xE1 && idx + 10 < read) // APP1 marker
                {
                    // Check "Exif\0\0"
                    if (header[idx + 4] == 0x45 && header[idx + 5] == 0x78 && header[idx + 6] == 0x69 && header[idx + 7] == 0x66 &&
                        header[idx + 8] == 0x00 && header[idx + 9] == 0x00)
                    {
                        int tiffStart = idx + 10;
                        return ParseTiffExif(header, tiffStart, read - tiffStart);
                    }
                }

                idx += 2 + len;
            }
        }

        return AstroMediaMetadata.Empty;
    }

    private static AstroMediaMetadata ParseTiffExif(byte[] data, int start, int length)
    {
        if (length < 8) return AstroMediaMetadata.Empty;
        bool littleEndian = data[start] == 0x49 && data[start + 1] == 0x49;

        ushort ReadU16(int offset)
        {
            int o = start + offset;
            if (o + 1 >= data.Length) return 0;
            return littleEndian
                ? (ushort)(data[o] | (data[o + 1] << 8))
                : (ushort)((data[o] << 8) | data[o + 1]);
        }

        uint ReadU32(int offset)
        {
            int o = start + offset;
            if (o + 3 >= data.Length) return 0;
            return littleEndian
                ? (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24))
                : (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);
        }

        uint ifdOffset = ReadU32(4);
        if (ifdOffset == 0 || ifdOffset >= length) return AstroMediaMetadata.Empty;

        double? expTime = null;
        int? iso = null;
        string? camera = null;

        void ReadIfd(uint offset)
        {
            if (offset == 0 || offset + 2 >= length) return;
            ushort count = ReadU16((int)offset);
            int cur = (int)offset + 2;

            for (int i = 0; i < count && cur + 12 <= length; i++, cur += 12)
            {
                ushort tag = ReadU16(cur);
                ushort type = ReadU16(cur + 2);
                uint valCount = ReadU32(cur + 4);
                uint valOffset = ReadU32(cur + 8);

                switch (tag)
                {
                    case 0x829A: // ExposureTime (Rational)
                        if (valOffset + 8 <= length)
                        {
                            uint num = ReadU32((int)valOffset);
                            uint den = ReadU32((int)valOffset + 4);
                            if (den > 0) expTime = (double)num / den;
                        }
                        break;
                    case 0x8827: // ISOSpeedRatings (Short)
                        iso = (int)(valOffset & 0xFFFF);
                        break;
                    case 0x0110: // Model (ASCII)
                        if (valOffset + Math.Min(32, valCount) <= length)
                        {
                            int strLen = (int)Math.Min(64, valCount);
                            camera = Encoding.ASCII.GetString(data, start + (int)valOffset, strLen).Trim('\0', ' ');
                        }
                        break;
                    case 0x8769: // ExifIFDPointer
                        ReadIfd(valOffset);
                        break;
                }
            }
        }

        ReadIfd(ifdOffset);

        string? shutter = expTime.HasValue ? AstroMediaMetadata.FormatShutterSpeed(expTime.Value) : null;
        string? expText = expTime.HasValue ? AstroMediaMetadata.FormatExposure(expTime.Value) : null;

        return new AstroMediaMetadata(
            ExposureTimeSeconds: expTime,
            ExposureText: expText,
            ShutterSpeedText: shutter,
            Iso: iso,
            Camera: camera);
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
