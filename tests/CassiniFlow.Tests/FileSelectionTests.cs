using CassiniFlow.Desktop.ViewModels;
using Xunit;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class FileSelectionTests
{
    [Fact]
    public void SourceFileItem_IsCheckedByDefault()
    {
        var item = new SourceFileItem();
        Assert.True(item.IsChecked, "Newly created SourceFileItem should have IsChecked = true by default.");
    }

    [Fact]
    public void MainWindowViewModel_AreAllFilesChecked_ReflectsItemCheckedState()
    {
        var vm = TestFactory.CreateMainWindowViewModel();

        var item1 = new SourceFileItem { FileName = "item1.ser", FullPath = "c:\\item1.ser", Frames = 100 };
        var item2 = new SourceFileItem { FileName = "item2.ser", FullPath = "c:\\item2.ser", Frames = 200 };

        vm.SourceFiles.Add(item1);
        vm.SourceFiles.Add(item2);

        // Both checked by default
        Assert.True(item1.IsChecked);
        Assert.True(item2.IsChecked);
        Assert.True(vm.AreAllFilesChecked);

        // Uncheck item1 -> intermediate state (null)
        item1.IsChecked = false;
        Assert.Null(vm.AreAllFilesChecked);

        // Uncheck item2 -> all unchecked (false)
        item2.IsChecked = false;
        Assert.False(vm.AreAllFilesChecked);

        // Re-check item1 -> intermediate state (null)
        item1.IsChecked = true;
        Assert.Null(vm.AreAllFilesChecked);

        // Re-check item2 -> all checked (true)
        item2.IsChecked = true;
        Assert.True(vm.AreAllFilesChecked);
    }

    [Fact]
    public void UpdateAvailableFrames_ConsidersOnlyCheckedFiles()
    {
        var vm = TestFactory.CreateMainWindowViewModel();
        vm.IsJoinMode = true;

        var item1 = new SourceFileItem { FileName = "item1.ser", FullPath = "c:\\item1.ser", Frames = 100 };
        var item2 = new SourceFileItem { FileName = "item2.ser", FullPath = "c:\\item2.ser", Frames = 200 };

        vm.SourceFiles.Add(item1);
        vm.SourceFiles.Add(item2);

        // Both checked -> in join mode sum is 300
        Assert.Equal(300, vm.MaxAvailableFrames);

        // Uncheck item2 -> only item1 (100) is considered
        item2.IsChecked = false;
        Assert.Equal(100, vm.MaxAvailableFrames);
    }

    [Fact]
    public async Task RunProcessingAsync_AbortsWhenNoFilesChecked()
    {
        var vm = TestFactory.CreateMainWindowViewModel();

        var item1 = new SourceFileItem { FileName = "item1.ser", FullPath = "c:\\item1.ser", Frames = 100, IsChecked = false };
        var item2 = new SourceFileItem { FileName = "item2.ser", FullPath = "c:\\item2.ser", Frames = 200, IsChecked = false };

        vm.SourceFiles.Add(item1);
        vm.SourceFiles.Add(item2);

        await vm.RunProcessingAsync();

        Assert.False(vm.IsProcessing);
        Assert.True(vm.ConsoleLogText.Contains("sélectionné") || vm.ConsoleLogText.Contains("selected"));
    }

    [Fact]
    public void UpdateAvailableFrames_ReflectsCustomInOutRanges()
    {
        var vm = TestFactory.CreateMainWindowViewModel();
        vm.IsJoinMode = true;

        var item1 = new SourceFileItem
        {
            FileName = "item1.ser",
            FullPath = "c:\\item1.ser",
            Frames = 1000,
            SelectionIn = 1,
            SelectionOut = 500 // 500 frames
        };
        var item2 = new SourceFileItem
        {
            FileName = "item2.ser",
            FullPath = "c:\\item2.ser",
            Frames = 870,
            SelectionIn = 1,
            SelectionOut = 328 // 328 frames
        };

        vm.SourceFiles.Add(item1);
        vm.SourceFiles.Add(item2);

        // In join mode: 500 + 328 = 828 frames (matching user scenario)
        Assert.Equal(828, vm.MaxAvailableFrames);
        Assert.Equal(828, vm.StackFrameCount);

        // Adjusting SelectionIn/SelectionOut dynamically updates the total
        item1.SelectionIn = 101; // now 101..500 = 400 frames
        Assert.Equal(728, vm.MaxAvailableFrames);
        Assert.Equal(728, vm.StackFrameCount);
    }

    [Fact]
    public void ProcessingOptions_GetEffectiveFrameRange_HonorsCustomRangesAndLimits()
    {
        var options = new CassiniFlow.Domain.Models.ProcessingOptions();
        string fileA = "c:\\videoA.ser";
        string fileB = "c:\\videoB.ser";

        // Default: raw frame count
        var rangeA1 = options.GetEffectiveFrameRange(fileA, 1000);
        Assert.Equal(1, rangeA1.StartFrame);
        Assert.Equal(1000, rangeA1.EndFrame);
        Assert.Equal(1000, rangeA1.EffectiveCount);

        // Custom FileFrameRanges takes priority
        options.FileFrameRanges[fileA] = (10, 509); // 500 frames
        var rangeA2 = options.GetEffectiveFrameRange(fileA, 1000);
        Assert.Equal(10, rangeA2.StartFrame);
        Assert.Equal(509, rangeA2.EndFrame);
        Assert.Equal(500, rangeA2.EffectiveCount);

        // LimitFrameRange applies when no custom range
        options.LimitFrameRange = true;
        options.StartFrame = 50;
        options.EndFrame = 149; // 100 frames

        // fileA still has custom range
        var rangeA3 = options.GetEffectiveFrameRange(fileA, 1000);
        Assert.Equal(500, rangeA3.EffectiveCount);

        // fileB uses global LimitFrameRange
        var rangeB = options.GetEffectiveFrameRange(fileB, 800);
        Assert.Equal(50, rangeB.StartFrame);
        Assert.Equal(149, rangeB.EndFrame);
        Assert.Equal(100, rangeB.EffectiveCount);
    }

    [Fact]
    public void SyncOptionsFromUi_PopulatesAllFileFrameRangesAndUpdatesAvailable()
    {
        var vm = TestFactory.CreateMainWindowViewModel();
        vm.IsJoinMode = true;

        var item1 = new SourceFileItem
        {
            FileName = "item1.ser",
            FullPath = "c:\\item1.ser",
            Frames = 1000,
            SelectionIn = 1,
            SelectionOut = 500
        };
        var item2 = new SourceFileItem
        {
            FileName = "item2.ser",
            FullPath = "c:\\item2.ser",
            Frames = 870,
            SelectionIn = 1,
            SelectionOut = 328
        };

        vm.SourceFiles.Add(item1);
        vm.SourceFiles.Add(item2);

        vm.SyncOptionsFromUi();

        Assert.Equal(2, vm.Options.FileFrameRanges.Count);
        Assert.Equal((1, 500), vm.Options.FileFrameRanges["c:\\item1.ser"]);
        Assert.Equal((1, 328), vm.Options.FileFrameRanges["c:\\item2.ser"]);

        // Sum of effective counts is 828 (not 1870)
        int sum = vm.Options.GetEffectiveFrameRange("c:\\item1.ser", 1000).EffectiveCount
                + vm.Options.GetEffectiveFrameRange("c:\\item2.ser", 870).EffectiveCount;
        Assert.Equal(828, sum);
    }
}
