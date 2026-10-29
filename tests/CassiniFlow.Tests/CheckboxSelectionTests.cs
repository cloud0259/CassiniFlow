using System.Collections.ObjectModel;
using CassiniFlow.Desktop.ViewModels;
using Xunit;

namespace CassiniFlow.Tests;

public class CheckboxSelectionTests
{
    [Fact]
    public void AreAllFilesChecked_EmptyList_ReturnsFalse()
    {
        var vm = new MainWindowViewModel();
        Assert.Empty(vm.SourceFiles);
        Assert.False(vm.AreAllFilesChecked);
    }

    [Fact]
    public void AreAllFilesChecked_AllChecked_ReturnsTrue()
    {
        var vm = new MainWindowViewModel();
        var f1 = new SourceFileItem { FileName = "file1.ser", IsChecked = true };
        var f2 = new SourceFileItem { FileName = "file2.ser", IsChecked = true };
        vm.SourceFiles.Add(f1);
        vm.SourceFiles.Add(f2);

        Assert.True(vm.AreAllFilesChecked);
    }

    [Fact]
    public void AreAllFilesChecked_NoneChecked_ReturnsFalse()
    {
        var vm = new MainWindowViewModel();
        var f1 = new SourceFileItem { FileName = "file1.ser", IsChecked = false };
        var f2 = new SourceFileItem { FileName = "file2.ser", IsChecked = false };
        vm.SourceFiles.Add(f1);
        vm.SourceFiles.Add(f2);

        Assert.False(vm.AreAllFilesChecked);
    }

    [Fact]
    public void AreAllFilesChecked_PartiallyChecked_ReturnsNullIndeterminate()
    {
        var vm = new MainWindowViewModel();
        var f1 = new SourceFileItem { FileName = "file1.ser", IsChecked = true };
        var f2 = new SourceFileItem { FileName = "file2.ser", IsChecked = false };
        vm.SourceFiles.Add(f1);
        vm.SourceFiles.Add(f2);

        Assert.Null(vm.AreAllFilesChecked);
    }

    [Fact]
    public void AreAllFilesChecked_DynamicItemIsCheckedChanged_UpdatesGlobalState()
    {
        var vm = new MainWindowViewModel();
        var f1 = new SourceFileItem { FileName = "file1.ser", IsChecked = true };
        var f2 = new SourceFileItem { FileName = "file2.ser", IsChecked = true };
        vm.SourceFiles.Add(f1);
        vm.SourceFiles.Add(f2);
        Assert.True(vm.AreAllFilesChecked);

        // User unchecks one item
        f2.IsChecked = false;
        Assert.Null(vm.AreAllFilesChecked);

        // User unchecks remaining item
        f1.IsChecked = false;
        Assert.False(vm.AreAllFilesChecked);

        // User checks both again
        f1.IsChecked = true;
        f2.IsChecked = true;
        Assert.True(vm.AreAllFilesChecked);
    }

    [Fact]
    public void AreAllFilesChecked_SetterTrue_ChecksAllFiles()
    {
        var vm = new MainWindowViewModel();
        var f1 = new SourceFileItem { FileName = "file1.ser", IsChecked = false };
        var f2 = new SourceFileItem { FileName = "file2.ser", IsChecked = false };
        vm.SourceFiles.Add(f1);
        vm.SourceFiles.Add(f2);

        // User checks global checkbox
        vm.AreAllFilesChecked = true;

        Assert.True(f1.IsChecked);
        Assert.True(f2.IsChecked);
        Assert.True(vm.AreAllFilesChecked);
    }

    [Fact]
    public void AreAllFilesChecked_SetterFalse_UnchecksAllFiles()
    {
        var vm = new MainWindowViewModel();
        var f1 = new SourceFileItem { FileName = "file1.ser", IsChecked = true };
        var f2 = new SourceFileItem { FileName = "file2.ser", IsChecked = true };
        vm.SourceFiles.Add(f1);
        vm.SourceFiles.Add(f2);

        // User unchecks global checkbox
        vm.AreAllFilesChecked = false;

        Assert.False(f1.IsChecked);
        Assert.False(f2.IsChecked);
        Assert.False(vm.AreAllFilesChecked);
    }

    [Fact]
    public void ToggleCheckAllFiles_TogglesBetweenAllCheckedAndUnchecked()
    {
        var vm = new MainWindowViewModel();
        var f1 = new SourceFileItem { FileName = "file1.ser", IsChecked = false };
        var f2 = new SourceFileItem { FileName = "file2.ser", IsChecked = false };
        vm.SourceFiles.Add(f1);
        vm.SourceFiles.Add(f2);

        // Toggle when none checked -> all checked
        vm.ToggleCheckAllFiles();
        Assert.True(f1.IsChecked);
        Assert.True(f2.IsChecked);
        Assert.True(vm.AreAllFilesChecked);

        // Toggle when all checked -> all unchecked
        vm.ToggleCheckAllFiles();
        Assert.False(f1.IsChecked);
        Assert.False(f2.IsChecked);
        Assert.False(vm.AreAllFilesChecked);
    }
}
