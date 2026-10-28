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
    private readonly record struct FrameScoreRecord(int FrameIndex, double QualityScore, int CentroidX, int CentroidY);

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

            int totalInputFrames = 0;
            int discardedNoPlanet = 0;
            int discardedQuality = 0;
            string lastOutputPath = string.Empty;

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

                        double score = StabilisationAndQualityEngine.ComputeQualityScore(work, options.QualityAlgorithm);
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
                            options.QualityAlgorithm);

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

                // Détermination du quota de trames à empiler
                double pctToKeep = options.Stacking.StackPercentages.Count > 0 ? options.Stacking.StackPercentages[0] : 25.0;
                int framesToStack = Math.Max(1, (int)Math.Round(scoreRecords.Count * (pctToKeep / 100.0)));
                if (options.Stacking.StackFrameCounts.Count > 0 && options.Stacking.StackFrameCounts[0] > 0)
                {
                    framesToStack = Math.Min(framesToStack, options.Stacking.StackFrameCounts[0]);
                }
                framesToStack = Math.Clamp(framesToStack, 1, scoreRecords.Count);
                discardedQuality += (scoreRecords.Count - framesToStack);

                var wantedList = scoreRecords.Take(framesToStack).ToList();
                var wantedSet = wantedList.ToDictionary(r => r.FrameIndex);

                progress?.Report(new PipelineProgressReport(
                    "Points d'Alignement",
                    47,
                    fileName,
                    0,
                    framesToStack,
                    "Calcul de la grille adaptative de points d'alignement (AP)..."));

                // Génération de la grille AP sur la meilleure trame de référence
                var grid = AlignmentGridGenerator.GenerateGrid(bestRefFrame, options.Stacking);

                progress?.Report(new PipelineProgressReport(
                    "Empilement Multi-Zones",
                    50,
                    fileName,
                    0,
                    framesToStack,
                    $"Grille AP : {grid.ActiveCount} points actifs. Empilement des {framesToStack} meilleures poses ({pctToKeep:F0}%)..."));

                // Initialisation du moteur d'empilement multi-zones O(1)
                var stacker = new MultiZoneStackingEngine(
                    bestRefFrame.Width,
                    bestRefFrame.Height,
                    bestRefFrame.Channels,
                    options.Stacking,
                    grid);

                // =========================================================================
                // PASSE 2 : Streaming et accumulation uniquement des trames retenues
                // Optimisation seek direct pour les SER : évite de décoder les trames rejetées.
                // =========================================================================
                int stackedSoFar = 0;

                if (ext == ".ser")
                {
                    using var ser = new SerFile(file);
                    // Parcours direct uniquement des index retenus
                    foreach (var record in wantedList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var raw = ser.ReadFrame(record.FrameIndex, options.SerBitDepthOverride);
                        AccumulateSingleFrame(raw, options, record, bestRefFrame, grid, stacker);
                        stackedSoFar++;

                        if (stackedSoFar % 5 == 0 || stackedSoFar == framesToStack)
                        {
                            int pct = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / framesToStack * 46.0), 0, 46);
                            progress?.Report(new PipelineProgressReport(
                                "Empilement Multi-Zones",
                                pct,
                                fileName,
                                stackedSoFar,
                                framesToStack,
                                $"Empilement {stackedSoFar}/{framesToStack} trames ({pct}%)..."));
                        }
                    }
                }
                else
                {
                    foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!wantedSet.TryGetValue(raw.FrameIndex, out var record))
                        {
                            continue;
                        }

                        AccumulateSingleFrame(raw, options, record, bestRefFrame, grid, stacker);
                        stackedSoFar++;

                        if (stackedSoFar % 5 == 0 || stackedSoFar == framesToStack)
                        {
                            int pct = 50 + Math.Clamp((int)Math.Round((double)stackedSoFar / framesToStack * 46.0), 0, 46);
                            progress?.Report(new PipelineProgressReport(
                                "Empilement Multi-Zones",
                                pct,
                                fileName,
                                stackedSoFar,
                                framesToStack,
                                $"Empilement {stackedSoFar}/{framesToStack} trames ({pct}%)..."));
                        }

                        if (stackedSoFar >= framesToStack) break;
                    }
                }

                progress?.Report(new PipelineProgressReport(
                    "Finalisation",
                    97,
                    fileName,
                    framesToStack,
                    framesToStack,
                    "Normalisation des niveaux, alignement chromatique RVB et accentuation..."));

                var stackedResult = stacker.ProduceStackedFrame();

                // Format d'image de sortie (TIFF 16-bit par défaut, FITS ou PNG)
                string outExt = options.OutputFormat switch
                {
                    OutputFormat.Fits => ".fit",
                    OutputFormat.Png => ".png",
                    _ => ".tif"
                };

                string outFileName = $"{baseStem}_stacked_p{(int)pctToKeep}_{grid.ActiveCount}ap{outExt}";
                string outFilePath = Path.Combine(outputDirectory, outFileName);

                UniversalMediaReader.WriteSingleImageFile(outFilePath, stackedResult, is16Bit: true);
                lastOutputPath = outFilePath;
            }

            sw.Stop();
            progress?.Report(new PipelineProgressReport(
                "Terminé",
                100,
                string.Empty,
                totalInputFrames,
                totalInputFrames,
                $"Empilement terminé avec succès en {sw.Elapsed.TotalSeconds:F2}s. Image générée : {lastOutputPath}"));

            return new PipelineRunSummary(
                Success: File.Exists(lastOutputPath),
                Elapsed: sw.Elapsed,
                TotalInputFrames: totalInputFrames,
                OutputFramesWritten: 1,
                DiscardedNoPlanet: discardedNoPlanet,
                DiscardedOverexposed: 0,
                DiscardedQuality: discardedQuality,
                OutputFilePath: lastOutputPath);
        }, cancellationToken);
    }

    private static void AccumulateSingleFrame(
        AstroFrame raw,
        ProcessingOptions options,
        FrameScoreRecord record,
        AstroFrame bestRefFrame,
        AlignmentGrid grid,
        MultiZoneStackingEngine stacker)
    {
        var workFrame = PrepareFrame(raw, options, out _, out _, out _);

        // 1. Décalage global
        var (globalDx, globalDy) = FastCrossCorrelation.ComputeShift(workFrame, bestRefFrame);

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

        // 3. Accumulation avec table de correspondances précalculée O(1)
        stacker.AccumulateFrame(workFrame, (int)Math.Round(globalDx), (int)Math.Round(globalDy));
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
}
