using System;
using Avalonia;
using Avalonia.Styling;
using CassiniFlow.Desktop.Services;

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

    public static void Initialize()
    {
        UserSettingsService.Load();
        var theme = UserSettingsService.Current.Theme?.ToLowerInvariant() switch
        {
            "light" => AppTheme.CleanLabLight,
            "night_vision" or "nightvision" or "red" => AppTheme.NightVision,
            _ => AppTheme.DeepSpaceDark
        };
        ApplyTheme(theme, save: false);
    }

    public static void ApplyTheme(AppTheme theme, bool save = true)
    {
        var targetVariant = theme switch
        {
            AppTheme.CleanLabLight => ThemeVariant.Light,
            AppTheme.NightVision => NightVisionVariant,
            _ => ThemeVariant.Dark
        };

        if (CurrentTheme == theme && Avalonia.Application.Current?.RequestedThemeVariant == targetVariant)
        {
            if (save)
            {
                SaveTheme(theme);
            }
            return;
        }

        CurrentTheme = theme;

        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant = targetVariant;
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
        string themeStr = theme switch
        {
            AppTheme.CleanLabLight => "light",
            AppTheme.NightVision => "night_vision",
            _ => "dark"
        };
        UserSettingsService.UpdateTheme(themeStr);
    }
}
