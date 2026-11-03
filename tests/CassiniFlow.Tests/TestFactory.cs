using System;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.Theme;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;

namespace CassiniFlow.Tests;

/// <summary>
/// Usine de test pour instancier des ViewModels et Services avec leurs dépendances explicites
/// sans nécessiter aucun constructeur de commodité dans le code de production.
/// </summary>
public static class TestFactory
{
    public static PipelineRunner CreatePipelineRunner(
        IAstroLocalization? localization = null,
        IStackingPipelineUseCase? stackingRunner = null,
        ICalibrationFrameProvider? calibrationProvider = null)
    {
        var loc = localization ?? new AstroLocalizationService();
        var calib = calibrationProvider ?? new CalibrationFrameProviderService(loc);
        var stacking = stackingRunner ?? new MultiZoneStackingPipelineRunner(loc, calib);
        return new PipelineRunner(loc, stacking, calib);
    }

    public static MainWindowViewModel CreateMainWindowViewModel(
        IAstroLocalization? localization = null,
        IUserSettingsService? userSettings = null,
        IThemeService? themeService = null,
        IRunPipelineUseCase? pipelineRunner = null,
        ICelestialTargetClassifier? celestialClassifier = null,
        TutorialViewModel? tutorialVM = null,
        IUpdateCheckerService? updateChecker = null)
    {
        var loc = localization ?? Loc.Localization;
        var settings = userSettings ?? new InMemoryUserSettingsService();
        var theme = themeService ?? new ThemeService(settings);
        var calib = new CalibrationFrameProviderService(loc);
        var stacking = new MultiZoneStackingPipelineRunner(loc, calib);
        var runner = pipelineRunner ?? new PipelineRunner(loc, stacking, calib);
        var classifier = celestialClassifier ?? new CelestialTargetClassifierService(loc);
        var tutorial = tutorialVM ?? new TutorialViewModel(loc, settings);

        return new MainWindowViewModel(
            loc,
            settings,
            theme,
            runner,
            classifier,
            tutorial,
            updateChecker);
    }
}

