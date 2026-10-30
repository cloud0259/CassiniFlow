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
}

/// <summary>
/// Service centralisé de persistance des préférences utilisateur (% APPDATA%/CassiniFlow/settings.json).
/// </summary>
public static class UserSettingsService
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CassiniFlow");

    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static UserSettings Current { get; private set; } = new();

    public static event Action? SettingsChanged;

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
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

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
            SettingsChanged?.Invoke();
        }
        catch
        {
            // Ignorer silencieusement les erreurs d'écriture I/O
        }
    }

    public static void UpdateCpuSettings(int maxCpuPercent, int warningThresholdPercent)
    {
        Current.MaxCpuUsagePercent = Math.Clamp(maxCpuPercent, 10, 100);
        Current.CpuWarningThresholdPercent = Math.Clamp(warningThresholdPercent, 50, 100);
        Save();
    }

    public static void UpdateTheme(string theme)
    {
        Current.Theme = theme;
        Save();
    }

    public static void UpdateLanguage(string lang)
    {
        Current.Language = lang;
        Save();
    }

    public static void MarkTutorialSeen()
    {
        Current.HasSeenTutorial = true;
        Save();
    }

    public static void UpdateProcessingSettings(
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
        int qualityKeep)
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
        Save();
    }
}
