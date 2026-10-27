using CassiniFlow.Desktop.Theme;

namespace CassiniFlow.Tests;

public class ThemeTests
{
    [Fact]
    public void ThemeManager_CanSwitchThemes()
    {
        ThemeManager.ApplyTheme(AppTheme.DeepSpaceDark, save: false);
        Assert.Equal(AppTheme.DeepSpaceDark, ThemeManager.CurrentTheme);

        ThemeManager.ApplyTheme(AppTheme.CleanLabLight, save: false);
        Assert.Equal(AppTheme.CleanLabLight, ThemeManager.CurrentTheme);

        ThemeManager.ApplyTheme(AppTheme.NightVision, save: false);
        Assert.Equal(AppTheme.NightVision, ThemeManager.CurrentTheme);

        // Reset to default
        ThemeManager.ApplyTheme(AppTheme.DeepSpaceDark, save: false);
    }

    [Fact]
    public void ThemeManager_ToggleNextTheme_CyclesThroughAllThemes()
    {
        ThemeManager.ApplyTheme(AppTheme.DeepSpaceDark, save: false);

        ThemeManager.ToggleNextTheme();
        Assert.Equal(AppTheme.CleanLabLight, ThemeManager.CurrentTheme);

        ThemeManager.ToggleNextTheme();
        Assert.Equal(AppTheme.NightVision, ThemeManager.CurrentTheme);

        ThemeManager.ToggleNextTheme();
        Assert.Equal(AppTheme.DeepSpaceDark, ThemeManager.CurrentTheme);
    }

    [Fact]
    public void ThemeManager_NightVisionVariant_HasProperKey()
    {
        Assert.NotNull(ThemeManager.NightVisionVariant);
        Assert.Equal("NightVision", ThemeManager.NightVisionVariant.Key);
    }

    [Fact]
    public void App_InitializesAndLoadsThemeDictionariesWithoutException()
    {
        var app = new CassiniFlow.Desktop.App();
        var ex = Record.Exception(() => app.Initialize());
        Assert.Null(ex);
    }
}
