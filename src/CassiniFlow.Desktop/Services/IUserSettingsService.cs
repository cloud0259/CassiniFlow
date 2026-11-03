using System;

namespace CassiniFlow.Desktop.Services;

/// <summary>
/// Port de service pour la gestion et la persistance des préférences utilisateur.
/// </summary>
public interface IUserSettingsService
{
    UserSettings Current { get; }
    event Action? SettingsChanged;

    void Load();
    void Save();
    void UpdateCpuSettings(int maxCpuPercent, int warningThresholdPercent);
    void UpdateTheme(string theme);
    void UpdateLanguage(string lang);
    void MarkTutorialSeen();
    void UpdateProcessingSettings(
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
        bool featheringEnabled = true);
}

