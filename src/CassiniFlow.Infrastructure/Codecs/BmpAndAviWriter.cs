using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Codecs;

/// <summary>
/// Cross-platform uncompressed 24-bit BMP and AVI (RIFF DIB) reader/writer.
/// </summary>
public static class BmpAndAviWriter
{
    public static unsafe void WriteBmp(string outputPath, AstroFrame frame)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        int width = frame.Width;
        int height = frame.Height;
        int rowStride = (width * 3 + 3) & ~3;
        int imageSize = rowStride * height;
        int fileSize = 54 + imageSize;

        Span<byte> hdr = stackalloc byte[54];
        hdr.Clear();
        hdr[0] = (byte)'B';
        hdr[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(2, 4), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(22, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.Slice(28, 2), 24);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(34, 4), imageSize);
        fs.Write(hdr);

        byte[] imgBuf = ArrayPool<byte>.Shared.Rent(imageSize);
        try
        {
            Array.Clear(imgBuf, 0, imageSize);
            int ch = frame.Channels;
            fixed (ushort* pSrc = frame.Data)
            fixed (byte* pDst = imgBuf)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* dstRow = pDst + (height - 1 - y) * rowStride;
                    if (ch == 1)
                    {
                        ushort* srcRow = pSrc + y * width;
                        for (int x = 0; x < width; x++)
                        {
                            byte v = (byte)(srcRow[x] >> 8);
                            int d = x * 3;
                            dstRow[d] = v;
                            dstRow[d + 1] = v;
                            dstRow[d + 2] = v;
                        }
                    }
                    else
                    {
                        ushort* srcRow = pSrc + y * width * 3;
                        for (int x = 0; x < width; x++)
                        {
                            int s = x * 3;
                            dstRow[s] = (byte)(srcRow[s + 2] >> 8);     // B
                            dstRow[s + 1] = (byte)(srcRow[s + 1] >> 8); // G
                            dstRow[s + 2] = (byte)(srcRow[s] >> 8);     // R
                        }
                    }
                }
            }
            fs.Write(imgBuf.AsSpan(0, imageSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(imgBuf);
        }
    }

    public static unsafe AstroFrame ReadBmp(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        Span<byte> hdr = stackalloc byte[54];
        fs.ReadExactly(hdr);
        if (hdr[0] != 'B' || hdr[1] != 'M')
            throw new InvalidDataException($"Incorrect BMP signature in '{filePath}'.");

        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(10, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(18, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(22, 4));
        short bpp = BinaryPrimitives.ReadInt16LittleEndian(hdr.Slice(28, 2));

        bool topDown = height < 0;
        height = Math.Abs(height);
        int bytesPerPixel = Math.Max(1, bpp / 8);
        int rowStride = (width * bytesPerPixel + 3) & ~3;
        int totalBytes = rowStride * height;

        fs.Seek(dataOffset, SeekOrigin.Begin);
        int channels = bytesPerPixel >= 3 ? 3 : 1;
        var frame = new AstroFrame(width, height, channels, 8);
        byte[] buf = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            fs.ReadExactly(buf.AsSpan(0, totalBytes));
            fixed (byte* pSrc = buf)
            fixed (ushort* pDst = frame.Data)
            {
                for (int i = 0; i < height; i++)
                {
                    int y = topDown ? i : (height - 1 - i);
                    byte* srcRow = pSrc + i * rowStride;
                    if (channels == 1)
                    {
                        ushort* dstRow = pDst + y * width;
                        for (int x = 0; x < width; x++)
                        {
                            dstRow[x] = (ushort)(srcRow[x] * 257);
                        }
                    }
                    else
                    {
                        ushort* dstRow = pDst + y * width * 3;
                        for (int x = 0; x < width; x++)
                        {
                            int s = x * bytesPerPixel;
                            int d = x * 3;
                            dstRow[d] = (ushort)(srcRow[s + 2] * 257);
                            dstRow[d + 1] = (ushort)(srcRow[s + 1] * 257);
                            dstRow[d + 2] = (ushort)(srcRow[s] * 257);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return frame;
    }

    public static unsafe AstroFrame ReadBmpCropped(
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
        Span<byte> hdr = stackalloc byte[54];
        fs.ReadExactly(hdr);
        if (hdr[0] != 'B' || hdr[1] != 'M')
            throw new InvalidDataException($"Incorrect BMP signature in '{filePath}'.");

        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(10, 4));
        int srcW = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(18, 4));
        int srcH = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(22, 4));
        short bpp = BinaryPrimitives.ReadInt16LittleEndian(hdr.Slice(28, 2));

        bool topDown = srcH < 0;
        srcH = Math.Abs(srcH);
        int bytesPerPixel = Math.Max(1, bpp / 8);
        int rowStride = (srcW * bytesPerPixel + 3) & ~3;
        int channels = bytesPerPixel >= 3 ? 3 : 1;

        cropWidth = Math.Max(16, cropWidth);
        cropHeight = Math.Max(16, cropHeight);
        int startX = centerX - (cropWidth / 2) - offsetX;
        int startY = centerY - (cropHeight / 2) - offsetY;
        if (protectBayerEvenSteps && channels == 1)
        {
            startX &= ~1;
            startY &= ~1;
        }

        var dst = new AstroFrame(cropWidth, cropHeight, channels, 8)
        {
            DetectedCentroidX = cropWidth / 2,
            DetectedCentroidY = cropHeight / 2
        };

        int copyY0 = Math.Max(0, -startY);
        int copyY1 = Math.Min(cropHeight, srcH - startY);
        int copyX0 = Math.Max(0, -startX);
        int copyX1 = Math.Min(cropWidth, srcW - startX);
        int copyW = copyX1 - copyX0;
        int copyH = copyY1 - copyY0;
        if (copyW <= 0 || copyH <= 0) return dst;

        int srcStartX = startX + copyX0;
        int srcStartY = startY + copyY0;
        int firstFileRow = topDown ? srcStartY : (srcH - (srcStartY + copyH));
        int subBytes = copyH * rowStride;
        byte[] buf = ArrayPool<byte>.Shared.Rent(subBytes);
        try
        {
            fs.Seek(dataOffset + (long)firstFileRow * rowStride, SeekOrigin.Begin);
            fs.ReadExactly(buf.AsSpan(0, subBytes));
            fixed (byte* pSrc = buf)
            fixed (ushort* pDst = dst.Data)
            {
                for (int r = 0; r < copyH; r++)
                {
                    int dstY = copyY0 + r;
                    int bufRow = topDown ? r : (copyH - 1 - r);
                    byte* srcRow = pSrc + bufRow * rowStride + srcStartX * bytesPerPixel;
                    if (channels == 1)
                    {
                        ushort* dstRow = pDst + dstY * cropWidth + copyX0;
                        for (int x = 0; x < copyW; x++)
                        {
                            dstRow[x] = (ushort)(srcRow[x] * 257);
                        }
                    }
                    else
                    {
                        ushort* dstRow = pDst + (dstY * cropWidth + copyX0) * 3;
                        for (int x = 0; x < copyW; x++)
                        {
                            int s = x * bytesPerPixel;
                            int d = x * 3;
                            dstRow[d] = (ushort)(srcRow[s + 2] * 257);
                            dstRow[d + 1] = (ushort)(srcRow[s + 1] * 257);
                            dstRow[d + 2] = (ushort)(srcRow[s] * 257);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return dst;
    }

    /// <summary>
    /// Fast header inspection for standard AVI RIFF files to extract dimensions, frame count, and FPS without external codecs.
    /// </summary>
    public static bool TryReadAviHeader(string filePath, out int width, out int height, out int frameCount, out double fps)
    {
        width = 0;
        height = 0;
        frameCount = 0;
        fps = 30.0;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 64) return false;
            Span<byte> buf = stackalloc byte[64];
            int read = fs.Read(buf);
            if (read < 64) return false;
            if (buf[0] != 'R' || buf[1] != 'I' || buf[2] != 'F' || buf[3] != 'F' ||
                buf[8] != 'A' || buf[9] != 'V' || buf[10] != 'I' || buf[11] != ' ')
                return false;

            fs.Seek(12, SeekOrigin.Begin);
            int searchLength = (int)Math.Min(4096L, fs.Length - 12);
            byte[] searchBuf = new byte[searchLength];
            fs.ReadExactly(searchBuf);

            int idx = -1;
            for (int i = 0; i <= searchBuf.Length - 48; i++)
            {
                if (searchBuf[i] == 'a' && searchBuf[i + 1] == 'v' && searchBuf[i + 2] == 'i' && searchBuf[i + 3] == 'h')
                {
                    idx = i + 8; // skip 'avih' (4 bytes) + chunk length (4 bytes)
                    break;
                }
            }

            if (idx >= 0 && idx + 40 <= searchBuf.Length)
            {
                int usPerFrame = BinaryPrimitives.ReadInt32LittleEndian(searchBuf.AsSpan(idx, 4));
                int totalFrames = BinaryPrimitives.ReadInt32LittleEndian(searchBuf.AsSpan(idx + 16, 4));
                int w = BinaryPrimitives.ReadInt32LittleEndian(searchBuf.AsSpan(idx + 32, 4));
                int h = BinaryPrimitives.ReadInt32LittleEndian(searchBuf.AsSpan(idx + 36, 4));
                if (w > 0 && h > 0 && totalFrames > 0)
                {
                    width = w;
                    height = h;
                    frameCount = totalFrames;
                    if (usPerFrame > 0) fps = 1_000_000.0 / usPerFrame;
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a standard uncompressed DIB AVI 1.0 container compatible with astronomical processing engines.
    /// </summary>
    public static void WriteUncompressedAvi(string outputPath, IReadOnlyList<AstroFrame> frames, int fps = 20, bool safeFrameWidth = true)
    {
        if (frames.Count == 0) return;

        int srcWidth = frames[0].Width;
        int width = safeFrameWidth ? ((srcWidth + 3) & ~3) : srcWidth;
        int height = frames[0].Height;
        int rowStride = (width * 3 + 3) & ~3;
        int frameBytes = rowStride * height;
        int frameCount = frames.Count;
        int microSecPerFrame = (int)Math.Round(1_000_000.0 / Math.Max(1, fps));

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

        int moviDataBytes = frameCount * (8 + frameBytes);
        int idx1Bytes = frameCount * 16;
        int hdrlBytes = 192;
        int riffSize = 4 + (8 + hdrlBytes) + (8 + 4 + moviDataBytes) + (8 + idx1Bytes);

        // RIFF AVI Header
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffSize);
        bw.Write(Encoding.ASCII.GetBytes("AVI "));

        // LIST hdrl
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(hdrlBytes);
        bw.Write(Encoding.ASCII.GetBytes("hdrl"));

        // avih chunk (56 bytes)
        bw.Write(Encoding.ASCII.GetBytes("avih"));
        bw.Write(56);
        bw.Write(microSecPerFrame);
        bw.Write(frameBytes * fps);
        bw.Write(0);
        bw.Write(0x10); // AVIF_HASINDEX
        bw.Write(frameCount);
        bw.Write(0);
        bw.Write(1); // streams
        bw.Write(frameBytes);
        bw.Write(width);
        bw.Write(height);
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

        // LIST strl (116 bytes)
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(116);
        bw.Write(Encoding.ASCII.GetBytes("strl"));

        // strh (56 bytes)
        bw.Write(Encoding.ASCII.GetBytes("strh"));
        bw.Write(56);
        bw.Write(Encoding.ASCII.GetBytes("vids"));
        bw.Write(Encoding.ASCII.GetBytes("DIB "));
        bw.Write(0);
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write(0);
        bw.Write(1);
        bw.Write(fps);
        bw.Write(0);
        bw.Write(frameCount);
        bw.Write(frameBytes);
        bw.Write(-1);
        bw.Write(0);
        bw.Write((short)0);
        bw.Write((short)0);
        bw.Write((short)width);
        bw.Write((short)height);

        // strf (BITMAPINFOHEADER 40 bytes)
        bw.Write(Encoding.ASCII.GetBytes("strf"));
        bw.Write(40);
        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1);
        bw.Write((short)24);
        bw.Write(0); // BI_RGB
        bw.Write(frameBytes);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);

        // LIST movi
        bw.Write(Encoding.ASCII.GetBytes("LIST"));
        bw.Write(4 + moviDataBytes);
        bw.Write(Encoding.ASCII.GetBytes("movi"));

        byte[] row = new byte[rowStride];
        for (int f = 0; f < frameCount; f++)
        {
            var frame = frames[f];
            bw.Write(Encoding.ASCII.GetBytes("00db"));
            bw.Write(frameBytes);

            for (int y = height - 1; y >= 0; y--)
            {
                Array.Clear(row);
                int maxCopyX = Math.Min(width, frame.Width);
                for (int x = 0; x < maxCopyX; x++)
                {
                    if (frame.Channels == 1)
                    {
                        byte v = (byte)(frame.Data[y * frame.Width + x] >> 8);
                        row[x * 3] = v;
                        row[x * 3 + 1] = v;
                        row[x * 3 + 2] = v;
                    }
                    else
                    {
                        int idx = (y * frame.Width + x) * 3;
                        row[x * 3] = (byte)(frame.Data[idx + 2] >> 8);
                        row[x * 3 + 1] = (byte)(frame.Data[idx + 1] >> 8);
                        row[x * 3 + 2] = (byte)(frame.Data[idx] >> 8);
                    }
                }
                bw.Write(row);
            }
        }

        // idx1 chunk
        bw.Write(Encoding.ASCII.GetBytes("idx1"));
        bw.Write(idx1Bytes);
        int chunkOffset = 4;
        for (int f = 0; f < frameCount; f++)
        {
            bw.Write(Encoding.ASCII.GetBytes("00db"));
            bw.Write(0x10); // AVIIF_KEYFRAME
            bw.Write(chunkOffset);
            bw.Write(frameBytes);
            chunkOffset += 8 + frameBytes;
        }
    }
}
