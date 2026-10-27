namespace CassiniFlow.Domain.Models;

/// <summary>
/// In-memory 16-bit per channel image frame buffer (normalized 0..65535 per sample)
/// supporting both Monochrome (Channels=1) and RGB Colour (Channels=3), plus raw Bayer metadata.
/// </summary>
public sealed class AstroFrame
{
    public int FrameIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Channels { get; set; } // 1 = Mono/Bayer, 3 = RGB
    public int OriginalBitDepth { get; set; } = 8;
    public bool IsBayerRaw { get; set; }
    public BayerPattern BayerPattern { get; set; } = BayerPattern.Rggb;
    public DateTime? TimestampUtc { get; set; }
    public double QualityScore { get; set; }
    public int DetectedCentroidX { get; set; } = -1;
    public int DetectedCentroidY { get; set; } = -1;

    /// <summary>
    /// Interleaved 16-bit unsigned samples (0..65535).
    /// If Channels == 1: length is Width * Height.
    /// If Channels == 3: length is Width * Height * 3 (R, G, B order).
    /// </summary>
    public ushort[] Data { get; set; }

    public AstroFrame(int width, int height, int channels, int originalBitDepth = 8)
    {
        Width = width;
        Height = height;
        Channels = channels;
        OriginalBitDepth = originalBitDepth;
        Data = new ushort[width * height * channels];
    }

    public AstroFrame Clone()
    {
        var copy = new AstroFrame(Width, Height, Channels, OriginalBitDepth)
        {
            FrameIndex = FrameIndex,
            IsBayerRaw = IsBayerRaw,
            BayerPattern = BayerPattern,
            TimestampUtc = TimestampUtc,
            QualityScore = QualityScore,
            DetectedCentroidX = DetectedCentroidX,
            DetectedCentroidY = DetectedCentroidY
        };
        Array.Copy(Data, copy.Data, Data.Length);
        return copy;
    }

    /// <summary>
    /// Computes maximum pixel values (8-bit equivalent 0..255) for UI display.
    /// </summary>
    public (int MaxGrey, int MaxR, int MaxG, int MaxB) ComputeMaxPixelValues8Bit()
    {
        if (Data.Length == 0) return (0, 0, 0, 0);

        if (Channels == 1)
        {
            ushort max = 0;
            foreach (ushort v in Data)
            {
                if (v > max) max = v;
            }
            int m8 = max >> 8;
            return (m8, m8, m8, m8);
        }
        else
        {
            ushort mr = 0, mg = 0, mb = 0;
            for (int i = 0; i < Data.Length; i += 3)
            {
                if (Data[i] > mr) mr = Data[i];
                if (Data[i + 1] > mg) mg = Data[i + 1];
                if (Data[i + 2] > mb) mb = Data[i + 2];
            }
            int r8 = mr >> 8, g8 = mg >> 8, b8 = mb >> 8;
            return (Math.Max(r8, Math.Max(g8, b8)), r8, g8, b8);
        }
    }
}
