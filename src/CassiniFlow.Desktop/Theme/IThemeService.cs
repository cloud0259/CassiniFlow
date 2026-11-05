using System;

namespace CassiniFlow.Desktop.Theme;

public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    event Action? ThemeChanged;

    void Initialize();
    void ApplyTheme(AppTheme theme, bool save = true);
    void ToggleNextTheme();
}
