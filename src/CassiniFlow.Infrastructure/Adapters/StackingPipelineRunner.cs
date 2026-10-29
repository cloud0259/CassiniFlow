using System.Diagnostics;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Codecs;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Exécuteur haute performance du pipeline d'empilement multi-zones et du flux direct unifié.
/// - Streaming 2 passes à mémoire constante (< 150 Mo).
/// - Démattriçage automatique des trames Bayer brutes pour éliminer tout motif de trame/grille.
/// - Accès direct aux poses sélectionnées (seek par index pour les fichiers SER).
/// - Corrélation multi-zones parallélisée sur l'ensemble des cœurs processeur.
/// - Calcul précis et proportionnel du pourcentage d'avancement pour chaque étape.
/// </summary>
public static class StackingPipelineRunner
{
    private readonly record struct FrameScoreRecord(int FrameIndex, double QualityScore, int CentroidX, int CentroidY, string FilePath = "");

    public static async Task<PipelineRunSummary> ExecuteAsync(
        IReadOnlyList<string> inputFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(outputDirectory);

            var validFiles = inputFiles.Where(File.Exists).ToList();
            if (validFiles.Count == 0)
            {
                return new PipelineRunSummary(false, TimeSpan.Zero, 0, 0, 0, 0, 0, string.Empty);
            }

            if (options.RunMode == ProcessingRunMode.JoinMode && validFiles.Count > 1)
            {
                return ExecuteJoinModeInternal(validFiles, options, outputDirectory, sw, progress, cancellationToken);
            }

            return ExecuteBatchModeInternal(validFiles, options, outputDirectory, sw, progress, cancellationToken);
        }, cancellationToken);
    }

    private static PipelineRunSummary ExecuteBatchModeInternal(
        List<string> validFiles,
        ProcessingOptions options,
        string outputDirectory,
        Stopwatch sw,
        IProgress<PipelineProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        int totalInputFrames = 0;
        int discardedNoPlanet = 0;
        int discardedQuality = 0;
        string lastOutputPath = string.Empty;
        int outputFramesWritten = 0;
        var allGeneratedFiles = new List<string>();

        foreach (var file in validFiles)
        {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(file);
                string baseStem = Path.GetFileNameWithoutExtension(file);

                var summary = UniversalMediaReader.InspectFile(file);
                int expectedTotalFrames = Math.Max(1, summary.FrameCount);

                string ext = Path.GetExtension(file).ToLowerInvariant();

                progress?.Report(new PipelineProgressReport(
                    "Analyse de Qualité",
                    1,
                    fileName,
                    0,
                    expectedTotalFrames,
                    $"Évaluation de la netteté et sélection de la trame de référence ({expectedTotalFrames} frames)..."));

                // =========================================================================
                // PASSE 1 : Analyse de qualité seeing & Sélection de la trame de référence
                // Zéro mise en cache en RAM : seule la meilleure trame est conservée en mémoire.
                // Accélération multi-cœurs CPU sur fichiers SER.
                // =========================================================================
                List<FrameScoreRecord> scoreRecords;
                AstroFrame? bestRefFrame = null;

                if (ext == ".ser")
                {
                    using var ser = new SerFile(file);
                    int totalFrames = ser.Header.FrameCount;
                    var records = new FrameScoreRecord?[totalFrames];
                    int processedCount = 0;
                    int bestIdx = 0;
                    double bestScore = double.MinValue;
                    object lockObj = new();

                    Parallel.For(0, totalFrames, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = cancellationToken
                    }, i =>
                    {
                        var raw = ser.ReadFrame(i, options.SerBitDepthOverride);
                        var work = PrepareFrame(raw, options, out int cx, out int cy, out bool accepted);
                        if (!accepted)
                        {
                            Interlocked.Increment(ref discardedNoPlanet);
                            return;
                        }

                        double score = StabilisationAndQualityEngine.ComputeQualityScore(
                            work,
                            options.QualityAlgorithm,
                            noiseRobust: options.Stacking.NoiseRobust);
                        records[i] = new FrameScoreRecord(i, score, cx, cy);

                        lock (lockObj)
                        {
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestIdx = i;
                            }
                        }

                        int done = Interlocked.Increment(ref processedCount);
                        if (done % 50 == 0 || done == totalFrames)
                        {
                            int pct = Math.Clamp((int)Math.Round((double)done / totalFrames * 45.0), 1, 45);
                            progress?.Report(new PipelineProgressReport(
                                "Analyse de Qualité",
                                pct,
                                fileName,
                                done,
                                totalFrames,
                                $"Analyse de qualité {done}/{totalFrames} ({pct}%)..."));
                        }
                    });

                    totalInputFrames += totalFrames;
                    scoreRecords = records.Where(r => r.HasValue).Select(r => r!.Value).ToList();

                    if (scoreRecords.Count > 0)
                    {
                        var rawBest = ser.ReadFrame(bestIdx, options.SerBitDepthOverride);
                        bestRefFrame = PrepareFrame(rawBest, options, out _, out _, out _);
                    }
                }
                else
                {
                    scoreRecords = new List<FrameScoreRecord>(expectedTotalFrames);
                    double bestQuality = double.MinValue;
                    int pass1Processed = 0;

                    foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        totalInputFrames++;
                        pass1Processed++;

                        var workFrame = PrepareFrame(raw, options, out int cx, out int cy, out bool accepted);
                        if (!accepted)
                        {
                            discardedNoPlanet++;
                            continue;
                        }

                        double score = StabilisationAndQualityEngine.ComputeQualityScore(
                            workFrame,
                            options.QualityAlgorithm,
                            noiseRobust: options.Stacking.NoiseRobust);

                        scoreRecords.Add(new FrameScoreRecord(raw.FrameIndex, score, cx, cy));

                        if (score > bestQuality || bestRefFrame is null)
                        {
                            bestQuality = score;
                            bestRefFrame = workFrame.Clone();
                        }

                        if (pass1Processed % 10 == 0 || pass1Processed == expectedTotalFrames)
                        {
                            int pct = Math.Clamp((int)Math.Round((double)pass1Processed / expectedTotalFrames * 45.0), 1, 45);
                            progress?.Report(new PipelineProgressReport(
                                "Analyse de Qualité",
                                pct,
                                fileName,
                                pass1Processed,
                                expectedTotalFrames,
                                $"Analyse de qualité {pass1Processed}/{expectedTotalFrames} ({pct}%)..."));
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
                    if (ext == ".ser")
                    {
                        using var ser = new SerFile(file);
                        var rawManual = ser.ReadFrame(manualRecord.FrameIndex, options.SerBitDepthOverride);
                        bestRefFrame = PrepareFrame(rawManual, options, out _, out _, out _);
                    }
                }

                // Génération de la trame de référence Double Stack (AutoStakkert Double Stack Reference)
                if (options.Stacking.DoubleStackReference && scoreRecords.Count >= 3)
                {
                    progress?.Report(new PipelineProgressReport(
                        "Trame de Référence",
                        46,
                        fileName,
                        0,
                        scoreRecords.Count,
                        "Synthèse de la trame de référence Double Stack (élimination du bruit de grenaille)..."));

                    bestRefFrame = CreateDoubleStackReference(scoreRecords, bestRefFrame, options);
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

                progress?.Report(new PipelineProgressReport(
                    "Points d'Alignement",
                    47,
                    fileName,
                    0,
                    scoreRecords.Count,
                    "Calcul de la grille adaptative de points d'alignement (AP)..."));

                // Génération de la grille AP sur la meilleure trame de référence (partagée par tous les paliers)
                var grid = AlignmentGridGenerator.GenerateGrid(bestRefFrame, options.Stacking);

                // Initialisation des stackers pour chaque palier
                var tiers = new List<StackingTier>();
                foreach (var pct in activePercentages)
                {
                    int count = Math.Max(1, (int)Math.Round(scoreRecords.Count * (pct / 100.0)));
                    count = Math.Min(count, quotaMax);
                    count = Math.Clamp(count, 1, scoreRecords.Count);

                    var stacker = new MultiZoneStackingEngine(
                        bestRefFrame.Width,
                        bestRefFrame.Height,
                        bestRefFrame.Channels,
                        options.Stacking,
                        grid);

                    tiers.Add(new StackingTier
                    {
                        Percentage = pct,
                        FramesToStack = count,
                        Stacker = stacker
                    });
                }

                int maxFramesToStack = tiers.Max(t => t.FramesToStack);
                discardedQuality += (scoreRecords.Count - maxFramesToStack);

                var wantedList = scoreRecords.Take(maxFramesToStack).ToList();
                var wantedRankDict = new Dictionary<int, int>(maxFramesToStack);
                for (int i = 0; i < wantedList.Count; i++)
                {
                    wantedRankDict[wantedList[i].FrameIndex] = i;
                }

                string tiersDesc = string.Join(", ", tiers.Select(t => $"{t.FramesToStack} trames ({t.Percentage:F0}%)"));
                progress?.Report(new PipelineProgressReport(
                    "Empilement Multi-Zones",
                    50,
                    fileName,
                    0,
                    maxFramesToStack,
                    $"Grille AP : {grid.ActiveCount} points actifs. Empilement : {tiersDesc}..."));

                // =========================================================================
                // PASSE 2 : Streaming et accumulation uniquement des trames retenues
                // Optimisation seek direct pour les SER : évite de décoder les trames rejetées.
                // =========================================================================
                int stackedSoFar = 0;

                if (ext == ".ser")
                {
                    using var ser = new SerFile(file);
                    for (int i = 0; i < wantedList.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var record = wantedList[i];
                        var raw = ser.ReadFrame(record.FrameIndex, options.SerBitDepthOverride);
                        AccumulateFrameIntoTiers(raw, options, record, bestRefFrame, grid, tiers, i);
                        stackedSoFar++;

                        if (stackedSoFar % 5 == 0 || stackedSoFar == maxFramesToStack)
                        {
                            int pct = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / maxFramesToStack * 46.0), 0, 46);
                            progress?.Report(new PipelineProgressReport(
                                "Empilement Multi-Zones",
                                pct,
                                fileName,
                                stackedSoFar,
                                maxFramesToStack,
                                $"Empilement {stackedSoFar}/{maxFramesToStack} trames ({pct}%)..."));
                        }
                    }
                }
                else
                {
                    foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!wantedRankDict.TryGetValue(raw.FrameIndex, out int rank))
                        {
                            continue;
                        }

                        var record = wantedList[rank];
                        AccumulateFrameIntoTiers(raw, options, record, bestRefFrame, grid, tiers, rank);
                        stackedSoFar++;

                        if (stackedSoFar % 5 == 0 || stackedSoFar == maxFramesToStack)
                        {
                            int pct = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / maxFramesToStack * 46.0), 0, 46);
                            progress?.Report(new PipelineProgressReport(
                                "Empilement Multi-Zones",
                                pct,
                                fileName,
                                stackedSoFar,
                                maxFramesToStack,
                                $"Empilement {stackedSoFar}/{maxFramesToStack} trames ({pct}%)..."));
                        }

                        if (stackedSoFar >= maxFramesToStack) break;
                    }
                }

                progress?.Report(new PipelineProgressReport(
                    "Finalisation",
                    97,
                    fileName,
                    maxFramesToStack,
                    maxFramesToStack,
                    "Normalisation des niveaux, alignement chromatique RVB et accentuation..."));

                // Format d'image de sortie (TIFF 16-bit par défaut, FITS ou PNG)
                string outExt = options.OutputFormat switch
                {
                    OutputFormat.Fits => ".fit",
                    OutputFormat.Png => ".png",
                    _ => ".tif"
                };

                foreach (var tier in tiers)
                {
                    var stackedResult = tier.Stacker.ProduceStackedFrame();
                    string outFileName = $"{baseStem}_stacked_p{(int)tier.Percentage}_{grid.ActiveCount}ap{outExt}";
                    string outFilePath = Path.Combine(outputDirectory, outFileName);

                    UniversalMediaReader.WriteSingleImageFile(outFilePath, stackedResult, is16Bit: true);
                    tier.OutputFilePath = outFilePath;
                    lastOutputPath = outFilePath;
                    outputFramesWritten++;
                    allGeneratedFiles.Add(outFilePath);
                }
            }

            sw.Stop();
            string generatedFilesText = string.Join(", ", allGeneratedFiles.Select(Path.GetFileName));
            progress?.Report(new PipelineProgressReport(
                "Terminé",
                100,
                string.Empty,
                totalInputFrames,
                totalInputFrames,
                $"Empilement terminé avec succès en {sw.Elapsed.TotalSeconds:F2}s. {outputFramesWritten} image(s) générée(s) : {generatedFilesText}"));

            return new PipelineRunSummary(
                Success: File.Exists(lastOutputPath),
                Elapsed: sw.Elapsed,
                TotalInputFrames: totalInputFrames,
                OutputFramesWritten: outputFramesWritten,
                DiscardedNoPlanet: discardedNoPlanet,
                DiscardedOverexposed: 0,
                DiscardedQuality: discardedQuality,
                OutputFilePath: lastOutputPath);
    }

    private static PipelineRunSummary ExecuteJoinModeInternal(
        List<string> validFiles,
        ProcessingOptions options,
        string outputDirectory,
        Stopwatch sw,
        IProgress<PipelineProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        int totalInputFrames = 0;
        int discardedNoPlanet = 0;
        int discardedQuality = 0;
        string lastOutputPath = string.Empty;
        int outputFramesWritten = 0;
        var allGeneratedFiles = new List<string>();

        int totalExpectedFrames = validFiles.Sum(f => Math.Max(1, UniversalMediaReader.InspectFile(f).FrameCount));
        int globalPass1Count = 0;

        var allScoreRecords = new List<FrameScoreRecord>();
        AstroFrame? bestRefFrame = null;
        double bestQuality = double.MinValue;
        object lockObj = new();

        // =========================================================================
        // PASSE 1 (FUSION) : Évaluation seeing et recherche de la meilleure trame
        // de référence globale sur l'ENSEMBLE des fichiers sources.
        // =========================================================================
        for (int fileIdx = 0; fileIdx < validFiles.Count; fileIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = validFiles[fileIdx];
            string fileName = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();

            progress?.Report(new PipelineProgressReport(
                "Analyse de Qualité (Fusion)",
                1,
                fileName,
                globalPass1Count,
                totalExpectedFrames,
                $"Évaluation globale [{fileIdx + 1}/{validFiles.Count}] : {fileName}..."));

            if (ext == ".ser")
            {
                using var ser = new SerFile(file);
                int totalFrames = ser.Header.FrameCount;
                var records = new FrameScoreRecord?[totalFrames];
                int fileBestIdx = -1;
                double fileBestScore = double.MinValue;

                Parallel.For(0, totalFrames, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = cancellationToken
                }, i =>
                {
                    var raw = ser.ReadFrame(i, options.SerBitDepthOverride);
                    var work = PrepareFrame(raw, options, out int cx, out int cy, out bool accepted);
                    if (!accepted)
                    {
                        Interlocked.Increment(ref discardedNoPlanet);
                        return;
                    }

                    double score = StabilisationAndQualityEngine.ComputeQualityScore(
                        work,
                        options.QualityAlgorithm,
                        noiseRobust: options.Stacking.NoiseRobust);
                    records[i] = new FrameScoreRecord(i, score, cx, cy, file);

                    lock (lockObj)
                    {
                        if (score > fileBestScore)
                        {
                            fileBestScore = score;
                            fileBestIdx = i;
                        }
                    }

                    int done = Interlocked.Increment(ref globalPass1Count);
                    if (done % 50 == 0 || done == totalExpectedFrames)
                    {
                        int pct = Math.Clamp((int)Math.Round((double)done / totalExpectedFrames * 45.0), 1, 45);
                        progress?.Report(new PipelineProgressReport(
                            "Analyse de Qualité (Fusion)",
                            pct,
                            fileName,
                            done,
                            totalExpectedFrames,
                            $"Analyse globale {done}/{totalExpectedFrames} ({pct}%)..."));
                    }
                });

                totalInputFrames += totalFrames;
                allScoreRecords.AddRange(records.Where(r => r.HasValue).Select(r => r!.Value));

                if (fileBestIdx >= 0 && fileBestScore > bestQuality)
                {
                    lock (lockObj)
                    {
                        if (fileBestScore > bestQuality)
                        {
                            bestQuality = fileBestScore;
                            var rawBest = ser.ReadFrame(fileBestIdx, options.SerBitDepthOverride);
                            bestRefFrame = PrepareFrame(rawBest, options, out _, out _, out _);
                        }
                    }
                }
            }
            else
            {
                foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalInputFrames++;
                    int done = Interlocked.Increment(ref globalPass1Count);

                    var workFrame = PrepareFrame(raw, options, out int cx, out int cy, out bool accepted);
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
                        progress?.Report(new PipelineProgressReport(
                            "Analyse de Qualité (Fusion)",
                            pct,
                            fileName,
                            done,
                            totalExpectedFrames,
                            $"Analyse globale {done}/{totalExpectedFrames} ({pct}%)..."));
                    }
                }
            }
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
            progress?.Report(new PipelineProgressReport(
                "Trame de Référence (Fusion)",
                46,
                Path.GetFileName(validFiles[0]),
                0,
                allScoreRecords.Count,
                "Synthèse globale de la trame de référence Double Stack..."));

            bestRefFrame = CreateDoubleStackReference(allScoreRecords, bestRefFrame, options);
        }

        var activePercentages = options.Stacking.StackPercentages.Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
        if (activePercentages.Count == 0)
        {
            activePercentages = [25.0];
        }

        int quotaMax = (options.Stacking.StackFrameCounts.Count > 0 && options.Stacking.StackFrameCounts[0] > 0)
            ? options.Stacking.StackFrameCounts[0]
            : int.MaxValue;

        progress?.Report(new PipelineProgressReport(
            "Points d'Alignement (Fusion)",
            47,
            Path.GetFileName(validFiles[0]),
            0,
            allScoreRecords.Count,
            "Calcul de la grille adaptative de points d'alignement (AP)..."));

        var grid = AlignmentGridGenerator.GenerateGrid(bestRefFrame, options.Stacking);

        var tiers = new List<StackingTier>();
        foreach (var pct in activePercentages)
        {
            int count = Math.Max(1, (int)Math.Round(allScoreRecords.Count * (pct / 100.0)));
            count = Math.Min(count, quotaMax);
            count = Math.Clamp(count, 1, allScoreRecords.Count);

            var stacker = new MultiZoneStackingEngine(
                bestRefFrame.Width,
                bestRefFrame.Height,
                bestRefFrame.Channels,
                options.Stacking,
                grid);

            tiers.Add(new StackingTier
            {
                Percentage = pct,
                FramesToStack = count,
                Stacker = stacker
            });
        }

        int maxFramesToStack = tiers.Max(t => t.FramesToStack);
        discardedQuality += (allScoreRecords.Count - maxFramesToStack);

        var wantedList = allScoreRecords.Take(maxFramesToStack).ToList();

        string tiersDesc = string.Join(", ", tiers.Select(t => $"{t.FramesToStack} trames ({t.Percentage:F0}%)"));
        progress?.Report(new PipelineProgressReport(
            "Empilement Multi-Zones (Fusion)",
            50,
            Path.GetFileName(validFiles[0]),
            0,
            maxFramesToStack,
            $"Grille AP : {grid.ActiveCount} points actifs. Empilement fusionné : {tiersDesc}..."));

        // =========================================================================
        // PASSE 2 (FUSION) : Streaming et accumulation par fichier source
        // Chaque fichier n'est ouvert et parcouru qu'une seule fois.
        // =========================================================================
        int stackedSoFar = 0;
        var framesByFile = wantedList
            .Select((rec, globalRank) => (Record: rec, GlobalRank: globalRank))
            .GroupBy(x => x.Record.FilePath);

        foreach (var group in framesByFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = group.Key;
            string fileName = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();

            if (ext == ".ser")
            {
                using var ser = new SerFile(file);
                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var raw = ser.ReadFrame(item.Record.FrameIndex, options.SerBitDepthOverride);
                    AccumulateFrameIntoTiers(raw, options, item.Record, bestRefFrame, grid, tiers, item.GlobalRank);
                    stackedSoFar++;

                    if (stackedSoFar % 5 == 0 || stackedSoFar == maxFramesToStack)
                    {
                        int pct = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / maxFramesToStack * 46.0), 0, 46);
                        progress?.Report(new PipelineProgressReport(
                            "Empilement Multi-Zones (Fusion)",
                            pct,
                            fileName,
                            stackedSoFar,
                            maxFramesToStack,
                            $"Empilement fusionné {stackedSoFar}/{maxFramesToStack} trames ({pct}%)..."));
                    }
                }
            }
            else
            {
                var targetRanks = group.ToDictionary(x => x.Record.FrameIndex, x => x.GlobalRank);
                int neededForThisFile = targetRanks.Count;
                int collectedForThisFile = 0;

                foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!targetRanks.TryGetValue(raw.FrameIndex, out int rank))
                        continue;

                    var record = wantedList[rank];
                    AccumulateFrameIntoTiers(raw, options, record, bestRefFrame, grid, tiers, rank);
                    stackedSoFar++;
                    collectedForThisFile++;

                    if (stackedSoFar % 5 == 0 || stackedSoFar == maxFramesToStack)
                    {
                        int pct = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / maxFramesToStack * 46.0), 0, 46);
                        progress?.Report(new PipelineProgressReport(
                            "Empilement Multi-Zones (Fusion)",
                            pct,
                            fileName,
                            stackedSoFar,
                            maxFramesToStack,
                            $"Empilement fusionné {stackedSoFar}/{maxFramesToStack} trames ({pct}%)..."));
                    }

                    if (collectedForThisFile >= neededForThisFile) break;
                }
            }
        }

        progress?.Report(new PipelineProgressReport(
            "Finalisation (Fusion)",
            97,
            Path.GetFileName(validFiles[0]),
            maxFramesToStack,
            maxFramesToStack,
            "Normalisation des niveaux, alignement chromatique RVB et accentuation..."));

        string outExt = options.OutputFormat switch
        {
            OutputFormat.Fits => ".fit",
            OutputFormat.Png => ".png",
            _ => ".tif"
        };

        string baseStem = Path.GetFileNameWithoutExtension(validFiles[0]);

        foreach (var tier in tiers)
        {
            var stackedResult = tier.Stacker.ProduceStackedFrame();
            string outFileName = $"{baseStem}_joined_stacked_p{(int)tier.Percentage}_{grid.ActiveCount}ap{outExt}";
            string outFilePath = Path.Combine(outputDirectory, outFileName);

            UniversalMediaReader.WriteSingleImageFile(outFilePath, stackedResult, is16Bit: true);
            tier.OutputFilePath = outFilePath;
            lastOutputPath = outFilePath;
            outputFramesWritten++;
            allGeneratedFiles.Add(outFilePath);
        }

        sw.Stop();
        string generatedFilesText = string.Join(", ", allGeneratedFiles.Select(Path.GetFileName));
        progress?.Report(new PipelineProgressReport(
            "Terminé",
            100,
            string.Empty,
            totalInputFrames,
            totalInputFrames,
            $"Empilement fusionné terminé avec succès en {sw.Elapsed.TotalSeconds:F2}s ({validFiles.Count} vidéos combinées). Image(s) générée(s) : {generatedFilesText}"));

        return new PipelineRunSummary(
            Success: File.Exists(lastOutputPath),
            Elapsed: sw.Elapsed,
            TotalInputFrames: totalInputFrames,
            OutputFramesWritten: outputFramesWritten,
            DiscardedNoPlanet: discardedNoPlanet,
            DiscardedOverexposed: 0,
            DiscardedQuality: discardedQuality,
            OutputFilePath: lastOutputPath);
    }

    private static void AccumulateFrameIntoTiers(
        AstroFrame raw,
        ProcessingOptions options,
        FrameScoreRecord record,
        AstroFrame bestRefFrame,
        AlignmentGrid grid,
        List<StackingTier> tiers,
        int rank)
    {
        var workFrame = PrepareFrame(raw, options, out _, out _, out _);

        // 1. Décalage global (désactivable si vidéo pré-stabilisée style AutoStakkert)
        double globalDx = 0.0;
        double globalDy = 0.0;
        if (!options.Stacking.DisableStabilisation)
        {
            var (gdx, gdy) = FastCrossCorrelation.ComputeShift(workFrame, bestRefFrame);
            globalDx = gdx;
            globalDy = gdy;
        }

        // 2. Décalages locaux multi-zones parallélisés sur tous les cœurs CPU
        var activePoints = grid.Points.Where(p => p.IsActive).ToList();
        Parallel.ForEach(activePoints, ap =>
        {
            var (tileDx, tileDy) = FastCrossCorrelation.ComputeTileShift(
                workFrame,
                bestRefFrame,
                ap.X,
                ap.Y,
                ap.Size,
                globalDx,
                globalDy);
            ap.ShiftX = tileDx;
            ap.ShiftY = tileDy;
        });

        int shiftX = (int)Math.Round(globalDx);
        int shiftY = (int)Math.Round(globalDy);

        // 3. Accumulation dans chaque palier dont le quota inclut cette trame
        foreach (var tier in tiers)
        {
            if (rank < tier.FramesToStack)
            {
                tier.Stacker.AccumulateFrame(workFrame, shiftX, shiftY);
            }
        }
    }

    private static AstroFrame PrepareFrame(
        AstroFrame raw,
        ProcessingOptions options,
        out int centroidX,
        out int centroidY,
        out bool accepted)
    {
        var work = raw;
        accepted = true;

        // Démattriçage systématique des trames Bayer pour éliminer tout motif de trame en pointillés
        if (work.Channels == 1 && !options.ProtectBayerPattern &&
            (options.DebayerEnabled || work.IsBayerRaw || DetectBayerPatternFast(work, out _)))
        {
            var pattern = work.IsBayerRaw
                ? work.BayerPattern
                : (DetectBayerPatternFast(work, out var detected) ? detected : options.DebayerPattern);
            work = ImageProcessor.Debayer(work, pattern, options.DebayerType);
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
            work = FieldDerotationEngine.ApplyDerotation(work, angle);
        }

        centroidX = work.Width / 2;
        centroidY = work.Height / 2;

        if (options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne)
        {
            var (prepFrame, det) = PipelineRunner.PreviewSingleFrame(
                work,
                options,
                applyCropToOutput: options.EnableCropping);

            if (options.CheckPlanetObjectPresent && !det.Accepted)
            {
                accepted = false;
                return work;
            }

            work = prepFrame;
            centroidX = det.CentroidX;
            centroidY = det.CentroidY;
        }

        return work;
    }

    private static bool DetectBayerPatternFast(AstroFrame frame, out BayerPattern detectedPattern)
    {
        detectedPattern = BayerPattern.Rggb;
        if (frame.Channels != 1 || frame.Width < 32 || frame.Height < 32)
            return false;

        ushort[] data = frame.Data;
        int w = frame.Width;
        int h = frame.Height;
        long sum00 = 0, sum10 = 0, sum01 = 0, sum11 = 0;
        int count = 0;

        int yStart = h / 4;
        int yEnd = (h * 3) / 4;
        int xStart = w / 4;
        int xEnd = (w * 3) / 4;

        for (int y = yStart; y < yEnd; y += 4)
        {
            int r0 = y * w;
            int r1 = (y + 1) * w;
            for (int x = xStart; x < xEnd; x += 4)
            {
                ushort v00 = data[r0 + x];
                ushort v10 = data[r0 + x + 1];
                ushort v01 = data[r1 + x];
                ushort v11 = data[r1 + x + 1];

                if (v00 < 2000 && v10 < 2000 && v01 < 2000 && v11 < 2000) continue;

                sum00 += v00;
                sum10 += v10;
                sum01 += v01;
                sum11 += v11;
                count++;
            }
        }

        if (count < 100) return false;

        double m00 = (double)sum00 / count;
        double m10 = (double)sum10 / count;
        double m01 = (double)sum01 / count;
        double m11 = (double)sum11 / count;
        double mean = (m00 + m10 + m01 + m11) * 0.25;
        if (mean < 500) return false;

        // In Bayer matrix, diagonally opposed green pixels have nearly identical average responses
        // while red and blue differ significantly
        double diffDiag1 = Math.Abs(m10 - m01) / mean; // Green if RGGB / BGGR
        double diffDiag2 = Math.Abs(m00 - m11) / mean; // Green if GRBG / GBRG

        if (diffDiag1 < 0.08 && diffDiag2 > 0.12)
        {
            detectedPattern = m00 > m11 ? BayerPattern.Rggb : BayerPattern.Bggr;
            return true;
        }

        if (diffDiag2 < 0.08 && diffDiag1 > 0.12)
        {
            detectedPattern = m10 > m01 ? BayerPattern.Grbg : BayerPattern.Gbrg;
            return true;
        }

        return false;
    }

    private static AstroFrame CreateDoubleStackReference(
        IReadOnlyList<FrameScoreRecord> sortedRecords,
        AstroFrame initialBest,
        ProcessingOptions options)
    {
        int countToAverage = Math.Clamp(Math.Min(10, Math.Max(3, sortedRecords.Count / 10)), 3, sortedRecords.Count);
        int w = initialBest.Width;
        int h = initialBest.Height;
        int ch = initialBest.Channels;
        int totalPixels = w * h;

        double[] accum = new double[totalPixels * ch];
        double[] counts = new double[totalPixels];

        AccumulateToRefBuffer(accum, counts, initialBest, 0, 0, w, h, ch);

        for (int i = 1; i < countToAverage; i++)
        {
            var rec = sortedRecords[i];
            string file = rec.FilePath;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;

            string ext = Path.GetExtension(file).ToLowerInvariant();
            AstroFrame raw;
            if (ext == ".ser")
            {
                using var ser = new SerFile(file);
                raw = ser.ReadFrame(rec.FrameIndex, options.SerBitDepthOverride);
            }
            else
            {
                raw = UniversalMediaReader.ReadSingleFrame(file, rec.FrameIndex);
            }

            var work = PrepareFrame(raw, options, out _, out _, out bool ok);
            if (!ok) continue;

            var (dx, dy) = FastCrossCorrelation.ComputeShift(work, initialBest);
            AccumulateToRefBuffer(accum, counts, work, (int)Math.Round(dx), (int)Math.Round(dy), w, h, ch);
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

    private static unsafe void AccumulateToRefBuffer(
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
        public MultiZoneStackingEngine Stacker { get; init; } = null!;
        public string OutputFilePath { get; set; } = string.Empty;
    }
}
