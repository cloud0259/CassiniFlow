using System.IO;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using Xunit;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class CalibrationFrameTests
{
    [Fact]
    public void MasterFrameAccumulator_AveragesMultipleFramesCorrectly()
    {
        // Arrange
        var accumulator = new MasterFrameAccumulator();

        var frame1 = new AstroFrame(2, 2, 1);
        frame1.Data[0] = 100;
        frame1.Data[1] = 200;
        frame1.Data[2] = 300;
        frame1.Data[3] = 400;

        var frame2 = new AstroFrame(2, 2, 1);
        frame2.Data[0] = 200;
        frame2.Data[1] = 400;
        frame2.Data[2] = 600;
        frame2.Data[3] = 800;

        // Act
        accumulator.AddFrame(frame1);
        accumulator.AddFrame(frame2);
        var master = accumulator.ProduceMasterFrame();

        // Assert
        Assert.NotNull(master);
        Assert.Equal(2, accumulator.Count);
        Assert.Equal(150, master.Data[0]); // (100 + 200) / 2
        Assert.Equal(300, master.Data[1]); // (200 + 400) / 2
        Assert.Equal(450, master.Data[2]); // (300 + 600) / 2
        Assert.Equal(600, master.Data[3]); // (400 + 800) / 2
    }

    [Fact]
    public void MasterFrameAccumulator_WithFlatDark_SubtractsDarkBeforeAccumulating()
    {
        // Arrange
        var flatDark = new AstroFrame(2, 2, 1);
        flatDark.Data[0] = 50;
        flatDark.Data[1] = 100;
        flatDark.Data[2] = 150;
        flatDark.Data[3] = 200;

        var accumulator = new MasterFrameAccumulator(darkSubtractor: flatDark);

        var flat1 = new AstroFrame(2, 2, 1);
        flat1.Data[0] = 1000;
        flat1.Data[1] = 2000;
        flat1.Data[2] = 3000;
        flat1.Data[3] = 4000;

        // Act
        accumulator.AddFrame(flat1);
        var master = accumulator.ProduceMasterFrame();

        // Assert
        Assert.NotNull(master);
        Assert.Equal(950, master.Data[0]);  // 1000 - 50
        Assert.Equal(1900, master.Data[1]); // 2000 - 100
        Assert.Equal(2850, master.Data[2]); // 3000 - 150
        Assert.Equal(3800, master.Data[3]); // 4000 - 200
    }

    [Fact]
    public void ImageProcessor_ApplyCalibration_SubtractsDarkAndDividesFlat()
    {
        // Arrange
        var frame = new AstroFrame(2, 2, 1);
        frame.Data[0] = 1000;
        frame.Data[1] = 2000;
        frame.Data[2] = 3000;
        frame.Data[3] = 4000;

        var dark = new AstroFrame(2, 2, 1);
        dark.Data[0] = 100;
        dark.Data[1] = 200;
        dark.Data[2] = 300;
        dark.Data[3] = 400;

        // Flat with mean = 1000, pixel 0 vignette at 500
        var flat = new AstroFrame(2, 2, 1);
        flat.Data[0] = 500;
        flat.Data[1] = 1000;
        flat.Data[2] = 1000;
        flat.Data[3] = 1500;

        // Act
        ImageProcessor.ApplyCalibration(
            frame,
            dark,
            replaceHotPixels: false,
            hotPixelThresholdPercent: 35,
            darkHasBayer: false,
            flat);

        // Assert
        // Pixel 0: (1000 - 100) * (meanFlat(1000) / 500) = 900 * 2 = 1800
        Assert.Equal(1800, frame.Data[0]);

        // Pixel 1: (2000 - 200) * (1000 / 1000) = 1800
        Assert.Equal(1800, frame.Data[1]);
    }

    [Fact]
    public void MainWindowViewModel_CalibrationFiles_AreNotAddedToSourceFilesPreview()
    {
        // Arrange
        var vm = new MainWindowViewModel();

        string tempDark = Path.GetTempFileName();
        string tempFlat = Path.GetTempFileName();
        string tempFlatDark = Path.GetTempFileName();

        try
        {
            // Act: Add calibration files
            vm.AddDarkFiles([tempDark]);
            vm.AddFlatFiles([tempFlat]);
            vm.AddFlatDarkFiles([tempFlatDark]);

            // Assert: Calibration collections are populated
            Assert.Single(vm.DarkFiles);
            Assert.Single(vm.FlatFiles);
            Assert.Single(vm.FlatDarkFiles);
            Assert.True(vm.HasCalibrationFiles);
            Assert.Equal("1 dark(s)", vm.DarkCountText);
            Assert.Equal("1 flat(s)", vm.FlatCountText);
            Assert.Equal("1 flat-dark(s)", vm.FlatDarkCountText);

            // Assert: SourceFiles remains EMPTY! (Calibration files must NEVER be in preview/slider)
            Assert.Empty(vm.SourceFiles);

            // Sync options
            vm.SyncOptionsFromUi();
            Assert.True(vm.Options.DarkFrameSubtract);
            Assert.True(vm.Options.FlatFrameCalibrate);
            Assert.True(vm.Options.FlatDarkFrameSubtract);
            Assert.Single(vm.Options.DarkFiles);
            Assert.Single(vm.Options.FlatFiles);
            Assert.Single(vm.Options.FlatDarkFiles);
            Assert.Contains(Path.GetFileName(tempDark), vm.DarkFilesTooltip);
            Assert.Contains(Path.GetFileName(tempFlat), vm.FlatFilesTooltip);
            Assert.Contains(Path.GetFileName(tempFlatDark), vm.FlatDarkFilesTooltip);

            // Clear calibration
            vm.ClearAllCalibrationFiles();
            Assert.Empty(vm.DarkFiles);
            Assert.Empty(vm.FlatFiles);
            Assert.Empty(vm.FlatDarkFiles);
            Assert.False(vm.HasCalibrationFiles);
            Assert.Empty(vm.DarkFilesTooltip);
            Assert.Empty(vm.FlatFilesTooltip);
            Assert.Empty(vm.FlatDarkFilesTooltip);
        }
        finally
        {
            try { File.Delete(tempDark); } catch { }
            try { File.Delete(tempFlat); } catch { }
            try { File.Delete(tempFlatDark); } catch { }
        }
    }

    [Fact]
    public void MainWindowViewModel_RemoveIndividualCalibrationFile_UpdatesStateCorrectly()
    {
        var vm = new MainWindowViewModel();
        string tempDark1 = Path.GetTempFileName();
        string tempDark2 = Path.GetTempFileName();
        string tempFlat = Path.GetTempFileName();
        string tempFlatDark = Path.GetTempFileName();

        try
        {
            vm.AddDarkFiles([tempDark1, tempDark2]);
            vm.AddFlatFiles([tempFlat]);
            vm.AddFlatDarkFiles([tempFlatDark]);

            Assert.Equal(2, vm.DarkFiles.Count);
            Assert.Single(vm.FlatFiles);
            Assert.Single(vm.FlatDarkFiles);

            // Remove one dark
            vm.RemoveDarkFile(tempDark1);
            Assert.Single(vm.DarkFiles);
            Assert.Contains(tempDark2, vm.DarkFiles);
            Assert.DoesNotContain(tempDark1, vm.DarkFiles);

            // Remove flat
            vm.RemoveFlatFile(tempFlat);
            Assert.Empty(vm.FlatFiles);

            // Remove flat dark
            vm.RemoveFlatDarkFile(tempFlatDark);
            Assert.Empty(vm.FlatDarkFiles);

            // Remaining dark still marks calibration as active
            Assert.True(vm.HasCalibrationFiles);

            // Remove remaining dark
            vm.RemoveDarkFile(tempDark2);
            Assert.Empty(vm.DarkFiles);
            Assert.False(vm.HasCalibrationFiles);
        }
        finally
        {
            try { File.Delete(tempDark1); } catch { }
            try { File.Delete(tempDark2); } catch { }
            try { File.Delete(tempFlat); } catch { }
            try { File.Delete(tempFlatDark); } catch { }
        }
    }

    [Fact]
    public void MainWindowViewModel_SelectedSourceFileTabIndex_DefaultsToZeroAndCanBeSet()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(0, vm.SelectedSourceFileTabIndex);

        vm.SelectedSourceFileTabIndex = 1;
        Assert.Equal(1, vm.SelectedSourceFileTabIndex);

        vm.SelectedSourceFileTabIndex = 2;
        Assert.Equal(2, vm.SelectedSourceFileTabIndex);

        vm.SelectedSourceFileTabIndex = 3;
        Assert.Equal(3, vm.SelectedSourceFileTabIndex);
    }

    [Fact]
    public void Converters_ExtractFileNameAndFormatSizeCorrectly()
    {
        var nameConverter = CassiniFlow.Desktop.Converters.FileNameConverter.Instance;
        var sizeConverter = CassiniFlow.Desktop.Converters.FileSizeConverter.Instance;

        string temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, new byte[2048]); // 2 KB

            var fileName = nameConverter.Convert(temp, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(Path.GetFileName(temp), fileName);

            var sizeText = sizeConverter.Convert(temp, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("2.0 KB", sizeText);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}

