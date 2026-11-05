using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Codecs;

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
