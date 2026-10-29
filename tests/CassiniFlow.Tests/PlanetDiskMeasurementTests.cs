using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using Xunit;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class PlanetDiskMeasurementTests
{
    [Fact]
    public void CalculateEquivalentDiameter_ReturnsExpectedDiameter()
    {
        // For a circle with radius R=50, Area = pi * 50^2 = 7853.98
        int pixelCount = (int)Math.Round(Math.PI * 50 * 50);
        double diameter = PlanetDiskCalculator.CalculateEquivalentDiameter(pixelCount, scale: 1.0);

        Assert.InRange(diameter, 99.0, 101.0);
    }

    [Fact]
    public void CalculateEquivalentDiameter_WithScaling_ReturnsUnscaledDiameter()
    {
        // If preview scale is 0.5 (preview is half size), detected pixel count corresponds to R=25 in preview (area ~1963)
        int previewPixelCount = (int)Math.Round(Math.PI * 25 * 25);
        double originalDiameter = PlanetDiskCalculator.CalculateEquivalentDiameter(previewPixelCount, scale: 0.5);

        Assert.InRange(originalDiameter, 99.0, 101.0);
    }

    [Fact]
    public void ComputeMedian_ReturnsExpectedMedian()
    {
        var odd = new List<double> { 50.0, 100.0, 75.0 };
        Assert.Equal(75.0, PlanetDiskCalculator.ComputeMedian(odd));

        var even = new List<double> { 10.0, 20.0, 30.0, 40.0 };
        Assert.Equal(25.0, PlanetDiskCalculator.ComputeMedian(even));

        var empty = new List<double>();
        Assert.Null(PlanetDiskCalculator.ComputeMedian(empty));
    }

    [Fact]
    public void MeasureDiameterPixels_WithPreviewFrameLoader_MeasuresSyntheticDisk()
    {
        int w = 200, h = 200, r = 30;
        var frame = new AstroFrame(w, h, 3, 16);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - 100;
                int dy = y - 100;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 40000;
                    frame.Data[idx + 1] = 40000;
                    frame.Data[idx + 2] = 40000;
                }
            }
        }

        var prevLoader = PlanetSizeAnalyzer.PreviewFrameLoader;
        try
        {
            PlanetSizeAnalyzer.PreviewFrameLoader = (path, frameIdx, maxDim) => (frame, w, h);
            var measured = PlanetSizeAnalyzer.MeasureDiameterPixels("test.ser", 10, samples: 3);

            Assert.NotNull(measured);
            // Expected diameter is ~60px (radius 30)
            Assert.InRange(measured.Value, 58.0, 62.0);
        }
        finally
        {
            PlanetSizeAnalyzer.PreviewFrameLoader = prevLoader;
        }
    }

    [Fact]
    public void SourceFileItem_IdentifiedObjectProperties_FormatCorrectly()
    {
        var item = new CassiniFlow.Desktop.ViewModels.SourceFileItem();
        Assert.False(item.HasIdentifiedObject);
        Assert.Equal(string.Empty, item.IdentifiedObjectBadge);
        Assert.Equal("—", item.IdentifiedObjectSummary);

        item.IdentifiedObjectText = "Jupiter (4 lunes détectées)";
        Assert.True(item.HasIdentifiedObject);
        Assert.Equal("🪐 Jupiter (4 lunes détectées)", item.IdentifiedObjectBadge);
        Assert.Equal("Jupiter (4 lunes détectées)", item.IdentifiedObjectSummary);
    }

    [Fact]
    public void MainWindowViewModel_MeasuringState_UpdatesGlobalStatusText()
    {
        var vm = new CassiniFlow.Desktop.ViewModels.MainWindowViewModel();
        Assert.False(vm.IsMeasuringPlanetSizes);
        Assert.True(vm.CanAddFiles);

        vm.IsMeasuringPlanetSizes = true;
        vm.MeasuringStatusText = "Mesure en cours (1/2)...";

        Assert.False(vm.CanAddFiles);
        Assert.Equal("Mesure en cours (1/2)...", vm.GlobalStatusText);

        vm.IsMeasuringPlanetSizes = false;
        Assert.True(vm.CanAddFiles);
    }

    [Fact]
    public async Task MainWindowViewModel_AnalyzePlanetSizes_IdentifiesObjectAndLogs()
    {
        var vm = new CassiniFlow.Desktop.ViewModels.MainWindowViewModel();
        var item = new CassiniFlow.Desktop.ViewModels.SourceFileItem
        {
            FileName = "mars_test.ser",
            FullPath = "e:\\mars_test.ser",
            Frames = 100
        };
        vm.SourceFiles.Add(item);

        int w = 200, h = 200, r = 25;
        var frame = new AstroFrame(w, h, 3, 16);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dx = x - 100;
                int dy = y - 100;
                if (dx * dx + dy * dy <= r * r)
                {
                    int idx = (y * w + x) * 3;
                    frame.Data[idx] = 45000;     // Red (Mars ocre)
                    frame.Data[idx + 1] = 25000; // Green
                    frame.Data[idx + 2] = 20000; // Blue
                }
            }
        }

        var prevLoader = PlanetSizeAnalyzer.PreviewFrameLoader;
        try
        {
            PlanetSizeAnalyzer.PreviewFrameLoader = (path, frameIdx, maxDim) => (frame, w, h);

            await vm.AnalyzePlanetSizes();

            Assert.True(item.HasIdentifiedObject);
            Assert.Contains("Mars", item.IdentifiedObjectText);
            Assert.Contains("🪐", item.IdentifiedObjectBadge);
            Assert.Contains("Mars", vm.ConsoleLogText);
            Assert.Contains("🪐", vm.ConsoleLogText);
        }
        finally
        {
            PlanetSizeAnalyzer.PreviewFrameLoader = prevLoader;
        }
    }
}
