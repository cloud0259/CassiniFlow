using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Synthetic;

public enum SyntheticSubject
{
    JupiterRggb,
    LunarClaviusMono,
    SaturnRingsRgb
}

/// <summary>
/// Generates realistic 16-bit synthetic planetary and lunar frames (with simulated atmospheric seeing & drift)
/// for instant real-time testing in the Frame Viewer and automated unit tests.
/// </summary>
public static class SyntheticAstroGenerator
{
    public static List<AstroFrame> GenerateSequence(SyntheticSubject subject, int frameCount = 24, int width = 480, int height = 360)
    {
        var list = new List<AstroFrame>(frameCount);
        var startTime = new DateTime(2026, 10, 24, 21, 45, 18, DateTimeKind.Utc);

        for (int i = 0; i < frameCount; i++)
        {
            double driftX = Math.Sin(i * 0.45) * 8.0;
            double driftY = Math.Cos(i * 0.35) * 6.0;
            double seeingSharpness = 0.55 + 0.45 * Math.Abs(Math.Cos(i * 0.25));

            var frame = subject switch
            {
                SyntheticSubject.LunarClaviusMono => CreateLunarSurfaceFrame(i, width, height, driftX, driftY, seeingSharpness),
                SyntheticSubject.SaturnRingsRgb => CreateSaturnFrame(i, width, height, driftX, driftY, seeingSharpness),
                _ => CreateJupiterFrame(i, width, height, driftX, driftY, seeingSharpness)
            };

            frame.TimestampUtc = startTime.AddMilliseconds(i * 12);
            list.Add(frame);
        }

        return list;
    }

