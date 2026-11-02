using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Codecs;

/// <summary>
/// Native FITS (Flexible Image Transport System) reader, writer, and header inspector.
/// Supports 8-bit, 16-bit signed/BZERO, and 32-bit float FITS images.
/// </summary>
public static class FitsFile
{
    public const int BlockSize = 2880;
    public const int CardSize = 80;

    public sealed class FitsMetadata
    {
        public bool Simple { get; set; }
        public int BitPix { get; set; }
        public int NAxis { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Planes { get; set; } = 1;
        public double BZero { get; set; }
        public double BScale { get; set; } = 1.0;
        public string BayerPat { get; set; } = string.Empty;
        public long DataStartOffset { get; set; }
        public List<string> Cards { get; } = [];

        public string FormatDetailedReport(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FITS File Details: {Path.GetFileName(filePath)}");
            sb.AppendLine(new string('=', 56));
            sb.AppendLine($"BITPIX     : {BitPix}");
            sb.AppendLine($"NAXIS      : {NAxis}");
            sb.AppendLine($"Dimensions : {Width} x {Height} x {Planes}");
            sb.AppendLine($"BZERO      : {BZero}");
            sb.AppendLine($"BSCALE     : {BScale}");
            if (!string.IsNullOrEmpty(BayerPat)) sb.AppendLine($"BAYERPAT   : {BayerPat}");
            sb.AppendLine(new string('-', 56));
            foreach (string c in Cards) sb.AppendLine(c);
            return sb.ToString();
        }
    }

    public static FitsMetadata ReadMetadata(Stream stream)
    {
        var meta = new FitsMetadata();
        byte[] block = new byte[BlockSize];
        bool endFound = false;

        while (!endFound && stream.Read(block, 0, BlockSize) == BlockSize)
        {
            for (int offset = 0; offset < BlockSize; offset += CardSize)
            {
                string card = Encoding.ASCII.GetString(block, offset, CardSize);
                string trimmed = card.TrimEnd();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    meta.Cards.Add(trimmed);
                }

                string key = card[..Math.Min(8, card.Length)].Trim();
                if (key == "END")
                {
                    endFound = true;
                    break;
                }

                int eqIdx = card.IndexOf('=');
                if (eqIdx < 0) continue;

                string valPart = card[(eqIdx + 1)..];
                int slashIdx = valPart.IndexOf('/');
                if (slashIdx >= 0) valPart = valPart[..slashIdx];
                valPart = valPart.Trim().Trim('\'').Trim();

                switch (key)
                {
                    case "SIMPLE": meta.Simple = valPart.Equals("T", StringComparison.OrdinalIgnoreCase); break;
                    case "BITPIX": if (int.TryParse(valPart, out int bp)) meta.BitPix = bp; break;
                    case "NAXIS": if (int.TryParse(valPart, out int na)) meta.NAxis = na; break;
                    case "NAXIS1": if (int.TryParse(valPart, out int n1)) meta.Width = n1; break;
                    case "NAXIS2": if (int.TryParse(valPart, out int n2)) meta.Height = n2; break;
                    case "NAXIS3": if (int.TryParse(valPart, out int n3)) meta.Planes = Math.Max(1, n3); break;
                    case "BZERO": if (double.TryParse(valPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double bz)) meta.BZero = bz; break;
                    case "BSCALE": if (double.TryParse(valPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double bs)) meta.BScale = bs; break;
                    case "BAYERPAT": meta.BayerPat = valPart; break;
                }
            }
        }

        meta.DataStartOffset = stream.Position;
        return meta;
    }

