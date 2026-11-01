using System.Globalization;
using System.Text;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Service du domaine générant un rapport textuel détaillé d'exécution (.txt)
/// documentant l'ensemble des fichiers sources, options de traitement et métriques finales.
/// </summary>
public static class ExecutionLogGenerator
{
    public static string Generate(
        ProcessingOptions options,
        PipelineRunSummary summary,
        IReadOnlyList<string> sourceFiles,
        DateTime? timestampUtc = null,
        IReadOnlyList<string>? generatedFiles = null,
        string? detectedTarget = null)
    {
        var sb = new StringBuilder(2048);
        var time = timestampUtc ?? DateTime.UtcNow;
        double elapsedSec = Math.Max(0.001, summary.Elapsed.TotalSeconds);
        double fps = summary.TotalInputFrames / elapsedSec;

        sb.AppendLine("================================================================================");
        sb.AppendLine("                           CASSINIFLOW EXECUTION LOG                            ");
        sb.AppendLine("================================================================================");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Timestamp (UTC)      : {time:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Version  : 1.0.0");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Operating System     : {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime              : .NET {Environment.Version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Workflow Mode        : {options.Stacking.WorkflowMode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Run Mode             : {options.RunMode}");
        sb.AppendLine();

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("EXECUTION PERFORMANCE & METRICS");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Status               : {(summary.Success ? "SUCCESS" : "FAILED / PARTIAL")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Duration             : {summary.Elapsed:hh\\:mm\\:ss\\.fff} ({summary.Elapsed.TotalSeconds:F2} s)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Processing Speed     : {fps:F1} fps");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Total Input Frames   : {summary.TotalInputFrames}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Output Frames Written: {summary.OutputFramesWritten}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Discarded (No Object): {summary.DiscardedNoPlanet}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Discarded (Overexp.) : {summary.DiscardedOverexposed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Discarded (Quality)  : {summary.DiscardedQuality}");
        sb.AppendLine();

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(CultureInfo.InvariantCulture, $"SOURCE FILES ({sourceFiles.Count})");
        sb.AppendLine("--------------------------------------------------------------------------------");
        if (sourceFiles.Count == 0)
        {
            sb.AppendLine("  (No source files specified)");
        }
        else
        {
            foreach (var f in sourceFiles)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {f}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("PROCESSING PARAMETERS");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("[Object Detection & Stabilisation]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Mode               : {options.StabilisationMode}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Target Object      : {detectedTarget ?? (options.CheckPlanetObjectPresent ? "Auto-Detect" : "None")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Check Planet Present: {options.CheckPlanetObjectPresent}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Centre in Frame    : {options.CentreObjectInFrame}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Auto Threshold     : {options.AutoDetectionThreshold}");
        if (!options.AutoDetectionThreshold)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Manual Threshold   : {options.ManualDetectionThreshold}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Min Object Size    : {options.MinimumObjectSizePixels} px");

        sb.AppendLine();
        sb.AppendLine("[Cropping & Geometry]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Cropping Enabled   : {options.EnableCropping}");
        if (options.EnableCropping)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Dimensions         : {options.CropWidth} x {options.CropHeight} px");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Offset (X, Y)      : ({options.CropOffsetX}, {options.CropOffsetY})");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Rotation Angle     : {options.Rotation}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Auto-Rotate Align  : {options.AutoRotateAlign}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Flip (Horiz / Vert): {options.FlipHorizontalX} / {options.FlipVerticalY}");

        sb.AppendLine();
        sb.AppendLine("[Color & Debayering]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Debayer Enabled    : {options.DebayerEnabled}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Debayer Pattern    : {options.DebayerPattern}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Debayer Algorithm  : {options.DebayerType}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Convert to Mono    : {options.ConvertToMonochrome}");

        sb.AppendLine();
        sb.AppendLine("[Quality Filtering & Sorting]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Quality Estimation : {options.EnableQualityEstimation}");
        if (options.EnableQualityEstimation)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Algorithm          : {options.QualityAlgorithm}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Reorder by Quality : {options.QualityReorderFrames}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Limit Frames       : {options.EnableQualityLimiting}");
            if (options.EnableQualityLimiting)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Limiting Mode      : {options.QualityLimitingMode}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Frames To Keep     : {options.QualityFramesToKeep}");
            }
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Reject Overexposed : {options.RejectOverexposedFrames} (Max pixels: {options.MaxOverexposedPixels})");

        sb.AppendLine();
        sb.AppendLine("[Calibration Frames]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Dark Frame Subtract: {options.DarkFrameSubtract} ({options.DarkFiles.Count} files)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Flat Frame Calibr. : {options.FlatFrameCalibrate} ({options.FlatFiles.Count} files)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Flat-Dark Subtract : {options.FlatDarkFrameSubtract} ({options.FlatDarkFiles.Count} files)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Hot Pixel Correc.  : {options.ReplaceHotPixels}");

        if (options.Stacking.WorkflowMode is not StackingWorkflowMode.PreProcessingOnly)
        {
            sb.AppendLine();
            sb.AppendLine("[Stacking Engine]");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Stabilisation Mode : {options.Stacking.StabilisationMode}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Reference Mode     : {options.Stacking.ReferenceMode}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Stack Percentages  : {string.Join(", ", options.Stacking.StackPercentages.Select(t => $"{t.ToString(CultureInfo.InvariantCulture)}%"))}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Drizzle Mode       : {options.Stacking.Drizzle}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Sharpening Active  : {options.Stacking.SharpeningEnabled}");
        }

        sb.AppendLine();
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("OUTPUT ARTIFACTS");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Format               : {options.OutputFormat}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"WinJUPOS Naming      : {options.GenerateWinJuposFilenames}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Primary Output File  : {(string.IsNullOrEmpty(summary.OutputFilePath) ? "(None)" : summary.OutputFilePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Execution Log File   : {(string.IsNullOrEmpty(summary.LogFilePath) ? "(None)" : summary.LogFilePath)}");

        if (generatedFiles is { Count: > 0 })
        {
            sb.AppendLine("All Generated Files:");
            foreach (var gf in generatedFiles)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {gf}");
            }
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                             END OF EXECUTION LOG                               ");
        sb.AppendLine("================================================================================");

        return sb.ToString();
    }
}
