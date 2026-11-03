using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Moteur du domaine accumulant en continu les trames de calibration (Dark, Flat, Flat-Dark)
/// afin de calculer la trame maîtresse (Master Frame) moyenne sans surcharge mémoire.
/// </summary>
public sealed class MasterFrameAccumulator
{
    private long[]? _sum;
    private int _count;
    private int _width;
    private int _height;
    private int _channels;
    private int _bitDepth = 8;
    private readonly AstroFrame? _darkSubtractor;

    public int Count => _count;

    public MasterFrameAccumulator(AstroFrame? darkSubtractor = null)
    {
        _darkSubtractor = darkSubtractor;
    }

    public unsafe void AddFrame(AstroFrame frame)
    {
        int len = frame.Data.Length;
        if (_sum == null)
        {
            _width = frame.Width;
            _height = frame.Height;
            _channels = frame.Channels;
            _bitDepth = frame.OriginalBitDepth;
            _sum = new long[len];
        }
        else if (frame.Width != _width || frame.Height != _height || frame.Channels != _channels)
        {
            return;
        }

        fixed (ushort* pFrame = frame.Data)
        fixed (long* pSum = _sum)
        {
            if (_darkSubtractor != null && _darkSubtractor.Data.Length == len)
            {
                fixed (ushort* pDark = _darkSubtractor.Data)
                {
                    for (int i = 0; i < len; i++)
                    {
                        int val = pFrame[i] - pDark[i];
                        pSum[i] += val > 0 ? val : 0;
                    }
                }
            }
            else
            {
                for (int i = 0; i < len; i++)
                {
                    pSum[i] += pFrame[i];
                }
            }
        }
        _count++;
    }

    public unsafe AstroFrame? ProduceMasterFrame()
    {
        if (_count == 0 || _sum == null) return null;

        var master = new AstroFrame(_width, _height, _channels, _bitDepth);
        int len = _sum.Length;
        double invCount = 1.0 / _count;

        fixed (long* pSum = _sum)
        fixed (ushort* pMaster = master.Data)
        {
            for (int i = 0; i < len; i++)
            {
                pMaster[i] = (ushort)Math.Clamp((long)Math.Round(pSum[i] * invCount), 0, 65535);
            }
        }

        return master;
    }
}