    private static AstroFrame CreateJupiterFrame(int index, int w, int h, double dx, double dy, double sharpness)
    {
        var frame = new AstroFrame(w, h, 3, 16) { FrameIndex = index };
        double cx = w * 0.5 + dx;
        double cy = h * 0.5 + dy;
        double radius = Math.Min(w, h) * 0.23;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double rx = x - cx;
                double ry = (y - cy) * 1.07; // Oblate spheroid
                double dist = Math.Sqrt(rx * rx + ry * ry);
                int idx = (y * w + x) * 3;

                if (dist <= radius)
                {
                    double norm = dist / radius;
                    double limbDarkening = Math.Pow(1.0 - norm * norm * 0.72, 0.45);
                    double lat = ry / radius;

                    // Equatorial belts (SEB & NEB)
                    double beltMod = 1.0 - 0.28 * sharpness * (Math.Exp(-Math.Pow((lat - 0.22) * 8.5, 2)) + Math.Exp(-Math.Pow((lat + 0.20) * 8.5, 2)));
                    // Fine festoons modulated by sharpness
                    double detail = 1.0 + 0.08 * sharpness * Math.Sin(rx * 0.25) * Math.Cos(ry * 0.35);

                    // Great Red Spot
                    double grsDist = Math.Sqrt(Math.Pow((rx - radius * 0.28) / 14.0, 2) + Math.Pow((ry - radius * 0.22) / 9.0, 2));
                    double grsBoostR = grsDist < 1.0 ? 0.25 * (1.0 - grsDist) * sharpness : 0.0;
                    double grsDropGB = grsDist < 1.0 ? 0.22 * (1.0 - grsDist) * sharpness : 0.0;

                    double r = Math.Clamp((0.88 * beltMod * detail + grsBoostR) * limbDarkening, 0.0, 1.0);
                    double g = Math.Clamp((0.74 * beltMod * detail - grsDropGB) * limbDarkening, 0.0, 1.0);
                    double b = Math.Clamp((0.56 * beltMod * detail - grsDropGB) * limbDarkening, 0.0, 1.0);

                    frame.Data[idx] = (ushort)(r * 58000);
                    frame.Data[idx + 1] = (ushort)(g * 54000);
                    frame.Data[idx + 2] = (ushort)(b * 46000);
                }
                else
                {
                    // Galilean Moon Io on the right
                    double ioDist = Math.Sqrt(Math.Pow(x - (cx + radius * 1.65), 2) + Math.Pow(y - (cy - radius * 0.35), 2));
                    if (ioDist <= 4.2)
                    {
                        ushort v = (ushort)(48000 * Math.Max(0.2, 1.0 - ioDist / 4.5));
                        frame.Data[idx] = v;
                        frame.Data[idx + 1] = (ushort)(v * 0.9);
                        frame.Data[idx + 2] = (ushort)(v * 0.65);
                    }
                    else
                    {
                        ushort bg = 512;
                        frame.Data[idx] = bg;
                        frame.Data[idx + 1] = bg;
                        frame.Data[idx + 2] = bg;
                    }
                }
            }
        }

        return frame;
    }

    private static AstroFrame CreateLunarSurfaceFrame(int index, int w, int h, double dx, double dy, double sharpness)
    {
        var frame = new AstroFrame(w, h, 1, 16) { FrameIndex = index };
        (double x, double y, double r)[] craters =
        [
            (w * 0.50 + dx, h * 0.50 + dy, 54.0),
            (w * 0.45 + dx, h * 0.54 + dy, 16.0),
            (w * 0.55 + dx, h * 0.46 + dy, 12.0),
            (w * 0.28 + dx, h * 0.32 + dy, 32.0),
            (w * 0.72 + dx, h * 0.68 + dy, 38.0)
        ];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double baseLum = 0.42 + 0.05 * sharpness * Math.Sin((x - dx) * 0.18) * Math.Cos((y - dy) * 0.18);
                foreach (var (cx, cy, cr) in craters)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (Math.Abs(d - cr) < 4.5)
                    {
                        // Bright sunlit crater rim on right, shadow on left
                        double rimSign = (x - cx) / Math.Max(1.0, cr);
                        baseLum += 0.28 * sharpness * rimSign * (1.0 - Math.Abs(d - cr) / 4.5);
                    }
                    else if (d < cr)
                    {
                        baseLum -= 0.10;
                    }
                }
                frame.Data[y * w + x] = (ushort)(Math.Clamp(baseLum, 0.05, 0.96) * 65535);
            }
        }

        return frame;
    }

    private static AstroFrame CreateSaturnFrame(int index, int w, int h, double dx, double dy, double sharpness)
    {
        var frame = new AstroFrame(w, h, 3, 16) { FrameIndex = index };
        double cx = w * 0.5 + dx;
        double cy = h * 0.5 + dy;
        double cosA = Math.Cos(0.28), sinA = Math.Sin(0.28);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double tx = x - cx;
                double ty = y - cy;
                double rx = tx * cosA - ty * sinA;
                double ry = tx * sinA + ty * cosA;
                int idx = (y * w + x) * 3;

                double globeDist = Math.Sqrt(rx * rx + (ry * 1.12) * (ry * 1.12));
                double ringEll = Math.Sqrt((rx / 96.0) * (rx / 96.0) + (ry / 28.0) * (ry / 28.0));

                if (globeDist <= 34.0)
                {
                    double limb = Math.Sqrt(1.0 - (globeDist / 36.0) * (globeDist / 36.0));
                    double band = 1.0 - 0.15 * sharpness * Math.Sin(ry * 0.25);
                    frame.Data[idx] = (ushort)(54000 * limb * band);
                    frame.Data[idx + 1] = (ushort)(48000 * limb * band);
                    frame.Data[idx + 2] = (ushort)(35000 * limb * band);
                }
                else if (ringEll is >= 0.48 and <= 1.0)
                {
                    // Cassini division around 0.80
                    double cassini = Math.Abs(ringEll - 0.80) < 0.035 ? (1.0 - 0.75 * sharpness) : 1.0;
                    frame.Data[idx] = (ushort)(50000 * cassini);
                    frame.Data[idx + 1] = (ushort)(44500 * cassini);
                    frame.Data[idx + 2] = (ushort)(34000 * cassini);
                }
                else
                {
                    frame.Data[idx] = 450;
                    frame.Data[idx + 1] = 450;
                    frame.Data[idx + 2] = 450;
                }
            }
        }

        return frame;
    }
}
