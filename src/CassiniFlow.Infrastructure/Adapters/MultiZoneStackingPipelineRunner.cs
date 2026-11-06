using System.Collections.Concurrent;
using System.Diagnostics;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Codecs;
using OpenCvSharp;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Exécuteur haute performance du pipeline d'empilement multi-zones et du flux direct unifié.
/// - Passe 1 Zero-Allocation : évaluation directe sur pointeur MemoryMapped SER (0 allocation LOH, pas de débayérisation complète).
/// - Passe 2 Cumulative : un seul moteur d'empilement accumule les trames par ordre de qualité décroissante et exporte chaque palier (%) au vol.
/// - Cache de corrélation de phase (ReferenceCorrelationCache) : fenêtres de Hanning et tuiles de référence précalculées.
/// - Journalisation détaillée de chaque palier (%) dans la console en direct et dans le rapport d'exécution (.txt).
/// </summary>
public sealed class MultiZoneStackingPipelineRunner : IStackingPipelineUseCase
{
    private readonly IAstroLocalization _loc;
    private readonly ICalibrationFrameProvider _calibrationProvider;

    public MultiZoneStackingPipelineRunner(IAstroLocalization localization, ICalibrationFrameProvider calibrationProvider)
    {
        _loc = localization ?? throw new ArgumentNullException(nameof(localization));
        _calibrationProvider = calibrationProvider ?? throw new ArgumentNullException(nameof(calibrationProvider));
    }

    private readonly record struct FrameScoreRecord(int FrameIndex, double QualityScore, int CentroidX, int CentroidY, string FilePath = "");

