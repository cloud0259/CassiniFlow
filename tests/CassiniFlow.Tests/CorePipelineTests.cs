using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Application.Pipeline;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Codecs;
using CassiniFlow.Infrastructure.Serialization;
using CassiniFlow.Infrastructure.Synthetic;
using CassiniFlow.Infrastructure.WinJupos;

namespace CassiniFlow.Tests;

public class CorePipelineTests
{
    [Fact]
    public void PofSerializer_RoundTrip_PreservesAllKeyOptions()
    {
        var original = new ProcessingOptions
        {
            RunMode = ProcessingRunMode.JoinMode,
            ActivePreset = OptimisationPreset.Planetary,
            DebayerEnabled = true,
            DebayerPattern = BayerPattern.Gbrg,
            CheckPlanetObjectPresent = true,
            CentreObjectInFrame = true,
            EnableCropping = true,
            CropWidth = 512,
            CropHeight = 512,
            EnableQualityEstimation = true,
            EnableQualityLimiting = true,
            QualityLimitingMode = QualityLimitingType.PercentageOfFrames,
            QualityFramesToKeep = 30,
            OutputFormat = OutputFormat.Ser,
            GenerateWinJuposFilenames = true
        };

        string pofText = ProfileSerializer.SerializeToPof(original);
        Assert.StartsWith(ProfileSerializer.Header, pofText);

        var parsed = ProfileSerializer.ParseFromPof(pofText);
        Assert.Equal(ProcessingRunMode.JoinMode, parsed.RunMode);
        Assert.Equal(OptimisationPreset.Planetary, parsed.ActivePreset);
        Assert.Equal(BayerPattern.Gbrg, parsed.DebayerPattern);
        Assert.Equal(512, parsed.CropWidth);
        Assert.Equal(30, parsed.QualityFramesToKeep);
        Assert.Equal(OutputFormat.Ser, parsed.OutputFormat);
        Assert.True(parsed.GenerateWinJuposFilenames);

        var cliArgs = ProfileSerializer.GenerateCommandLineArguments(parsed);
        Assert.Contains("-join", cliArgs);
        Assert.Contains("-ctr", cliArgs);
        Assert.Contains("-qpcnt=30", cliArgs);
        Assert.Contains("-ser", cliArgs);
    }

    [Fact]
    public void SerFile_WriteAndRead_Preserves16BitFramesAndTimestamps()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.ser");
        try
        {
            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 4, width: 128, height: 128);
            var hdr = new SerHeader
            {
                ImageWidth = 128,
                ImageHeight = 128,
                PixelDepthPerPlane = 16,
                ColorId = SerColorId.Rgb,
                FrameCount = frames.Count,
                Observer = "UnitTest",
                DateTimeUtcTicks = frames[0].TimestampUtc!.Value.Ticks
            };

            SerFile.WriteSerFile(tempFile, hdr, frames);

            using var reader = new SerFile(tempFile);
            Assert.Equal(4, reader.Header.FrameCount);
            Assert.Equal(128, reader.Header.ImageWidth);
            Assert.True(reader.Header.HasTrailerTimestamps);

            var f0 = reader.ReadFrame(0);
            Assert.Equal(frames[0].Data[500], f0.Data[500]);
            Assert.Equal(frames[0].TimestampUtc, f0.TimestampUtc);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void FitsFile_WriteAndRead_PreservesImageDimensionsAndPixels()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.fit");
        try
        {
            var seq = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.LunarClaviusMono, frameCount: 1, width: 96, height: 96);
            FitsFile.WriteFrame(tempFile, seq[0]);

            var loaded = FitsFile.ReadFrame(tempFile);
            Assert.Equal(96, loaded.Width);
            Assert.Equal(96, loaded.Height);
            Assert.Equal(1, loaded.Channels);
            Assert.Equal(seq[0].Data[100], loaded.Data[100]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void WinJuposHelper_ExtractsTimestampAndFormatsPrefix()
    {
        bool ok = WinJuposHelper.TryExtractDateTime("2026-10-24-2145_3-Jup_ZWO_ASI678MC.ser", false, out var dt);
        Assert.True(ok);
        Assert.Equal(2026, dt.Year);
        Assert.Equal(10, dt.Month);
        Assert.Equal(24, dt.Day);
        Assert.Equal(21, dt.Hour);
        Assert.Equal(45, dt.Minute);

        string prefix = WinJuposHelper.FormatWinJuposPrefix(dt);
        Assert.Equal("2026-10-24-2145_3", prefix);
    }

    [Fact]
    public async Task CassiniFlowPipelineRunner_ExecutesFullPlanetaryWorkflow()
    {
        string tempOutDir = Path.Combine(Path.GetTempPath(), $"CassiniFlow_run_{Guid.NewGuid():N}");
        try
        {
            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 12, width: 200, height: 200);
            var options = new ProcessingOptions();
            options.ApplyPreset(OptimisationPreset.Planetary);
            options.CropWidth = 120;
            options.CropHeight = 120;
            options.EnableQualityLimiting = true;
            options.QualityLimitingMode = QualityLimitingType.PercentageOfFrames;
            options.QualityFramesToKeep = 50;

            var summary = await PipelineRunner.ExecuteAsync(frames, options, tempOutDir);
            Assert.True(summary.Success);
            Assert.Equal(6, summary.OutputFramesWritten);
            Assert.True(File.Exists(summary.OutputFilePath));
        }
        finally
        {
            if (Directory.Exists(tempOutDir)) Directory.Delete(tempOutDir, true);
        }
    }

