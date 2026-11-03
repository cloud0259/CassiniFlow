using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.Theme;

namespace CassiniFlow.Tests;

public class ThemeTests
{
    [Fact]
    public void ThemeService_CanSwitchThemes()
    {
        IThemeService themeService = new ThemeService(new JsonUserSettingsService());

        themeService.ApplyTheme(AppTheme.DeepSpaceDark, save: false);
        Assert.Equal(AppTheme.DeepSpaceDark, themeService.CurrentTheme);

        themeService.ApplyTheme(AppTheme.CleanLabLight, save: false);
        Assert.Equal(AppTheme.CleanLabLight, themeService.CurrentTheme);

        themeService.ApplyTheme(AppTheme.NightVision, save: false);
        Assert.Equal(AppTheme.NightVision, themeService.CurrentTheme);

        // Reset to default
        themeService.ApplyTheme(AppTheme.DeepSpaceDark, save: false);
    }

    [Fact]
    public void ThemeService_ToggleNextTheme_CyclesThroughAllThemes()
    {
        IThemeService themeService = new ThemeService(new JsonUserSettingsService());

        themeService.ApplyTheme(AppTheme.DeepSpaceDark, save: false);

        themeService.ToggleNextTheme();
        Assert.Equal(AppTheme.CleanLabLight, themeService.CurrentTheme);

        themeService.ToggleNextTheme();
        Assert.Equal(AppTheme.NightVision, themeService.CurrentTheme);

        themeService.ToggleNextTheme();
        Assert.Equal(AppTheme.DeepSpaceDark, themeService.CurrentTheme);
    }

    [Fact]
    public void ThemeService_NightVisionVariant_HasProperKey()
    {
        Assert.NotNull(ThemeService.NightVisionVariant);
        Assert.Equal("NightVision", ThemeService.NightVisionVariant.Key);
    }

    [Fact]
    public void App_InitializesAndLoadsThemeDictionariesWithoutException()
    {
        var app = new CassiniFlow.Desktop.App();
        var ex = Record.Exception(() => app.Initialize());
        Assert.Null(ex);
    }
}
