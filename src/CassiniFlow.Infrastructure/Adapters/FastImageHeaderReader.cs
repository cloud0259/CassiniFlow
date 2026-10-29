using System;
using System.Buffers.Binary;
using System.IO;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Metadata;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Ultra-fast image header reader that extracts dimensions, bit-depth and channels
/// directly from file headers (PNG, JPEG, TIFF/DNG, BMP) without decoding pixel data.
/// Allows loading hundreds of images instantaneously without loading pixel buffers.
/// </summary>
public static class FastImageHeaderReader
{
    public static bool TryInspect(string filePath, out MediaFileSummary? summary)
    {
        summary = null;
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length < 16) return false;

            string ext = fi.Extension.ToLowerInvariant();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);

            int width = 0, height = 0, depth = 8, channels = 3;
            bool ok = ext switch
            {
                ".png" => TryReadPng(fs, out width, out height, out depth, out channels),
                ".jpg" or ".jpeg" => TryReadJpeg(fs, out width, out height, out depth, out channels),
                ".bmp" => TryReadBmp(fs, out width, out height, out depth, out channels),
                ".tif" or ".tiff" or ".dng" => TryReadTiff(fs, out width, out height, out depth, out channels),
                _ => false
            };

            if (!ok || width <= 0 || height <= 0) return false;

            var astroMeta = AstroMetadataExtractor.Extract(filePath);
            summary = new MediaFileSummary(
                filePath,
                $"{ext.TrimStart('.').ToUpperInvariant()} {depth}-bit",
                1,
                width,
                height,
                1.0,
                fi.Length,
                $"Fichier Image : {fi.Name} ({width}×{height}, {depth}-bit, Canaux={channels})",
                astroMeta);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadPng(Stream stream, out int width, out int height, out int depth, out int channels)
    {
        width = 0; height = 0; depth = 8; channels = 3;
        Span<byte> header = stackalloc byte[32];
        int read = stream.Read(header);
        if (read < 26) return false;

        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47 ||
            header[4] != 0x0D || header[5] != 0x0A || header[6] != 0x1A || header[7] != 0x0A)
            return false;

        // Chunk type must be IHDR
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
            return false;

        width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
        depth = header[24];
        byte colorType = header[25];

        channels = colorType switch
        {
            0 => 1, // Grayscale
            2 => 3, // RGB
            3 => 3, // Palette index
            4 => 2, // Grayscale + Alpha
            6 => 4, // RGBA
            _ => 3
        };

        return width > 0 && height > 0;
    }

    public static bool TryReadJpeg(Stream stream, out int width, out int height, out int depth, out int channels)
    {
        width = 0; height = 0; depth = 8; channels = 3;
        Span<byte> buf = stackalloc byte[4];
        if (stream.Read(buf.Slice(0, 2)) < 2) return false;
        if (buf[0] != 0xFF || buf[1] != 0xD8) return false; // SOI marker

        Span<byte> markerBuf = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            int b = stream.ReadByte();
            if (b == -1) break;
            if (b != 0xFF) continue;

            int marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte();
            if (marker == -1 || marker == 0xD9 || marker == 0xDA) break; // EOI or Start of Scan

            // Length of marker segment
            if (stream.Read(markerBuf.Slice(0, 2)) < 2) break;
            ushort segLen = BinaryPrimitives.ReadUInt16BigEndian(markerBuf.Slice(0, 2));
            if (segLen < 2) break;

            // SOF0 (0xC0), SOF1 (0xC1), SOF2 (0xC2), etc.
            if ((marker >= 0xC0 && marker <= 0xC3) || (marker >= 0xC5 && marker <= 0xC7) ||
                (marker >= 0xC9 && marker <= 0xCB) || (marker >= 0xCD && marker <= 0xCF))
            {
                Span<byte> sof = stackalloc byte[6];
                if (stream.Read(sof) < 6) break;
                depth = sof[0];
                height = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(sof.Slice(3, 2));
                channels = sof[5];
                return width > 0 && height > 0;
            }
            else
            {
                // Skip segment payload
                long toSkip = segLen - 2;
                if (stream.CanSeek)
                {
                    stream.Seek(toSkip, SeekOrigin.Current);
                }
                else
                {
                    for (long s = 0; s < toSkip; s++) stream.ReadByte();
                }
            }
        }

        return false;
    }

    public static bool TryReadBmp(Stream stream, out int width, out int height, out int depth, out int channels)
    {
        width = 0; height = 0; depth = 8; channels = 3;
        Span<byte> header = stackalloc byte[30];
        int read = stream.Read(header);
        if (read < 28) return false;

        // BM signature
        if (header[0] != 0x42 || header[1] != 0x4D) return false;

        width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(18, 4));
        int h = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(22, 4));
        height = Math.Abs(h);

        ushort bpp = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
        if (bpp == 8) { depth = 8; channels = 1; }
        else if (bpp == 16) { depth = 16; channels = 1; }
        else if (bpp == 24) { depth = 8; channels = 3; }
        else if (bpp == 32) { depth = 8; channels = 4; }
        else { depth = bpp; channels = 3; }

        return width > 0 && height > 0;
    }

    public static bool TryReadTiff(Stream stream, out int width, out int height, out int depth, out int channels)
    {
        width = 0; height = 0; depth = 8; channels = 3;
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) < 8) return false;

        bool isLittleEndian;
        if (header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)
            isLittleEndian = true;
        else if (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A)
            isLittleEndian = false;
        else
            return false;

        uint ifdOffset = isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));

        if (ifdOffset < 8 || ifdOffset >= stream.Length) return false;
        stream.Seek(ifdOffset, SeekOrigin.Begin);

        Span<byte> countBuf = stackalloc byte[2];
        if (stream.Read(countBuf) < 2) return false;
        ushort numTags = isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(countBuf)
            : BinaryPrimitives.ReadUInt16BigEndian(countBuf);

        if (numTags > 500) return false; // Sanity check

        Span<byte> tagBuf = stackalloc byte[12];
        bool foundW = false, foundH = false;

        for (int i = 0; i < numTags; i++)
        {
            if (stream.Read(tagBuf) < 12) break;
            ushort tagId = isLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(tagBuf.Slice(0, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(tagBuf.Slice(0, 2));
            ushort tagType = isLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(tagBuf.Slice(2, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(tagBuf.Slice(2, 2));

            // ImageWidth (256 / 0x0100)
            if (tagId == 0x0100)
            {
                width = ReadTiffUint(tagBuf.Slice(8, 4), tagType, isLittleEndian);
                foundW = true;
            }
            // ImageLength (257 / 0x0101)
            else if (tagId == 0x0101)
            {
                height = ReadTiffUint(tagBuf.Slice(8, 4), tagType, isLittleEndian);
                foundH = true;
            }
            // BitsPerSample (258 / 0x0102)
            else if (tagId == 0x0102)
            {
                depth = ReadTiffUint(tagBuf.Slice(8, 4), tagType, isLittleEndian);
                if (depth == 0) depth = 8;
            }
            // SamplesPerPixel (277 / 0x0115)
            else if (tagId == 0x0115)
            {
                channels = ReadTiffUint(tagBuf.Slice(8, 4), tagType, isLittleEndian);
                if (channels == 0) channels = 3;
            }

            if (foundW && foundH && depth > 0 && channels > 0)
                break;
        }

        return width > 0 && height > 0;
    }

    private static int ReadTiffUint(ReadOnlySpan<byte> valBuf, ushort type, bool isLittleEndian)
    {
        // Type 3 = SHORT (uint16), Type 4 = LONG (uint32)
        if (type == 3)
        {
            return isLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(valBuf.Slice(0, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(valBuf.Slice(0, 2));
        }
        if (type == 4)
        {
            return (int)(isLittleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(valBuf)
                : BinaryPrimitives.ReadUInt32BigEndian(valBuf));
        }
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(valBuf.Slice(0, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(valBuf.Slice(0, 2));
    }
}
