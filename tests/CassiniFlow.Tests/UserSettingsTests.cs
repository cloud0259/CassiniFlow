using System;
using System.IO;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Adapters;

namespace CassiniFlow.Tests;

public class UserSettingsTests
{
    [Fact]
    public void PipelineRunner_WorkerThreadCount_AllowsFullThreadUtilization()
    {
        var options100 = new ProcessingOptions { MaxCpuUsagePercent = 100 };
        int threads100 = PipelineRunner.GetSafeWorkerThreadCount(options100);
        Assert.Equal(Environment.ProcessorCount, threads100);

        var options50 = new ProcessingOptions { MaxCpuUsagePercent = 50 };
        int threads50 = PipelineRunner.GetSafeWorkerThreadCount(options50);
        Assert.Equal(Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 0.5)), threads50);

        var options0 = new ProcessingOptions { MaxCpuUsagePercent = 0 };
        int threads0 = PipelineRunner.GetSafeWorkerThreadCount(options0);
        Assert.Equal(Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 0.1)), threads0);
    }

    [Fact]
    public void UserSettings_DefaultValues_AreCorrect()
    {
        var model = new UserSettings();
        Assert.Equal("fr", model.Language);
        Assert.Equal("Dark", model.Theme);
        Assert.Equal(80, model.MaxCpuUsagePercent);
        Assert.Equal(85, model.CpuWarningThresholdPercent);
        Assert.False(model.HasSeenTutorial);
        Assert.False(model.WinJuposNaming);
        Assert.Equal(0, model.SelectedOutputFormatIndex);
        Assert.Equal(0, model.SelectedStackingImageFormatIndex);
        Assert.Equal("_cassiniflow", model.OutputSuffix);
        Assert.Equal(string.Empty, model.CustomOutputDirectory);
        Assert.False(model.AutoRotateAlign);
        Assert.Equal(448, model.CropWidth);
        Assert.Equal(448, model.CropHeight);
        Assert.True(model.EnableCropping);
    }

    [Fact]
    public void UserSettingsService_UpdateCpuSettings_ClampsValues()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        try
        {
            IUserSettingsService service = new JsonUserSettingsService(tempFile);
            service.UpdateCpuSettings(150, 120);
            Assert.Equal(100, service.Current.MaxCpuUsagePercent);
            Assert.Equal(100, service.Current.CpuWarningThresholdPercent);

            service.UpdateCpuSettings(5, 20);
            Assert.Equal(10, service.Current.MaxCpuUsagePercent);
            Assert.Equal(50, service.Current.CpuWarningThresholdPercent);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void UserSettingsService_UpdateProcessingSettings_PersistsValues()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        try
        {
            IUserSettingsService service = new JsonUserSettingsService(tempFile);
            service.UpdateProcessingSettings(
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

            Assert.True(service.Current.WinJuposNaming);
            Assert.Equal(2, service.Current.SelectedOutputFormatIndex);
            Assert.Equal(1, service.Current.SelectedStackingImageFormatIndex);
            Assert.Equal("_custom", service.Current.OutputSuffix);
            Assert.Equal(@"C:\CustomOutput", service.Current.CustomOutputDirectory);
            Assert.True(service.Current.AutoRotateAlign);
            Assert.Equal(512, service.Current.CropWidth);
            Assert.Equal(512, service.Current.CropHeight);
            Assert.True(service.Current.EnableCropping);
            Assert.True(service.Current.EnableQualityEstimation);
            Assert.Equal(40, service.Current.QualityFramesToKeep);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void MainWindowViewModel_InitializesAndPersistsWinJuposSetting()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        try
        {
            IUserSettingsService service = new JsonUserSettingsService(tempFile);
            service.UpdateProcessingSettings(
                winJuposNaming: true,
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

            var vm = TestFactory.CreateMainWindowViewModel(userSettings: service);
            Assert.True(vm.WinJuposNaming);

            vm.WinJuposNaming = false;
            Assert.False(service.Current.WinJuposNaming);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
