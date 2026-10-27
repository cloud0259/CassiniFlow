using System.Globalization;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Infrastructure.Serialization;
using CassiniFlow.Infrastructure.Synthetic;

namespace CassiniFlow.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-help" or "--help" or "-h"))
        {
            PrintHelp();
            return 0;
        }

        var options = new ProcessingOptions();
        var inputFiles = new List<string>();

        foreach (string arg in args)
        {
            if (arg.StartsWith('-'))
            {
                ParseFlag(arg, options);
            }
            else if (arg.EndsWith(".pof", StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
            {
                options = ProfileSerializer.ParseFromPof(await File.ReadAllTextAsync(arg));
            }
            else
            {
                inputFiles.Add(arg);
            }
        }

        Console.WriteLine("CassiniFlow Command Line Processor");

        string outDir = string.IsNullOrWhiteSpace(options.CustomOutputDirectory)
            ? Path.Combine(Environment.CurrentDirectory, "cassiniflow_output")
            : options.CustomOutputDirectory;

        var progress = new Progress<PipelineProgressReport>(r =>
        {
            Console.WriteLine($"[@PROGRESS={r.PercentComplete}%] {r.Stage}: {r.LogMessage}");
        });

        PipelineRunSummary summary;
        if (inputFiles.Count > 0)
        {
            foreach (string file in inputFiles)
            {
                if (!File.Exists(file))
                {
                    Console.Error.WriteLine($"Error: could not open file '{file}'");
                    return 1;
                }
            }
            summary = await PipelineRunner.ExecuteFilesAsync(inputFiles, options, outDir, progress);
        }
        else
        {
            var frames = SyntheticAstroGenerator.GenerateSequence(SyntheticSubject.JupiterRggb, 24);
            summary = await PipelineRunner.ExecuteAsync(frames, options, outDir, progress);
        }

        Console.WriteLine($"Completed in {summary.Elapsed.TotalSeconds:F2}s â€” Output: {summary.OutputFilePath}");
        return summary.Success ? 0 : 1;
    }

    private static void ParseFlag(string arg, ProcessingOptions o)
    {
        int eq = arg.IndexOf('=');
        string flag = (eq > 0 ? arg[..eq] : arg).ToLowerInvariant();
        string val = eq > 0 ? arg[(eq + 1)..] : string.Empty;
        int IntVal(int def = 0) => int.TryParse(val.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : def;

        switch (flag)
        {
            case "-join" or "-j": o.RunMode = ProcessingRunMode.JoinMode; break;
            case "-check" or "-chk": o.CheckPlanetObjectPresent = true; break;
            case "-centre" or "-center" or "-ctr": o.CentreObjectInFrame = true; break;
            case "-saverejected" or "-sr": o.SaveRejectedFrames = true; break;
            case "-minsize" or "-ms": o.MinimumObjectSizePixels = IntVal(25); break;
            case "-minpixel" or "-mp": o.AutoDetectionThreshold = false; o.ManualDetectionThreshold = IntVal(40); break;
            case "-overexposed" or "-oe": o.RejectOverexposedFrames = true; o.MaxOverexposedPixels = IntVal(60); break;
            case "-crop" or "-c": o.EnableCropping = true; o.CropWidth = o.CropHeight = IntVal(448); break;
            case "-cropx" or "-cx": o.EnableCropping = true; o.CropWidth = IntVal(448); break;
            case "-cropy" or "-cy": o.EnableCropping = true; o.CropHeight = IntVal(448); break;
            case "-offsetx" or "-ox": o.CropOffsetX = IntVal(); break;
            case "-offsety" or "-oy": o.CropOffsetY = IntVal(); break;
            case "-debayer" or "-db": o.DebayerEnabled = true; o.DebayerPattern = (BayerPattern)Math.Clamp(IntVal(2), 0, 3); break;
            case "-pb": o.ProtectBayerPattern = true; o.DebayerEnabled = false; break;
            case "-histostretch" or "-hs": o.EnableHistogramStretch = true; o.HistogramStretchWhitePointPercent = IntVal(80); break;
            case "-hsrgb": o.HistogramStretchRgbIndividually = true; break;
            case "-hs_bp": o.HistogramStretchBlackPointZero = true; break;
            case "-quality" or "-q":
                o.EnableQualityEstimation = true;
                if (!string.IsNullOrEmpty(val)) { o.EnableQualityLimiting = true; o.QualityLimitingMode = QualityLimitingType.NumberOfFrames; o.QualityFramesToKeep = IntVal(100); }
                break;
            case "-qpcnt":
                o.EnableQualityEstimation = true;
                o.EnableQualityLimiting = true;
                o.QualityLimitingMode = QualityLimitingType.PercentageOfFrames;
                o.QualityFramesToKeep = IntVal(25);
                break;
            case "-qweight" or "-qw": o.EnableQualityWeighting = true; break;
            case "-qold": o.QualityAlgorithm = QualityAlgorithm.OriginalNinox; break;
            case "-qhisto": o.QualityAlgorithm = QualityAlgorithm.PeakHistogram; break;
            case "-qtotal": o.QualityAlgorithm = QualityAlgorithm.TotalBrightness; break;
            case "-ser": o.OutputFormat = OutputFormat.Ser; break;
            case "-avi": o.OutputFormat = OutputFormat.Avi; break;
            case "-mp4": o.OutputFormat = OutputFormat.Mp4; break;
            case "-fits": o.OutputFormat = OutputFormat.Fits; break;
            case "-bmp": o.OutputFormat = OutputFormat.Bmp; break;
            case "-png": o.OutputFormat = OutputFormat.Png; break;
            case "-tif" or "-tiff": o.OutputFormat = OutputFormat.Tiff; break;
            case "-flip_x" or "-flipx": o.FlipHorizontalX = true; break;
            case "-flip_y" or "-flipy": o.FlipVerticalY = true; break;
            case "-rotate90": o.Rotation = RotationAngle.Rotate90Clockwise; break;
            case "-rotate180": o.Rotation = RotationAngle.Rotate180; break;
            case "-rotate270": o.Rotation = RotationAngle.Rotate90Anticlockwise; break;
            case "-cpu": o.MaxCpuUsagePercent = Math.Clamp(IntVal(70), 15, 95); break;
            case "-outdir" or "-od": o.CustomOutputDirectory = val; break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            New-CassiniFlow v3.0.0 (.NET 10 Cross-Platform Planetary Imaging PreProcessor)
            Usage:
              CassiniFlow [options] [file.pof] [input_file(s)]

            Compatible with all historical CassiniFlow v2.5.9 CLI flags:
              -check | -chk          Enable object/planet detection
              -centre | -ctr         Centre object in each frame
              -crop=N | -cx=W -cy=H  Crop frame around object centre
              -debayer=0..3 | -db=x  Debayer pattern (0:GBRG, 1:BGGR, 2:RGGB, 3:GRBG)
              -q | -q=N | -qpcnt=X   Enable quality sorting and keep top N or X% frames
              -qold | -qhisto        Select Ninox or Peak Histogram quality algorithm
              -en_ss                 Enable surface feature stabilisation (AFB)
              -ser | -avi | -fits    Select output file format
            """);
    }
}
