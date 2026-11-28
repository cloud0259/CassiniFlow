using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Serialization;
using Xunit;

namespace CassiniFlow.Tests;

public class FrameRetentionTests
{
    [Theory]
    [InlineData(1000, 25.0, 250)]
    [InlineData(101, 50.0, 51)] // AwayFromZero: 50.5 -> 51
    [InlineData(10, 1.0, 1)]    // Minimum clamp 1
    [InlineData(0, 25.0, 0)]
    public void FrameRetentionRule_Percentage_ResolvesDeterministically(int available, double pct, int expected)
    {
        var rule = FrameRetentionRule.FromPercentage(pct);
        Assert.Equal(expected, rule.ResolveFrameCount(available));
    }

    [Theory]
    [InlineData(1000, 500, 500)]
    [InlineData(300, 1000, 300)] // Clamped to available frames
    [InlineData(0, 100, 0)]
    public void FrameRetentionRule_FrameCount_ResolvesDeterministically(int available, int count, int expected)
    {
        var rule = FrameRetentionRule.FromFrameCount(count);
        Assert.Equal(expected, rule.ResolveFrameCount(available));
    }

    [Fact]
    public void StackingOptions_ResolveTiers_HasNoHidden200FrameQuotaCap()
    {
        var stacking = new StackingOptions
        {
            RetentionUnit = FrameRetentionUnit.Percentage,
            StackPercentages = [50.0]
        };

        var tiers = stacking.ResolveTiers(5000);
        Assert.Single(tiers);
        Assert.Equal(2500, tiers[0].FramesToStack);
        Assert.Equal(50.0, tiers[0].EffectivePercentage);
    }

    [Fact]
    public void StackingOptions_ResolveTiers_FrameCountMode_DeduplicatesClampedTiers()
    {
        var stacking = new StackingOptions
        {
            RetentionUnit = FrameRetentionUnit.FrameCount,
            StackFixedFrameCounts = [500, 1500, 3000]
        };

        // Sur une vidéo de 1200 frames, 1500 et 3000 se résolvent tous les deux à 1200 : dédupliqués
        var tiers = stacking.ResolveTiers(1200);
        Assert.Equal(2, tiers.Count);
        Assert.Equal(500, tiers[0].FramesToStack);
        Assert.Equal(1200, tiers[1].FramesToStack);
    }

    [Fact]
    public void MainWindowViewModel_StackingSelectionPreview_UpdatesWithActiveTiers()
    {
        var vm = TestFactory.CreateMainWindowViewModel();
        vm.TimelineSelectionIn = 1;
        vm.TimelineSelectionOut = 1000;

        vm.SetWorkflowMode("directstream");
        vm.SelectedStackRetentionUnitIndex = 0;
        vm.StackPercentage1 = 20;
        vm.StackPercentage2 = 50;
        vm.StackPercentage3 = 0;

        Assert.Contains("200 (20%)", vm.StackingSelectionPreviewText);
        Assert.Contains("500 (50%)", vm.StackingSelectionPreviewText);

        vm.SelectedStackRetentionUnitIndex = 1;
        vm.StackFrameCount1 = 250;
        vm.StackFrameCount2 = 0;
        vm.StackFrameCount3 = 0;

        Assert.Contains("250 (25%)", vm.StackingSelectionPreviewText);
    }

    [Fact]
    public void MainWindowViewModel_SyncOptionsFromUi_PreventsDoubleFilteringInAllInOneMode()
    {
        var vm = TestFactory.CreateMainWindowViewModel();
        vm.SetWorkflowMode("directstream");
        vm.EnableQuality = true;
        vm.LimitQualityFrames = true;
        vm.QualityKeepValue = 25;

        vm.SyncOptionsFromUi();

        Assert.False(vm.Options.EnableQualityLimiting);
        Assert.Empty(vm.Options.Stacking.StackFrameCounts);
    }

    [Fact]
    public void MainWindowViewModel_BatchMode_DoesNotOverwriteFixedFrameCountWhenSelectingShorterFile()
    {
        var vm = TestFactory.CreateMainWindowViewModel();
        vm.SetWorkflowMode("stack");
        vm.IsJoinMode = false;
        vm.SelectedStackRetentionUnitIndex = 1; // FrameCount
        vm.StackFrameCount1 = 2000;

        var longFile = new SourceFileItem { FileName = "long.ser", FullPath = "long.ser", Frames = 5000 };
        var shortFile = new SourceFileItem { FileName = "short.ser", FullPath = "short.ser", Frames = 800 };

        vm.SourceFiles.Add(longFile);
        vm.SourceFiles.Add(shortFile);
        vm.SelectedSourceFile = shortFile;
        vm.UpdateAvailableFrames();

        // La préférence utilisateur (2000) ne doit jamais être écrasée par la vidéo courte (800)
        Assert.Equal(2000, vm.StackFrameCount1);
    }

    [Fact]
    public void ProfileSerializer_RoundTrips_RetentionUnitAndFixedFrameCounts()
    {
        var options = new ProcessingOptions();
        options.Stacking.WorkflowMode = StackingWorkflowMode.StackingOnly;
        options.Stacking.RetentionUnit = FrameRetentionUnit.FrameCount;
        options.Stacking.StackFixedFrameCounts = [800, 1600];

        string json = ProfileSerializer.SerializeToJson(options);
        var loaded = ProfileSerializer.ParseFromJson(json);

        Assert.Equal(FrameRetentionUnit.FrameCount, loaded.Stacking.RetentionUnit);
        Assert.Equal([800, 1600], loaded.Stacking.StackFixedFrameCounts);
        Assert.Empty(loaded.Stacking.StackFrameCounts);
    }
}