    public static AstroFrame ReadFrame(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var meta = ReadMetadata(fs);
        if (!meta.Simple || meta.Width <= 0 || meta.Height <= 0)
        {
            throw new InvalidDataException($"Invalid FITS header in '{filePath}'.");
        }

        int channels = meta.Planes >= 3 ? 3 : 1;
        var frame = new AstroFrame(meta.Width, meta.Height, channels, Math.Abs(meta.BitPix))
        {
            IsBayerRaw = !string.IsNullOrEmpty(meta.BayerPat)
        };

        int pixelCount = meta.Width * meta.Height;
        switch (meta.BitPix)
        {
            case 8:
                Read8BitPlanes(fs, frame, pixelCount, channels);
                break;

            case 16:
                Read16BitPlanes(fs, frame, meta, pixelCount, channels);
                break;

            default:
                throw new NotSupportedException($"Unsupported FITS BITPIX={meta.BitPix}. Only 8 and 16-bit are currently supported.");
        }

        return frame;
    }

    /// <summary>
    /// Reads only a cropped sub-window around (centerX, centerY) from a FITS file without reading or allocating the full sensor frame.
    /// </summary>
    public static unsafe AstroFrame ReadFrameCropped(
        string filePath,
        int centerX,
        int centerY,
        int cropWidth,
        int cropHeight,
        int offsetX = 0,
        int offsetY = 0,
        bool protectBayerEvenSteps = true)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var meta = ReadMetadata(fs);
        if (!meta.Simple || meta.Width <= 0 || meta.Height <= 0)
        {
            throw new InvalidDataException($"Invalid FITS header in '{filePath}'.");
        }

        cropWidth = Math.Max(16, cropWidth);
        cropHeight = Math.Max(16, cropHeight);

        int startX = centerX - (cropWidth / 2) - offsetX;
        int startY = centerY - (cropHeight / 2) - offsetY;

        int channels = meta.Planes >= 3 ? 3 : 1;
        bool isBayer = !string.IsNullOrEmpty(meta.BayerPat);
        if (protectBayerEvenSteps && (isBayer || channels == 1))
        {
            startX &= ~1;
            startY &= ~1;
        }

        var dst = new AstroFrame(cropWidth, cropHeight, channels, Math.Abs(meta.BitPix))
        {
            IsBayerRaw = isBayer,
            DetectedCentroidX = cropWidth / 2,
            DetectedCentroidY = cropHeight / 2
        };

        int srcW = meta.Width;
        int srcH = meta.Height;

        int copyY0 = Math.Max(0, -startY);
        int copyY1 = Math.Min(cropHeight, srcH - startY);
        int copyX0 = Math.Max(0, -startX);
        int copyX1 = Math.Min(cropWidth, srcW - startX);

        int copyW = copyX1 - copyX0;
        int copyH = copyY1 - copyY0;
        if (copyW <= 0 || copyH <= 0)
        {
            return dst;
        }

        int srcStartX = startX + copyX0;
        int srcStartY = startY + copyY0;
        int bytesPerSample = meta.BitPix switch
        {
            8 => 1,
            16 => 2,
            _ => throw new NotSupportedException($"Unsupported FITS BITPIX={meta.BitPix}. Only 8 and 16-bit are currently supported.")
        };

        long planeBytes = (long)srcW * srcH * bytesPerSample;
        long rowBytes = (long)srcW * bytesPerSample;
        int subBlockBytes = (int)((long)(copyH - 1) * rowBytes + (long)copyW * bytesPerSample);
        byte[] rented = ArrayPool<byte>.Shared.Rent(subBlockBytes);

