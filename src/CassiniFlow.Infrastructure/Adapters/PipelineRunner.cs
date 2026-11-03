using System.Diagnostics;
using System.Globalization;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Codecs;
using CassiniFlow.Infrastructure.WinJupos;
using OpenCvSharp;

namespace CassiniFlow.Infrastructure.Adapters;

internal readonly record struct FrameScanEntry(
    int FileIndex,
    int FrameIndex,
    int CentroidX,
    int CentroidY,
    double QualityScore);

/// <summary>
/// High-speed streaming CassiniFlow execution pipeline.
/// Uses a Single-Pass Bounded Top-K Min-Heap with zero-allocation Mat ROI quality scoring when cropped frames fit in RAM (< 900 MB),
/// and falls back to a Two-Pass Streaming writer (supporting AVI, MP4, SER, BMP, FITS) for uncropped 4K sequences.
/// </summary>
public sealed class PipelineRunner : IRunPipelineUseCase
{
    private readonly IAstroLocalization _loc;
    private readonly IStackingPipelineUseCase _stackingRunner;
    private readonly ICalibrationFrameProvider _calibrationProvider;

    public PipelineRunner(
        IAstroLocalization localization,
        IStackingPipelineUseCase stackingRunner,
        ICalibrationFrameProvider calibrationProvider)
    {
        _loc = localization ?? throw new ArgumentNullException(nameof(localization));
        _stackingRunner = stackingRunner ?? throw new ArgumentNullException(nameof(stackingRunner));
        _calibrationProvider = calibrationProvider ?? throw new ArgumentNullException(nameof(calibrationProvider));
    }

    Task<PipelineRunSummary> IRunPipelineUseCase.ExecuteFilesAsync(
        IReadOnlyList<string> inputFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress,
        CancellationToken cancellationToken) => ExecuteFilesAsync(inputFiles, options, outputDirectory, progress, cancellationToken);

    public static (AstroFrame ProcessedFrame, DetectionResult Detection) PreviewSingleFrame(
        AstroFrame rawInput,
        ProcessingOptions options,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        bool applyCropToOutput = false,
        double previewScale = 1.0)
    {
        var work = rawInput.Clone();

        ImageProcessor.ApplyCalibration(
            work,
            masterDark,
            options.ReplaceHotPixels,
            options.HotPixelThreshold,
            options.DarkDataHasBayer,
            masterFlat);

        if (options.DebayerEnabled && !options.ProtectBayerPattern)
        {
            if (work.Channels == 1)
            {
                work = ImageProcessor.Debayer(work, options.DebayerPattern, options.DebayerType);
            }
            else if (work.Channels == 3 && ImageProcessor.IsMonochrome(work))
            {
                var mono = ImageProcessor.ConvertToMonochrome(work, MonochromeConversionMode.UseRgbChannels);
                work = ImageProcessor.Debayer(mono, options.DebayerPattern, options.DebayerType);
            }
        }

        if (options.Binning != BinningMode.Disabled)
        {
            work = ImageProcessor.ApplyBinning(work, options.Binning, options.BinCalculation);
        }

        if (options.ConvertToMonochrome && work.Channels == 3)
        {
            work = ImageProcessor.ConvertToMonochrome(work, options.MonochromeConversionType);
        }

        ImageProcessor.ApplyGainAndGamma(work, options.InputGain, options.Gamma);

        if (options.EnableHistogramStretch)
        {
            ImageProcessor.ApplyHistogramStretch(
                work,
                options.HistogramStretchWhitePointPercent,
                options.HistogramStretchBlackPointZero,
                options.HistogramStretchRgbIndividually);
        }

        int scaledMinSize = Math.Max(2, (int)Math.Round(options.MinimumObjectSizePixels * previewScale));
        var detection = StabilisationAndQualityEngine.DetectPlanetOrObject(
            work,
            options.CheckPlanetObjectPresent && options.StabilisationMode == StabilisationMode.ObjectPlanetary,
            options.AutoDetectionThreshold,
            options.ManualDetectionThreshold,
            scaledMinSize,
            options.EdgeInShadow,
            options.RejectOverexposedFrames,
            options.MaxOverexposedPixels,
            rejectClippedObjects: false);

        bool centrePlanet = options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        if (applyCropToOutput || (!options.EnableCropping && !centrePlanet))
        {
            if (applyCropToOutput && (options.EnableCropping || centrePlanet))
            {
                int scaledCropW = options.EnableCropping
                    ? Math.Max(16, (int)Math.Round(options.CropWidth * previewScale))
                    : work.Width;
                int scaledCropH = options.EnableCropping
                    ? Math.Max(16, (int)Math.Round(options.CropHeight * previewScale))
                    : work.Height;
                int scaledOffX = options.EnableCropping
                    ? (int)Math.Round(options.CropOffsetX * previewScale)
                    : 0;
                int scaledOffY = options.EnableCropping
                    ? (int)Math.Round(options.CropOffsetY * previewScale)
                    : 0;

                int targetX = centrePlanet
                    ? detection.CentroidX
                    : work.Width / 2;
                int targetY = centrePlanet
                    ? detection.CentroidY
                    : work.Height / 2;

                work = ImageProcessor.CropAroundPoint(
                    work,
                    targetX,
                    targetY,
                    scaledCropW,
                    scaledCropH,
                    scaledOffX,
                    scaledOffY,
                    options.ProtectBayerPattern);

                int startX = targetX - (scaledCropW / 2) - scaledOffX;
                int startY = targetY - (scaledCropH / 2) - scaledOffY;
                detection = detection with
                {
                    CentroidX = detection.CentroidX - startX,
                    CentroidY = detection.CentroidY - startY
                };
            }

            if (options.FlipHorizontalX || options.FlipVerticalY || options.Rotation != RotationAngle.None)
            {
                int pw = work.Width;
                int ph = work.Height;
                work = ImageProcessor.ApplyFlipAndRotation(
                    work,
                    options.FlipHorizontalX,
                    options.FlipVerticalY,
                    options.Rotation);

                int cx = detection.CentroidX;
                int cy = detection.CentroidY;
                int fx = options.FlipHorizontalX ? (pw - 1 - cx) : cx;
                int fy = options.FlipVerticalY ? (ph - 1 - cy) : cy;
                (int rx, int ry) = options.Rotation switch
                {
                    RotationAngle.Rotate90Clockwise => (ph - 1 - fy, fx),
                    RotationAngle.Rotate90Anticlockwise => (fy, pw - 1 - fx),
                    RotationAngle.Rotate180 => (pw - 1 - fx, ph - 1 - fy),
                    _ => (fx, fy)
                };
                detection = detection with { CentroidX = rx, CentroidY = ry };
            }
        }
        else
        {
            if (options.FlipHorizontalX || options.FlipVerticalY || options.Rotation != RotationAngle.None)
            {
                int pw = work.Width;
                int ph = work.Height;
                work = ImageProcessor.ApplyFlipAndRotation(
                    work,
                    options.FlipHorizontalX,
                    options.FlipVerticalY,
                    options.Rotation);

                int cx = detection.CentroidX;
                int cy = detection.CentroidY;
                int fx = options.FlipHorizontalX ? (pw - 1 - cx) : cx;
                int fy = options.FlipVerticalY ? (ph - 1 - cy) : cy;
                (int rx, int ry) = options.Rotation switch
                {
                    RotationAngle.Rotate90Clockwise => (ph - 1 - fy, fx),
                    RotationAngle.Rotate90Anticlockwise => (fy, pw - 1 - fx),
                    RotationAngle.Rotate180 => (pw - 1 - fx, ph - 1 - fy),
                    _ => (fx, fy)
                };
                detection = detection with { CentroidX = rx, CentroidY = ry };
            }
        }

        if (options.EnableFieldDerotation)
        {
            double safeFps = (double)options.WinJuposInputFps > 0 ? (double)options.WinJuposInputFps : 30.0;
            double angle = FieldDerotationEngine.ComputeFrameDerotationAngle(
                work.FrameIndex,
                Math.Max(100, work.FrameIndex + 1),
                safeFps,
                options.ObserverLatitudeDegrees,
                options.TargetAltitudeDegrees,
                options.TargetAzimuthDegrees);
            double? pivX = (applyCropToOutput && options.EnableCropping) ? null : detection.CentroidX;
            double? pivY = (applyCropToOutput && options.EnableCropping) ? null : detection.CentroidY;
            work = FieldDerotationEngine.ApplyDerotation(work, angle, pivX, pivY);
        }

        work.DetectedCentroidX = detection.CentroidX;
        work.DetectedCentroidY = detection.CentroidY;
        work.QualityScore = options.EnableQualityEstimation
            ? StabilisationAndQualityEngine.ComputeQualityScore(
                work,
                options.QualityAlgorithm,
                options.QualityMinSubsample,
                options.QualityMaxSubsample,
                options.QualitySubsampleIncrement)
            : 100.0;

        return (work, detection);
    }

    public static List<AstroFrame> BuildAnimationSequence(IReadOnlyList<AstroFrame> sourceFrames, ProcessingOptions options)
    {
        if (sourceFrames.Count == 0) return [];

        var result = new List<AstroFrame>();
        int repeatCount = options.AnimationRepeatAllSequence ? Math.Max(1, options.AnimationRepeatAllCount) : 1;

        for (int rep = 0; rep < repeatCount; rep++)
        {
            bool isLastRepeat = rep == repeatCount - 1;

            if (options.AnimationForwardPlay)
            {
                result.AddRange(sourceFrames);
                if (options.AnimationPauseFinalForwardFrame && sourceFrames.Count > 0)
                {
                    for (int p = 0; p < options.AnimationPauseForwardCount; p++)
                    {
                        result.Add(sourceFrames[^1]);
                    }
                }
            }

            bool skipReverseThisTime = isLastRepeat && options.AnimationDisableReverseInLastRepeat;
            if (options.AnimationReversePlay && !skipReverseThisTime)
            {
                for (int i = sourceFrames.Count - 1; i >= 0; i--)
                {
                    result.Add(sourceFrames[i]);
                }

                if (options.AnimationPauseFinalReverseFrame && sourceFrames.Count > 0)
                {
                    for (int p = 0; p < options.AnimationPauseReverseCount; p++)
                    {
                        result.Add(sourceFrames[0]);
                    }
                }
            }
        }

        if (result.Count == 0)
        {
            result.AddRange(sourceFrames);
        }

        return result;
    }