    public async Task<PipelineRunSummary> ExecuteAsync(
        IReadOnlyList<string> inputFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var (masterDark, masterFlat) = _calibrationProvider.PrepareCalibrationMasters(
                options,
                outputDirectory,
                progress,
                cancellationToken);

            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(outputDirectory);

            var validFiles = inputFiles.Where(File.Exists).ToList();
            if (validFiles.Count == 0)
            {
                return new PipelineRunSummary(false, TimeSpan.Zero, 0, 0, 0, 0, 0, string.Empty);
            }

            if (options.RunMode == ProcessingRunMode.JoinMode && validFiles.Count > 1)
            {
                return ExecuteJoinModeInternal(validFiles, options, outputDirectory, sw, progress, cancellationToken, masterDark, masterFlat);
            }

            return ExecuteBatchModeInternal(validFiles, options, outputDirectory, sw, progress, cancellationToken, masterDark, masterFlat);
        }, cancellationToken);
    }

    private PipelineRunSummary ExecuteBatchModeInternal(
        List<string> validFiles,
        ProcessingOptions options,
        string outputDirectory,
        Stopwatch sw,
        IProgress<PipelineProgressReport>? progress,
        CancellationToken cancellationToken,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null)
    {
        int totalInputFrames = 0;
        int discardedNoPlanet = 0;
        int discardedQuality = 0;
        string lastOutputPath = string.Empty;
        int outputFramesWritten = 0;
        var allGeneratedFiles = new List<string>();
        var allTierSummaries = new List<StackedTierSummary>();
        int maxWorkers = PipelineRunner.GetSafeWorkerThreadCount(options);

        foreach (var file in validFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(file);
            string baseStem = Path.GetFileNameWithoutExtension(file);

            var summary = UniversalMediaReader.InspectFile(file);
            var (fileStart, fileEnd, framesToAnalyze) = options.GetEffectiveFrameRange(file, summary.FrameCount);
            int expectedTotalFrames = Math.Max(1, framesToAnalyze);
            string ext = Path.GetExtension(file).ToLowerInvariant();

            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.QualityAnalysis"), 1, fileName, 0, expectedTotalFrames, _loc.Format("Progress.FastSharpnessEval", expectedTotalFrames)));

            // =========================================================================
            // PASSE 1 : Analyse de qualité seeing & Sélection de la trame de référence
            // Zero-Allocation sur fichiers SER, vidéos AVI/MP4/MOV/MKV et images FITS/TIFF/PNG/BMP.
            // =========================================================================
            List<FrameScoreRecord> scoreRecords;
            AstroFrame? bestRefFrame = null;
            bool canUseZeroAlloc = masterDark == null && masterFlat == null;

            if (ext == ".ser")
            {
                using var ser = new SerFile(file);
                int totalFrames = ser.Header.FrameCount;
                var (serStart, serEnd, serFramesToAnalyze) = options.GetEffectiveFrameRange(file, totalFrames);
                int startIdx = serStart - 1;
                int endIdx = serEnd - 1;
                framesToAnalyze = Math.Max(0, endIdx - startIdx + 1);

                var records = new FrameScoreRecord?[totalFrames];
                int processedCount = 0;
                int bestIdx = startIdx;
                double bestScore = double.MinValue;
                object lockObj = new();
                int reportStep = Math.Max(50, framesToAnalyze / 40);

                Parallel.For(startIdx, endIdx + 1, new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxWorkers,
                    CancellationToken = cancellationToken
                }, i =>
                {
                    bool accepted;
                    int cx, cy;
                    double score;

                    if (canUseZeroAlloc)
                    {
                        (accepted, cx, cy, score) = ser.AnalyzeFrameZeroAlloc(i, options, options.SerBitDepthOverride);
                    }
                    else
                    {
                        var raw = ser.ReadFrame(i, options.SerBitDepthOverride);
                        var work = PrepareFrame(raw, options, out cx, out cy, out accepted, masterDark, masterFlat, totalFrames);
                        score = accepted
                            ? StabilisationAndQualityEngine.ComputeQualityScore(work, options.QualityAlgorithm, noiseRobust: options.Stacking.NoiseRobust)
                            : 0.0;
                    }

                    if (!accepted)
                    {
                        Interlocked.Increment(ref discardedNoPlanet);
                        return;
                    }

                    records[i] = new FrameScoreRecord(i, score, cx, cy, file);

                    if (score > bestScore)
                    {
                        lock (lockObj)
                        {
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestIdx = i;
                            }
                        }
                    }

                    int done = Interlocked.Increment(ref processedCount);
                    if (done % reportStep == 0 || done == framesToAnalyze)
                    {
                        int pct = Math.Clamp((int)Math.Round((double)done / Math.Max(1, framesToAnalyze) * 45.0), 1, 45);
                        int stepPct = Math.Clamp((int)Math.Round((double)done / Math.Max(1, framesToAnalyze) * 100.0), 1, 100);
                        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.QualityAnalysis"), pct, fileName, done, framesToAnalyze, _loc.Format("Progress.QualityEvalProgress", done, framesToAnalyze, stepPct)));
                    }
                });

                totalInputFrames += framesToAnalyze;
                scoreRecords = new List<FrameScoreRecord>(framesToAnalyze);
                for (int i = startIdx; i <= endIdx; i++)
                {
                    if (records[i].HasValue)
                    {
                        scoreRecords.Add(records[i]!.Value);
                    }
                }

                if (scoreRecords.Count > 0)
                {
                    var bestRec = records[bestIdx] ?? scoreRecords[0];
                    bestRefFrame = ReadAndPrepareSerFrameForStacking(ser, bestRec, options, masterDark, masterFlat, totalFrames);
                }
            }
            else if (canUseZeroAlloc &&
                     options.Binning == BinningMode.Disabled &&
                     UniversalMediaReader.VideoExtensions.Contains(ext) &&
                     TryAnalyzeVideoPass1ZeroAlloc(
                         file,
                         expectedTotalFrames,
                         options,
                         maxWorkers,
                         progress,
                         _loc.Get("Progress.QualityAnalysis"),
                         ref discardedNoPlanet,
                         ref totalInputFrames,
                         cancellationToken,
                         out var videoRecords,
                         out var videoBestRef))
            {
                scoreRecords = videoRecords;
                bestRefFrame = videoBestRef;
            }
            else if (canUseZeroAlloc &&
                     options.Binning == BinningMode.Disabled &&
                     (ext is ".fit" or ".fits" or ".fts" || UniversalMediaReader.ImageExtensions.Contains(ext)))
            {
                totalInputFrames++;
                var (accepted, cx, cy, score) = AnalyzeSingleImageFileZeroAlloc(file, options);
                scoreRecords = new List<FrameScoreRecord>(1);
                if (accepted)
                {
                    var rec = new FrameScoreRecord(0, score, cx, cy, file);
                    scoreRecords.Add(rec);
                    bestRefFrame = ReadAndPrepareNonSerFrameForStacking(rec, options, masterDark, masterFlat, 1);
                }
                else
                {
                    discardedNoPlanet++;
                }
            }
            else
            {
                int startIdx = fileStart - 1;
                int endIdx = fileEnd - 1;
                scoreRecords = new List<FrameScoreRecord>(framesToAnalyze);
                double bestQuality = double.MinValue;
                int pass1Processed = 0;
                int reportStep = Math.Max(10, framesToAnalyze / 40);

                foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (raw.FrameIndex < startIdx) continue;
                    if (raw.FrameIndex > endIdx) break;

                    totalInputFrames++;
                    pass1Processed++;

                    var workFrame = PrepareFrame(raw, options, out int cx, out int cy, out bool accepted, masterDark, masterFlat, framesToAnalyze);
                    if (!accepted)
                    {
                        discardedNoPlanet++;
                        continue;
                    }

                    double score = StabilisationAndQualityEngine.ComputeQualityScore(
                        workFrame,
                        options.QualityAlgorithm,
                        noiseRobust: options.Stacking.NoiseRobust);

                    scoreRecords.Add(new FrameScoreRecord(raw.FrameIndex, score, cx, cy, file));

                    if (score > bestQuality || bestRefFrame is null)
                    {
                        bestQuality = score;
                        bestRefFrame = workFrame.Clone();
                    }

                    if (pass1Processed % reportStep == 0 || pass1Processed == framesToAnalyze)
                    {
                        int pct = Math.Clamp((int)Math.Round((double)pass1Processed / framesToAnalyze * 45.0), 1, 45);
                        int stepPct = Math.Clamp((int)Math.Round((double)pass1Processed / framesToAnalyze * 100.0), 1, 100);
                        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.QualityAnalysis"), pct, fileName, pass1Processed, framesToAnalyze, _loc.Format("Progress.QualityEvalProgress", pass1Processed, framesToAnalyze, stepPct)));
                    }
                }
            }

            if (scoreRecords.Count == 0 || bestRefFrame is null)
            {
                continue;
            }

            // Tri décroissant par qualité
            scoreRecords.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));

            // Sélection de la trame de référence (Automatique ou Manuelle)
            if (options.Stacking.ReferenceMode == ReferenceFrameMode.Manual)
            {
                int targetIdx = Math.Clamp(options.Stacking.ManualReferenceFrameIndex, 0, scoreRecords.Count - 1);
                var manualRecord = scoreRecords.FirstOrDefault(r => r.FrameIndex == targetIdx);
                if (string.IsNullOrEmpty(manualRecord.FilePath))
                {
                    manualRecord = scoreRecords[0];
                }
                if (ext == ".ser")
                {
                    using var ser = new SerFile(file);
                    bestRefFrame = ReadAndPrepareSerFrameForStacking(ser, manualRecord, options, masterDark, masterFlat, ser.Header.FrameCount);
                }
                else
                {
                    bestRefFrame = ReadAndPrepareNonSerFrameForStacking(manualRecord, options, masterDark, masterFlat, expectedTotalFrames);
                }
            }

            // Génération de la trame de référence Double Stack (AutoStakkert Double Stack Reference)
            if (options.Stacking.DoubleStackReference && scoreRecords.Count >= 3)
            {
                progress?.Report(new PipelineProgressReport(_loc.Get("Progress.ReferenceFrame"), 46, fileName, 0, scoreRecords.Count, _loc.Get("Progress.DoubleStackSynth")));

                bestRefFrame = CreateDoubleStackReference(scoreRecords, bestRefFrame, options, masterDark, masterFlat, expectedTotalFrames);
            }

            // Détermination des paliers d'empilement
            var activePercentages = options.Stacking.StackPercentages.Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
            if (activePercentages.Count == 0)
            {
                activePercentages = [25.0];
            }

            int quotaMax = (options.Stacking.StackFrameCounts.Count > 0 && options.Stacking.StackFrameCounts[0] > 0)
                ? options.Stacking.StackFrameCounts[0]
                : int.MaxValue;

            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.AlignmentPoints"), 47, fileName, 0, scoreRecords.Count, _loc.Get("Progress.PreparingApGrid")));

            // Réutilisation de la grille AP placée par l'utilisateur si ses dimensions correspondent, sinon génération
            AlignmentGrid grid = ResolveAlignmentGrid(bestRefFrame, options.Stacking);

            // Construction des paliers triés par nombre de trames croissant
            var tiers = new List<StackingTier>();
            foreach (var pct in activePercentages)
            {
                int count = Math.Max(1, (int)Math.Round(scoreRecords.Count * (pct / 100.0)));
                count = Math.Min(count, quotaMax);
                count = Math.Clamp(count, 1, scoreRecords.Count);

                tiers.Add(new StackingTier
                {
                    Percentage = pct,
                    FramesToStack = count
                });
            }

            int maxFramesToStack = tiers.Max(t => t.FramesToStack);
            discardedQuality += (scoreRecords.Count - maxFramesToStack);

            var wantedList = scoreRecords.Take(maxFramesToStack).ToList();
            string tiersDesc = string.Join(", ", tiers.Select(t => _loc.Format("Stacking.TierFramesDesc", t.FramesToStack, t.Percentage)));

            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.MultiZoneStacking"), 50, fileName, 0, maxFramesToStack, _loc.Format("Progress.ApGridPaliers", grid.ActiveCount, grid.TileSize, tiersDesc)));

            string outExt = options.OutputFormat switch
            {
                OutputFormat.Fits => ".fit",
                OutputFormat.Png => ".png",
                _ => ".tif"
            };

            // =========================================================================
            // PASSE 2 : Empilement cumulatif unique avec cache de corrélation précalculé
            // =========================================================================
            var cumulativeStacker = new MultiZoneStackingEngine(
                bestRefFrame.Width,
                bestRefFrame.Height,
                bestRefFrame.Channels,
                options.Stacking,
                grid);

            using var corrCache = new ReferenceCorrelationCache(bestRefFrame, grid);
            int stackedSoFar = 0;
            int nextTierIdx = 0;

            void CheckAndExportCompletedTiers()
            {
                while (nextTierIdx < tiers.Count && stackedSoFar >= tiers[nextTierIdx].FramesToStack)
                {
                    var tier = tiers[nextTierIdx];
                    var stackedResult = cumulativeStacker.ProduceStackedFrame();
                    string outFileName = $"{baseStem}_stacked_p{(int)tier.Percentage}_{grid.ActiveCount}ap{outExt}";
                    string outFilePath = Path.Combine(outputDirectory, outFileName);

                    UniversalMediaReader.WriteSingleImageFile(outFilePath, stackedResult, is16Bit: true);
                    tier.OutputFilePath = outFilePath;
                    lastOutputPath = outFilePath;
                    outputFramesWritten++;
                    allGeneratedFiles.Add(outFilePath);

                    allTierSummaries.Add(new StackedTierSummary(
                        Percentage: tier.Percentage,
                        FramesStacked: tier.FramesToStack,
                        TotalValidFrames: scoreRecords.Count,
                        ActiveApCount: grid.ActiveCount,
                        TileSize: grid.TileSize,
                        OutputFilePath: outFilePath));

                    int pctProgress = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / maxFramesToStack * 48.0), 0, 48);
                    progress?.Report(new PipelineProgressReport(_loc.Format("Progress.TierCompleted", tier.Percentage), pctProgress, fileName, stackedSoFar, maxFramesToStack, _loc.Format("Progress.TierStackedSuccess", tier.Percentage, tier.FramesToStack, scoreRecords.Count, grid.ActiveCount, grid.TileSize, outFileName)));

                    nextTierIdx++;
                }
            }

            ExecuteParallelPass2Slices(
                wantedList,
                tiers,
                cumulativeStacker,
                corrCache,
                options,
                masterDark,
                masterFlat,
                expectedTotalFrames,
                maxFramesToStack,
                maxWorkers,
                fileName,
                _loc.Get("Progress.MultiZoneStacking"),
                progress,
                cancellationToken,
                ref stackedSoFar,
                ref nextTierIdx,
                CheckAndExportCompletedTiers);
        }

        sw.Stop();
        string generatedFilesText = string.Join(", ", allGeneratedFiles.Select(Path.GetFileName));
        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.Done"), 100, string.Empty, totalInputFrames, totalInputFrames, _loc.Format("Progress.StackingCompleted", sw.Elapsed.TotalSeconds, outputFramesWritten, generatedFilesText)));

        string logStem = validFiles.Count == 1 ? Path.GetFileNameWithoutExtension(validFiles[0]) : "stacking_batch";
        string logPath = Path.Combine(outputDirectory, $"{logStem}_stacked_log.txt");
        var runSummary = new PipelineRunSummary(
            Success: File.Exists(lastOutputPath),
            Elapsed: sw.Elapsed,
            TotalInputFrames: totalInputFrames,
            OutputFramesWritten: outputFramesWritten,
            DiscardedNoPlanet: discardedNoPlanet,
            DiscardedOverexposed: 0,
            DiscardedQuality: discardedQuality,
            OutputFilePath: lastOutputPath,
            LogFilePath: logPath,
            StackedTiers: allTierSummaries);

        try
        {
            string logText = ExecutionLogGenerator.Generate(
                options,
                runSummary,
                validFiles,
                timestampUtc: DateTime.UtcNow,
                generatedFiles: allGeneratedFiles,
                stackedTiers: allTierSummaries);
            File.WriteAllText(logPath, logText);
        }
        catch
        {
        }

        return runSummary;
    }

    private PipelineRunSummary ExecuteJoinModeInternal(
        List<string> validFiles,
        ProcessingOptions options,
        string outputDirectory,
        Stopwatch sw,
        IProgress<PipelineProgressReport>? progress,
        CancellationToken cancellationToken,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null)
    {
        int totalInputFrames = 0;
        int discardedNoPlanet = 0;
        int discardedQuality = 0;
        string lastOutputPath = string.Empty;
        int outputFramesWritten = 0;
        var allGeneratedFiles = new List<string>();
        var allTierSummaries = new List<StackedTierSummary>();
        int maxWorkers = PipelineRunner.GetSafeWorkerThreadCount(options);

        int totalExpectedFrames = Math.Max(1, validFiles.Sum(f =>
        {
            var summary = UniversalMediaReader.InspectFile(f);
            return options.GetEffectiveFrameRange(f, summary.FrameCount).EffectiveCount;
        }));
        int globalPass1Count = 0;

        var allScoreRecords = new List<FrameScoreRecord>(totalExpectedFrames);
        AstroFrame? bestRefFrame = null;
        double bestQuality = double.MinValue;
        object lockObj = new();

        bool canUseZeroAlloc = masterDark == null && masterFlat == null;

        // =========================================================================
        // PASSE 1 (FUSION) : Évaluation seeing et recherche de la meilleure trame
        // de référence globale sur l'ENSEMBLE des fichiers sources.
        // =========================================================================
        var singleImageFiles = new List<string>();
        var containerFiles = new List<string>();

        foreach (string f in validFiles)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".fit" or ".fits" or ".fts" || UniversalMediaReader.ImageExtensions.Contains(ext))
            {
                singleImageFiles.Add(f);
            }
            else
            {
                containerFiles.Add(f);
            }
        }

        // 1A. Parallélisation multi-cœurs sur les séquences d'images individuelles (FITS, TIFF, PNG, BMP, DNG, JPG)
        if (singleImageFiles.Count > 0)
        {
            var imgRecords = new FrameScoreRecord?[singleImageFiles.Count];
            int bestImgIdx = -1;
            double bestImgScore = double.MinValue;
            int reportStep = Math.Max(5, singleImageFiles.Count / 20);

            Parallel.For(0, singleImageFiles.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = maxWorkers,
                CancellationToken = cancellationToken
            }, idx =>
            {
                string imgFile = singleImageFiles[idx];
                bool accepted;
                int cx, cy;
                double score;

                if (canUseZeroAlloc && options.Binning == BinningMode.Disabled)
                {
                    (accepted, cx, cy, score) = AnalyzeSingleImageFileZeroAlloc(imgFile, options);
                }
                else
                {
                    var raw = UniversalMediaReader.ReadSingleFrame(imgFile, 0, options.SerBitDepthOverride);
                    var work = PrepareFrame(raw, options, out cx, out cy, out accepted, masterDark, masterFlat, totalExpectedFrames);
                    score = accepted
                        ? StabilisationAndQualityEngine.ComputeQualityScore(work, options.QualityAlgorithm, noiseRobust: options.Stacking.NoiseRobust)
                        : 0.0;
                }

                Interlocked.Increment(ref totalInputFrames);
                int done = Interlocked.Increment(ref globalPass1Count);

                if (!accepted)
                {
                    Interlocked.Increment(ref discardedNoPlanet);
                    return;
                }

                imgRecords[idx] = new FrameScoreRecord(0, score, cx, cy, imgFile);

                if (score > bestImgScore)
                {
                    lock (lockObj)
                    {
                        if (score > bestImgScore)
                        {
                            bestImgScore = score;
                            bestImgIdx = idx;
                        }
                    }
                }

                if (done % reportStep == 0 || done == totalExpectedFrames)
                {
                    int pct = Math.Clamp((int)Math.Round((double)done / totalExpectedFrames * 45.0), 1, 45);
                    int stepPct = Math.Clamp((int)Math.Round((double)done / totalExpectedFrames * 100.0), 1, 100);
                    progress?.Report(new PipelineProgressReport(_loc.Get("Progress.QualityAnalysisJoin"), pct, Path.GetFileName(imgFile), done, totalExpectedFrames, _loc.Format("Progress.GlobalEvalProgress", done, totalExpectedFrames, stepPct)));
                }
            });

            for (int i = 0; i < imgRecords.Length; i++)
            {
                if (imgRecords[i].HasValue)
                {
                    allScoreRecords.Add(imgRecords[i]!.Value);
                }
            }

            if (bestImgIdx >= 0 && bestImgScore > bestQuality)
            {
                bestQuality = bestImgScore;
                var bestRec = imgRecords[bestImgIdx]!.Value;
                bestRefFrame = ReadAndPrepareNonSerFrameForStacking(bestRec, options, masterDark, masterFlat, totalExpectedFrames);
            }
        }

        // 1B. Analyse des conteneurs multi-trames (SER, AVI, MP4, MOV, MKV)
        for (int fileIdx = 0; fileIdx < containerFiles.Count; fileIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = containerFiles[fileIdx];
            string fileName = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();

            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.QualityAnalysisJoin"), Math.Clamp((int)Math.Round((double)globalPass1Count / Math.Max(1, totalExpectedFrames) * 45.0), 1, 45), fileName, globalPass1Count, totalExpectedFrames, _loc.Format("Progress.GlobalEvalFile", fileIdx + 1, containerFiles.Count, fileName)));

            if (ext == ".ser")
            {
                using var ser = new SerFile(file);
                int totalFrames = ser.Header.FrameCount;
                var (serStart, serEnd, framesToAnalyze) = options.GetEffectiveFrameRange(file, totalFrames);
                int startIdx = serStart - 1;
                int endIdx = serEnd - 1;

                var records = new FrameScoreRecord?[totalFrames];
                int fileBestIdx = -1;
                double fileBestScore = double.MinValue;

                Parallel.For(startIdx, endIdx + 1, new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxWorkers,
                    CancellationToken = cancellationToken
                }, i =>
                {
                    bool accepted;
                    int cx, cy;
                    double score;

                    if (canUseZeroAlloc)
                    {
                        (accepted, cx, cy, score) = ser.AnalyzeFrameZeroAlloc(i, options, options.SerBitDepthOverride);
                    }
                    else
                    {
                        var raw = ser.ReadFrame(i, options.SerBitDepthOverride);
                        var work = PrepareFrame(raw, options, out cx, out cy, out accepted, masterDark, masterFlat, totalFrames);
                        score = accepted
                            ? StabilisationAndQualityEngine.ComputeQualityScore(work, options.QualityAlgorithm, noiseRobust: options.Stacking.NoiseRobust)
                            : 0.0;
                    }

                    if (!accepted)
                    {
                        Interlocked.Increment(ref discardedNoPlanet);
                        return;
                    }

                    records[i] = new FrameScoreRecord(i, score, cx, cy, file);

                    if (score > fileBestScore)
                    {
                        lock (lockObj)
                        {
                            if (score > fileBestScore)
                            {
                                fileBestScore = score;
                                fileBestIdx = i;
                            }
                        }
                    }

                    int done = Interlocked.Increment(ref globalPass1Count);
                    if (done % 50 == 0 || done == totalExpectedFrames)
                    {
                        int pct = Math.Clamp((int)Math.Round((double)done / totalExpectedFrames * 45.0), 1, 45);
                        int stepPct = Math.Clamp((int)Math.Round((double)done / totalExpectedFrames * 100.0), 1, 100);
                        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.QualityAnalysisJoin"), pct, fileName, done, totalExpectedFrames, _loc.Format("Progress.GlobalEvalProgress", done, totalExpectedFrames, stepPct)));
                    }
                });

                totalInputFrames += framesToAnalyze;
                for (int i = startIdx; i <= endIdx; i++)
                {
                    if (records[i].HasValue)
                    {
                        allScoreRecords.Add(records[i]!.Value);
                    }
                }

                if (fileBestIdx >= 0 && fileBestScore > bestQuality)
                {
                    lock (lockObj)
                    {
                        if (fileBestScore > bestQuality)
                        {
                            bestQuality = fileBestScore;
                            var bestRec = records[fileBestIdx]!.Value;
                            bestRefFrame = ReadAndPrepareSerFrameForStacking(ser, bestRec, options, masterDark, masterFlat, totalFrames);
                        }
                    }
                }
            }
            else if (canUseZeroAlloc &&
                     options.Binning == BinningMode.Disabled &&
                     UniversalMediaReader.VideoExtensions.Contains(ext) &&
                     TryAnalyzeVideoPass1ZeroAlloc(
                         file,
                         Math.Max(1, options.GetEffectiveFrameRange(file, UniversalMediaReader.InspectFile(file).FrameCount).EffectiveCount),
                         options,
                         maxWorkers,
                         progress,
                         _loc.Get("Progress.QualityAnalysisJoin"),
                         ref discardedNoPlanet,
                         ref totalInputFrames,
                         cancellationToken,
                         out var videoRecords,
                         out var videoBestRef,
                         ref globalPass1Count,
                         totalExpectedFrames))
            {
                allScoreRecords.AddRange(videoRecords);
                if (videoRecords.Count > 0 && videoBestRef is not null)
                {
                    double maxVideoScore = videoRecords.Max(r => r.QualityScore);
                    if (maxVideoScore > bestQuality || bestRefFrame is null)
                    {
                        bestQuality = maxVideoScore;
                        bestRefFrame = videoBestRef;
                    }
                }
            }
            else
            {
                var fileInfo = UniversalMediaReader.InspectFile(file);
                var (fileStart, fileEnd, _) = options.GetEffectiveFrameRange(file, fileInfo.FrameCount);
                int startIdx = fileStart - 1;
                int endIdx = fileEnd - 1;

                foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (raw.FrameIndex < startIdx) continue;
                    if (raw.FrameIndex > endIdx) break;

                    totalInputFrames++;
                    int done = Interlocked.Increment(ref globalPass1Count);

                    var workFrame = PrepareFrame(raw, options, out int cx, out int cy, out bool accepted, masterDark, masterFlat, totalExpectedFrames);
                    if (!accepted)
                    {
                        discardedNoPlanet++;
                        continue;
                    }

                    double score = StabilisationAndQualityEngine.ComputeQualityScore(
                        workFrame,
                        options.QualityAlgorithm,
                        noiseRobust: options.Stacking.NoiseRobust);

                    allScoreRecords.Add(new FrameScoreRecord(raw.FrameIndex, score, cx, cy, file));

                    if (score > bestQuality || bestRefFrame is null)
                    {
                        bestQuality = score;
                        bestRefFrame = workFrame.Clone();
                    }

                    if (done % 10 == 0 || done == totalExpectedFrames)
                    {
                        int pct = Math.Clamp((int)Math.Round((double)done / totalExpectedFrames * 45.0), 1, 45);
                        int stepPct = Math.Clamp((int)Math.Round((double)done / totalExpectedFrames * 100.0), 1, 100);
                        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.QualityAnalysisJoin"), pct, fileName, done, totalExpectedFrames, _loc.Format("Progress.GlobalEvalProgress", done, totalExpectedFrames, stepPct)));
                    }
                }
            }
        }

        if (allScoreRecords.Count > 0)
        {
            progress?.Report(new PipelineProgressReport(
                _loc.Get("Progress.QualityAnalysisJoin"),
                45,
                Path.GetFileName(validFiles[0]),
                allScoreRecords.Count,
                totalExpectedFrames,
                _loc.Format("Progress.GlobalEvalProgress", allScoreRecords.Count, totalExpectedFrames, 100)));
        }

        if (allScoreRecords.Count == 0 || bestRefFrame is null)
        {
            return new PipelineRunSummary(false, sw.Elapsed, totalInputFrames, 0, discardedNoPlanet, 0, 0, string.Empty);
        }

        // Tri global décroissant par score de qualité
        allScoreRecords.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));

        // Synthèse de référence Double Stack multi-fichiers
        if (options.Stacking.DoubleStackReference && allScoreRecords.Count >= 3)
        {
            progress?.Report(new PipelineProgressReport(_loc.Get("Progress.ReferenceFrame"), 46, Path.GetFileName(validFiles[0]), 0, allScoreRecords.Count, _loc.Get("Progress.DoubleStackSynthGlobal")));

            bestRefFrame = CreateDoubleStackReference(allScoreRecords, bestRefFrame, options, masterDark, masterFlat, totalExpectedFrames);
        }

        var activePercentages = options.Stacking.StackPercentages.Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
        if (activePercentages.Count == 0)
        {
            activePercentages = [25.0];
        }

        int quotaMax = (options.Stacking.StackFrameCounts.Count > 0 && options.Stacking.StackFrameCounts[0] > 0)
            ? options.Stacking.StackFrameCounts[0]
            : int.MaxValue;

        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.AlignmentPointsJoin"), 47, Path.GetFileName(validFiles[0]), 0, allScoreRecords.Count, _loc.Get("Progress.PreparingApGrid")));

        AlignmentGrid grid = ResolveAlignmentGrid(bestRefFrame, options.Stacking);

        var tiers = new List<StackingTier>();
        foreach (var pct in activePercentages)
        {
            int count = Math.Max(1, (int)Math.Round(allScoreRecords.Count * (pct / 100.0)));
            count = Math.Min(count, quotaMax);
            count = Math.Clamp(count, 1, allScoreRecords.Count);

            tiers.Add(new StackingTier
            {
                Percentage = pct,
                FramesToStack = count
            });
        }

        int maxFramesToStack = tiers.Max(t => t.FramesToStack);
        discardedQuality += (allScoreRecords.Count - maxFramesToStack);

        var wantedList = allScoreRecords.Take(maxFramesToStack).ToList();

        string tiersDesc = string.Join(", ", tiers.Select(t => _loc.Format("Stacking.TierFramesDesc", t.FramesToStack, t.Percentage)));
        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.MultiZoneStackingJoin"), 50, Path.GetFileName(validFiles[0]), 0, maxFramesToStack, _loc.Format("Progress.ApGridJoined", grid.ActiveCount, grid.TileSize, tiersDesc)));

        string outExt = options.OutputFormat switch
        {
            OutputFormat.Fits => ".fit",
            OutputFormat.Png => ".png",
            _ => ".tif"
        };

        string baseStem = Path.GetFileNameWithoutExtension(validFiles[0]);

        // =========================================================================
        // PASSE 2 (FUSION) : Accumulateur cumulatif unique & Parallélisation par paliers
        // =========================================================================
        var cumulativeStacker = new MultiZoneStackingEngine(
            bestRefFrame.Width,
            bestRefFrame.Height,
            bestRefFrame.Channels,
            options.Stacking,
            grid);

        using var corrCache = new ReferenceCorrelationCache(bestRefFrame, grid);
        int stackedSoFar = 0;
        int nextTierIdx = 0;

        void CheckAndExportCompletedJoinTiers()
        {
            while (nextTierIdx < tiers.Count && stackedSoFar >= tiers[nextTierIdx].FramesToStack)
            {
                var tier = tiers[nextTierIdx];
                var stackedResult = cumulativeStacker.ProduceStackedFrame();
                string outFileName = $"{baseStem}_joined_stacked_p{(int)tier.Percentage}_{grid.ActiveCount}ap{outExt}";
                string outFilePath = Path.Combine(outputDirectory, outFileName);

                UniversalMediaReader.WriteSingleImageFile(outFilePath, stackedResult, is16Bit: true);
                tier.OutputFilePath = outFilePath;
                lastOutputPath = outFilePath;
                outputFramesWritten++;
                allGeneratedFiles.Add(outFilePath);

                allTierSummaries.Add(new StackedTierSummary(
                    Percentage: tier.Percentage,
                    FramesStacked: tier.FramesToStack,
                    TotalValidFrames: allScoreRecords.Count,
                    ActiveApCount: grid.ActiveCount,
                    TileSize: grid.TileSize,
                    OutputFilePath: outFilePath));

                int pctProgress = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / maxFramesToStack * 48.0), 0, 48);
                progress?.Report(new PipelineProgressReport(_loc.Format("Progress.TierCompleted", tier.Percentage), pctProgress, Path.GetFileName(validFiles[0]), stackedSoFar, maxFramesToStack, _loc.Format("Progress.TierStackedSuccess", tier.Percentage, tier.FramesToStack, allScoreRecords.Count, grid.ActiveCount, grid.TileSize, outFileName)));

                nextTierIdx++;
            }
        }

        ExecuteParallelPass2Slices(
            wantedList,
            tiers,
            cumulativeStacker,
            corrCache,
            options,
            masterDark,
            masterFlat,
            totalExpectedFrames,
            maxFramesToStack,
            maxWorkers,
            Path.GetFileName(validFiles[0]),
            _loc.Get("Progress.MultiZoneStackingMerge"),
            progress,
            cancellationToken,
            ref stackedSoFar,
            ref nextTierIdx,
            CheckAndExportCompletedJoinTiers);

        sw.Stop();
        string generatedFilesText = string.Join(", ", allGeneratedFiles.Select(Path.GetFileName));
        progress?.Report(new PipelineProgressReport(_loc.Get("Progress.Done"), 100, string.Empty, totalInputFrames, totalInputFrames, _loc.Format("Progress.StackingJoinedCompleted", sw.Elapsed.TotalSeconds, validFiles.Count, generatedFilesText)));

        string logPath = Path.Combine(outputDirectory, $"{baseStem}_joined_stacked_log.txt");
        var summary = new PipelineRunSummary(
            Success: File.Exists(lastOutputPath),
            Elapsed: sw.Elapsed,
            TotalInputFrames: totalInputFrames,
            OutputFramesWritten: outputFramesWritten,
            DiscardedNoPlanet: discardedNoPlanet,
            DiscardedOverexposed: 0,
            DiscardedQuality: discardedQuality,
            OutputFilePath: lastOutputPath,
            LogFilePath: logPath,
            StackedTiers: allTierSummaries);

        try
        {
            string logText = ExecutionLogGenerator.Generate(
                options,
                summary,
                validFiles,
                timestampUtc: DateTime.UtcNow,
                generatedFiles: allGeneratedFiles,
                stackedTiers: allTierSummaries);
            File.WriteAllText(logPath, logText);
        }
        catch
        {
        }

        return summary;
    }

    private AlignmentGrid ResolveAlignmentGrid(AstroFrame bestRefFrame, StackingOptions stackingOptions)
    {
        if (stackingOptions.CustomAlignmentGrid is { ActiveCount: > 0 } custom &&
            custom.FrameWidth == bestRefFrame.Width &&
            custom.FrameHeight == bestRefFrame.Height)
        {
            int targetAnchorX = bestRefFrame.DetectedCentroidX > 0 ? bestRefFrame.DetectedCentroidX : bestRefFrame.Width / 2;
            int targetAnchorY = bestRefFrame.DetectedCentroidY > 0 ? bestRefFrame.DetectedCentroidY : bestRefFrame.Height / 2;
            int dxAnchor = custom.AnchorPoint.X > 0 ? (targetAnchorX - custom.AnchorPoint.X) : 0;
            int dyAnchor = custom.AnchorPoint.Y > 0 ? (targetAnchorY - custom.AnchorPoint.Y) : 0;

            int w = bestRefFrame.Width;
            int h = bestRefFrame.Height;

            var clonedPoints = new List<AlignmentPoint>(custom.Points.Count);
            foreach (var p in custom.Points)
            {
                int half = Math.Max(8, p.Size / 2);
                int nx = Math.Clamp(p.X + dxAnchor, half, Math.Max(half, w - half));
                int ny = Math.Clamp(p.Y + dyAnchor, half, Math.Max(half, h - half));
                clonedPoints.Add(new AlignmentPoint(p.Id, nx, ny, p.Size, p.IsActive)
                {
                    Brightness = p.Brightness,
                    Contrast = p.Contrast
                });
            }

            return new AlignmentGrid(
                custom.TileSize,
                custom.MinBrightness,
                clonedPoints,
                (targetAnchorX, targetAnchorY),
                custom.FrameWidth,
                custom.FrameHeight);
        }

        return AlignmentGridGenerator.GenerateGrid(bestRefFrame, stackingOptions);
    }

    /// <summary>
    /// Exécute la Passe 2 en tranches parallèles par palier (%) avec un accumulateur cumulatif unique,
    /// en optimisant l'accès aux fichiers SER (MemoryMapped), vidéos AVI/MP4/MOV/MKV (pré-extraction ROI sans allocation inutile)
    /// et séquences d'images FITS/TIFF/PNG/BMP/DNG (lecture recadrée multi-cœurs).
    /// </summary>
    private void ExecuteParallelPass2Slices(
        List<FrameScoreRecord> wantedList,
        List<StackingTier> tiers,
        MultiZoneStackingEngine cumulativeStacker,
        ReferenceCorrelationCache corrCache,
        ProcessingOptions options,
        AstroFrame? masterDark,
        AstroFrame? masterFlat,
        int defaultTotalFrames,
        int maxFramesToStack,
        int maxWorkers,
        string progressFileName,
        string progressStageName,
        IProgress<PipelineProgressReport>? progress,
        CancellationToken cancellationToken,
        ref int stackedSoFar,
        ref int nextTierIdx,
        Action checkAndExportCompletedTiers)
    {
        var openSers = new Dictionary<string, SerFile>(StringComparer.OrdinalIgnoreCase);
        var preExtractedVideoFrames = new Dictionary<(string FilePath, int FrameIndex), (AstroFrame Frame, bool AlreadyCropped)>();

        try
        {
            // 1. Ouvre les fichiers SER une seule fois et pré-extrait les sous-fenêtres des vidéos séquentielles (AVI/MP4/MOV/MKV)
            foreach (var fileGroup in wantedList.GroupBy(r => r.FilePath))
            {
                string file = fileGroup.Key;
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                string ext = Path.GetExtension(file).ToLowerInvariant();

                if (ext == ".ser")
                {
                    if (!openSers.ContainsKey(file))
                    {
                        openSers[file] = new SerFile(file);
                    }
                }
                else if (UniversalMediaReader.VideoExtensions.Contains(ext))
                {
                    ExtractWantedVideoFramesForPass2(file, fileGroup.ToList(), options, masterDark, masterFlat, preExtractedVideoFrames, cancellationToken);
                }
            }

            int apCount = Math.Max(1, corrCache.CachedActivePoints.Count);
            int parallelWorkers = (corrCache.ReferenceFrame.Width * corrCache.ReferenceFrame.Height > 800 * 800)
                ? Math.Clamp(maxWorkers, 1, 6)
                : maxWorkers;

            int localStackedSoFar = stackedSoFar;
            int sliceStart = 0;

            while (nextTierIdx < tiers.Count)
            {
                int sliceEnd = tiers[nextTierIdx].FramesToStack;
                if (sliceEnd > sliceStart)
                {
                    Parallel.For(
                        sliceStart,
                        sliceEnd,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelWorkers,
                            CancellationToken = cancellationToken
                        },
                        () => (
                            Buffer: cumulativeStacker.CreateLocalBuffer(),
                            ShiftX: new double[apCount],
                            ShiftY: new double[apCount]
                        ),
                        (i, _, local) =>
                        {
                            var record = wantedList[i];
                            string ext = Path.GetExtension(record.FilePath).ToLowerInvariant();
                            AstroFrame workFrame;

                            if (ext == ".ser" && openSers.TryGetValue(record.FilePath, out var ser))
                            {
                                workFrame = ReadAndPrepareSerFrameForStacking(ser, record, options, masterDark, masterFlat, ser.Header.FrameCount);
                            }
                            else if (preExtractedVideoFrames.TryGetValue((record.FilePath, record.FrameIndex), out var extracted))
                            {
                                int cx = extracted.AlreadyCropped ? extracted.Frame.Width / 2 : record.CentroidX;
                                int cy = extracted.AlreadyCropped ? extracted.Frame.Height / 2 : record.CentroidY;
                                workFrame = PrepareFrameFastForStacking(
                                    extracted.Frame,
                                    options,
                                    cx,
                                    cy,
                                    masterDark,
                                    masterFlat,
                                    alreadyCropped: extracted.AlreadyCropped,
                                    totalFrames: defaultTotalFrames);
                            }
                            else
                            {
                                workFrame = ReadAndPrepareNonSerFrameForStacking(record, options, masterDark, masterFlat, defaultTotalFrames);
                            }

                            double globalDx = 0.0;
                            double globalDy = 0.0;
                            if (!options.Stacking.DisableStabilisation)
                            {
                                (globalDx, globalDy) = corrCache.ComputeGlobalShift(workFrame);
                            }

                            corrCache.ComputeAllTileShiftsThreadSafe(workFrame, globalDx, globalDy, local.ShiftX, local.ShiftY);

                            int shiftX = (int)Math.Round(globalDx);
                            int shiftY = (int)Math.Round(globalDy);
                            cumulativeStacker.AccumulateFrameIntoBuffer(
                                workFrame,
                                shiftX,
                                shiftY,
                                local.ShiftX,
                                local.ShiftY,
                                local.Buffer);

                            int done = Interlocked.Increment(ref localStackedSoFar);
                            if (done % 25 == 0 || done == maxFramesToStack)
                            {
                                int pct = 50 + Math.Clamp((int)Math.Round((double)done / maxFramesToStack * 46.0), 0, 46);
                                int stepPct = Math.Clamp((int)Math.Round((double)done / Math.Max(1, maxFramesToStack) * 100.0), 1, 100);
                                progress?.Report(new PipelineProgressReport(progressStageName, pct, progressFileName, done, maxFramesToStack, _loc.Format("Progress.StackingFramesProgress", done, maxFramesToStack, stepPct)));
                            }

                            return local;
                        },
                        local =>
                        {
                            lock (cumulativeStacker)
                            {
                                cumulativeStacker.MergeLocalBuffer(local.Buffer);
                            }
                        });
                }

                stackedSoFar = localStackedSoFar;
                sliceStart = sliceEnd;
                checkAndExportCompletedTiers();
            }
        }
        finally
        {
            foreach (var ser in openSers.Values)
            {
                ser.Dispose();
            }
        }
    }

    /// <summary>
    /// Parcourt une seule fois un fichier vidéo (AVI/MP4/MOV/MKV) pour en extraire uniquement les trames retenues
    /// pour l'empilement, en découpant directement la sous-fenêtre recadrée depuis la Mat OpenCV native (sans allocation plein capteur).
    /// </summary>
    private void ExtractWantedVideoFramesForPass2(
        string filePath,
        List<FrameScoreRecord> wantedRecords,
        ProcessingOptions options,
        AstroFrame? masterDark,
        AstroFrame? masterFlat,
        Dictionary<(string FilePath, int FrameIndex), (AstroFrame Frame, bool AlreadyCropped)> dstDict,
        CancellationToken cancellationToken)
    {
        if (wantedRecords.Count == 0) return;

        var byFrameIndex = new Dictionary<int, FrameScoreRecord>(wantedRecords.Count);
        int minIdx = int.MaxValue;
        int maxIdx = int.MinValue;
        foreach (var r in wantedRecords)
        {
            byFrameIndex[r.FrameIndex] = r;
            if (r.FrameIndex < minIdx) minIdx = r.FrameIndex;
            if (r.FrameIndex > maxIdx) maxIdx = r.FrameIndex;
        }

        bool centrePlanetary = options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        bool canCropDirectFromMat =
            masterDark == null &&
            masterFlat == null &&
            options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne &&
            (options.EnableCropping || centrePlanetary) &&
            options.Binning == BinningMode.Disabled;

        if (UniversalMediaReader.IsOpenCvNativeAvailable)
        {
            VideoCapture? cap = null;
            try
            {
                cap = new VideoCapture(filePath);
            }
            catch
            {
                cap = null;
            }

            if (cap != null && cap.IsOpened())
            {
                using (cap)
                using (var rawMat = new Mat())
                {
                    if (minIdx > 0)
                    {
                        cap.PosFrames = minIdx;
                    }

                    int idx = Math.Max(0, minIdx);
                    int collected = 0;

                    while (idx <= maxIdx && cap.Read(rawMat) && !rawMat.Empty())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (byFrameIndex.TryGetValue(idx, out var rec))
                        {
                            if (canCropDirectFromMat)
                            {
                                int cropW = options.EnableCropping ? Math.Max(16, options.CropWidth) : rawMat.Width;
                                int cropH = options.EnableCropping ? Math.Max(16, options.CropHeight) : rawMat.Height;
                                int offX = options.EnableCropping ? options.CropOffsetX : 0;
                                int offY = options.EnableCropping ? options.CropOffsetY : 0;
                                int targetX = centrePlanetary ? rec.CentroidX : rawMat.Width / 2;
                                int targetY = centrePlanetary ? rec.CentroidY : rawMat.Height / 2;

                                var cropped = UniversalMediaReader.ExtractCroppedAstroFrameFromMat(
                                    rawMat,
                                    idx,
                                    targetX,
                                    targetY,
                                    cropW,
                                    cropH,
                                    offX,
                                    offY,
                                    protectBayerEvenSteps: options.DebayerEnabled);
                                dstDict[(filePath, idx)] = (cropped, true);
                            }
                            else
                            {
                                dstDict[(filePath, idx)] = (UniversalMediaReader.MatToAstroFrame(rawMat, idx), false);
                            }

                            collected++;
                            if (collected >= byFrameIndex.Count) break;
                        }
                        idx++;
                    }
                    return;
                }
            }
            cap?.Dispose();
        }

        // Fallback via EnumerateFrames
        int fallbackCollected = 0;
        foreach (var raw in UniversalMediaReader.EnumerateFrames(filePath, Math.Max(0, minIdx), maxIdx))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (byFrameIndex.ContainsKey(raw.FrameIndex))
            {
                dstDict[(filePath, raw.FrameIndex)] = (raw, false);
                fallbackCollected++;
                if (fallbackCollected >= byFrameIndex.Count) break;
            }
        }
    }

    /// <summary>
    /// Analyse la Passe 1 d'un fichier vidéo (AVI/MP4/MOV/MKV) directement sur les pointeurs Mat OpenCV natifs
    /// (zéro allocation d'AstroFrame par trame) et en parallèle par segments lorsque la vidéo est longue.
    /// </summary>
    private bool TryAnalyzeVideoPass1ZeroAlloc(
        string filePath,
        int expectedTotalFrames,
        ProcessingOptions options,
        int maxWorkers,
        IProgress<PipelineProgressReport>? progress,
        string stageName,
        ref int discardedNoPlanet,
        ref int totalInputFrames,
        CancellationToken cancellationToken,
        out List<FrameScoreRecord> scoreRecords,
        out AstroFrame? bestRefFrame)
    {
        int dummyGlobalDone = 0;
        return TryAnalyzeVideoPass1ZeroAlloc(
            filePath,
            expectedTotalFrames,
            options,
            maxWorkers,
            progress,
            stageName,
            ref discardedNoPlanet,
            ref totalInputFrames,
            cancellationToken,
            out scoreRecords,
            out bestRefFrame,
            ref dummyGlobalDone,
            0);
    }

    private bool TryAnalyzeVideoPass1ZeroAlloc(
        string filePath,
        int expectedTotalFrames,
        ProcessingOptions options,
        int maxWorkers,
        IProgress<PipelineProgressReport>? progress,
        string stageName,
        ref int discardedNoPlanet,
        ref int totalInputFrames,
        CancellationToken cancellationToken,
        out List<FrameScoreRecord> scoreRecords,
        out AstroFrame? bestRefFrame,
        ref int globalPass1Count,
        int globalTotalExpected)
    {
        scoreRecords = [];
        bestRefFrame = null;

        if (!UniversalMediaReader.IsOpenCvNativeAvailable)
        {
            return false;
        }

        VideoCapture? testCap = null;
        try
        {
            testCap = new VideoCapture(filePath);
            if (!testCap.IsOpened())
            {
                testCap.Dispose();
                return false;
            }
        }
        catch
        {
            testCap?.Dispose();
            return false;
        }

        int rawCount = Math.Max(expectedTotalFrames, testCap.FrameCount);
        testCap.Dispose();

        var (fileStart, fileEnd, framesToAnalyze) = options.GetEffectiveFrameRange(filePath, rawCount);
        int startIdx = fileStart - 1;
        int endIdx = fileEnd - 1;
        framesToAnalyze = Math.Max(1, framesToAnalyze);

        var records = new ConcurrentBag<FrameScoreRecord>();
        int localProcessed = 0;
        int localDiscarded = 0;
        string fileName = Path.GetFileName(filePath);
        int reportTotal = globalTotalExpected > 0 ? globalTotalExpected : framesToAnalyze;
        int reportStep = Math.Max(15, reportTotal / 40);

        int chunkSize = 200;
        var chunks = new List<(int Start, int End)>();
        if (framesToAnalyze >= 300 && maxWorkers > 1)
        {
            for (int s = startIdx; s <= endIdx; s += chunkSize)
            {
                chunks.Add((s, Math.Min(endIdx, s + chunkSize - 1)));
            }
        }
        else
        {
            chunks.Add((startIdx, endIdx));
        }

        int localGlobalCount = globalPass1Count;

        Parallel.ForEach(
            chunks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxWorkers, 1, 6),
                CancellationToken = cancellationToken
            },
            chunk =>
            {
                using var cap = new VideoCapture(filePath);
                if (!cap.IsOpened()) return;

                if (chunk.Start > 0)
                {
                    cap.PosFrames = chunk.Start;
                }

                using var rawMat = new Mat();
                int idx = chunk.Start;
                int hintX = -1, hintY = -1, cachedThresh = -1;

                while (idx <= chunk.End && cap.Read(rawMat) && !rawMat.Empty())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((idx - chunk.Start) % 45 == 0) cachedThresh = -1;

                    var (accepted, cx, cy, score) = UniversalMediaReader.AnalyzeMatZeroAlloc(
                        rawMat,
                        options,
                        ref hintX,
                        ref hintY,
                        ref cachedThresh);

                    Interlocked.Increment(ref localProcessed);
                    int done = globalTotalExpected > 0
                        ? Interlocked.Increment(ref localGlobalCount)
                        : localProcessed;

                    if (!accepted)
                    {
                        Interlocked.Increment(ref localDiscarded);
                    }
                    else
                    {
                        records.Add(new FrameScoreRecord(idx, score, cx, cy, filePath));
                    }

                    if (done % reportStep == 0 || done == reportTotal)
                    {
                        int pct = Math.Clamp((int)Math.Round((double)done / Math.Max(1, reportTotal) * 45.0), 1, 45);
                        int stepPct = Math.Clamp((int)Math.Round((double)done / Math.Max(1, reportTotal) * 100.0), 1, 100);
                        progress?.Report(new PipelineProgressReport(stageName, pct, fileName, done, reportTotal, _loc.Format("Progress.QualityEvalProgress", done, reportTotal, stepPct)));
                    }

                    idx++;
                }
            });

        if (localProcessed == 0)
        {
            return false;
        }

        Interlocked.Add(ref totalInputFrames, localProcessed);
        Interlocked.Add(ref discardedNoPlanet, localDiscarded);
        if (globalTotalExpected > 0)
        {
            globalPass1Count = localGlobalCount;
        }

        scoreRecords = records.OrderBy(r => r.FrameIndex).ToList();
        if (scoreRecords.Count > 0)
        {
            var bestRec = scoreRecords.MaxBy(r => r.QualityScore);
            bestRefFrame = ReadAndPrepareNonSerFrameForStacking(bestRec, options, null, null, rawCount);
        }

        return true;
    }

    /// <summary>
    /// Analyse rapide sans débayérisation plein capteur pour une image individuelle (FITS, TIFF, PNG, BMP, JPG, DNG).
    /// </summary>
    private (bool Accepted, int CentroidX, int CentroidY, double QualityScore) AnalyzeSingleImageFileZeroAlloc(
        string filePath,
        ProcessingOptions options)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        int hx = -1, hy = -1, thresh = -1;

        if (ext == ".dng")
        {
            using var dngMat = DngReader.Decode(filePath);
            if (dngMat != null && !dngMat.Empty())
            {
                return UniversalMediaReader.AnalyzeMatZeroAlloc(dngMat, options, ref hx, ref hy, ref thresh);
            }
        }
        else if (UniversalMediaReader.ImageExtensions.Contains(ext) && ext != ".bmp" && UniversalMediaReader.IsOpenCvNativeAvailable)
        {
            using var mat = Cv2.ImRead(filePath, ImreadModes.Unchanged);
            if (!mat.Empty())
            {
                return UniversalMediaReader.AnalyzeMatZeroAlloc(mat, options, ref hx, ref hy, ref thresh);
            }
        }

        var raw = UniversalMediaReader.ReadSingleFrame(filePath, 0, options.SerBitDepthOverride);
        bool checkPlanet = options.CheckPlanetObjectPresent && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
            raw,
            checkPlanet,
            options.AutoDetectionThreshold,
            options.ManualDetectionThreshold,
            options.MinimumObjectSizePixels,
            options.EdgeInShadow,
            options.RejectOverexposedFrames,
            options.MaxOverexposedPixels,
            rejectClippedObjects: false);

        if (checkPlanet && !det.Accepted)
        {
            return (false, det.CentroidX, det.CentroidY, 0.0);
        }

        bool centrePlanet = options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        int targetX = centrePlanet ? det.CentroidX : raw.Width / 2;
        int targetY = centrePlanet ? det.CentroidY : raw.Height / 2;
        int estRadius = Math.Max(options.MinimumObjectSizePixels / 2, (int)Math.Sqrt(Math.Max(16, det.DetectedPixelCount) / Math.PI));
        int planetDiameter = Math.Clamp(estRadius * 2 + 24, 48, Math.Min(raw.Width, raw.Height));
        int roiW = options.EnableCropping ? Math.Min(Math.Max(16, options.CropWidth), planetDiameter) : planetDiameter;
        int roiH = options.EnableCropping ? Math.Min(Math.Max(16, options.CropHeight), planetDiameter) : planetDiameter;

        var roiFrame = ImageProcessor.CropAroundPoint(
            raw,
            targetX,
            targetY,
            roiW,
            roiH,
            options.EnableCropping ? options.CropOffsetX : 0,
            options.EnableCropping ? options.CropOffsetY : 0,
            protectBayerEvenSteps: false);

        double score = StabilisationAndQualityEngine.ComputeQualityScore(
            roiFrame,
            options.QualityAlgorithm,
            options.QualityMinSubsample,
            options.QualityMaxSubsample,
            options.QualitySubsampleIncrement,
            options.Stacking.NoiseRobust);

        return (true, det.CentroidX, det.CentroidY, score);
    }

    /// <summary>
    /// Lit et prépare une trame non-SER (FITS, BMP, TIFF, PNG, DNG, vidéo) en Passe 2 en ne lisant/découpant
    /// que la sous-fenêtre recadrée lorsque le recadrage est actif.
    /// </summary>
    private AstroFrame ReadAndPrepareNonSerFrameForStacking(
        FrameScoreRecord record,
        ProcessingOptions options,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        int totalFrames = 0)
    {
        string ext = Path.GetExtension(record.FilePath).ToLowerInvariant();
        bool centrePlanetary = options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        bool isSingleImage = ext is ".fit" or ".fits" or ".fts" || UniversalMediaReader.ImageExtensions.Contains(ext);

        if (isSingleImage &&
            masterDark == null &&
            masterFlat == null &&
            options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne &&
            options.EnableCropping &&
            options.Binning == BinningMode.Disabled)
        {
            int cropW = Math.Max(16, options.CropWidth);
            int cropH = Math.Max(16, options.CropHeight);
            int offX = options.CropOffsetX;
            int offY = options.CropOffsetY;
            int targetX = centrePlanetary ? record.CentroidX : Math.Max(cropW / 2, record.CentroidX);
            int targetY = centrePlanetary ? record.CentroidY : Math.Max(cropH / 2, record.CentroidY);
            if (!centrePlanetary)
            {
                var info = UniversalMediaReader.InspectFile(record.FilePath);
                targetX = info.Width / 2;
                targetY = info.Height / 2;
            }

            var croppedRaw = UniversalMediaReader.ReadSingleFrameCropped(
                record.FilePath,
                record.FrameIndex,
                targetX,
                targetY,
                cropW,
                cropH,
                offX,
                offY,
                protectBayerEvenSteps: options.DebayerEnabled,
                serBitDepthOverride: options.SerBitDepthOverride);

            return PrepareFrameFastForStacking(
                croppedRaw,
                options,
                cropW / 2,
                cropH / 2,
                masterDark,
                masterFlat,
                alreadyCropped: true,
                totalFrames: totalFrames);
        }

        var fullRaw = UniversalMediaReader.ReadSingleFrame(record.FilePath, record.FrameIndex, options.SerBitDepthOverride);
        return PrepareFrameFastForStacking(
            fullRaw,
            options,
            record.CentroidX,
            record.CentroidY,
            masterDark,
            masterFlat,
            alreadyCropped: false,
            totalFrames: totalFrames);
    }

    /// <summary>
    /// Lit et prépare une trame SER en Passe 2 en ne lisant depuis la mémoire mappée que la sous-fenêtre
    /// recadrée (ReadFrameCropped) lorsque le recadrage est actif. Évite de lire et débayériser toute la trame plein capteur.
    /// </summary>
    private AstroFrame ReadAndPrepareSerFrameForStacking(
        SerFile ser,
        FrameScoreRecord record,
        ProcessingOptions options,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        int totalFrames = 0)
    {
        int effectiveTotal = totalFrames > 0 ? totalFrames : ser.Header.FrameCount;
        bool centrePlanetary = options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        if (masterDark == null &&
            masterFlat == null &&
            options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne &&
            (options.EnableCropping || centrePlanetary) &&
            options.Binning == BinningMode.Disabled)
        {
            int cropW = options.EnableCropping ? Math.Max(16, options.CropWidth) : ser.Header.ImageWidth;
            int cropH = options.EnableCropping ? Math.Max(16, options.CropHeight) : ser.Header.ImageHeight;
            int offX = options.EnableCropping ? options.CropOffsetX : 0;
            int offY = options.EnableCropping ? options.CropOffsetY : 0;
            int targetX = centrePlanetary
                ? record.CentroidX
                : ser.Header.ImageWidth / 2;
            int targetY = centrePlanetary
                ? record.CentroidY
                : ser.Header.ImageHeight / 2;

            var croppedRaw = ser.ReadFrameCropped(
                record.FrameIndex,
                targetX,
                targetY,
                cropW,
                cropH,
                offX,
                offY,
                options.SerBitDepthOverride);

            return PrepareFrameFastForStacking(
                croppedRaw,
                options,
                cropW / 2,
                cropH / 2,
                masterDark,
                masterFlat,
                alreadyCropped: true,
                totalFrames: effectiveTotal);
        }

        var fullRaw = ser.ReadFrame(record.FrameIndex, options.SerBitDepthOverride);
        return PrepareFrameFastForStacking(
            fullRaw,
            options,
            record.CentroidX,
            record.CentroidY,
            masterDark,
            masterFlat,
            alreadyCropped: false,
            totalFrames: effectiveTotal);
    }

    /// <summary>
    /// Prépare une trame retenue en Passe 2 en réutilisant le centroïde déjà calculé en Passe 1,
    /// en recadrant AVANT la débayérisation lorsque c'est possible (gain 10x-15x).
    /// </summary>
    private AstroFrame PrepareFrameFastForStacking(
        AstroFrame raw,
        ProcessingOptions options,
        int knownCentroidX,
        int knownCentroidY,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        bool alreadyCropped = false,
        int totalFrames = 0)
    {
        var work = raw;

        if (masterDark != null || masterFlat != null)
        {
            ImageProcessor.ApplyCalibration(
                work,
                masterDark,
                options.ReplaceHotPixels,
                options.HotPixelThreshold,
                options.DarkDataHasBayer,
                masterFlat);
        }

        bool centrePlanetary = options.CentreObjectInFrame && options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        bool canCropBeforeDebayer =
            !alreadyCropped &&
            options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne &&
            (options.EnableCropping || centrePlanetary) &&
            options.Binning == BinningMode.Disabled;

        if (canCropBeforeDebayer)
        {
            int cropW = options.EnableCropping ? Math.Max(16, options.CropWidth) : work.Width;
            int cropH = options.EnableCropping ? Math.Max(16, options.CropHeight) : work.Height;
            int offX = options.EnableCropping ? options.CropOffsetX : 0;
            int offY = options.EnableCropping ? options.CropOffsetY : 0;
            int targetX = centrePlanetary
                ? knownCentroidX
                : work.Width / 2;
            int targetY = centrePlanetary
                ? knownCentroidY
                : work.Height / 2;

            work = ImageProcessor.CropAroundPoint(
                work,
                targetX,
                targetY,
                cropW,
                cropH,
                offX,
                offY,
                protectBayerEvenSteps: work.IsBayerRaw || options.DebayerEnabled);
            alreadyCropped = true;
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

        if (options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne)
        {
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

            if ((options.EnableCropping || centrePlanetary) && !alreadyCropped)
            {
                int cropW = options.EnableCropping ? Math.Max(16, options.CropWidth) : work.Width;
                int cropH = options.EnableCropping ? Math.Max(16, options.CropHeight) : work.Height;
                int offX = options.EnableCropping ? options.CropOffsetX : 0;
                int offY = options.EnableCropping ? options.CropOffsetY : 0;
                int targetX = centrePlanetary
                    ? knownCentroidX
                    : work.Width / 2;
                int targetY = centrePlanetary
                    ? knownCentroidY
                    : work.Height / 2;

                work = ImageProcessor.CropAroundPoint(
                    work,
                    targetX,
                    targetY,
                    cropW,
                    cropH,
                    offX,
                    offY,
                    options.ProtectBayerPattern);
                alreadyCropped = true;
            }

            if (options.FlipHorizontalX || options.FlipVerticalY || options.Rotation != RotationAngle.None)
            {
                work = ImageProcessor.ApplyFlipAndRotation(
                    work,
                    options.FlipHorizontalX,
                    options.FlipVerticalY,
                    options.Rotation);
            }
        }

        if (options.EnableFieldDerotation)
        {
            double safeFps = (double)options.WinJuposInputFps > 0 ? (double)options.WinJuposInputFps : 30.0;
            int effectiveTotal = totalFrames > 0 ? totalFrames : Math.Max(100, work.FrameIndex + 1);
            double angle = FieldDerotationEngine.ComputeFrameDerotationAngle(
                work.FrameIndex,
                effectiveTotal,
                safeFps,
                options.ObserverLatitudeDegrees,
                options.TargetAltitudeDegrees,
                options.TargetAzimuthDegrees);
            double? pivotX = alreadyCropped ? null : (knownCentroidX > 0 ? knownCentroidX : null);
            double? pivotY = alreadyCropped ? null : (knownCentroidY > 0 ? knownCentroidY : null);
            work = FieldDerotationEngine.ApplyDerotation(work, angle, pivotX, pivotY);
        }

        work.DetectedCentroidX = alreadyCropped ? work.Width / 2 : (knownCentroidX > 0 ? knownCentroidX : work.Width / 2);
        work.DetectedCentroidY = alreadyCropped ? work.Height / 2 : (knownCentroidY > 0 ? knownCentroidY : work.Height / 2);
        return work;
    }

    private AstroFrame PrepareFrame(
        AstroFrame raw,
        ProcessingOptions options,
        out int centroidX,
        out int centroidY,
        out bool accepted,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        int totalFrames = 0)
    {
        var detectWork = raw;
        accepted = true;

        if (masterDark != null || masterFlat != null)
        {
            detectWork = raw.Clone();
            ImageProcessor.ApplyCalibration(
                detectWork,
                masterDark,
                options.ReplaceHotPixels,
                options.HotPixelThreshold,
                options.DarkDataHasBayer,
                masterFlat);
        }

        if (options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne &&
            options.Binning != BinningMode.Disabled)
        {
            if (options.DebayerEnabled && !options.ProtectBayerPattern && detectWork.Channels == 1)
            {
                detectWork = ImageProcessor.Debayer(detectWork, options.DebayerPattern, options.DebayerType);
            }
            detectWork = ImageProcessor.ApplyBinning(detectWork, options.Binning, options.BinCalculation);
        }

        if (options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne)
        {
            var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
                detectWork,
                options.CheckPlanetObjectPresent && options.StabilisationMode == StabilisationMode.ObjectPlanetary,
                options.AutoDetectionThreshold,
                options.ManualDetectionThreshold,
                options.MinimumObjectSizePixels,
                options.EdgeInShadow,
                options.RejectOverexposedFrames,
                options.MaxOverexposedPixels,
                rejectClippedObjects: false);

            if (options.CheckPlanetObjectPresent && !det.Accepted)
            {
                centroidX = detectWork.Width / 2;
                centroidY = detectWork.Height / 2;
                accepted = false;
                return raw;
            }

            centroidX = det.CentroidX;
            centroidY = det.CentroidY;
            return PrepareFrameFastForStacking(
                raw,
                options,
                centroidX,
                centroidY,
                masterDark,
                masterFlat,
                alreadyCropped: false,
                totalFrames: totalFrames);
        }

        centroidX = detectWork.Width / 2;
        centroidY = detectWork.Height / 2;
        return PrepareFrameFastForStacking(
            raw,
            options,
            centroidX,
            centroidY,
            masterDark,
            masterFlat,
            alreadyCropped: false,
            totalFrames: totalFrames);
    }

    private AstroFrame CreateDoubleStackReference(
        IReadOnlyList<FrameScoreRecord> sortedRecords,
        AstroFrame initialBest,
        ProcessingOptions options,
        AstroFrame? masterDark = null,
        AstroFrame? masterFlat = null,
        int totalFrames = 0)
    {
        int countToAverage = Math.Clamp(Math.Min(10, Math.Max(3, sortedRecords.Count / 10)), 3, sortedRecords.Count);
        int w = initialBest.Width;
        int h = initialBest.Height;
        int ch = initialBest.Channels;
        int totalPixels = w * h;

        double[] accum = new double[totalPixels * ch];
        double[] counts = new double[totalPixels];

        AccumulateToRefBuffer(accum, counts, initialBest, 0, 0, w, h, ch);

        string? lastSerPath = null;
        SerFile? cachedSer = null;

        try
        {
            for (int i = 1; i < countToAverage; i++)
            {
                var rec = sortedRecords[i];
                string file = rec.FilePath;
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;

                string ext = Path.GetExtension(file).ToLowerInvariant();
                AstroFrame work;
                if (ext == ".ser")
                {
                    if (cachedSer is null || lastSerPath != file)
                    {
                        cachedSer?.Dispose();
                        cachedSer = new SerFile(file);
                        lastSerPath = file;
                    }
                    work = ReadAndPrepareSerFrameForStacking(cachedSer, rec, options, masterDark, masterFlat, totalFrames > 0 ? totalFrames : cachedSer.Header.FrameCount);
                }
                else
                {
                    work = ReadAndPrepareNonSerFrameForStacking(rec, options, masterDark, masterFlat, totalFrames);
                }

                if (work.Width != w || work.Height != h) continue;

                var (dx, dy) = FastCrossCorrelation.ComputeShift(work, initialBest);
                AccumulateToRefBuffer(accum, counts, work, (int)Math.Round(dx), (int)Math.Round(dy), w, h, ch);
            }
        }
        finally
        {
            cachedSer?.Dispose();
        }

        ushort[] outData = new ushort[accum.Length];
        for (int p = 0; p < totalPixels; p++)
        {
            double c = counts[p];
            if (c > 0)
            {
                for (int cIdx = 0; cIdx < ch; cIdx++)
                {
                    outData[p * ch + cIdx] = (ushort)Math.Clamp(Math.Round(accum[p * ch + cIdx] / c), 0, 65535);
                }
            }
            else
            {
                for (int cIdx = 0; cIdx < ch; cIdx++)
                {
                    outData[p * ch + cIdx] = initialBest.Data[p * ch + cIdx];
                }
            }
        }

        var refFrame = new AstroFrame(w, h, ch, originalBitDepth: 16);
        Array.Copy(outData, refFrame.Data, outData.Length);
        return ConvolutionSharpeningFilter.ApplySharpening(refFrame, blendPercent: 12);
    }

    private unsafe void AccumulateToRefBuffer(
        double[] accum,
        double[] counts,
        AstroFrame frame,
        int dx,
        int dy,
        int w,
        int h,
        int ch)
    {
        fixed (ushort* src = frame.Data)
        {
            for (int y = 0; y < h; y++)
            {
                int srcY = y - dy;
                if (srcY < 0 || srcY >= h) continue;

                int rowOffset = y * w;
                int srcRowOffset = srcY * w;

                for (int x = 0; x < w; x++)
                {
                    int srcX = x - dx;
                    if (srcX < 0 || srcX >= w) continue;

                    int pDst = rowOffset + x;
                    int pSrc = srcRowOffset + srcX;

                    for (int c = 0; c < ch; c++)
                    {
                        accum[pDst * ch + c] += src[pSrc * ch + c];
                    }
                    counts[pDst] += 1.0;
                }
            }
        }
    }

    private sealed class StackingTier
    {
        public double Percentage { get; init; }
        public int FramesToStack { get; init; }
        public MultiZoneStackingEngine? Stacker { get; set; }
        public string OutputFilePath { get; set; } = string.Empty;
    }
}