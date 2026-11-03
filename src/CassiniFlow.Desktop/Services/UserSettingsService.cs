using System;
using System.IO;
using System.Text.Json;

namespace CassiniFlow.Desktop.Services;

public sealed class UserSettings
{
    public string Language { get; set; } = "fr";
    public string Theme { get; set; } = "Dark";
    public int MaxCpuUsagePercent { get; set; } = 80;
    public int CpuWarningThresholdPercent { get; set; } = 85;
    public bool HasSeenTutorial { get; set; } = false;

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

/// <summary>
/// Implémentation concrète et injectable du service de persistance des préférences utilisateur (%APPDATA%/CassiniFlow/settings.json).
/// </summary>
public class JsonUserSettingsService : IUserSettingsService
{
    private readonly string _settingsFilePath;
    private readonly string _settingsDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public UserSettings Current { get; private set; } = new();

    public event Action? SettingsChanged;

    public JsonUserSettingsService(string? customSettingsFilePath = null)
    {
        _settingsDirectory = customSettingsFilePath != null
            ? Path.GetDirectoryName(customSettingsFilePath) ?? string.Empty
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CassiniFlow");
        _settingsFilePath = customSettingsFilePath ?? Path.Combine(_settingsDirectory, "settings.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<UserSettings>(json);
                if (loaded is not null)
                {
                    Current = loaded;
                    Current.MaxCpuUsagePercent = Math.Clamp(Current.MaxCpuUsagePercent, 10, 100);
                    Current.CpuWarningThresholdPercent = Math.Clamp(Current.CpuWarningThresholdPercent, 50, 100);
                    return;
                }
            }
        }
        catch
        {
            // Fallback aux valeurs par défaut
        }

        Current = new UserSettings();
    }

    public void Save()
    {
        try
        {
            if (!string.IsNullOrEmpty(_settingsDirectory))
            {
                Directory.CreateDirectory(_settingsDirectory);
            }
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
            SettingsChanged?.Invoke();
        }
        catch
        {
            // Ignorer silencieusement les erreurs d'écriture I/O
        }
    }

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
        Current.SelectedOutputFormatIndex = outputFormatIndex;
        Current.SelectedStackingImageFormatIndex = stackingFormatIndex;
        Current.OutputSuffix = outputSuffix;
        Current.CustomOutputDirectory = customOutputDir;
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

/// <summary>
/// Implémentation isolée en mémoire de <see cref="IUserSettingsService"/> pour les tests unitaires et le design-time.
/// Évite toute pollution du fichier settings.json système.
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


