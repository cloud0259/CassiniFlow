using System.Diagnostics;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Infrastructure.Adapters;
using OpenCvSharp;
using Xunit;

namespace CassiniFlow.Tests;

public class FastImageLoadingTests
{
    [Fact]
    public void FastImageHeaderReader_InspectsPngJpegBmpTiffAccurately()
    {
        if (!UniversalMediaReader.IsOpenCvNativeAvailable) return;

        string dir = Path.Combine(Path.GetTempPath(), $"fast_img_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            string pngPath = Path.Combine(dir, "test.png");
            string jpgPath = Path.Combine(dir, "test.jpg");
            string bmpPath = Path.Combine(dir, "test.bmp");
            string tifPath = Path.Combine(dir, "test.tif");

            using (var mat = new Mat(200, 300, MatType.CV_8UC3, new Scalar(50, 100, 150)))
            {
                Cv2.ImWrite(pngPath, mat);
                Cv2.ImWrite(jpgPath, mat);
                Cv2.ImWrite(bmpPath, mat);
                Cv2.ImWrite(tifPath, mat);
            }

            // PNG
            Assert.True(FastImageHeaderReader.TryInspect(pngPath, out var pngSummary));
            Assert.NotNull(pngSummary);
            Assert.Equal(300, pngSummary.Width);
            Assert.Equal(200, pngSummary.Height);

            // JPEG
            Assert.True(FastImageHeaderReader.TryInspect(jpgPath, out var jpgSummary));
            Assert.NotNull(jpgSummary);
            Assert.Equal(300, jpgSummary.Width);
            Assert.Equal(200, jpgSummary.Height);

            // BMP
            Assert.True(FastImageHeaderReader.TryInspect(bmpPath, out var bmpSummary));
            Assert.NotNull(bmpSummary);
            Assert.Equal(300, bmpSummary.Width);
            Assert.Equal(200, bmpSummary.Height);

            // TIFF
            Assert.True(FastImageHeaderReader.TryInspect(tifPath, out var tifSummary));
            Assert.NotNull(tifSummary);
            Assert.Equal(300, tifSummary.Width);
            Assert.Equal(200, tifSummary.Height);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FastImageHeaderReader_IsExtremelyFastForHundredsOfImages()
    {
        if (!UniversalMediaReader.IsOpenCvNativeAvailable) return;

        string dir = Path.Combine(Path.GetTempPath(), $"fast_batch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            string template = Path.Combine(dir, "template.png");
            using (var mat = new Mat(480, 640, MatType.CV_8UC3, new Scalar(10, 20, 30)))
            {
                Cv2.ImWrite(template, mat);
            }

            int count = 100;
            var paths = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string copyPath = Path.Combine(dir, $"frame_{i:D4}.png");
                File.Copy(template, copyPath, true);
                paths.Add(copyPath);
            }

            var sw = Stopwatch.StartNew();
            int inspected = 0;
            foreach (var path in paths)
            {
                if (FastImageHeaderReader.TryInspect(path, out var summary) && summary != null)
                {
                    inspected++;
                }
            }
            sw.Stop();

            Assert.Equal(count, inspected);
            // 100 images must be inspected in under 1500 ms (typically < 30ms locally, with buffer for CI runners)
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Inspecting {count} images took {sw.ElapsedMilliseconds} ms, which is too slow!");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MainWindowViewModel_HasLoadingFileProperties()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.IsLoadingFiles);
        Assert.True(vm.CanAddFiles);
        Assert.Equal(string.Empty, vm.LoadingStatusText);
    }
}
