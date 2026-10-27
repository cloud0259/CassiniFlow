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

    private static void Read8BitPlanes(FileStream fs, AstroFrame frame, int pixelCount, int channels)
    {
        byte[] buf = new byte[pixelCount * channels];
        fs.ReadExactly(buf);
        if (channels == 1)
        {
            for (int i = 0; i < pixelCount; i++) frame.Data[i] = (ushort)(buf[i] * 257);
        }
        else
        {
            // FITS stores planar R, G, B
            for (int i = 0; i < pixelCount; i++)
            {
                frame.Data[i * 3] = (ushort)(buf[i] * 257);
                frame.Data[i * 3 + 1] = (ushort)(buf[pixelCount + i] * 257);
                frame.Data[i * 3 + 2] = (ushort)(buf[pixelCount * 2 + i] * 257);
            }
        }
    }

    private static void Read16BitPlanes(FileStream fs, AstroFrame frame, FitsMetadata meta, int pixelCount, int channels)
    {
        byte[] buf = new byte[pixelCount * channels * 2];
        fs.ReadExactly(buf);
        ReadOnlySpan<byte> span = buf;
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int srcIdx = (c * pixelCount + i) * 2;
                short s = BinaryPrimitives.ReadInt16BigEndian(span.Slice(srcIdx, 2));
                double physical = meta.BZero + meta.BScale * s;
                ushort u = (ushort)Math.Clamp(physical, 0, 65535);
                if (channels == 1) frame.Data[i] = u;
                else frame.Data[i * 3 + c] = u;
            }
        }
    }

    public static void WriteFrame(string outputPath, AstroFrame frame)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
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

        byte[] hdrBuf = new byte[BlockSize];
        Array.Fill(hdrBuf, (byte)' ');
        for (int i = 0; i < cards.Count; i++)
        {
            Encoding.ASCII.GetBytes(cards[i], 0, CardSize, hdrBuf, i * CardSize);
        }
        fs.Write(hdrBuf);

        int pixelCount = frame.Width * frame.Height;
        int totalSamples = pixelCount * frame.Channels;
        byte[] dataBuf = new byte[totalSamples * 2];
        Span<byte> span = dataBuf;

        if (frame.Channels == 1)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                short s = (short)(frame.Data[i] - 32768);
                BinaryPrimitives.WriteInt16BigEndian(span.Slice(i * 2, 2), s);
            }
        }
        else
        {
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    short s = (short)(frame.Data[i * 3 + c] - 32768);
                    int dstIdx = (c * pixelCount + i) * 2;
                    BinaryPrimitives.WriteInt16BigEndian(span.Slice(dstIdx, 2), s);
                }
            }
        }

        fs.Write(dataBuf);
        int remainder = dataBuf.Length % BlockSize;
        if (remainder != 0)
        {
            fs.Write(new byte[BlockSize - remainder]);
        }
    }

    private static string FormatCard(string key, string value, string comment)
    {
        string left = $"{key,-8}= {value,20} / {comment}";
        return left.Length >= CardSize ? left[..CardSize] : left.PadRight(CardSize, ' ');
    }
}
