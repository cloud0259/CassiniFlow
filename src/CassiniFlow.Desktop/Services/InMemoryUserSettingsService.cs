using System;

namespace CassiniFlow.Desktop.Services;

/// <summary>
/// Implémentation en mémoire sans fichier physique, utile pour les tests unitaires et le mode dégradé.
/// </summary>
public class InMemoryUserSettingsService : IUserSettingsService
{
    public UserSettings Current { get; } = new();

    public event Action? SettingsChanged;

    public void Load() { }

    public void Save() => SettingsChanged?.Invoke();

    public void UpdateCpuSettings(int maxCpuPercent, int warningThresholdPercent)
    {
        Current.MaxCpuUsagePercent = Math.Clamp(maxCpuPercent, 10, 100);
        Current.CpuWarningThresholdPercent = Math.Clamp(warningThresholdPercent, 50, 100);
        Save();
    }

    public void UpdateTheme(string theme)
    {
        Current.Theme = theme;
        Save();
    }

    public void UpdateLanguage(string lang)
    {
        Current.Language = lang;
        Save();
    }

    public void MarkTutorialSeen()
    {
        Current.HasSeenTutorial = true;
        Save();
    }

    public void UpdateProcessingSettings(
        bool winJuposNaming,
        int outputFormatIndex,
        int stackingFormatIndex,
        string outputSuffix,
        string customOutputDir,
        bool autoRotate,
        int cropWidth,
        int cropHeight,
        bool enableCropping,
        bool enableQuality,
        int qualityKeep,
        int stackPct1 = 25,
        int stackPct2 = 0,
        int stackPct3 = 0,
        int alignmentTileSize = 48,
        int minBrightness = 15,
        bool normalizeBrightness = true,
        int normalizeTargetPct = 75,
        bool correctAdc = true,
        bool sharpeningEnabled = true,
        int sharpeningBlendPct = 10,
        bool featheringEnabled = true)
    {
        Current.WinJuposNaming = winJuposNaming;
        Current.SelectedOutputFormatIndex = Math.Clamp(outputFormatIndex, 0, 6);
        Current.SelectedStackingImageFormatIndex = Math.Clamp(stackingFormatIndex, 0, 2);
        Current.OutputSuffix = outputSuffix ?? string.Empty;
        Current.CustomOutputDirectory = customOutputDir ?? string.Empty;
        Current.AutoRotateAlign = autoRotate;
        Current.CropWidth = cropWidth;
        Current.CropHeight = cropHeight;
        Current.EnableCropping = enableCropping;
        Current.EnableQualityEstimation = enableQuality;
        Current.QualityFramesToKeep = qualityKeep;
        Current.StackPercentage1 = Math.Clamp(stackPct1, 0, 100);
        Current.StackPercentage2 = Math.Clamp(stackPct2, 0, 100);
        Current.StackPercentage3 = Math.Clamp(stackPct3, 0, 100);
        Current.AlignmentTileSize = Math.Clamp(alignmentTileSize, 16, 512);
        Current.MinBrightnessThreshold = Math.Clamp(minBrightness, 1, 254);
        Current.NormalizeBrightness = normalizeBrightness;
        Current.NormalizeTargetPercent = Math.Clamp(normalizeTargetPct, 10, 100);
        Current.CorrectAtmosphericDispersion = correctAdc;
        Current.SharpeningEnabled = sharpeningEnabled;
        Current.SharpeningBlendPercent = Math.Clamp(sharpeningBlendPct, 0, 100);
        Current.FeatheringEnabled = featheringEnabled;
        Save();
    }
}