    [Fact]
    public void UniversalMediaReader_InspectsAndDecodesAviVideo()
    {
        string tempAvi = Path.Combine(Path.GetTempPath(), $"test_video_{Guid.NewGuid():N}.avi");
        string tempMp4 = Path.Combine(Path.GetTempPath(), $"test_video_{Guid.NewGuid():N}.mp4");
        try
        {
            var seq = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.SaturnRingsRgb, frameCount: 6, width: 160, height: 120);
            UniversalMediaReader.WriteVideoFile(tempAvi, seq, fps: 30, useMp4Container: false);
            UniversalMediaReader.WriteVideoFile(tempMp4, seq, fps: 30, useMp4Container: true);

            var infoAvi = UniversalMediaReader.InspectFile(tempAvi);
            Assert.Equal(6, infoAvi.FrameCount);
            Assert.Equal(160, infoAvi.Width);
            Assert.Equal(120, infoAvi.Height);

            var singleFrame = UniversalMediaReader.ReadSingleFrame(tempAvi, 2);
            Assert.Equal(160, singleFrame.Width);
            Assert.Equal(120, singleFrame.Height);

            var infoMp4 = UniversalMediaReader.InspectFile(tempMp4);
            Assert.True(infoMp4.FrameCount >= 5);
            Assert.Equal(160, infoMp4.Width);
            Assert.Equal(120, infoMp4.Height);
        }
        finally
        {
            UniversalMediaReader.ClosePreviewCache();
            if (File.Exists(tempAvi)) File.Delete(tempAvi);
            if (File.Exists(tempMp4)) File.Delete(tempMp4);
        }
    }

    [Fact]
    public async Task ExecuteFilesAsync_JoinModeWithQualityLimitingAndSerOutput_WritesAllSelectedFrames()
    {
        string tempMp4A = Path.Combine(Path.GetTempPath(), $"test_joinA_{Guid.NewGuid():N}.mp4");
        string tempMp4B = Path.Combine(Path.GetTempPath(), $"test_joinB_{Guid.NewGuid():N}.mp4");
        string tempOutDir = Path.Combine(Path.GetTempPath(), $"CassiniFlow_join_ser_{Guid.NewGuid():N}");
        try
        {
            var seq = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.SaturnRingsRgb, frameCount: 8, width: 320, height: 240);
            UniversalMediaReader.WriteVideoFile(tempMp4A, seq, fps: 30, useMp4Container: true);
            UniversalMediaReader.WriteVideoFile(tempMp4B, seq, fps: 30, useMp4Container: true);

            var options = new ProcessingOptions
            {
                RunMode = ProcessingRunMode.JoinMode,
                StabilisationMode = StabilisationMode.ObjectPlanetary,
                CheckPlanetObjectPresent = true,
                CentreObjectInFrame = true,
                EnableCropping = true,
                CropWidth = 160,
                CropHeight = 160,
                EnableQualityEstimation = true,
                QualityReorderFrames = true,
                EnableQualityLimiting = true,
                QualityLimitingMode = QualityLimitingType.PercentageOfFrames,
                QualityFramesToKeep = 50,
                OutputFormat = OutputFormat.Ser
            };

            var summary = await PipelineRunner.ExecuteFilesAsync([tempMp4A, tempMp4B], options, tempOutDir);
            Assert.True(summary.Success);
            Assert.True(summary.OutputFramesWritten >= 6);
            Assert.True(File.Exists(summary.OutputFilePath));

            using var ser = new SerFile(summary.OutputFilePath);
            Assert.Equal(8, ser.Header.FrameCount);
        }
        finally
        {
            UniversalMediaReader.ClosePreviewCache();
            if (File.Exists(tempMp4A)) File.Delete(tempMp4A);
            if (File.Exists(tempMp4B)) File.Delete(tempMp4B);
            if (Directory.Exists(tempOutDir)) Directory.Delete(tempOutDir, true);
        }
    }

    [Fact]
    public async Task Pipeline_CropAndRotate_ProducesExpectedOutput()
    {
        string tempAvi = Path.Combine(Path.GetTempPath(), $"test_in_{Guid.NewGuid():N}.avi");
        string tempOutDir = Path.Combine(Path.GetTempPath(), $"test_out_{Guid.NewGuid():N}");
        try
        {
            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 4, width: 200, height: 200);
            UniversalMediaReader.WriteVideoFile(tempAvi, frames, 25.0, false);

            var options = new ProcessingOptions
            {
                EnableCropping = true,
                CropWidth = 64,
                CropHeight = 64,
                OutputFormat = OutputFormat.Bmp,
                Rotation = RotationAngle.Rotate90Clockwise,
                FlipHorizontalX = true
            };

            var summary = await PipelineRunner.ExecuteFilesAsync([tempAvi], options, tempOutDir);
            Assert.True(summary.Success);
            Assert.Equal(4, summary.OutputFramesWritten);

            var bmpFiles = Directory.GetFiles(tempOutDir, "*.bmp");
            Assert.Equal(4, bmpFiles.Length);
            foreach (var bmp in bmpFiles)
            {
                var loaded = BmpAndAviWriter.ReadBmp(bmp);
                Assert.Equal(64, loaded.Width);
                Assert.Equal(64, loaded.Height);
            }
        }
        finally
        {
            UniversalMediaReader.ClosePreviewCache();
            if (File.Exists(tempAvi)) File.Delete(tempAvi);
            if (Directory.Exists(tempOutDir)) Directory.Delete(tempOutDir, true);
        }
    }
}
