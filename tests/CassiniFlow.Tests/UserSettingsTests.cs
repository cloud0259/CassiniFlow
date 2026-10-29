using System.IO;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Domain.Models;
using Xunit;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class UserSettingsTests
{
    [Fact]
    public void PipelineRunner_WorkerThreadCount_AllowsFullThreadUtilization()
    {
        int totalCores = Environment.ProcessorCount;
        
        // 100% CPU usage should return total logical cores (no artificial reservation)
        var options100 = new ProcessingOptions { MaxCpuUsagePercent = 100 };
        int threads100 = PipelineRunner.GetSafeWorkerThreadCount(options100);
        Assert.Equal(totalCores, threads100);

        // 50% CPU usage
        var options50 = new ProcessingOptions { MaxCpuUsagePercent = 50 };
        int threads50 = PipelineRunner.GetSafeWorkerThreadCount(options50);
        int expected50 = Math.Clamp((int)Math.Round(totalCores * 0.50), 1, totalCores);
        Assert.Equal(expected50, threads50);

        // Clamp minimum to 10% and at least 1 core
        var options0 = new ProcessingOptions { MaxCpuUsagePercent = 0 };
        int threads0 = PipelineRunner.GetSafeWorkerThreadCount(options0);
        Assert.True(threads0 >= 1);
    }

    [Fact]
    public void UserSettingsModel_DefaultValues_AreCorrect()
    {
        var model = new UserSettings();
        Assert.Equal("Dark", model.Theme);
        Assert.Equal("fr", model.Language);
        Assert.Equal(80, model.MaxCpuUsagePercent);
        Assert.Equal(85, model.CpuWarningThresholdPercent);
        Assert.False(model.HasSeenTutorial);
        Assert.False(model.WinJuposNaming);
        Assert.Equal(0, model.SelectedOutputFormatIndex);
        Assert.Equal(0, model.SelectedStackingImageFormatIndex);
        Assert.Equal("_cassiniflow", model.OutputSuffix);
        Assert.False(model.AutoRotateAlign);
        Assert.Equal(448, model.CropWidth);
        Assert.Equal(448, model.CropHeight);
        Assert.True(model.EnableCropping);
    }

    [Fact]
    public void UserSettingsService_UpdateCpuSettings_ClampsValues()
    {
        UserSettingsService.UpdateCpuSettings(150, 120);
        Assert.Equal(100, UserSettingsService.Current.MaxCpuUsagePercent);
        Assert.Equal(100, UserSettingsService.Current.CpuWarningThresholdPercent);

        UserSettingsService.UpdateCpuSettings(5, 20);
        Assert.Equal(10, UserSettingsService.Current.MaxCpuUsagePercent);
        Assert.Equal(50, UserSettingsService.Current.CpuWarningThresholdPercent);

        // Restore default
        UserSettingsService.UpdateCpuSettings(80, 85);
    }

    [Fact]
    public void UserSettingsService_UpdateProcessingSettings_PersistsValues()
    {
        UserSettingsService.UpdateProcessingSettings(
            winJuposNaming: true,
            outputFormatIndex: 2,
            stackingFormatIndex: 1,
            outputSuffix: "_custom",
            customOutputDir: @"C:\CustomOutput",
            autoRotate: true,
            cropWidth: 512,
            cropHeight: 512,
            enableCropping: true,
            enableQuality: true,
            qualityKeep: 40);

        Assert.True(UserSettingsService.Current.WinJuposNaming);
        Assert.Equal(2, UserSettingsService.Current.SelectedOutputFormatIndex);
        Assert.Equal(1, UserSettingsService.Current.SelectedStackingImageFormatIndex);
        Assert.Equal("_custom", UserSettingsService.Current.OutputSuffix);
        Assert.Equal(@"C:\CustomOutput", UserSettingsService.Current.CustomOutputDirectory);
        Assert.True(UserSettingsService.Current.AutoRotateAlign);
        Assert.Equal(512, UserSettingsService.Current.CropWidth);
        Assert.Equal(512, UserSettingsService.Current.CropHeight);
        Assert.True(UserSettingsService.Current.EnableCropping);
        Assert.True(UserSettingsService.Current.EnableQualityEstimation);
        Assert.Equal(40, UserSettingsService.Current.QualityFramesToKeep);

        // Restore default
        UserSettingsService.UpdateProcessingSettings(
            winJuposNaming: false,
            outputFormatIndex: 0,
            stackingFormatIndex: 0,
            outputSuffix: "_cassiniflow",
            customOutputDir: string.Empty,
            autoRotate: false,
            cropWidth: 448,
            cropHeight: 448,
            enableCropping: true,
            enableQuality: false,
            qualityKeep: 25);
    }

    [Fact]
    public void MainWindowViewModel_InitializesAndPersistsWinJuposSetting()
    {
        // Arrange: set WinJupos to true in UserSettings
        UserSettingsService.Current.WinJuposNaming = true;

        // Act: construct MainWindowViewModel
        var vm = new MainWindowViewModel();

        // Assert: VM loaded setting
        Assert.True(vm.WinJuposNaming);

        // Act: change through VM
        vm.WinJuposNaming = false;

        // Assert: automatically persisted
        Assert.False(UserSettingsService.Current.WinJuposNaming);
    }
}
