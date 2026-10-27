using System.IO;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Domain.Models;
using Xunit;

namespace CassiniFlow.Tests;

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
}
