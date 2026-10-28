using System.Diagnostics;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Exécuteur du pipeline d'empilement haute résolution multi-zones et du flux direct unifié.
/// Utilise une architecture streaming à mémoire bornée (< 150 Mo) en 2 passes sans mise en cache globale des trames :
/// - Passe 1 : Estimation de qualité seeing et sélection de la meilleure trame de référence.
/// - Passe 2 : Recalage sub-pixel multi-zones et accumulation O(1) des meilleures poses sélectionnées.
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

                // =========================================================================
                // PASSE 1 : Analyse de qualité seeing & Sélection de la trame de référence
                // Zéro mise en cache en RAM : seule la meilleure trame est conservée en mémoire.
                // =========================================================================
                progress?.Report(new PipelineProgressReport(
                    "Analyse & Sélection",
                    5,
                    fileName,
                    0,
                    0,
                    $"Analyse de qualité de la séquence {fileName}..."));

                var scoreRecords = new List<FrameScoreRecord>();
                AstroFrame? bestRefFrame = null;
                double bestQuality = double.MinValue;
                int currentFileFrameCount = 0;

                foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalInputFrames++;
                    currentFileFrameCount++;

                    AstroFrame workFrame = raw;
                    int cx = workFrame.Width / 2;
                    int cy = workFrame.Height / 2;

                    // Si Mode Flux Direct Intégral, détecter l'objet et centrer
                    if (options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne)
                    {
                        var (prepFrame, det) = PipelineRunner.PreviewSingleFrame(
                            raw,
                            options,
                            applyCropToOutput: options.EnableCropping);

                        if (options.CheckPlanetObjectPresent && !det.Accepted)
                        {
                            discardedNoPlanet++;
                            continue;
                        }

                        workFrame = prepFrame;
                        cx = det.CentroidX;
                        cy = det.CentroidY;
                    }

                    // Calcul de la qualité seeing
                    double score = StabilisationAndQualityEngine.ComputeQualityScore(
                        workFrame,
                        options.QualityAlgorithm);

                    scoreRecords.Add(new FrameScoreRecord(raw.FrameIndex, score, cx, cy));

                    // Conserver uniquement la meilleure pose comme trame de référence
                    if (score > bestQuality || bestRefFrame is null)
                    {
                        bestQuality = score;
                        bestRefFrame = workFrame.Clone();
                    }

                    if (currentFileFrameCount % 15 == 0)
                    {
                        progress?.Report(new PipelineProgressReport(
                            "Analyse de Qualité",
                            Math.Min(40, 5 + (int)Math.Round((double)currentFileFrameCount / Math.Max(100, currentFileFrameCount) * 35.0)),
                            fileName,
                            currentFileFrameCount,
                            currentFileFrameCount,
                            $"Analyse de qualité trame {currentFileFrameCount}..."));
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

                // Indexation rapide des trames sélectionnées
                var wantedSet = scoreRecords.Take(framesToStack).ToDictionary(r => r.FrameIndex);

                progress?.Report(new PipelineProgressReport(
                    "Points d'Alignement",
                    45,
                    fileName,
                    0,
                    framesToStack,
                    "Calcul et placement de la grille de points d'alignement (AP)..."));

                // Génération de la grille AP sur la meilleure trame de référence
                var grid = AlignmentGridGenerator.GenerateGrid(bestRefFrame, options.Stacking);

                progress?.Report(new PipelineProgressReport(
                    "Recalage & Empilement",
                    50,
                    fileName,
                    0,
                    framesToStack,
                    $"Grille prête : {grid.ActiveCount} points actifs sur {grid.TotalCount}. Empilement des {framesToStack} meilleures trames ({pctToKeep}%)..."));

                // Initialisation du moteur d'empilement multi-zones O(1)
                var stacker = new MultiZoneStackingEngine(
                    bestRefFrame.Width,
                    bestRefFrame.Height,
                    bestRefFrame.Channels,
                    options.Stacking,
                    grid);

                // =========================================================================
                // PASSE 2 : Streaming et accumulation uniquement des trames retenues
                // Mémoire constante : chaque trame est immédiatement libérée après accumulation.
                // =========================================================================
                int stackedSoFar = 0;

                foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!wantedSet.TryGetValue(raw.FrameIndex, out var record))
                    {
                        continue;
                    }

                    AstroFrame workFrame = raw;
                    if (options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne)
                    {
                        var (prepFrame, _) = PipelineRunner.PreviewSingleFrame(
                            raw,
                            options,
                            applyCropToOutput: options.EnableCropping);
                        workFrame = prepFrame;
                    }

                    // Calcul du décalage global par corrélation de phase
                    var (globalDx, globalDy) = FastCrossCorrelation.ComputeShift(workFrame, bestRefFrame);

                    // Calcul des décalages locaux pour chaque AP actif
                    foreach (var ap in grid.Points)
                    {
                        if (!ap.IsActive) continue;
                        var (tileDx, tileDy) = FastCrossCorrelation.ComputeTileShift(workFrame, bestRefFrame, ap.X, ap.Y, ap.Size);
                        ap.ShiftX = tileDx;
                        ap.ShiftY = tileDy;
                    }

                    // Accumulation dans la mémoire 64-bit avec lissage
                    stacker.AccumulateFrame(workFrame, (int)Math.Round(globalDx), (int)Math.Round(globalDy));
                    stackedSoFar++;

                    if (stackedSoFar % 5 == 0 || stackedSoFar == framesToStack)
                    {
                        int pct = 50 + (int)Math.Round((double)stackedSoFar / Math.Max(1, framesToStack) * 45.0);
                        progress?.Report(new PipelineProgressReport(
                            "Empilement Multi-Zones",
                            pct,
                            fileName,
                            stackedSoFar,
                            framesToStack,
                            $"Accumulation trame {stackedSoFar}/{framesToStack} ({pct}%)..."));
                    }

                    if (stackedSoFar >= framesToStack)
                    {
                        break;
                    }
                }

                progress?.Report(new PipelineProgressReport(
                    "Finalisation",
                    96,
                    fileName,
                    framesToStack,
                    framesToStack,
                    "Normalisation, alignement chromatique RVB et accentuation finale..."));

                var stackedResult = stacker.ProduceStackedFrame();

                // Détermination du format d'image de sortie (TIFF 16-bit par défaut, FITS ou PNG)
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
}
