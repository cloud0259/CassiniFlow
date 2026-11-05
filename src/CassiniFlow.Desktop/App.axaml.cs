using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.Theme;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Desktop.Views;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Ports.Outbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using Microsoft.Extensions.DependencyInjection;

namespace CassiniFlow.Desktop;

public partial class App : Avalonia.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        var localization = Services.GetRequiredService<IAstroLocalization>();
        var userSettings = Services.GetRequiredService<IUserSettingsService>();
        var themeService = Services.GetRequiredService<IThemeService>();

        Loc.Localization = localization;
        Loc.UserSettings = userSettings;
        Loc.LoadSettings();
        themeService.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAstroLocalization, AstroLocalizationService>();
        services.AddSingleton<IUserSettingsService, JsonUserSettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ICalibrationFrameProvider, CalibrationFrameProviderService>();
        services.AddSingleton<IStackingPipelineUseCase, MultiZoneStackingPipelineRunner>();
        services.AddSingleton<IRunPipelineUseCase, PipelineRunner>();
        services.AddSingleton<ICelestialTargetClassifier, CelestialTargetClassifierService>();
        services.AddSingleton<IUpdateCheckerService, GitHubReleaseUpdateChecker>();
        services.AddSingleton<IQualityTimelineScanner, QualityTimelineScanner>();
        services.AddSingleton<IQualityTimelineUseCase, QualityTimelineUseCase>();

        services.AddTransient<TutorialViewModel>();
        services.AddTransient<AlignmentOptionsViewModel>();
        services.AddTransient<StackingOptionsViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MainWindowViewModel>();
    }
}
