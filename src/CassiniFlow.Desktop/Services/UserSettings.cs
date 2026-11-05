namespace CassiniFlow.Desktop.Services;

public sealed class UserSettings
{
    public string Language { get; set; } = "fr";
    public string Theme { get; set; } = "Dark";
    public int MaxCpuUsagePercent { get; set; } = 80;
    public int CpuWarningThresholdPercent { get; set; } = 85;
    public bool HasSeenTutorial { get; set; } = false;
    public bool CheckUpdatesOnStartup { get; set; } = true;

    // Persisted preferences (Feature #40)
    public bool WinJuposNaming { get; set; } = false;
    public int SelectedOutputFormatIndex { get; set; } = 0;
    public int SelectedStackingImageFormatIndex { get; set; } = 0;
    public string OutputSuffix { get; set; } = "_cassiniflow";
    public string CustomOutputDirectory { get; set; } = string.Empty;
    public bool AutoRotateAlign { get; set; } = false;
    public int CropWidth { get; set; } = 448;
    public int CropHeight { get; set; } = 448;
    public bool EnableCropping { get; set; } = true;
    public bool EnableQualityEstimation { get; set; } = false;
    public int QualityFramesToKeep { get; set; } = 25;
    public bool DetectNaturalSatellites { get; set; } = true;
    public double SatelliteDetectionSensitivity { get; set; } = 1.0;

    // Persisted stacking & alignment preferences
    public int StackPercentage1 { get; set; } = 25;
    public int StackPercentage2 { get; set; } = 0;
    public int StackPercentage3 { get; set; } = 0;
    public int AlignmentTileSize { get; set; } = 48;
    public int MinBrightnessThreshold { get; set; } = 15;
    public bool NormalizeBrightness { get; set; } = true;
    public int NormalizeTargetPercent { get; set; } = 75;
    public bool CorrectAtmosphericDispersion { get; set; } = true;
    public bool SharpeningEnabled { get; set; } = true;
    public int SharpeningBlendPercent { get; set; } = 10;
    public bool FeatheringEnabled { get; set; } = true;
}
