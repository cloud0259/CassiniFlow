using System.Diagnostics;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Exécuteur du pipeline d'empilement haute résolution multi-zones et du flux direct unifié.
/// Permet l'enchaînement en direct en mémoire sans fichier intermédiaire sur disque.
/// </summary>
public static class StackingPipelineRunner
{
    public static async Task<PipelineRunSummary> ExecuteAsync(
        IReadOnlyList<string> inputFiles,
        ProcessingOptions options,
        string outputDirectory,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
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

            progress?.Report(new PipelineProgressReport(
                "Analyse & Sélection",
                10,
                fileName,
                0,
                0,
                $"Lecture et analyse de la séquence {fileName}..."));

            // Lecture et collecte des trames en mémoire
            var frames = new List<AstroFrame>();
            int frameIdx = 0;
            foreach (var raw in UniversalMediaReader.EnumerateFrames(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                totalInputFrames++;

                // Si Mode Flux Direct Intégral (All-in-One), exécuter le prétraitement à la volée en mémoire
                AstroFrame processed = raw;
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

                    processed = prepFrame;
                }

                // Calcul du score de qualité seeing
                processed.QualityScore = StabilisationAndQualityEngine.ComputeQualityScore(
                    processed,
                    options.QualityAlgorithm);

                frames.Add(processed);
                frameIdx++;

                if (frameIdx % 25 == 0)
                {
                    progress?.Report(new PipelineProgressReport(
                        "Analyse & Sélection",
                        30,
                        fileName,
                        frameIdx,
                        frameIdx,
                        $"Analyse de qualité trame {frameIdx}..."));
                }
            }

            if (frames.Count == 0)
            {
                continue;
            }

            // Tri par qualité décroissante
            frames.Sort((a, b) => b.QualityScore.CompareTo(a.QualityScore));

            // Trame de référence : la meilleure trame
            var refFrame = frames[0];

            progress?.Report(new PipelineProgressReport(
                "Calcul des Points d'Alignement",
                50,
                fileName,
                0,
                frames.Count,
                "Positionnement de la grille de points d'alignement (AP)..."));

            // Génération de la grille de points d'alignement (AP Grid)
            var grid = AlignmentGridGenerator.GenerateGrid(refFrame, options.Stacking);

            progress?.Report(new PipelineProgressReport(
                "Recalage & Empilement",
                60,
                fileName,
                0,
                frames.Count,
                $"Grille prête : {grid.ActiveCount} points d'alignement retenus ({grid.TotalCount} calculés)."));

            // Détermination du quota de trames à empiler
            double pctToKeep = options.Stacking.StackPercentages.Count > 0 ? options.Stacking.StackPercentages[0] : 25.0;
            int framesToStack = Math.Max(1, (int)Math.Round(frames.Count * (pctToKeep / 100.0)));
            if (options.Stacking.StackFrameCounts.Count > 0 && options.Stacking.StackFrameCounts[0] > 0)
            {
                framesToStack = Math.Min(framesToStack, options.Stacking.StackFrameCounts[0]);
            }
            framesToStack = Math.Clamp(framesToStack, 1, frames.Count);

            discardedQuality += (frames.Count - framesToStack);

            // Initialisation du moteur d'empilement multi-zones
            var stacker = new MultiZoneStackingEngine(refFrame.Width, refFrame.Height, refFrame.Channels, options.Stacking, grid);

            for (int i = 0; i < framesToStack; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var curFrame = frames[i];

                // Calcul du décalage global par corrélation de phase
                var (globalDx, globalDy) = FastCrossCorrelation.ComputeShift(curFrame, refFrame);

                // Calcul des décalages locaux pour chaque AP actif
                foreach (var ap in grid.Points.Where(p => p.IsActive))
                {
                    var (tileDx, tileDy) = FastCrossCorrelation.ComputeTileShift(curFrame, refFrame, ap.X, ap.Y, ap.Size);
                    ap.ShiftX = tileDx;
                    ap.ShiftY = tileDy;
                }

                stacker.AccumulateFrame(curFrame, (int)Math.Round(globalDx), (int)Math.Round(globalDy));

                if (i % 10 == 0 || i == framesToStack - 1)
                {
                    int pct = 60 + (int)Math.Round((double)i / Math.Max(1, framesToStack) * 35.0);
                    progress?.Report(new PipelineProgressReport(
                        "Empilement Multi-Zones",
                        pct,
                        fileName,
                        i + 1,
                        framesToStack,
                        $"Accumulation de la pose {i + 1}/{framesToStack} ({pctToKeep}% retenues)..."));
                }
            }

            progress?.Report(new PipelineProgressReport(
                "Restauration & Finalisation",
                95,
                fileName,
                framesToStack,
                framesToStack,
                "Normalisation, alignement RVB et accentuation..."));

            var stackedFrame = stacker.ProduceStackedFrame();

            // Enregistrement de l'image finale
            string outExt = options.OutputFormat == OutputFormat.Fits ? ".fit" : ".tif";
            string outFileName = $"{baseStem}_stacked_p{(int)pctToKeep}_{grid.ActiveCount}ap{outExt}";
            string outFilePath = Path.Combine(outputDirectory, outFileName);

            UniversalMediaReader.WriteSingleImageFile(outFilePath, stackedFrame, is16Bit: true);
            lastOutputPath = outFilePath;
        }

        sw.Stop();
        progress?.Report(new PipelineProgressReport(
            "Terminé",
            100,
            string.Empty,
            totalInputFrames,
            totalInputFrames,
            $"Empilement terminé avec succès. Image générée : {lastOutputPath}"));

        return new PipelineRunSummary(
            Success: File.Exists(lastOutputPath),
            Elapsed: sw.Elapsed,
            TotalInputFrames: totalInputFrames,
            OutputFramesWritten: 1,
            DiscardedNoPlanet: discardedNoPlanet,
            DiscardedOverexposed: 0,
            DiscardedQuality: discardedQuality,
            OutputFilePath: lastOutputPath);
    }
}
