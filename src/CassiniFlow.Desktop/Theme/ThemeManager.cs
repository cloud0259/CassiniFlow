using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;

namespace CassiniFlow.Desktop.Theme;

public enum AppTheme
{
    DeepSpaceDark,
    CleanLabLight,
    NightVision
}

public static class ThemeManager
{
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.DeepSpaceDark;
    public static event Action? ThemeChanged;

    public static readonly ThemeVariant NightVisionVariant = new("NightVision", ThemeVariant.Dark);

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CassiniFlow", "settings.json");

    private sealed class SettingsFile
    {
        public string? Language { get; set; }
        public string? Theme { get; set; }
    }

    public static void Initialize()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<SettingsFile>(json);
                var theme = settings?.Theme?.ToLowerInvariant() switch
                {
                    "light" => AppTheme.CleanLabLight,
                    "night_vision" or "nightvision" or "red" => AppTheme.NightVision,
                    _ => AppTheme.DeepSpaceDark
                };
                ApplyTheme(theme, save: false);
                return;
            }
        }
        catch
        {
            // fallback
        }

        ApplyTheme(AppTheme.DeepSpaceDark, save: false);
    }

    public static void ApplyTheme(AppTheme theme, bool save = true)
    {
        CurrentTheme = theme;

        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = theme switch
            {
                AppTheme.CleanLabLight => ThemeVariant.Light,
                AppTheme.NightVision => NightVisionVariant,
                _ => ThemeVariant.Dark
            };
        }

        if (save)
        {
            SaveTheme(theme);
        }

        ThemeChanged?.Invoke();
    }

    public static void ToggleNextTheme()
    {
        var next = CurrentTheme switch
        {
            AppTheme.DeepSpaceDark => AppTheme.CleanLabLight,
            AppTheme.CleanLabLight => AppTheme.NightVision,
            _ => AppTheme.DeepSpaceDark
        };
        ApplyTheme(next, save: true);
    }

    private static void SaveTheme(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            SettingsFile settings = new();
            if (File.Exists(SettingsPath))
            {
                try
                {
                    settings = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(SettingsPath)) ?? new();
                }
                catch
                {
                    settings = new();
                }
            }

            settings.Theme = theme switch
            {
                AppTheme.CleanLabLight => "light",
                AppTheme.NightVision => "night_vision",
                _ => "dark"
            };

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Dossier non accessible ou lock
        }
    }
}
