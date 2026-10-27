using System.Globalization;
using System.Text.RegularExpressions;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.WinJupos;

/// <summary>
/// Extracts capture Date/Time from astro camera filenames (FireCapture, SharpCap, IC Capture, WinJUPOS)
/// and generates standardized WinJUPOS filenames (YYYY-MM-DD-HHMM_S).
/// </summary>
public static partial class WinJuposHelper
{
    [GeneratedRegex(@"(\d{4})[-_]?(\d{2})[-_]?(\d{2})[-_T\s]?(\d{2})[-_:]?(\d{2})(?:[-_:]?(\d{2})|[._](\d))")]
    private static partial Regex StandardIsoOrFireCaptureRegex();

    [GeneratedRegex(@"(\d{2})[-_]?(\d{2})[-_]?(\d{2,4})[-_\s]+(\d{2})[-_:]?(\d{2})[-_:]?(\d{2})")]
    private static partial Regex DmyRegex();

    public static bool TryExtractDateTime(string filePath, bool convertLocalToUtc, out DateTime extractedUtc)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        extractedUtc = DateTime.UtcNow;

        var m = StandardIsoOrFireCaptureRegex().Match(name);
        if (m.Success)
        {
            int year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int hour = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            int minute = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            int second = m.Groups[6].Success
                ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture)
                : int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture) * 6; // tenths of a minute

            if (IsValidDate(year, month, day, hour, minute, second, out var dt))
            {
                extractedUtc = convertLocalToUtc ? DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                return true;
            }
        }

        var m2 = DmyRegex().Match(name);
        if (m2.Success)
        {
            int day = int.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture);
            int month = int.Parse(m2.Groups[2].Value, CultureInfo.InvariantCulture);
            int year = int.Parse(m2.Groups[3].Value, CultureInfo.InvariantCulture);
            if (year < 100) year += 2000;
            int hour = int.Parse(m2.Groups[4].Value, CultureInfo.InvariantCulture);
            int minute = int.Parse(m2.Groups[5].Value, CultureInfo.InvariantCulture);
            int second = int.Parse(m2.Groups[6].Value, CultureInfo.InvariantCulture);

            if (IsValidDate(year, month, day, hour, minute, second, out var dt))
            {
                extractedUtc = convertLocalToUtc ? DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats a UTC DateTime into WinJUPOS standard prefix: YYYY-MM-DD-HHMM_S
    /// where S is tenths of a minute (0..9).
    /// </summary>
    public static string FormatWinJuposPrefix(DateTime utcTime)
    {
        var u = utcTime.ToUniversalTime();
        int tenthsOfMinute = (int)Math.Round((u.Second + u.Millisecond / 1000.0) / 6.0);
        if (tenthsOfMinute >= 10)
        {
            u = u.AddMinutes(1);
            tenthsOfMinute = 0;
        }
        return $"{u:yyyy-MM-dd-HHmm}_{tenthsOfMinute}";
    }

    public static DateTime ComputeReferenceTime(
        DateTime startUtc,
        IReadOnlyList<AstroFrame> outputFrames,
        int totalInputFrames,
        decimal fallbackFps,
        WinJuposTimeReference mode)
    {
        if (outputFrames.Count == 0) return startUtc;

        if (mode == WinJuposTimeReference.FirstOutputFrameTime)
        {
            return outputFrames[0].TimestampUtc ?? startUtc.AddSeconds(outputFrames[0].FrameIndex / (double)Math.Max(1m, fallbackFps));
        }

        if (mode == WinJuposTimeReference.EarliestFrameTime)
        {
            var minFrame = outputFrames.MinBy(f => f.FrameIndex)!;
            return minFrame.TimestampUtc ?? startUtc.AddSeconds(minFrame.FrameIndex / (double)Math.Max(1m, fallbackFps));
        }

        // Default: MidPointTime
        if (outputFrames[0].TimestampUtc.HasValue && outputFrames[^1].TimestampUtc.HasValue)
        {
            long minTicks = outputFrames.Min(f => f.TimestampUtc ?? startUtc).Ticks;
            long maxTicks = outputFrames.Max(f => f.TimestampUtc ?? startUtc).Ticks;
            return new DateTime((minTicks + maxTicks) / 2, DateTimeKind.Utc);
        }

        double durationSec = totalInputFrames / (double)Math.Max(1m, fallbackFps);
        return startUtc.AddSeconds(durationSec * 0.5);
    }

    private static bool IsValidDate(int y, int m, int d, int h, int min, int s, out DateTime dt)
    {
        dt = default;
        if (y is < 1990 or > 2100 || m is < 1 or > 12 || d is < 1 or > 31 || h is < 0 or > 23 || min is < 0 or > 59 || s is < 0 or > 59)
            return false;
        try
        {
            dt = new DateTime(y, m, d, h, min, s);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
