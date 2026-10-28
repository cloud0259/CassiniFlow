namespace CassiniFlow.Domain.Models;

/// <summary>
/// Enumeration of all CassiniFlow enums extracted from CassiniFlow v2.5.9 via REA.
/// </summary>
public enum ProcessingRunMode
{
    BatchMode = 0,
    JoinMode = 1
}

public enum OptimisationPreset
{
    None = 0,
    Planetary = 1,
    SolarLunarFullDisc = 2,
    SolarLunarCloseUp = 3,
    Iss = 4,
    PlanetaryAnimationAvi = 5,
    PlanetaryAnimationGif = 6,
    AviArchive = 7
}

public enum FrameRangeType
{
    StartToEnd = 0,
    FramesFromCentre = 1
}

public enum BinningMode
{
    Disabled = 1,
    Bin2x2 = 2,
    Bin3x3 = 3,
    Bin4x4 = 4,
    Bin5x5 = 5
}

public enum BinningCalculation
{
    Average = 0,
    Sum = 1
}

public enum InputFrameColourMode
{
    AutoDetect = 0,
    Monochrome = 1,
    Colour = 2
}

public enum BayerPattern
{
    Gbrg = 0,
    Bggr = 1,
    Rggb = 2,
    Grbg = 3
}

public enum DebayerAlgorithm
{
    Bilinear = 0,
    HqLinear = 1,
    SmoothHue = 2
}

public enum RawDebayerAlgorithm
{
    Bilinear = 0,
    Vng = 1,
    Ppg = 2,
    Ahd = 3
}

public enum RawColourSpace
{
    RawSensor = 0,
    SrgbD65 = 1,
    AdobeRgbD65 = 2,
    WideGamutRgbD65 = 3,
    KodakProPhotoRgbD65 = 4,
    Xyz = 5,
    Aces = 6
}

public enum MonochromeConversionMode
{
    UseRgbChannels = 0,
    UseRChannelOnly = 1,
    UseGChannelOnly = 2,
    UseBChannelOnly = 3,
    UseRgChannels = 4,
    UseRbChannels = 5,
    UseGbChannels = 6
}

public enum StabilisationMode
{
    None = 0,
    ObjectPlanetary = 1,
    SurfaceFeature = 2
}

public enum LunarShadowEdge
{
    None = 0,
    TopLeft = 1,
    Left = 2,
    BottomLeft = 3,
    TopRight = 4,
    Right = 5,
    BottomRight = 6,
    Top = 7,
    Bottom = 8
}

public enum RotationAngle
{
    None = 0,
    Rotate90Clockwise = 1,
    Rotate90Anticlockwise = 2,
    Rotate180 = 3
}

public enum QualityAlgorithm
{
    GradientSharpness = 0,
    OriginalNinox = 1,
    PeakHistogram = 2,
    TotalBrightness = 3
}

public enum QualityLimitingType
{
    NumberOfFrames = 0,
    PercentageOfFrames = 1
}

public enum OutputFormat
{
    Avi = 0,
    Ser = 1,
    Bmp = 2,
    Tiff = 3,
    Png = 4,
    Fits = 5,
    AnimatedGif = 6,
    Mp4 = 7
}

public enum AviCodecType
{
    DibUncompressed = 0,
    UlrgUtVideoLossless = 1,
    Fmp4Mpeg4Lossy = 2
}

public enum GifQuantisationAlgorithm
{
    NeuralNetNeuQuant = 0,
    MedianCut = 1
}

public enum WinJuposTimeReference
{
    MidPointTime = 0,
    EarliestFrameTime = 1,
    FirstOutputFrameTime = 2
}

public enum DateTimeFilenameFormat
{
    FireCapture1 = 0,
    FireCapture1WithCount = 1,
    FireCapture2 = 2,
    FireCapture2WithCount = 3,
    IcCapture = 4,
    SharpCap = 5,
    WinJupos = 6,
    WinJuposOld = 7,
    WxAstroCapture = 8,
    Custom1 = 9,
    Custom2 = 10,
    Custom3 = 11
}

public enum StackingWorkflowMode
{
    PreProcessingOnly = 0,
    StackingOnly = 1,
    DirectStreamAllInOne = 2
}

public enum DrizzleScale
{
    None = 0,
    Drizzle15 = 1,
    Drizzle30 = 2
}