    public static List<AstroFrame> ApplyQualityWeighting(IReadOnlyList<AstroFrame> sortedFrames)
    {
        var weighted = new List<AstroFrame>(sortedFrames.Count * 2);
        int q25 = Math.Max(1, sortedFrames.Count / 4);
        int q50 = Math.Max(2, sortedFrames.Count / 2);

        for (int i = 0; i < sortedFrames.Count; i++)
        {
            weighted.Add(sortedFrames[i]);
            int bonus = i switch
            {
                _ when i < q25 => 2,
                _ when i < q50 => 1,
                _ => 0
            };
            for (int b = 0; b < bonus; b++)
            {
                weighted.Add(sortedFrames[i]);
            }
        }

        return weighted;
    }

    /// <summary>
    /// Computes the worker thread count according to options.MaxCpuUsagePercent (10% to 100%).
    /// Allows utilizing all logical threads if requested by the user.
    /// </summary>
    public static int GetSafeWorkerThreadCount(ProcessingOptions options)
    {
        int totalCores = Math.Max(1, Environment.ProcessorCount);
        if (totalCores == 1) return 1;

        int pct = Math.Clamp(options.MaxCpuUsagePercent, 10, 100);
        int byPercent = Math.Max(1, (int)Math.Round(totalCores * (pct / 100.0)));
        return Math.Clamp(byPercent, 1, totalCores);
    }

    public async Task<PipelineRunSummary> ExecuteFilesAsync(
        IReadOnlyList<string> inputFilePaths,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validPaths = inputFilePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0)
        {
            return new PipelineRunSummary(false, TimeSpan.Zero, 0, 0, 0, 0, 0, string.Empty);
        }

        if (options.Stacking.WorkflowMode is StackingWorkflowMode.StackingOnly or StackingWorkflowMode.DirectStreamAllInOne)
        {
            return await _stackingRunner.ExecuteAsync(validPaths, options, outputDirectory, progress, cancellationToken);
        }

        var (masterDark, masterFlat) = _calibrationProvider.PrepareCalibrationMasters(
            options,
            outputDirectory,
            progress,
            cancellationToken);

        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(outputDirectory);
        _ = ImageProcessor.GetGainGammaLut(options.InputGain, options.Gamma);

        if (options.RunMode == ProcessingRunMode.BatchMode && validPaths.Count > 1)
        {
            int totalIn = 0, totalOut = 0, totalNoPlanet = 0, totalOver = 0, totalQual = 0;
            string lastOutputPath = string.Empty;
            string lastLogPath = string.Empty;
            object batchLock = new();
            int completedFiles = 0;

            int batchWorkers = Math.Clamp(GetSafeWorkerThreadCount(options) / 2, 1, 4);
            await Parallel.ForEachAsync(
                Enumerable.Range(0, validPaths.Count),
                new ParallelOptions { MaxDegreeOfParallelism = batchWorkers, CancellationToken = cancellationToken },
                async (f, ct) =>
                {
                    string file = validPaths[f];
                    var subSummary = await ProcessFileGroupStreamingAsync([file], options, outputDirectory, progress, ct, masterDark, masterFlat);
                    lock (batchLock)
                    {
                        completedFiles++;
                        totalIn += subSummary.TotalInputFrames;
                        totalOut += subSummary.OutputFramesWritten;
                        totalNoPlanet += subSummary.DiscardedNoPlanet;
                        totalOver += subSummary.DiscardedOverexposed;
                        totalQual += subSummary.DiscardedQuality;
                        if (!string.IsNullOrEmpty(subSummary.OutputFilePath))
                        {
                            lastOutputPath = subSummary.OutputFilePath;
                            if (!string.IsNullOrEmpty(subSummary.LogFilePath))
                            {
                                lastLogPath = subSummary.LogFilePath;
                            }
                        }
                    }
                });

            sw.Stop();
            return new PipelineRunSummary(totalOut > 0, sw.Elapsed, totalIn, totalOut, totalNoPlanet, totalOver, totalQual, lastOutputPath, lastLogPath);
        }

