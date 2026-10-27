using CassiniFlow.Application.Pipeline;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.UseCases;

public sealed class PreviewFrameUseCase : IPreviewFrameUseCase
{
    public PreviewFrameResult GeneratePreview(
        AstroFrame rawInput,
        ProcessingOptions options,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        bool applyCropToOutput = false,
        double previewScale = 1.0)
    {
        var work = rawInput.Clone();
        int origW = rawInput.Width;
        int origH = rawInput.Height;

        var context = new FramePipelineContext(options, masterDark, masterFlat);

        // 1. Calibration
        new CalibrationStep().Execute(work, context);

        // 2. Debayering
        work = new DebayerStep().Execute(work, context);

        // 3. Centering and detection
        work = new CenteringAndDetectionStep().Execute(work, context);
        var detection = context.LastDetection ?? new DetectionResult(true, null, work.Width / 2, work.Height / 2, 0, 0, 0);

        // 4. Cropping (if requested)
        if (applyCropToOutput && options.EnableCropping)
        {
            work = new CroppingStep().Execute(work, context);
        }

        // 5. Enhancements
        work = new EnhancementsStep().Execute(work, context);

        return new PreviewFrameResult(work, detection, origW, origH);
    }
}
