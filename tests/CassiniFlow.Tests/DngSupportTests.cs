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
}