        return await ProcessFileGroupStreamingAsync(validPaths, options, outputDirectory, progress, cancellationToken, masterDark, masterFlat);
    }

    public static bool IsImageOutputFormat(OutputFormat format) =>
        format is OutputFormat.Bmp or OutputFormat.Png or OutputFormat.Tiff or OutputFormat.Fits;

    public static string GetImageFramePath(
        string dir,
        string stem,
        OutputFormat format,
        int seqNumber,
        int totalFrames,
        bool leadingZeros)
    {
        string ext = format switch
        {
            OutputFormat.Fits => ".fit",
            OutputFormat.Bmp => ".bmp",
            OutputFormat.Png => ".png",
            OutputFormat.Tiff => ".tif",
            _ => ".png"
        };

        string numStr;
        if (!leadingZeros)
        {
            numStr = seqNumber.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            int digits = totalFrames >= 10000 ? 5 : 4;
            numStr = seqNumber.ToString($"D{digits}", CultureInfo.InvariantCulture);
        }

        return Path.Combine(dir, $"{stem}_{numStr}{ext}");
    }

    private async Task<PipelineRunSummary> ProcessFileGroupStreamingAsync(
        IReadOnlyList<string> groupFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress,
        CancellationToken cancellationToken,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null)
    {
        var sw = Stopwatch.StartNew();
        int totalInputFrames = 0;
        int outputFramesWritten = 0;
        int discardedNoPlanet = 0;
        int discardedQuality = 0;
        double sourceFps = 25.0;
        int firstWidth = 640;
        int firstHeight = 480;

        var fileSummaries = new List<MediaFileSummary>(groupFiles.Count);
        foreach (string path in groupFiles)
        {
            var info = UniversalMediaReader.InspectFile(path);
            fileSummaries.Add(info);
            if (info.Fps > 0) sourceFps = info.Fps;
            firstWidth = info.Width;
            firstHeight = info.Height;

            int startIdx = options.LimitFrameRange ? Math.Max(0, options.StartFrame - 1) : 0;
            int endIdx = options.LimitFrameRange ? Math.Min(info.FrameCount - 1, options.EndFrame - 1) : info.FrameCount - 1;
            totalInputFrames += Math.Max(0, endIdx - startIdx + 1);
        }

        double outFps = options.AviFpsSameAsInput ? Math.Max(1.0, sourceFps) : Math.Max(1.0, options.AviFpsValue);
        string sourceStem = Path.GetFileNameWithoutExtension(groupFiles[0]);
        string baseName = options.GenerateWinJuposFilenames
            ? WinJuposHelper.FormatWinJuposPrefix(DateTime.UtcNow)
            : sourceStem;
        string fullStem = $"{options.OutputFilenamePrefix}{baseName}{options.OutputFilenameSuffix}";

        string outputPath = options.OutputFormat switch
        {
            OutputFormat.Avi => Path.Combine(outputDirectory, $"{fullStem}.avi"),
            OutputFormat.Mp4 => Path.Combine(outputDirectory, $"{fullStem}.mp4"),
            OutputFormat.Ser => Path.Combine(outputDirectory, $"{fullStem}.ser"),
            _ => GetImageFramePath(outputDirectory, fullStem, options.OutputFormat, 1, Math.Max(1, totalInputFrames), options.AddLeadingZeros)
        };

        int outW = options.EnableCropping ? Math.Max(16, options.CropWidth) : firstWidth;
        int outH = options.EnableCropping ? Math.Max(16, options.CropHeight) : firstHeight;
        if (options.Binning != BinningMode.Disabled && !options.EnableCropping)
        {
            int bin = (int)options.Binning;
            outW = Math.Max(16, outW / bin);
            outH = Math.Max(16, outH / bin);
        }
        if (options.Rotation is RotationAngle.Rotate90Clockwise or RotationAngle.Rotate90Anticlockwise)
        {
            (outW, outH) = (outH, outW);
        }
        outW = (outW + 1) & ~1;
        outH = (outH + 1) & ~1;

        int outChannels = options.ConvertToMonochrome ? 1 : 3;
        bool isSource16Bit = groupFiles[0].EndsWith(".ser", StringComparison.OrdinalIgnoreCase) ||
                             groupFiles[0].EndsWith(".fit", StringComparison.OrdinalIgnoreCase) ||
                             groupFiles[0].EndsWith(".fits", StringComparison.OrdinalIgnoreCase) ||
                             (fileSummaries.Count > 0 && fileSummaries[0].FormatBadge.Contains("16"));

        bool isImageOutput = IsImageOutputFormat(options.OutputFormat);

        await Task.Run(() =>
        {
            bool checkPlanet = options.CheckPlanetObjectPresent && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
            bool centrePlanet = options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary;

            // Rotation automatique : angle à appliquer à chaque fichier pour l'aligner sur l'orientation du premier
            double[] fileRotationDeg = ComputeAutoRotations(groupFiles, fileSummaries, options, progress);
            bool hasAutoRotation = fileRotationDeg.Any(a => Math.Abs(a) >= 0.05);

            // Determine maximum number of frames to keep and adapt RAM budget dynamically to the PC's available memory (4 GB laptop up to 64 GB workstation)
            int maxKeepTarget = (options.EnableQualityEstimation && options.EnableQualityLimiting)
                ? (options.QualityLimitingMode == QualityLimitingType.PercentageOfFrames
                    ? Math.Max(1, totalInputFrames * options.QualityFramesToKeep / 100)
                    : Math.Min(totalInputFrames, Math.Max(1, options.QualityFramesToKeep)))
                : totalInputFrames;

            long totalSystemRam = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long maxHeapBudgetBytes = totalSystemRam > 0
                ? Math.Clamp(totalSystemRam / 6, 256L * 1024 * 1024, 3400L * 1024 * 1024)
                : 900L * 1024 * 1024;

            long bytesPerCroppedFrame = (long)outW * outH * outChannels * sizeof(ushort);
            bool fitsInSinglePassHeap = (maxKeepTarget * bytesPerCroppedFrame) <= maxHeapBudgetBytes;

            // CASE 1: Streaming Direct (No Quality Sorting and (huge uncropped output > RAM budget OR image sequence))
            if (!options.EnableQualityEstimation && !options.AnimationReversePlay && (!options.AnimationRepeatAllSequence || options.AnimationRepeatAllCount <= 1) && (!fitsInSinglePassHeap || isImageOutput))
            {
                using var writer = new PipelineFrameWriter(
                    outputPath,
                    outputDirectory,
                    fullStem,
                    options.OutputFormat,
                    outW,
                    outH,
                    outChannels,
                    outFps,
                    isSource16Bit,
                    totalInputFrames,
                    options.AddLeadingZeros,
                    isImageOutput);

                int processedCount = 0;
                int reportStep = Math.Max(1, totalInputFrames / 20);

                for (int fIdx = 0; fIdx < groupFiles.Count; fIdx++)
                {
                    string path = groupFiles[fIdx];
                    var info = fileSummaries[fIdx];
                    int startIdx = options.LimitFrameRange ? Math.Max(0, options.StartFrame - 1) : 0;
                    int endIdx = options.LimitFrameRange ? Math.Min(info.FrameCount - 1, options.EndFrame - 1) : info.FrameCount - 1;

                    StreamFramesThroughProcessor(
                        path,
                        startIdx,
                        endIdx,
                        options,
                        checkPlanet,
                        centrePlanet,
                        cancellationToken,
                        (frame, _) =>
                        {
                            processedCount++;
                            if (frame is null)
                            {
                                discardedNoPlanet++;
                            }
                            else
                            {
                                writer.WriteFrame(hasAutoRotation ? AutoRotationAligner.Rotate(frame, fileRotationDeg[fIdx]) : frame);
                            }

                            if (processedCount % reportStep == 0 || processedCount == totalInputFrames)
                            {
                                int pct = Math.Clamp((int)(processedCount * 95.0 / Math.Max(1, totalInputFrames)), 5, 98);
                                progress?.Report(new PipelineProgressReport(_loc.Get("Progress.DirectStreaming"), pct, Path.GetFileName(path), processedCount, totalInputFrames, _loc.Format("Progress.FramesProcessedWritten", processedCount, totalInputFrames, writer.FramesWritten)));
                            }
                        });
                }

                // Fallback if 100% of frames were rejected by planet threshold on a personal video
                if (writer.FramesWritten == 0 && totalInputFrames > 0 && checkPlanet)
                {
                    progress?.Report(new PipelineProgressReport(_loc.Get("Progress.NoRejectionMode"), 50, sourceStem, 0, totalInputFrames, _loc.Get("Progress.NoIsolatedPlanetDetected")));
                    discardedNoPlanet = 0;
                    processedCount = 0;

                    for (int fIdx = 0; fIdx < groupFiles.Count; fIdx++)
                    {
                        string path = groupFiles[fIdx];
                        var info = fileSummaries[fIdx];
                        int startIdx = options.LimitFrameRange ? Math.Max(0, options.StartFrame - 1) : 0;
                        int endIdx = options.LimitFrameRange ? Math.Min(info.FrameCount - 1, options.EndFrame - 1) : info.FrameCount - 1;

                        StreamFramesThroughProcessor(
                            path,
                            startIdx,
                            endIdx,
                            options,
                            enforcePlanetRejection: false,
                            centrePlanet: centrePlanet,
                            cancellationToken,
                            (frame, _) =>
                            {
                                processedCount++;
                                if (frame is not null)
                                {
                                    writer.WriteFrame(hasAutoRotation ? AutoRotationAligner.Rotate(frame, fileRotationDeg[fIdx]) : frame);
                                }
                            });
                    }
                }

                writer.Finish();
                writer.FlushFallback(options, outFps);
                outputFramesWritten = writer.FramesWritten;
                return;
            }

            // CASE 2: Parallel Multi-Core Processing (Quality Sorting OR Multi-File Parallel Join)
            if (fitsInSinglePassHeap)
            {
                // FAST PARALLEL MULTI-CORE 1-PASS BOUNDED TOP-K MIN-HEAP:
                // Decodes all input video files simultaneously across CPU cores with multi-threaded FFmpeg,
                // uses 8-microsecond local ROI centroid tracking, scores quality directly on raw Mat ROI (0 allocations for rejected frames!),
                // and recycles evicted AstroFrame buffers (0 LOH GC pressure!).
                var topKHeap = new PriorityQueue<AstroFrame, double>(maxKeepTarget + 2);
                object heapLock = new();
                double currentMinScoreInHeap = double.MinValue;
                int scanned = 0;
                int acceptedBeforeLimit = 0;
                int reportStep = Math.Max(1, totalInputFrames / 20);

                var segments = BuildParallelSegments(groupFiles, fileSummaries, options, targetChunkFrames: 180);
                int maxParallelism = GetSafeWorkerThreadCount(options);

                Parallel.ForEach(
                    segments,
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellationToken },
                    seg =>
                    {
                        ScanSegmentIntoTopKHeap(
                            seg.FilePath,
                            seg.StartFrame,
                            seg.EndFrame,
                            seg.GlobalFrameBase,
                            options,
                            checkPlanet,
                            centrePlanet,
                            maxKeepTarget,
                            topKHeap,
                            heapLock,
                            ref currentMinScoreInHeap,
                            ref scanned,
                            ref acceptedBeforeLimit,
                            ref discardedNoPlanet,
                            cancellationToken,
                            doneCount =>
                            {
                                if (doneCount % reportStep == 0 || doneCount == totalInputFrames)
                                {
                                    int pct = Math.Clamp((int)(doneCount * 82.0 / Math.Max(1, totalInputFrames)), 5, 84);
                                    int keptNow;
                                    lock (heapLock) keptNow = topKHeap.Count;
                                    progress?.Report(new PipelineProgressReport(_loc.Get("Progress.ParallelAnalysis"), pct, Path.GetFileName(seg.FilePath), doneCount, totalInputFrames, _loc.Format("Progress.FramesAnalysedKept", doneCount, totalInputFrames, keptNow)));
                                }
                            });
                    });

                // Fallback if 100% were rejected by planet threshold
                if (topKHeap.Count == 0 && totalInputFrames > 0 && checkPlanet)
                {
                    progress?.Report(new PipelineProgressReport(_loc.Get("Progress.NoRejectionMode"), 50, sourceStem, 0, totalInputFrames, _loc.Get("Progress.NoIsolatedPlanetParallel")));
                    discardedNoPlanet = 0;
                    scanned = 0;
                    acceptedBeforeLimit = 0;
                    currentMinScoreInHeap = double.MinValue;

                    Parallel.ForEach(
                        segments,
                        new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellationToken },
                        seg =>
                        {
                            ScanSegmentIntoTopKHeap(
                                seg.FilePath,
                                seg.StartFrame,
                                seg.EndFrame,
                                seg.GlobalFrameBase,
                                options,
                                enforcePlanetRejection: false,
                                centrePlanet: centrePlanet,
                                maxKeepTarget,
                                topKHeap,
                                heapLock,
                                ref currentMinScoreInHeap,
                                ref scanned,
                                ref acceptedBeforeLimit,
                                ref discardedNoPlanet,
                                cancellationToken,
                                _ => { });
                        });
                }

                // Extract winning frames from min-heap
                int targetFinalCount = (options.EnableQualityEstimation && options.EnableQualityLimiting)
                    ? (options.QualityLimitingMode == QualityLimitingType.PercentageOfFrames
                        ? Math.Max(1, acceptedBeforeLimit * options.QualityFramesToKeep / 100)
                        : Math.Min(acceptedBeforeLimit, Math.Max(1, options.QualityFramesToKeep)))
                    : acceptedBeforeLimit;

                var winners = new List<AstroFrame>(topKHeap.Count);
                while (topKHeap.Count > 0)
                {
                    winners.Add(topKHeap.Dequeue());
                }

                if (options.EnableQualityEstimation)
                {
                    winners.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));
                    if (winners.Count > targetFinalCount && targetFinalCount > 0)
                    {
                        winners = winners.Take(targetFinalCount).ToList();
                    }
                    discardedQuality = Math.Max(0, acceptedBeforeLimit - winners.Count);

                    if (!options.QualityReorderFrames)
                    {
                        winners.Sort((a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
                    }
                }
                else
                {
                    winners.Sort((a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
                }

                progress?.Report(new PipelineProgressReport(_loc.Get("Progress.EncodingPass2"), 90, Path.GetFileName(outputPath), winners.Count, totalInputFrames, _loc.Format("Progress.WritingFramesTo", winners.Count, Path.GetFileName(outputPath))));

                int postWorkers = GetSafeWorkerThreadCount(options);
                Parallel.For(0, winners.Count, new ParallelOptions { MaxDegreeOfParallelism = postWorkers, CancellationToken = cancellationToken }, i =>
                {
                    var wFrame = winners[i];
                    int wFileIdx = hasAutoRotation ? FindFileIndexForGlobalFrame(segments, wFrame.FrameIndex) : 0;
                    var dummyDet = new DetectionResult(true, null, wFrame.DetectedCentroidX, wFrame.DetectedCentroidY, 100, 0, 40);
                    ApplyPostCropTransformations(ref wFrame, options, dummyDet, recomputeQuality: false);
                    if (hasAutoRotation) wFrame = AutoRotationAligner.Rotate(wFrame, fileRotationDeg[wFileIdx]);
                    winners[i] = wFrame;
                });

                if (options.EnableQualityEstimation && options.EnableQualityWeighting)
                {
                    winners = ApplyQualityWeighting(winners);
                }

                if (options.AnimationReversePlay || options.AnimationRepeatAllSequence || options.AnimationPauseFinalForwardFrame)
                {
                    winners = BuildAnimationSequence(winners, options);
                }

                if (winners.Count > 0)
                {
                    if (options.OutputFormat is OutputFormat.Avi or OutputFormat.Mp4)
                    {
                        UniversalMediaReader.WriteVideoFile(outputPath, winners, outFps, options.OutputFormat == OutputFormat.Mp4, options.AviCodec);
                        outputFramesWritten = winners.Count;
                    }
                    else
                    {
                        WriteNonVideoOutput(outputPath, winners, options, isSource16Bit);
                        outputFramesWritten = winners.Count;
                    }
                }

                return;
            }

            // FALLBACK 2-PASS STREAMING (for uncropped 4K videos where kept frames > 3.4 GB)
            // Pass 1 runs in PARALLEL with ZERO AstroFrame allocations (only centroid + quality score on raw Mat!)
            var scanEntries = new List<FrameScanEntry>(totalInputFrames);
            object scanLock = new();
            int scannedCount = 0;
            int scanReportStep = Math.Max(1, totalInputFrames / 20);
            var pass1Segments = BuildParallelSegments(groupFiles, fileSummaries, options, targetChunkFrames: 180);
            int pass1Workers = GetSafeWorkerThreadCount(options);

            Parallel.ForEach(
                pass1Segments,
                new ParallelOptions { MaxDegreeOfParallelism = pass1Workers, CancellationToken = cancellationToken },
                seg =>
                {
                    ScanSegmentMetadataOnly(
                        seg.FileIndex,
                        seg.FilePath,
                        seg.StartFrame,
                        seg.EndFrame,
                        options,
                        checkPlanet,
                        centrePlanet,
                        scanEntries,
                        scanLock,
                        ref scannedCount,
                        ref discardedNoPlanet,
                        cancellationToken,
                        doneCount =>
                        {
                            if (doneCount % scanReportStep == 0 || doneCount == totalInputFrames)
                            {
                                int pct = Math.Clamp((int)(doneCount * 55.0 / Math.Max(1, totalInputFrames)), 5, 55);
                                progress?.Report(new PipelineProgressReport(_loc.Get("Progress.ParallelAnalysis"), pct, Path.GetFileName(seg.FilePath), doneCount, totalInputFrames, _loc.Format("Progress.Pass1FramesAnalysed", doneCount, totalInputFrames)));
                            }
                        });
                });

            scanEntries.Sort((a, b) =>
            {
                int cmp = a.FileIndex.CompareTo(b.FileIndex);
                return cmp != 0 ? cmp : a.FrameIndex.CompareTo(b.FrameIndex);
            });

            if (options.EnableQualityEstimation && scanEntries.Count > 0)
            {
                if (options.EnableQualityLimiting || options.QualityReorderFrames)
                {
                    scanEntries.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));
                }

                if (options.EnableQualityLimiting)
                {
                    int keepCount = options.QualityLimitingMode == QualityLimitingType.PercentageOfFrames
                        ? Math.Max(1, scanEntries.Count * options.QualityFramesToKeep / 100)
                        : Math.Min(scanEntries.Count, Math.Max(1, options.QualityFramesToKeep));

                    discardedQuality = scanEntries.Count - keepCount;
                    scanEntries = scanEntries.Take(keepCount).ToList();
                }

                if (!options.QualityReorderFrames)
                {
                    scanEntries.Sort((a, b) =>
                    {
                        int cmp = a.FileIndex.CompareTo(b.FileIndex);
                        return cmp != 0 ? cmp : a.FrameIndex.CompareTo(b.FrameIndex);
                    });
                }
            }

            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.EncodingPass2"), 65, Path.GetFileName(outputPath), 0, scanEntries.Count, _loc.Format("Progress.Pass2WritingBest", scanEntries.Count, Path.GetFileName(outputPath))));

            using (var pass2Writer = new PipelineFrameWriter(
                outputPath,
                outputDirectory,
                fullStem,
                options.OutputFormat,
                outW,
                outH,
                outChannels,
                outFps,
                isSource16Bit,
                scanEntries.Count,
                options.AddLeadingZeros,
                isImageOutput))
            {
                foreach (var fileGroup in scanEntries.GroupBy(x => x.FileIndex))
                {
                    string path = groupFiles[fileGroup.Key];
                    var wantedByFrame = fileGroup.ToDictionary(x => x.FrameIndex);
                    int minFrame = wantedByFrame.Keys.Min();
                    int maxFrame = wantedByFrame.Keys.Max();

                    StreamSelectedFramesFromVideo(
                        path,
                        minFrame,
                        maxFrame,
                        wantedByFrame,
                        options,
                        centrePlanet,
                        cancellationToken,
                        (_, frame) =>
                        {
                            if (hasAutoRotation) frame = AutoRotationAligner.Rotate(frame, fileRotationDeg[fileGroup.Key]);
                            pass2Writer.WriteFrame(frame);
                        });
                }

                pass2Writer.Finish();
                outputFramesWritten = pass2Writer.FramesWritten;
            }
        }, cancellationToken);

        sw.Stop();
        double fpsSpeed = totalInputFrames / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.Done"), 100, Path.GetFileName(outputPath), outputFramesWritten, totalInputFrames, _loc.Format("Progress.FinishedInSeconds", sw.Elapsed.TotalSeconds, fpsSpeed, outputFramesWritten, outputPath)));

        string logPath = Path.Combine(outputDirectory, $"{fullStem}_log.txt");
        var summary = new PipelineRunSummary(
            outputFramesWritten > 0,
            sw.Elapsed,
            totalInputFrames,
            outputFramesWritten,
            discardedNoPlanet,
            0,
            discardedQuality,
            outputPath,
            logPath);

        try
        {
            string logText = ExecutionLogGenerator.Generate(
                options,
                summary,
                groupFiles,
                timestampUtc: DateTime.UtcNow);
            File.WriteAllText(logPath, logText);
        }
        catch
        {
        }

        return summary;
    }

    /// <summary>
    /// Calcule la rotation (en degrés) à appliquer à chaque fichier pour que l'orientation de l'objet
    /// (axe des anneaux de Saturne par exemple) corresponde à celle du premier fichier exploitable.
    /// </summary>
    private double[] ComputeAutoRotations(
        IReadOnlyList<string> groupFiles,
        IReadOnlyList<MediaFileSummary> fileSummaries,
        ProcessingOptions options,
        IProgress<PipelineProgressReport>? progress)
    {
        var result = new double[groupFiles.Count];
        if (!options.AutoRotateAlign || groupFiles.Count < 2) return result;

        var angles = new AutoRotationAligner.OrientationResult?[groupFiles.Count];
        for (int i = 0; i < groupFiles.Count; i++)
        {
            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.OrientationDetection"), 2, Path.GetFileName(groupFiles[i]), 0, 0, _loc.Format("Progress.DetectingOrientation", Path.GetFileName(groupFiles[i]))));
            try { angles[i] = AutoRotationAligner.MeasureOrientation(groupFiles[i], fileSummaries[i].FrameCount); }
            catch { angles[i] = null; }
        }

        int refIdx = Array.FindIndex(angles, a => a is not null && a.Elongation >= AutoRotationAligner.MinReliableElongation);
        if (refIdx < 0)
        {
            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.AutoRotation"), 3, "", 0, 0, _loc.Get("Progress.SkippingAutoRotation")));
            return result;
        }

        double refAngle = angles[refIdx]!.AngleDegrees;
        for (int i = 0; i < groupFiles.Count; i++)
        {
            var a = angles[i];
            if (a is null || a.Elongation < AutoRotationAligner.MinReliableElongation || i == refIdx)
            {
                result[i] = 0;
                continue;
            }
            result[i] = AutoRotationAligner.NormalizeAxisAngle(refAngle - a.AngleDegrees);
        }

        string autoRotList = string.Join(", ", Enumerable.Range(0, groupFiles.Count)
            .Select(i => $"{Path.GetFileName(groupFiles[i])} {result[i]:+0.0;-0.0;0.0}°"));
        progress?.Report(new PipelineProgressReport(
            _loc.Get("Progress.AutoRotation"),
            3,
            "",
            0,
            0,
            _loc.Format("Progress.AutoRotationDetails", autoRotList)));
        return result;
    }

    private static int FindFileIndexForGlobalFrame(IReadOnlyList<VideoWorkSegment> segments, int globalFrame)
    {
        int fileIdx = 0;
        foreach (var seg in segments)
        {
            if (seg.GlobalFrameBase <= globalFrame) fileIdx = seg.FileIndex;
            else break;
        }
        return fileIdx;
    }

    private readonly record struct VideoWorkSegment(
        int FileIndex,
        string FilePath,
        int StartFrame,
        int EndFrame,
        int GlobalFrameBase);

    private static List<VideoWorkSegment> BuildParallelSegments(
        IReadOnlyList<string> groupFiles,
        IReadOnlyList<MediaFileSummary> fileSummaries,
        ProcessingOptions options,
        int targetChunkFrames = 220)
    {
        var segments = new List<VideoWorkSegment>();
        int globalBase = 0;

        for (int fIdx = 0; fIdx < groupFiles.Count; fIdx++)
        {
            string path = groupFiles[fIdx];
            var info = fileSummaries[fIdx];
            int startIdx = options.LimitFrameRange ? Math.Max(0, options.StartFrame - 1) : 0;
            int endIdx = options.LimitFrameRange ? Math.Min(info.FrameCount - 1, options.EndFrame - 1) : info.FrameCount - 1;
            int fileTotal = Math.Max(0, endIdx - startIdx + 1);
            if (fileTotal == 0) continue;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool canChunkVideo = ext == ".ser" || UniversalMediaReader.VideoExtensions.Contains(ext);
            if (canChunkVideo && fileTotal > targetChunkFrames)
            {
                for (int chunkStart = startIdx; chunkStart <= endIdx; chunkStart += targetChunkFrames)
                {
                    int chunkEnd = Math.Min(endIdx, chunkStart + targetChunkFrames - 1);
                    segments.Add(new VideoWorkSegment(fIdx, path, chunkStart, chunkEnd, globalBase));
                    globalBase += (chunkEnd - chunkStart + 1);
                }
            }
            else
            {
                segments.Add(new VideoWorkSegment(fIdx, path, startIdx, endIdx, globalBase));
                globalBase += fileTotal;
            }
        }

        return segments;
    }

    private static void ScanSegmentIntoTopKHeap(
        string filePath,
        int startIdx,
        int endIdx,
        int globalFrameBase,
        ProcessingOptions options,
        bool enforcePlanetRejection,
        bool centrePlanet,
        int maxKeepTarget,
        PriorityQueue<AstroFrame, double> topKHeap,
        object heapLock,
        ref double currentMinScoreInHeap,
        ref int scannedCount,
        ref int acceptedBeforeLimit,
        ref int discardedNoPlanet,
        CancellationToken cancellationToken,
        Action<int> onStep)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        var prevPriority = Thread.CurrentThread.Priority;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch
        {
            // Ignore on platforms that disallow changing thread priority
        }

        try
        {
        if (!UniversalMediaReader.IsOpenCvNativeAvailable && UniversalMediaReader.VideoExtensions.Contains(ext) && FfmpegCliBackend.IsAvailable())
        {
            FfmpegCliBackend.ScanSegmentIntoTopKHeapFast(
                filePath,
                startIdx,
                endIdx,
                globalFrameBase,
                options,
                enforcePlanetRejection,
                centrePlanet,
                maxKeepTarget,
                topKHeap,
                heapLock,
                ref currentMinScoreInHeap,
                ref scannedCount,
                ref acceptedBeforeLimit,
                ref discardedNoPlanet,
                cancellationToken,
                onStep);
            return;
        }

        if (UniversalMediaReader.IsOpenCvNativeAvailable && UniversalMediaReader.VideoExtensions.Contains(ext))
        {
        var cap = TryOpenVideoCapture(filePath);
        if (cap is not null) using (cap) {

            double fps = cap.Fps > 0 ? cap.Fps : 30.0;
            bool hasExtractedStart = WinJuposHelper.TryExtractDateTime(filePath, false, out var startUtc);
            if (!hasExtractedStart) startUtc = File.GetCreationTimeUtc(filePath);

            if (startIdx > 0)
            {
                cap.PosFrames = startIdx;
            }

            using var rawMat = new Mat();
            int idx = startIdx;
            int roiW = options.EnableCropping ? Math.Max(16, options.CropWidth) : 512;
            int roiH = options.EnableCropping ? Math.Max(16, options.CropHeight) : 512;
            int hintX = -1, hintY = -1, cachedThresh = -1;
            AstroFrame? spareFrame = null;

            while (idx <= endIdx && cap.Read(rawMat) && !rawMat.Empty())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int done = Interlocked.Increment(ref scannedCount);

                // Refresh auto-threshold every 45 frames
                if ((idx - startIdx) % 45 == 0) cachedThresh = -1;

                var det = UniversalMediaReader.DetectOnMatFastTracked(
                    rawMat,
                    enforcePlanetRejection,
                    options.AutoDetectionThreshold,
                    options.ManualDetectionThreshold,
                    options.MinimumObjectSizePixels,
                    ref hintX,
                    ref hintY,
                    ref cachedThresh);

                if (enforcePlanetRejection && !det.Accepted)
                {
                    Interlocked.Increment(ref discardedNoPlanet);
                    onStep(done);
                    idx++;
                    continue;
                }

                Interlocked.Increment(ref acceptedBeforeLimit);
                int targetX = centrePlanet ? det.CentroidX : rawMat.Width / 2;
                int targetY = centrePlanet ? det.CentroidY : rawMat.Height / 2;
                int offX = options.EnableCropping ? options.CropOffsetX : 0;
                int offY = options.EnableCropping ? options.CropOffsetY : 0;

                double score = options.EnableQualityEstimation
                    ? UniversalMediaReader.ComputeQualityOnMatRoiFast(
                        rawMat,
                        targetX - offX,
                        targetY - offY,
                        roiW,
                        roiH,
                        options.QualityAlgorithm,
                        options.QualityMinSubsample,
                        options.QualityMaxSubsample,
                        options.QualitySubsampleIncrement)
                    : 100.0;

                bool shouldExtract;
                lock (heapLock)
                {
                    shouldExtract = topKHeap.Count < maxKeepTarget || score > currentMinScoreInHeap;
                }

                if (shouldExtract)
                {
                    int globalIdx = globalFrameBase + (idx - startIdx);
                    AstroFrame work = (options.EnableCropping || centrePlanet)
                        ? UniversalMediaReader.ExtractCroppedAstroFrameFromMat(
                            rawMat,
                            globalIdx,
                            targetX,
                            targetY,
                            options.EnableCropping ? options.CropWidth : rawMat.Width,
                            options.EnableCropping ? options.CropHeight : rawMat.Height,
                            offX,
                            offY,
                            reusableDst: spareFrame,
                            protectBayerEvenSteps: options.DebayerEnabled || options.ProtectBayerPattern)
                        : UniversalMediaReader.MatToAstroFrame(rawMat, globalIdx);

                    work.DetectedCentroidX = det.CentroidX;
                    work.DetectedCentroidY = det.CentroidY;
                    work.QualityScore = score;
                    work.TimestampUtc = startUtc.AddSeconds(idx / fps);

                    lock (heapLock)
                    {
                        if (topKHeap.Count < maxKeepTarget)
                        {
                            topKHeap.Enqueue(work, score);
                            topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                            spareFrame = null;
                        }
                        else if (score > currentMinScoreInHeap)
                        {
                            var evicted = topKHeap.Dequeue();
                            topKHeap.Enqueue(work, score);
                            topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                            spareFrame = evicted;
                        }
                        else
                        {
                            spareFrame = work;
                        }
                    }
                }

                onStep(done);
                idx++;
            }
            return; }
        }

        if (ext == ".ser")
        {
            using var ser = new SerFile(filePath);
            int last = Math.Min(ser.Header.FrameCount - 1, endIdx);
            for (int idx = Math.Max(0, startIdx); idx <= last; idx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int done = Interlocked.Increment(ref scannedCount);

                var (accepted, cx, cy, fastScore) = ser.AnalyzeFrameZeroAlloc(idx, options, options.SerBitDepthOverride);
                if (enforcePlanetRejection && !accepted)
                {
                    Interlocked.Increment(ref discardedNoPlanet);
                    onStep(done);
                    continue;
                }

                Interlocked.Increment(ref acceptedBeforeLimit);
                double score = options.EnableQualityEstimation ? fastScore : 100.0;

                bool shouldExtract;
                lock (heapLock)
                {
                    shouldExtract = topKHeap.Count < maxKeepTarget || score > currentMinScoreInHeap;
                }

                if (shouldExtract)
                {
                    int targetX = centrePlanet ? cx : ser.Header.ImageWidth / 2;
                    int targetY = centrePlanet ? cy : ser.Header.ImageHeight / 2;
                    AstroFrame work = (options.EnableCropping || centrePlanet)
                        ? ser.ReadFrameCropped(
                            idx,
                            targetX,
                            targetY,
                            options.EnableCropping ? options.CropWidth : ser.Header.ImageWidth,
                            options.EnableCropping ? options.CropHeight : ser.Header.ImageHeight,
                            options.EnableCropping ? options.CropOffsetX : 0,
                            options.EnableCropping ? options.CropOffsetY : 0,
                            options.SerBitDepthOverride)
                        : ser.ReadFrame(idx, options.SerBitDepthOverride);

                    work.FrameIndex = globalFrameBase + (idx - startIdx);
                    work.DetectedCentroidX = cx;
                    work.DetectedCentroidY = cy;
                    if (options.DebayerEnabled && !options.ProtectBayerPattern && work.Channels == 1)
                    {
                        work = ImageProcessor.Debayer(work, options.DebayerPattern, options.DebayerType);
                    }
                    work.QualityScore = score;

                    lock (heapLock)
                    {
                        if (topKHeap.Count < maxKeepTarget)
                        {
                            topKHeap.Enqueue(work, score);
                            topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                        }
                        else if (score > currentMinScoreInHeap)
                        {
                            topKHeap.DequeueEnqueue(work, score);
                            topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                        }
                    }
                }

                onStep(done);
            }
            return;
        }

        // Non-video files (FITS, BMP, PNG, TIFF)
        foreach (var raw in UniversalMediaReader.EnumerateFrames(filePath, startIdx, endIdx, options.SerBitDepthOverride))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int done = Interlocked.Increment(ref scannedCount);
            var work = raw;
            work.FrameIndex = globalFrameBase + (raw.FrameIndex - startIdx);

            var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
                work,
                enforcePlanetRejection,
                options.AutoDetectionThreshold,
                options.ManualDetectionThreshold,
                options.MinimumObjectSizePixels,
                options.EdgeInShadow,
                options.RejectOverexposedFrames,
                options.MaxOverexposedPixels,
                rejectClippedObjects: false);

            if (enforcePlanetRejection && !det.Accepted)
            {
                Interlocked.Increment(ref discardedNoPlanet);
                onStep(done);
                continue;
            }

            Interlocked.Increment(ref acceptedBeforeLimit);
            work.DetectedCentroidX = det.CentroidX;
            work.DetectedCentroidY = det.CentroidY;
            if (options.EnableCropping || centrePlanet)
            {
                int targetX = centrePlanet ? det.CentroidX : work.Width / 2;
                int targetY = centrePlanet ? det.CentroidY : work.Height / 2;
                work = ImageProcessor.CropAroundPoint(
                    work,
                    targetX,
                    targetY,
                    options.EnableCropping ? options.CropWidth : work.Width,
                    options.EnableCropping ? options.CropHeight : work.Height,
                    options.EnableCropping ? options.CropOffsetX : 0,
                    options.EnableCropping ? options.CropOffsetY : 0,
                    protectBayerEvenSteps: work.IsBayerRaw || options.DebayerEnabled || options.ProtectBayerPattern);
            }

            if (options.DebayerEnabled && !options.ProtectBayerPattern)
            {
                if (work.Channels == 1)
                {
                    work = ImageProcessor.Debayer(work, options.DebayerPattern, options.DebayerType);
                }
                else if (work.Channels == 3 && ImageProcessor.IsMonochrome(work))
                {
                    var mono = ImageProcessor.ConvertToMonochrome(work, MonochromeConversionMode.UseRgbChannels);
                    work = ImageProcessor.Debayer(mono, options.DebayerPattern, options.DebayerType);
                }
            }

            double score = options.EnableQualityEstimation
                ? StabilisationAndQualityEngine.ComputeQualityScore(
                    work,
                    options.QualityAlgorithm,
                    options.QualityMinSubsample,
                    options.QualityMaxSubsample,
                    options.QualitySubsampleIncrement)
                : 100.0;
            work.QualityScore = score;

            lock (heapLock)
            {
                if (topKHeap.Count < maxKeepTarget)
                {
                    topKHeap.Enqueue(work, score);
                    topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                }
                else if (score > currentMinScoreInHeap)
                {
                    topKHeap.DequeueEnqueue(work, score);
                    topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                }
            }

            onStep(done);
        }
        }
        finally
        {
            try { Thread.CurrentThread.Priority = prevPriority; } catch { }
        }
    }

    private static void ScanSegmentMetadataOnly(
        int fileIndex,
        string filePath,
        int startIdx,
        int endIdx,
        ProcessingOptions options,
        bool enforcePlanetRejection,
        bool centrePlanet,
        List<FrameScanEntry> scanEntries,
        object scanLock,
        ref int scannedCount,
        ref int discardedNoPlanet,
        CancellationToken cancellationToken,
        Action<int> onStep)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        var localEntries = new List<FrameScanEntry>(Math.Max(16, endIdx - startIdx + 1));

        if (ext == ".ser")
        {
            using var ser = new SerFile(filePath);
            int last = Math.Min(ser.Header.FrameCount - 1, endIdx);
            for (int idx = Math.Max(0, startIdx); idx <= last; idx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int done = Interlocked.Increment(ref scannedCount);
                var (accepted, cx, cy, fastScore) = ser.AnalyzeFrameZeroAlloc(idx, options, options.SerBitDepthOverride);
                if (enforcePlanetRejection && !accepted)
                {
                    Interlocked.Increment(ref discardedNoPlanet);
                    onStep(done);
                    continue;
                }
                localEntries.Add(new FrameScanEntry(fileIndex, idx, cx, cy, options.EnableQualityEstimation ? fastScore : 100.0));
                onStep(done);
            }
            lock (scanLock)
            {
                scanEntries.AddRange(localEntries);
            }
            return;
        }

        if (UniversalMediaReader.IsOpenCvNativeAvailable && UniversalMediaReader.VideoExtensions.Contains(ext))
        {
        var cap = TryOpenVideoCapture(filePath);
        if (cap is not null) using (cap) {

            if (startIdx > 0)
            {
                cap.PosFrames = startIdx;
            }

            using var rawMat = new Mat();
            int idx = startIdx;
            int roiW = options.EnableCropping ? Math.Max(16, options.CropWidth) : 512;
            int roiH = options.EnableCropping ? Math.Max(16, options.CropHeight) : 512;
            int hintX = -1, hintY = -1, cachedThresh = -1;

            while (idx <= endIdx && cap.Read(rawMat) && !rawMat.Empty())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int done = Interlocked.Increment(ref scannedCount);
                if ((idx - startIdx) % 45 == 0) cachedThresh = -1;

                var det = UniversalMediaReader.DetectOnMatFastTracked(
                    rawMat,
                    enforcePlanetRejection,
                    options.AutoDetectionThreshold,
                    options.ManualDetectionThreshold,
                    options.MinimumObjectSizePixels,
                    ref hintX,
                    ref hintY,
                    ref cachedThresh);

                if (enforcePlanetRejection && !det.Accepted)
                {
                    Interlocked.Increment(ref discardedNoPlanet);
                    onStep(done);
                    idx++;
                    continue;
                }

                int targetX = centrePlanet ? det.CentroidX : rawMat.Width / 2;
                int targetY = centrePlanet ? det.CentroidY : rawMat.Height / 2;
                int offX = options.EnableCropping ? options.CropOffsetX : 0;
                int offY = options.EnableCropping ? options.CropOffsetY : 0;

                double score = options.EnableQualityEstimation
                    ? UniversalMediaReader.ComputeQualityOnMatRoiFast(
                        rawMat,
                        targetX - offX,
                        targetY - offY,
                        roiW,
                        roiH,
                        options.QualityAlgorithm,
                        options.QualityMinSubsample,
                        options.QualityMaxSubsample,
                        options.QualitySubsampleIncrement)
                    : 100.0;

                localEntries.Add(new FrameScanEntry(fileIndex, idx, det.CentroidX, det.CentroidY, score));
                onStep(done);
                idx++;
            }

            lock (scanLock)
            {
                scanEntries.AddRange(localEntries);
            }
            return; }
        }

        foreach (var raw in UniversalMediaReader.EnumerateFrames(filePath, startIdx, endIdx, options.SerBitDepthOverride))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int done = Interlocked.Increment(ref scannedCount);
            var work = raw;

            if (options.DebayerEnabled && !options.ProtectBayerPattern)
            {
                if (work.Channels == 1)
                {
                    work = ImageProcessor.Debayer(work, options.DebayerPattern, options.DebayerType);
                }
                else if (work.Channels == 3 && ImageProcessor.IsMonochrome(work))
                {
                    var mono = ImageProcessor.ConvertToMonochrome(work, MonochromeConversionMode.UseRgbChannels);
                    work = ImageProcessor.Debayer(mono, options.DebayerPattern, options.DebayerType);
                }
            }

            var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
                work,
                enforcePlanetRejection,
                options.AutoDetectionThreshold,
                options.ManualDetectionThreshold,
                options.MinimumObjectSizePixels,
                options.EdgeInShadow,
                options.RejectOverexposedFrames,
                options.MaxOverexposedPixels,
                rejectClippedObjects: false);

            if (enforcePlanetRejection && !det.Accepted)
            {
                Interlocked.Increment(ref discardedNoPlanet);
                onStep(done);
                continue;
            }

            double score = options.EnableQualityEstimation
                ? StabilisationAndQualityEngine.ComputeQualityScore(
                    work,
                    options.QualityAlgorithm,
                    options.QualityMinSubsample,
                    options.QualityMaxSubsample,
                    options.QualitySubsampleIncrement)
                : 100.0;

            localEntries.Add(new FrameScanEntry(fileIndex, raw.FrameIndex, det.CentroidX, det.CentroidY, score));
            onStep(done);
        }

        lock (scanLock)
        {
            scanEntries.AddRange(localEntries);
        }
    }

    private static void StreamFramesThroughProcessor(
        string filePath,
        int startIdx,
        int endIdx,
        ProcessingOptions options,
        bool enforcePlanetRejection,
        bool centrePlanet,
        CancellationToken cancellationToken,
        Action<AstroFrame?, DetectionResult> onFrameProcessed)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        var prevPriority = Thread.CurrentThread.Priority;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch
        {
            // Ignore on platforms that disallow changing thread priority
        }

        try
        {
        if (UniversalMediaReader.IsOpenCvNativeAvailable && UniversalMediaReader.VideoExtensions.Contains(ext))
        {
        var cap = TryOpenVideoCapture(filePath);
        if (cap is not null) using (cap) {

            if (startIdx > 0)
            {
                cap.PosFrames = startIdx;
            }

            using var rawMat = new Mat();
            int idx = startIdx;
            int hintX = -1, hintY = -1, cachedThresh = -1;

            while (idx <= endIdx && cap.Read(rawMat) && !rawMat.Empty())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((idx - startIdx) % 45 == 0) cachedThresh = -1;

                var det = UniversalMediaReader.DetectOnMatFastTracked(
                    rawMat,
                    enforcePlanetRejection,
                    options.AutoDetectionThreshold,
                    options.ManualDetectionThreshold,
                    options.MinimumObjectSizePixels,
                    ref hintX,
                    ref hintY,
                    ref cachedThresh);

                if (enforcePlanetRejection && !det.Accepted)
                {
                    onFrameProcessed(null, det);
                    idx++;
                    continue;
                }

                AstroFrame work;
                if (options.EnableCropping || centrePlanet)
                {
                    int targetX = centrePlanet ? det.CentroidX : rawMat.Width / 2;
                    int targetY = centrePlanet ? det.CentroidY : rawMat.Height / 2;
                    work = UniversalMediaReader.ExtractCroppedAstroFrameFromMat(
                        rawMat,
                        idx,
                        targetX,
                        targetY,
                        options.EnableCropping ? options.CropWidth : rawMat.Width,
                        options.EnableCropping ? options.CropHeight : rawMat.Height,
                        options.EnableCropping ? options.CropOffsetX : 0,
                        options.EnableCropping ? options.CropOffsetY : 0,
                        reusableDst: null,
                        protectBayerEvenSteps: options.DebayerEnabled || options.ProtectBayerPattern);
                }
                else
                {
                    work = UniversalMediaReader.MatToAstroFrame(rawMat, idx);
                }

                ApplyPostCropTransformations(ref work, options, det, recomputeQuality: options.EnableQualityEstimation);
                onFrameProcessed(work, det);
                idx++;
            }
            return; }
        }

        foreach (var raw in UniversalMediaReader.EnumerateFrames(filePath, startIdx, endIdx, options.SerBitDepthOverride))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var work = raw;

            if (options.DebayerEnabled && !options.ProtectBayerPattern)
            {
                if (work.Channels == 1)
                {
                    work = ImageProcessor.Debayer(work, options.DebayerPattern, options.DebayerType);
                }
                else if (work.Channels == 3 && ImageProcessor.IsMonochrome(work))
                {
                    var mono = ImageProcessor.ConvertToMonochrome(work, MonochromeConversionMode.UseRgbChannels);
                    work = ImageProcessor.Debayer(mono, options.DebayerPattern, options.DebayerType);
                }
            }

            var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
                work,
                enforcePlanetRejection,
                options.AutoDetectionThreshold,
                options.ManualDetectionThreshold,
                options.MinimumObjectSizePixels,
                options.EdgeInShadow,
                options.RejectOverexposedFrames,
                options.MaxOverexposedPixels,
                rejectClippedObjects: false);

            if (enforcePlanetRejection && !det.Accepted)
            {
                onFrameProcessed(null, det);
                continue;
            }

            if (options.EnableCropping || centrePlanet)
            {
                int targetX = centrePlanet ? det.CentroidX : work.Width / 2;
                int targetY = centrePlanet ? det.CentroidY : work.Height / 2;
                work = ImageProcessor.CropAroundPoint(
                    work,
                    targetX,
                    targetY,
                    options.EnableCropping ? options.CropWidth : work.Width,
                    options.EnableCropping ? options.CropHeight : work.Height,
                    options.EnableCropping ? options.CropOffsetX : 0,
                    options.EnableCropping ? options.CropOffsetY : 0,
                    options.ProtectBayerPattern);
            }

            ApplyPostCropTransformations(ref work, options, det, recomputeQuality: options.EnableQualityEstimation);
            onFrameProcessed(work, det);
        }
        }
        finally
        {
            try { Thread.CurrentThread.Priority = prevPriority; } catch { }
        }
    }

    private static void StreamSelectedFramesFromVideo(
        string filePath,
        int minFrame,
        int maxFrame,
        Dictionary<int, FrameScanEntry> wantedByFrame,
        ProcessingOptions options,
        bool centrePlanet,
        CancellationToken cancellationToken,
        Action<int, AstroFrame> onSelectedFrame)
    {
        if (!UniversalMediaReader.IsOpenCvNativeAvailable)
        {
            foreach (var raw in UniversalMediaReader.EnumerateFrames(filePath, minFrame, maxFrame, options.SerBitDepthOverride))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!wantedByFrame.TryGetValue(raw.FrameIndex, out var entry)) continue;
                var work = raw;
                var det = new DetectionResult(true, null, entry.CentroidX, entry.CentroidY, 100, 0, 40);
                if (options.EnableCropping || centrePlanet)
                {
                    work = ImageProcessor.CropAroundPoint(
                        work,
                        centrePlanet ? entry.CentroidX : work.Width / 2,
                        centrePlanet ? entry.CentroidY : work.Height / 2,
                        options.EnableCropping ? options.CropWidth : work.Width,
                        options.EnableCropping ? options.CropHeight : work.Height,
                        options.EnableCropping ? options.CropOffsetX : 0,
                        options.EnableCropping ? options.CropOffsetY : 0,
                        options.ProtectBayerPattern);
                }
                ApplyPostCropTransformations(ref work, options, det, recomputeQuality: false);
                onSelectedFrame(raw.FrameIndex, work);
            }
            return;
        }

        using var cap = TryOpenVideoCapture(filePath);
        if (cap is null)
        {
            foreach (var raw in UniversalMediaReader.EnumerateFrames(filePath, minFrame, maxFrame, options.SerBitDepthOverride))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!wantedByFrame.TryGetValue(raw.FrameIndex, out var entry)) continue;
                var work = raw;
                var det = new DetectionResult(true, null, entry.CentroidX, entry.CentroidY, 100, 0, 40);
                if (options.EnableCropping || centrePlanet)
                {
                    work = ImageProcessor.CropAroundPoint(
                        work,
                        centrePlanet ? entry.CentroidX : work.Width / 2,
                        centrePlanet ? entry.CentroidY : work.Height / 2,
                        options.EnableCropping ? options.CropWidth : work.Width,
                        options.EnableCropping ? options.CropHeight : work.Height,
                        options.EnableCropping ? options.CropOffsetX : 0,
                        options.EnableCropping ? options.CropOffsetY : 0,
                        options.ProtectBayerPattern);
                }
                ApplyPostCropTransformations(ref work, options, det, recomputeQuality: false);
                onSelectedFrame(raw.FrameIndex, work);
            }
            return;
        }

        if (minFrame > 0)
        {
            cap.PosFrames = minFrame;
        }

        using var rawMat = new Mat();
        int idx = minFrame;

        while (idx <= maxFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!wantedByFrame.TryGetValue(idx, out var entry))
            {
                if (!cap.Grab()) break;
                idx++;
                continue;
            }

            if (!cap.Read(rawMat) || rawMat.Empty()) break;

            var det = new DetectionResult(true, null, entry.CentroidX, entry.CentroidY, 100, 0, 40);
            AstroFrame work = (options.EnableCropping || centrePlanet)
                ? UniversalMediaReader.ExtractCroppedAstroFrameFromMat(
                    rawMat,
                    idx,
                    centrePlanet ? entry.CentroidX : rawMat.Width / 2,
                    centrePlanet ? entry.CentroidY : rawMat.Height / 2,
                    options.EnableCropping ? options.CropWidth : rawMat.Width,
                    options.EnableCropping ? options.CropHeight : rawMat.Height,
                    options.EnableCropping ? options.CropOffsetX : 0,
                    options.EnableCropping ? options.CropOffsetY : 0,
                    reusableDst: null,
                    protectBayerEvenSteps: options.DebayerEnabled || options.ProtectBayerPattern)
                : UniversalMediaReader.MatToAstroFrame(rawMat, idx);

            ApplyPostCropTransformations(ref work, options, det, recomputeQuality: false);
            onSelectedFrame(idx, work);
            idx++;
        }
    }

    private static void ApplyPostCropTransformations(ref AstroFrame work, ProcessingOptions options, DetectionResult det, bool recomputeQuality)
    {
        if (options.Binning != BinningMode.Disabled)
        {
            work = ImageProcessor.ApplyBinning(work, options.Binning, options.BinCalculation);
        }

        if (options.ConvertToMonochrome && work.Channels == 3)
        {
            work = ImageProcessor.ConvertToMonochrome(work, options.MonochromeConversionType);
        }

        if (options.EnableMedianNoiseFilter && work.Channels == 1)
        {
            work = ImageProcessor.ApplyMedianFilter3x3(work);
        }

        ImageProcessor.ApplyGainAndGamma(work, options.InputGain, options.Gamma);

        if (options.EnableHistogramStretch)
        {
            ImageProcessor.ApplyHistogramStretch(
                work,
                options.HistogramStretchWhitePointPercent,
                options.HistogramStretchBlackPointZero,
                options.HistogramStretchRgbIndividually);
        }

        work = ImageProcessor.ApplyFlipAndRotation(
            work,
            options.FlipHorizontalX,
            options.FlipVerticalY,
            options.Rotation);

        if (options.EnableFieldDerotation)
        {
            double safeFps = (double)options.WinJuposInputFps > 0 ? (double)options.WinJuposInputFps : 30.0;
            double angle = FieldDerotationEngine.ComputeFrameDerotationAngle(
                work.FrameIndex,
                Math.Max(100, work.FrameIndex + 1),
                safeFps,
                options.ObserverLatitudeDegrees,
                options.TargetAltitudeDegrees,
                options.TargetAzimuthDegrees);
            bool alreadyCentered = options.EnableCropping || (options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary);
            double? pivX = alreadyCentered ? null : det.CentroidX;
            double? pivY = alreadyCentered ? null : det.CentroidY;
            work = FieldDerotationEngine.ApplyDerotation(work, angle, pivX, pivY);
        }

        work.DetectedCentroidX = det.CentroidX;
        work.DetectedCentroidY = det.CentroidY;
        if (recomputeQuality)
        {
            work.QualityScore = options.EnableQualityEstimation
                ? StabilisationAndQualityEngine.ComputeQualityScore(
                    work,
                    options.QualityAlgorithm,
                    options.QualityMinSubsample,
                    options.QualityMaxSubsample,
                    options.QualitySubsampleIncrement)
                : 100.0;
        }
    }

    private static void WriteNonVideoOutput(string outputPath, List<AstroFrame> frames, ProcessingOptions options, bool isSource16Bit = false)
    {
        if (frames.Count == 0) return;

        if (options.OutputFormat == OutputFormat.Ser)
        {
            var hdr = new SerHeader
            {
                ImageWidth = frames[0].Width,
                ImageHeight = frames[0].Height,
                PixelDepthPerPlane = isSource16Bit ? 16 : 8,
                FrameCount = frames.Count,
                ColorId = frames[0].Channels == 3 ? SerColorId.Rgb : SerColorId.Mono,
                DateTimeUtcTicks = DateTime.UtcNow.Ticks
            };
            SerFile.WriteSerFile(outputPath, hdr, frames);
            return;
        }

        if (IsImageOutputFormat(options.OutputFormat))
        {
            string dir = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
            string stem = Path.GetFileNameWithoutExtension(outputPath);
            var match = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*?)(_\d{3,6})$");
            if (match.Success)
            {
                stem = match.Groups[1].Value;
            }

            int count = frames.Count;
            for (int i = 0; i < count; i++)
            {
                int seqNumber = i + 1;
                string framePath = GetImageFramePath(dir, stem, options.OutputFormat, seqNumber, count, options.AddLeadingZeros);
                UniversalMediaReader.WriteSingleImageFile(framePath, frames[i], isSource16Bit);
            }
        }
    }

    public async Task<PipelineRunSummary> ExecuteAsync(
        IReadOnlyList<AstroFrame> inputFrames,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (masterDark, masterFlat) = _calibrationProvider.PrepareCalibrationMasters(options, outputDirectory, progress, cancellationToken);
        var sw = Stopwatch.StartNew();
        int total = inputFrames.Count;
        int discardedNoPlanet = 0;
        int discardedQuality = 0;
        var accepted = new List<AstroFrame>(total);

        await Task.Run(() =>
        {
            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (proc, det) = PreviewSingleFrame(inputFrames[i], options, masterDark, masterFlat, applyCropToOutput: true);
                if (!det.Accepted)
                {
                    discardedNoPlanet++;
                    continue;
                }
                accepted.Add(proc);
            }
        }, cancellationToken);

        if (options.EnableQualityEstimation && accepted.Count > 0)
        {
            if (options.QualityReorderFrames)
            {
                accepted.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));
            }
            if (options.EnableQualityLimiting)
            {
                int keepCount = options.QualityLimitingMode == QualityLimitingType.PercentageOfFrames
                    ? Math.Max(1, accepted.Count * options.QualityFramesToKeep / 100)
                    : Math.Min(accepted.Count, Math.Max(1, options.QualityFramesToKeep));
                discardedQuality = accepted.Count - keepCount;
                accepted = accepted.Take(keepCount).ToList();
            }
        }

        Directory.CreateDirectory(outputDirectory);
        string outputPath = options.OutputFormat switch
        {
            OutputFormat.Avi => Path.Combine(outputDirectory, "cassiniflow_output.avi"),
            OutputFormat.Mp4 => Path.Combine(outputDirectory, "cassiniflow_output.mp4"),
            OutputFormat.Ser => Path.Combine(outputDirectory, "cassiniflow_output.ser"),
            _ => GetImageFramePath(outputDirectory, "cassiniflow_output", options.OutputFormat, 1, Math.Max(1, accepted.Count), options.AddLeadingZeros)
        };

        if (accepted.Count > 0)
        {
            if (options.OutputFormat is OutputFormat.Avi or OutputFormat.Mp4)
            {
                UniversalMediaReader.WriteVideoFile(outputPath, accepted, options.AviFpsValue, options.OutputFormat == OutputFormat.Mp4, options.AviCodec);
            }
            else
            {
                WriteNonVideoOutput(outputPath, accepted, options, isSource16Bit: true);
            }
        }

        sw.Stop();
        string logPath = Path.Combine(outputDirectory, "cassiniflow_output_log.txt");
        var summary = new PipelineRunSummary(accepted.Count > 0, sw.Elapsed, total, accepted.Count, discardedNoPlanet, 0, discardedQuality, outputPath, logPath);
        try
        {
            string logText = ExecutionLogGenerator.Generate(
                options,
                summary,
                sourceFiles: [],
                timestampUtc: DateTime.UtcNow);
            File.WriteAllText(logPath, logText);
        }
        catch
        {
        }
        return summary;
    }

    /// <summary>
    /// Encapsulates frame output writing across formats (AVI, MP4, SER, Image Sequences, In-Memory fallback).
    /// Uses switch dispatch and ensures deterministic cleanup of native resources.
    /// </summary>
    private sealed class PipelineFrameWriter : IDisposable
    {
        private readonly string _outputPath;
        private readonly string _outputDirectory;
        private readonly string _fullStem;
        private readonly OutputFormat _format;
        private readonly bool _isSource16Bit;
        private readonly int _totalFrames;
        private readonly bool _addLeadingZeros;
        private readonly VideoWriter? _videoWriter;
        private readonly Mat? _bgrMat;
        private readonly SerStreamWriter? _serWriter;
        private readonly List<AstroFrame>? _fallbackFrames;
        private int _framesWritten;

        public int FramesWritten => _framesWritten;

        public PipelineFrameWriter(
            string outputPath,
            string outputDirectory,
            string fullStem,
            OutputFormat format,
            int width,
            int height,
            int channels,
            double fps,
            bool isSource16Bit,
            int totalFrames,
            bool addLeadingZeros,
            bool isImageOutput)
        {
            _outputPath = outputPath;
            _outputDirectory = outputDirectory;
            _fullStem = fullStem;
            _format = format;
            _isSource16Bit = isSource16Bit;
            _totalFrames = totalFrames;
            _addLeadingZeros = addLeadingZeros;

            switch (format)
            {
                case OutputFormat.Avi when UniversalMediaReader.IsOpenCvNativeAvailable:
                    try
                    {
                        _videoWriter = new VideoWriter(outputPath, VideoWriter.FourCC('M', 'J', 'P', 'G'), fps, new Size(width, height), isColor: true);
                    }
                    catch
                    {
                        _videoWriter = null;
                    }
                    if (_videoWriter is not null && _videoWriter.IsOpened())
                    {
                        _bgrMat = new Mat(height, width, MatType.CV_8UC3);
                    }
                    else
                    {
                        _videoWriter?.Dispose();
                        _videoWriter = null;
                        _fallbackFrames = [];
                    }
                    break;

                case OutputFormat.Mp4 when UniversalMediaReader.IsOpenCvNativeAvailable:
                    try
                    {
                        _videoWriter = new VideoWriter(outputPath, VideoWriter.FourCC('m', 'p', '4', 'v'), fps, new Size(width, height), isColor: true);
                    }
                    catch
                    {
                        _videoWriter = null;
                    }
                    if (_videoWriter is not null && _videoWriter.IsOpened())
                    {
                        _bgrMat = new Mat(height, width, MatType.CV_8UC3);
                    }
                    else
                    {
                        _videoWriter?.Dispose();
                        _videoWriter = null;
                        _fallbackFrames = [];
                    }
                    break;

                case OutputFormat.Ser:
                    _serWriter = new SerStreamWriter(
                        outputPath,
                        width,
                        height,
                        channels,
                        pixelDepthPerPlane: isSource16Bit ? 16 : 8,
                        dateTimeTicks: DateTime.Now.Ticks,
                        dateTimeUtcTicks: DateTime.UtcNow.Ticks);
                    break;

                case OutputFormat.Bmp:
                case OutputFormat.Tiff:
                case OutputFormat.Png:
                case OutputFormat.Fits:
                    // Image sequences are written frame by frame directly to disk
                    break;

                default:
                    if (!isImageOutput)
                    {
                        _fallbackFrames = [];
                    }
                    break;
            }
        }

        public void WriteFrame(AstroFrame frame)
        {
            switch (_format)
            {
                case OutputFormat.Avi:
                case OutputFormat.Mp4:
                    if (_videoWriter is not null && _bgrMat is not null && _videoWriter.IsOpened())
                    {
                        UniversalMediaReader.CopyAstroFrameToBgrMat(frame, _bgrMat, _bgrMat.Width, _bgrMat.Height);
                        _videoWriter.Write(_bgrMat);
                        _framesWritten++;
                    }
                    else if (_fallbackFrames is not null)
                    {
                        _fallbackFrames.Add(frame);
                        _framesWritten++;
                    }
                    break;

                case OutputFormat.Ser:
                    if (_serWriter is not null)
                    {
                        _serWriter.WriteFrame(frame);
                        _framesWritten++;
                    }
                    break;

                case OutputFormat.Bmp:
                case OutputFormat.Tiff:
                case OutputFormat.Png:
                case OutputFormat.Fits:
                    int seqNumber = _framesWritten + 1;
                    string framePath = GetImageFramePath(_outputDirectory, _fullStem, _format, seqNumber, _totalFrames, _addLeadingZeros);
                    UniversalMediaReader.WriteSingleImageFile(framePath, frame, _isSource16Bit);
                    _framesWritten++;
                    break;

                default:
                    if (_fallbackFrames is not null)
                    {
                        _fallbackFrames.Add(frame);
                        _framesWritten++;
                    }
                    break;
            }
        }

        public void Finish()
        {
            _serWriter?.Finish();
        }

        public void FlushFallback(ProcessingOptions options, double fps)
        {
            if (_fallbackFrames is not { Count: > 0 }) return;

            if (options.OutputFormat is OutputFormat.Avi or OutputFormat.Mp4)
            {
                UniversalMediaReader.WriteVideoFile(_outputPath, _fallbackFrames, fps, options.OutputFormat == OutputFormat.Mp4, options.AviCodec);
            }
            else
            {
                WriteNonVideoOutput(_outputPath, _fallbackFrames, options, _isSource16Bit);
            }
        }

        public void Dispose()
        {
            _serWriter?.Dispose();
            _videoWriter?.Release();
            _videoWriter?.Dispose();
            _bgrMat?.Dispose();
        }
    }
    private static VideoCapture? TryOpenVideoCapture(string filePath)
    {
        try
        {
            var cap = new VideoCapture(filePath);
            if (cap.IsOpened()) return cap;
            cap.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

}
