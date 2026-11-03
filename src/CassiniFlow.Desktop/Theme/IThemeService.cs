using System;

namespace CassiniFlow.Desktop.Theme;

public enum AppTheme
{
    DeepSpaceDark,
    CleanLabLight,
    NightVision
}

public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    event Action? ThemeChanged;

    void Initialize();
    void ApplyTheme(AppTheme theme, bool save = true);
    void ToggleNextTheme();
}
