using CassiniFlow.Desktop.ViewModels;
using Xunit;

namespace CassiniFlow.Tests;

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
        var vm = new MainWindowViewModel();

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
        var vm = new MainWindowViewModel();
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
        var vm = new MainWindowViewModel();

        var item1 = new SourceFileItem { FileName = "item1.ser", FullPath = "c:\\item1.ser", Frames = 100, IsChecked = false };
        var item2 = new SourceFileItem { FileName = "item2.ser", FullPath = "c:\\item2.ser", Frames = 200, IsChecked = false };

        vm.SourceFiles.Add(item1);
        vm.SourceFiles.Add(item2);

        await vm.RunProcessingAsync();

        Assert.False(vm.IsProcessing);
        Assert.Contains("Aucun fichier n'est sélectionné", vm.ConsoleLogText);
    }
}

