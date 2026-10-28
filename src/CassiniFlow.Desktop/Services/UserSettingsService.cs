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
}
