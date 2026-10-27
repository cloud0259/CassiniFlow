using CassiniFlow.Application.Pipeline;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Models.ValueObjects;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Ports.Outbound;
using CassiniFlow.Infrastructure.Factories;
using CassiniFlow.Infrastructure.Serialization;
using CassiniFlow.Infrastructure.Synthetic;
using Xunit;

namespace CassiniFlow.Tests;

public class HexagonalArchitectureTests
{
    [Fact]
    public void DomainValueObjects_EnforceInvariantsAndImmutability()
    {
        var validCentroid = Centroid.At(320, 240);
        Assert.True(validCentroid.IsValid);
        Assert.Equal(320, validCentroid.X);
        Assert.Equal(240, validCentroid.Y);

        var invalid = Centroid.Invalid;
        Assert.False(invalid.IsValid);

        var dims = new FrameDimensions(1920, 1080);
        Assert.Equal(1920 * 1080, dims.TotalPixels);
        Assert.False(dims.IsEmpty);

        QualityScore q1 = 154.2;
        QualityScore q2 = 210.5;
        Assert.True(q2 > q1);
    }

    [Fact]
    public void PipelineStepPattern_ExecutesConfiguredStepsSequentially()
    {
        var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 1, width: 256, height: 256);
        var input = frames[0];

        var options = new ProcessingOptions
        {
            DebayerEnabled = false,
            EnableCropping = true,
            CropWidth = 128,
            CropHeight = 128,
            CentreObjectInFrame = true
        };

        var context = new FramePipelineContext(options);
        var pipeline = FrameProcessingPipeline.CreateStandard();

        var output = pipeline.Process(input, context);

        Assert.Equal(128, output.Width);
        Assert.Equal(128, output.Height);
        Assert.NotNull(context.LastDetection);
        Assert.True(context.LastDetection.Accepted);
    }

    [Fact]
    public void PreviewUseCase_ProcessesFrameThroughInboundPort()
    {
        IPreviewFrameUseCase useCase = new PreviewFrameUseCase();
        var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, frameCount: 1, width: 200, height: 200);

        var options = new ProcessingOptions
        {
            EnableCropping = true,
            CropWidth = 100,
            CropHeight = 100,
            ConvertToMonochrome = true
        };

        var result = useCase.GeneratePreview(frames[0], options, applyCropToOutput: true);

        Assert.NotNull(result);
        Assert.Equal(100, result.ProcessedFrame.Width);
        Assert.Equal(100, result.ProcessedFrame.Height);
        Assert.Equal(1, result.ProcessedFrame.Channels);
        Assert.True(result.Detection.Accepted);
    }

    [Fact]
    public void MediaReaderFactory_ImplementsOutboundPort()
    {
        IMediaReaderFactory factory = new MediaReaderFactory();
        Assert.NotNull(factory);

        IProfileSerializer ProfileSerializer = new ProfileSerializer();
        var pof = ProfileSerializer.SerializeToPof(new ProcessingOptions { CropWidth = 320 });
        Assert.Contains("CROP_WIDTH 320", pof);
    }
}
