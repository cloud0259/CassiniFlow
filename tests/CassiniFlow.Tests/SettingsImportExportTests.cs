using System;
using System.IO;
using System.Threading.Tasks;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Serialization;
using Xunit;

namespace CassiniFlow.Tests;

public class SettingsImportExportTests
{
    [Fact]
    public void SerializeAndDeserializeJson_Roundtrip_PreservesSettings()
    {
        var options = new ProcessingOptions
        {
            RunMode = ProcessingRunMode.JoinMode,
            DebayerEnabled = true,
            DebayerPattern = BayerPattern.Gbrg,
            EnableCropping = true,
            CropWidth = 512,
            CropHeight = 512,
            InputGain = 1.35m,
            Gamma = 1.15m,
            EnableQualityEstimation = true,
            QualityFramesToKeep = 150
        };
        options.Stacking.AlignmentTileSize = 104;
        options.Stacking.MinBrightnessThreshold = 25;
        options.Stacking.StackPercentages = [15.0, 30.0];

        string json = ProfileSerializer.SerializeToJson(options);
        Assert.NotNull(json);
        Assert.Contains("512", json);

        var restored = ProfileSerializer.ParseFromJson(json);
        Assert.Equal(ProcessingRunMode.JoinMode, restored.RunMode);
        Assert.True(restored.DebayerEnabled);
        Assert.Equal(BayerPattern.Gbrg, restored.DebayerPattern);
        Assert.True(restored.EnableCropping);
        Assert.Equal(512, restored.CropWidth);
        Assert.Equal(512, restored.CropHeight);
        Assert.Equal(1.35m, restored.InputGain);
        Assert.Equal(1.15m, restored.Gamma);
        Assert.True(restored.EnableQualityEstimation);
        Assert.Equal(150, restored.QualityFramesToKeep);
        Assert.Equal(104, restored.Stacking.AlignmentTileSize);
        Assert.Equal(25, restored.Stacking.MinBrightnessThreshold);
        Assert.Equal(2, restored.Stacking.StackPercentages.Count);
        Assert.Equal(15.0, restored.Stacking.StackPercentages[0]);
    }

    [Fact]
    public async Task SaveAndLoadFromFile_Roundtrip_WorksForJsonAndPof()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "cf_settings_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var options = new ProcessingOptions
            {
                RunMode = ProcessingRunMode.BatchMode,
                EnableCropping = true,
                CropWidth = 320,
                CropHeight = 320,
                HotPixelThreshold = 42
            };

            string jsonFile = Path.Combine(tempDir, "preset.cfset");
            ProfileSerializer.SaveToFile(options, jsonFile);
            Assert.True(File.Exists(jsonFile));

            var loadedJson = ProfileSerializer.LoadFromFile(jsonFile);
            Assert.Equal(320, loadedJson.CropWidth);
            Assert.Equal(42, loadedJson.HotPixelThreshold);

            string pofFile = Path.Combine(tempDir, "preset.pof");
            ProfileSerializer.SaveToFile(options, pofFile);
            Assert.True(File.Exists(pofFile));

            var loadedPof = ProfileSerializer.LoadFromFile(pofFile);
            Assert.Equal(320, loadedPof.CropWidth);
            Assert.Equal(42, loadedPof.HotPixelThreshold);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task MainWindowViewModel_ImportAndExport_UpdatesState()
    {
        var vm = TestFactory.CreateMainWindowViewModel();

        string tempDir = Path.Combine(Path.GetTempPath(), "cf_vm_settings_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            vm.CropWidth = 640;
            vm.CropHeight = 640;
            vm.EnableCropping = true;
            vm.InputGain = 1.25;
            vm.SelectedBayerPatternIndex = 1; // Grbg

            string file = Path.Combine(tempDir, "test_vm_preset.cfset");
            await vm.ExportSettingsToFileAsync(file);
            Assert.True(File.Exists(file));

            // Reset vm properties
            vm.CropWidth = 128;
            vm.CropHeight = 128;
            vm.InputGain = 1.0;

            await vm.ImportSettingsFromFileAsync(file);
            Assert.Equal(640, vm.CropWidth);
            Assert.Equal(640, vm.CropHeight);
            Assert.Equal(1.25, vm.InputGain);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
