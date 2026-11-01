using System.IO;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Adaptateur d'infrastructure chargé de charger les fichiers Dark, Flat et Flat-Dark
/// et de construire les trames maîtresses (Master Dark, Master Flat) pour le pipeline de traitement.
/// </summary>
public static class CalibrationFrameProvider
{
    public static AstroFrame? BuildMasterDark(
        IReadOnlyList<string> darkFilePaths,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validFiles = darkFilePaths.Where(File.Exists).ToList();
        if (validFiles.Count == 0) return null;

        var accumulator = new MasterFrameAccumulator();
        int totalFramesLoaded = 0;

        foreach (var file in validFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(file);
            progress?.Report(new PipelineProgressReport(
                "Calibration",
                5,
                fileName,
                totalFramesLoaded,
                0,
                $"Chargement trames Dark depuis '{fileName}'..."));

            foreach (var frame in UniversalMediaReader.EnumerateFrames(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                accumulator.AddFrame(frame);
                totalFramesLoaded++;
            }
        }

        return accumulator.ProduceMasterFrame();
    }

    public static AstroFrame? BuildMasterFlat(
        IReadOnlyList<string> flatFilePaths,
        AstroFrame? masterFlatDark = null,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validFiles = flatFilePaths.Where(File.Exists).ToList();
        if (validFiles.Count == 0) return null;

        var accumulator = new MasterFrameAccumulator(darkSubtractor: masterFlatDark);
        int totalFramesLoaded = 0;

        foreach (var file in validFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(file);
            progress?.Report(new PipelineProgressReport(
                "Calibration",
                10,
                fileName,
                totalFramesLoaded,
                0,
                $"Chargement trames Flat depuis '{fileName}'..."));

            foreach (var frame in UniversalMediaReader.EnumerateFrames(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                accumulator.AddFrame(frame);
                totalFramesLoaded++;
            }
        }

        return accumulator.ProduceMasterFrame();
    }

    public static (AstroFrame? MasterDark, AstroFrame? MasterFlat) PrepareCalibrationMasters(
        ProcessingOptions options,
        string? outputDirectory = null,
        IProgress<PipelineProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AstroFrame? masterDark = null;
        AstroFrame? masterFlat = null;

        // 1. Master Dark
        if (options.DarkFrameSubtract && options.DarkFiles.Count > 0)
        {
            progress?.Report(new PipelineProgressReport("Calibration", 2, string.Empty, 0, 0, "Calcul du Master Dark..."));
            masterDark = BuildMasterDark(options.DarkFiles, progress, cancellationToken);
            if (options.SaveMasterDark && !string.IsNullOrEmpty(outputDirectory) && masterDark != null)
            {
                string path = Path.Combine(outputDirectory, "master_dark.tif");
                UniversalMediaReader.WriteSingleImageFile(path, masterDark, is16Bit: true);
            }
        }

        // 2. Master Flat-Dark & Master Flat
        if (options.FlatFrameCalibrate && options.FlatFiles.Count > 0)
        {
            AstroFrame? masterFlatDark = null;
            if (options.FlatDarkFrameSubtract && options.FlatDarkFiles.Count > 0)
            {
                progress?.Report(new PipelineProgressReport("Calibration", 4, string.Empty, 0, 0, "Calcul du Master Flat-Dark..."));
                masterFlatDark = BuildMasterDark(options.FlatDarkFiles, progress, cancellationToken);
                if (options.SaveMasterFlatDark && !string.IsNullOrEmpty(outputDirectory) && masterFlatDark != null)
                {
                    string path = Path.Combine(outputDirectory, "master_flat_dark.tif");
                    UniversalMediaReader.WriteSingleImageFile(path, masterFlatDark, is16Bit: true);
                }
            }

            progress?.Report(new PipelineProgressReport("Calibration", 8, string.Empty, 0, 0, "Calcul du Master Flat..."));
            masterFlat = BuildMasterFlat(options.FlatFiles, masterFlatDark, progress, cancellationToken);
            if (options.SaveMasterFlat && !string.IsNullOrEmpty(outputDirectory) && masterFlat != null)
            {
                string path = Path.Combine(outputDirectory, "master_flat.tif");
                UniversalMediaReader.WriteSingleImageFile(path, masterFlat, is16Bit: true);
            }
        }

        return (masterDark, masterFlat);
    }
}