        try
        {
            bool isStandardU16 = Math.Abs(meta.BZero - 32768.0) < 1e-6 && Math.Abs(meta.BScale - 1.0) < 1e-6;
            double bZero = meta.BZero;
            double bScale = meta.BScale;

            fixed (byte* pBuf = rented)
            fixed (ushort* pDst = dst.Data)
            {
                for (int c = 0; c < channels; c++)
                {
                    long offset = meta.DataStartOffset + c * planeBytes + srcStartY * rowBytes + (long)srcStartX * bytesPerSample;
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.ReadExactly(rented.AsSpan(0, subBlockBytes));

                    for (int r = 0; r < copyH; r++)
                    {
                        int dstY = copyY0 + r;
                        byte* pRow = pBuf + r * rowBytes;

                        if (bytesPerSample == 1)
                        {
                            if (channels == 1)
                            {
                                ushort* dstRow = pDst + dstY * cropWidth + copyX0;
                                for (int x = 0; x < copyW; x++)
                                {
                                    dstRow[x] = (ushort)(pRow[x] * 257);
                                }
                            }
                            else
                            {
                                ushort* dstRow = pDst + (dstY * cropWidth + copyX0) * 3 + c;
                                for (int x = 0; x < copyW; x++)
                                {
                                    dstRow[x * 3] = (ushort)(pRow[x] * 257);
                                }
                            }
                        }
                        else
                        {
                            short* pRow16 = (short*)pRow;
                            if (channels == 1)
                            {
                                ushort* dstRow = pDst + dstY * cropWidth + copyX0;
                                if (isStandardU16)
                                {
                                    for (int x = 0; x < copyW; x++)
                                    {
                                        short s = BinaryPrimitives.ReverseEndianness(pRow16[x]);
                                        dstRow[x] = (ushort)(s + 32768);
                                    }
                                }
                                else
                                {
                                    for (int x = 0; x < copyW; x++)
                                    {
                                        short s = BinaryPrimitives.ReverseEndianness(pRow16[x]);
                                        dstRow[x] = (ushort)Math.Clamp(bZero + bScale * s, 0, 65535);
                                    }
                                }
                            }
                            else
                            {
                                ushort* dstRow = pDst + (dstY * cropWidth + copyX0) * 3 + c;
                                if (isStandardU16)
                                {
                                    for (int x = 0; x < copyW; x++)
                                    {
                                        short s = BinaryPrimitives.ReverseEndianness(pRow16[x]);
                                        dstRow[x * 3] = (ushort)(s + 32768);
                                    }
                                }
                                else
                                {
                                    for (int x = 0; x < copyW; x++)
                                    {
                                        short s = BinaryPrimitives.ReverseEndianness(pRow16[x]);
                                        dstRow[x * 3] = (ushort)Math.Clamp(bZero + bScale * s, 0, 65535);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return dst;
    }

    private static unsafe void Read8BitPlanes(FileStream fs, AstroFrame frame, int pixelCount, int channels)
    {
        int totalBytes = pixelCount * channels;
        byte[] buf = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            fs.ReadExactly(buf.AsSpan(0, totalBytes));
            fixed (byte* pSrc = buf)
            fixed (ushort* pDst = frame.Data)
            {
                if (channels == 1)
                {
                    for (int i = 0; i < pixelCount; i++) pDst[i] = (ushort)(pSrc[i] * 257);
                }
                else
                {
                    // FITS stores planar R, G, B
                    byte* pR = pSrc;
                    byte* pG = pSrc + pixelCount;
                    byte* pB = pSrc + pixelCount * 2;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int d = i * 3;
                        pDst[d] = (ushort)(pR[i] * 257);
                        pDst[d + 1] = (ushort)(pG[i] * 257);
                        pDst[d + 2] = (ushort)(pB[i] * 257);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static unsafe void Read16BitPlanes(FileStream fs, AstroFrame frame, FitsMetadata meta, int pixelCount, int channels)
    {
        int totalBytes = pixelCount * channels * 2;
        byte[] buf = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            fs.ReadExactly(buf.AsSpan(0, totalBytes));
            bool isStandardU16 = Math.Abs(meta.BZero - 32768.0) < 1e-6 && Math.Abs(meta.BScale - 1.0) < 1e-6;
            double bZero = meta.BZero;
            double bScale = meta.BScale;

            fixed (byte* pSrcBytes = buf)
            fixed (ushort* pDst = frame.Data)
            {
                short* pSrc = (short*)pSrcBytes;
                if (channels == 1)
                {
                    if (isStandardU16)
                    {
                        for (int i = 0; i < pixelCount; i++)
                        {
                            short s = BinaryPrimitives.ReverseEndianness(pSrc[i]);
                            pDst[i] = (ushort)(s + 32768);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < pixelCount; i++)
                        {
                            short s = BinaryPrimitives.ReverseEndianness(pSrc[i]);
                            pDst[i] = (ushort)Math.Clamp(bZero + bScale * s, 0, 65535);
                        }
                    }
                }
                else
                {
                    for (int c = 0; c < channels; c++)
                    {
                        short* pPlane = pSrc + c * pixelCount;
                        if (isStandardU16)
                        {
                            for (int i = 0; i < pixelCount; i++)
                            {
                                short s = BinaryPrimitives.ReverseEndianness(pPlane[i]);
                                pDst[i * 3 + c] = (ushort)(s + 32768);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < pixelCount; i++)
                            {
                                short s = BinaryPrimitives.ReverseEndianness(pPlane[i]);
                                pDst[i * 3 + c] = (ushort)Math.Clamp(bZero + bScale * s, 0, 65535);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static unsafe void WriteFrame(string outputPath, AstroFrame frame)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        var cards = new List<string>
        {
            FormatCard("SIMPLE", "T", "Standard FITS format"),
            FormatCard("BITPIX", "16", "16-bit unsigned via BZERO=32768"),
            FormatCard("NAXIS", frame.Channels == 3 ? "3" : "2", "Number of axes"),
            FormatCard("NAXIS1", frame.Width.ToString(CultureInfo.InvariantCulture), "Width"),
            FormatCard("NAXIS2", frame.Height.ToString(CultureInfo.InvariantCulture), "Height")
        };
        if (frame.Channels == 3)
        {
            cards.Add(FormatCard("NAXIS3", "3", "RGB planes"));
        }
        cards.Add(FormatCard("BZERO", "32768", "Unsigned 16-bit offset"));
        cards.Add(FormatCard("BSCALE", "1", "Default scale"));
        cards.Add(FormatCard("SWCREATE", "'New-CassiniFlow .NET 10'", "Created by New-CassiniFlow"));
        cards.Add("END".PadRight(CardSize, ' '));

        Span<byte> hdrBuf = stackalloc byte[BlockSize];
        hdrBuf.Fill((byte)' ');
        for (int i = 0; i < cards.Count; i++)
        {
            Encoding.ASCII.GetBytes(cards[i].AsSpan(0, CardSize), hdrBuf.Slice(i * CardSize, CardSize));
        }
        fs.Write(hdrBuf);

        int pixelCount = frame.Width * frame.Height;
        int totalSamples = pixelCount * frame.Channels;
        int totalBytes = totalSamples * 2;
        byte[] dataBuf = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            fixed (ushort* pSrc = frame.Data)
            fixed (byte* pDstBytes = dataBuf)
            {
                short* pDst = (short*)pDstBytes;
                if (frame.Channels == 1)
                {
                    for (int i = 0; i < pixelCount; i++)
                    {
                        pDst[i] = BinaryPrimitives.ReverseEndianness((short)(pSrc[i] - 32768));
                    }
                }
                else
                {
                    for (int c = 0; c < 3; c++)
                    {
                        short* pPlane = pDst + c * pixelCount;
                        for (int i = 0; i < pixelCount; i++)
                        {
                            pPlane[i] = BinaryPrimitives.ReverseEndianness((short)(pSrc[i * 3 + c] - 32768));
                        }
                    }
                }
            }

            fs.Write(dataBuf.AsSpan(0, totalBytes));
            int remainder = totalBytes % BlockSize;
            if (remainder != 0)
            {
                Span<byte> pad = stackalloc byte[BlockSize - remainder];
                pad.Clear();
                fs.Write(pad);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dataBuf);
        }
    }

    private static string FormatCard(string key, string value, string comment)
    {
        string left = $"{key,-8}= {value,20} / {comment}";
        return left.Length >= CardSize ? left[..CardSize] : left.PadRight(CardSize, ' ');
    }
}
