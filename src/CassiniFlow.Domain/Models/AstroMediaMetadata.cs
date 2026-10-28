using System;
using System.Globalization;

namespace CassiniFlow.Domain.Models;

/// <summary>
/// Métadonnées d'acquisition astronomique extraites en profondeur des fichiers
/// (FITS, SER, EXIF image ou fichiers compagnons FireCapture / SharpCap).
/// </summary>
public sealed record AstroMediaMetadata(
    double? ExposureTimeSeconds = null,
    string? ExposureText = null,
    string? ShutterSpeedText = null,
    int? Iso = null,
    double? Gain = null,
    string? Camera = null,
    string? Telescope = null,
    string? Filter = null,
    DateTime? TimestampUtc = null
)
{
    public static readonly AstroMediaMetadata Empty = new();

    public string IsoOrGainText
    {
        get
        {
            if (Iso.HasValue && Gain.HasValue) return $"ISO {Iso.Value} (Gain {Gain.Value.ToString("F0", CultureInfo.InvariantCulture)})";
            if (Iso.HasValue) return $"ISO {Iso.Value}";
            if (Gain.HasValue) return $"Gain {Gain.Value.ToString("F0", CultureInfo.InvariantCulture)}";
            return "—";
        }
    }

    public string EffectiveExposureText => ExposureTimeSeconds.HasValue
        ? FormatExposure(ExposureTimeSeconds.Value)
        : (ExposureText ?? "—");

    public string EffectiveShutterSpeedText => ExposureTimeSeconds.HasValue
        ? FormatShutterSpeed(ExposureTimeSeconds.Value)
        : (ShutterSpeedText ?? "—");

    public static string FormatExposure(double sec)
    {
        if (sec <= 0) return "—";
        if (sec >= 60)
        {
            int m = (int)(sec / 60);
            double s = sec % 60;
            return s > 0 ? $"{m}m {s.ToString("F0", CultureInfo.InvariantCulture)}s" : $"{m}m";
        }
        if (sec >= 1.0) return $"{sec.ToString("F2", CultureInfo.InvariantCulture)} s";
        if (sec >= 0.001) return $"{(sec * 1000.0).ToString("F1", CultureInfo.InvariantCulture)} ms";
        return $"{(sec * 1_000_000.0).ToString("F0", CultureInfo.InvariantCulture)} µs";
    }

    public static string FormatShutterSpeed(double sec)
    {
        if (sec <= 0) return "—";
        if (sec >= 1.0) return $"{sec.ToString("F2", CultureInfo.InvariantCulture)} s";
        double inv = 1.0 / sec;
        return $"1/{Math.Round(inv).ToString("F0", CultureInfo.InvariantCulture)} s";
    }
}
