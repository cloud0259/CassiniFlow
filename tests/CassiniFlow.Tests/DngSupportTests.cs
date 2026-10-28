using OpenCvSharp;
using Xunit;
using CassiniFlow.Infrastructure.Adapters;

namespace CassiniFlow.Tests;

public class DngSupportTests
{
    [Fact]
    public void ImageExtensions_ContainsDng()
    {
        Assert.Contains(".dng", UniversalMediaReader.ImageExtensions);
    }

    [Fact]
    public void UniversalMediaReader_CanInspectAndRead_DngFile()
    {
        if (!UniversalMediaReader.IsOpenCvNativeAvailable)
        {
            // On environments without native OpenCV backend, verify format extension is registered
            Assert.Contains(".dng", UniversalMediaReader.ImageExtensions);
            return;
        }

        string tempTif = Path.Combine(Path.GetTempPath(), $"dng_test_{Guid.NewGuid():N}.tif");
        string tempDng = Path.ChangeExtension(tempTif, ".dng");

        try
        {
            using (var mat = new Mat(120, 160, MatType.CV_8UC3, new Scalar(100, 150, 200)))
            {
                Cv2.ImWrite(tempTif, mat);
            }

            File.Copy(tempTif, tempDng, true);

            // 1. Inspect
            var summary = UniversalMediaReader.InspectFile(tempDng);
            Assert.NotNull(summary);
            Assert.Equal(160, summary.Width);
            Assert.Equal(120, summary.Height);
            Assert.Equal(1, summary.FrameCount);
            Assert.StartsWith("DNG", summary.FormatBadge);

            // 2. ReadPreviewFrame
            var preview = UniversalMediaReader.ReadPreviewFrame(tempDng, 0, maxPreviewDim: 100);
            Assert.NotNull(preview);
            Assert.NotNull(preview.Frame);
            Assert.True(preview.Frame.Width > 0);
            Assert.True(preview.Frame.Height > 0);

            // 3. ReadSingleFrame
            var frame = UniversalMediaReader.ReadSingleFrame(tempDng, 0);
            Assert.NotNull(frame);
            Assert.Equal(160, frame.Width);
            Assert.Equal(120, frame.Height);

            // 4. EnumerateFrames
            var frames = UniversalMediaReader.EnumerateFrames(tempDng, 0, 0).ToList();
            Assert.Single(frames);
        }
        finally
        {
            if (File.Exists(tempTif)) File.Delete(tempTif);
            if (File.Exists(tempDng)) File.Delete(tempDng);
        }
    }

    [Fact]
    public void UniversalMediaReader_CanReadCameraDng_WithSubIfdAndJpegStream_AndExtractMetadata()
    {
        string path = @"d:\Astro\Soleil\Eclipse\12082026\raw\PXL_20260812_172214874.RAW-02.ORIGINAL.dng";
        if (!File.Exists(path)) return; // Test is executed if file is present on local machine

        // 1. Inspect
        var summary = UniversalMediaReader.InspectFile(path);
        Assert.NotNull(summary);
        Assert.StartsWith("DNG", summary.FormatBadge);
        Assert.True(summary.Width > 0);
        Assert.True(summary.Height > 0);

        // Verify deep metadata extraction (standard EXIF tags)
        Assert.NotNull(summary.Metadata);
        Assert.False(string.IsNullOrEmpty(summary.Metadata.Camera));
        Assert.Equal(100, summary.Metadata.Iso);
        Assert.True(summary.Metadata.ExposureTimeSeconds.HasValue);
        Assert.Contains("1/2023", summary.Metadata.EffectiveShutterSpeedText);
        Assert.True(summary.Metadata.EffectiveExposureText.Contains("494") || summary.Metadata.EffectiveExposureText.Contains("µs") || summary.Metadata.EffectiveExposureText.Contains("ms"));

        // 2. ReadPreviewFrame
        var preview = UniversalMediaReader.ReadPreviewFrame(path, 0, maxPreviewDim: 640);
        Assert.NotNull(preview);
        Assert.NotNull(preview.Frame);
        Assert.True(preview.Frame.Width <= 640);
        Assert.True(preview.Frame.Height <= 640);

        // 3. ReadSingleFrame
        var single = UniversalMediaReader.ReadSingleFrame(path, 0);
        Assert.NotNull(single);
        Assert.Equal(summary.Width, single.Width);
        Assert.Equal(summary.Height, single.Height);

        // 4. EnumerateFrames
        var enumerated = UniversalMediaReader.EnumerateFrames(path, 0, 0).ToList();
        Assert.Single(enumerated);
        Assert.Equal(summary.Width, enumerated[0].Width);
    }
}
