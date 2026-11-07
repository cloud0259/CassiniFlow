using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Synthetic;

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
            // Dérive lente télescopique (monture équatoriale périodique + erreur de mise en station)
            double driftX = Math.Sin(i * 0.28) * 9.5 + Math.Cos(i * 0.08) * 4.0;
            double driftY = Math.Cos(i * 0.22) * 6.5 + Math.Sin(i * 0.06) * 3.0;

            // Fluctuation intermittente de Fried (Seeing 'Lucky Imaging') :
            // La majorité des trames subissent du flou atmosphérique, quelques trames furtives sont d'une netteté exceptionnelle
            double seeingNoise = Math.Sin(i * 1.7) * Math.Cos(i * 2.9);
            double seeingSharpness = 0.40 + 0.35 * Math.Pow((1.0 + Math.Sin(i * 0.35 + seeingNoise * 0.4)) * 0.5, 2.5);
            seeingSharpness = Math.Clamp(seeingSharpness, 0.25, 0.98);

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
        double radiusX = Math.Min(w, h) * 0.24;
        double radiusY = radiusX * 0.935; // Aplatissement polaire réel de Jupiter (oblateness 1/15)

        // Paramètres de phase de la turbulence de Kolmogorov animée dans le temps
        double t = index * 0.45;
        double rippleAmp = (1.05 - sharpness) * 2.2; // La turbulence déforme plus fort quand la netteté chute

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 3;

                // Champ de déformation vectorielle de front d'onde (Atmospheric Boiling / Convection)
                double waveX = Math.Sin(y * 0.08 + t) * Math.Cos(x * 0.05 - t * 0.7) * rippleAmp;
                double waveY = Math.Cos(x * 0.07 + t * 0.8) * Math.Sin(y * 0.06 + t * 0.5) * rippleAmp;

                // Coordonnées avec déformation atmosphérique
                double px = x + waveX;
                double py = y + waveY;

                // Dispersion chromatique atmosphérique (Dispersion réfractive naturelle : R vers le bas, B vers le haut)
                double dispPxR = px;
                double dispPyR = py + 1.25;

                double dispPxG = px;
                double dispPyG = py;

                double dispPxB = px;
                double dispPyB = py - 1.25;

                ushort r = SampleJupiterChannel(dispPxR, dispPyR, cx, cy, radiusX, radiusY, sharpness, channel: 0);
                ushort g = SampleJupiterChannel(dispPxG, dispPyG, cx, cy, radiusX, radiusY, sharpness, channel: 1);
                ushort b = SampleJupiterChannel(dispPxB, dispPyB, cx, cy, radiusX, radiusY, sharpness, channel: 2);

                frame.Data[idx] = r;
                frame.Data[idx + 1] = g;
                frame.Data[idx + 2] = b;
            }
        }

        return frame;
    }

    private static ushort SampleJupiterChannel(
        double px, double py,
        double cx, double cy,
        double rx, double ry,
        double sharpness,
        int channel)
    {
        double dx = px - cx;
        double dy = py - cy;
        double distNormSq = (dx * dx) / (rx * rx) + (dy * dy) / (ry * ry);

        if (distNormSq > 1.0)
        {
            // Satellite galiléen (Io) ponctuel
            double ioDx = px - (cx + rx * 1.65);
            double ioDy = py - (cy - ry * 0.35);
            double ioDistSq = ioDx * ioDx + ioDy * ioDy;
            if (ioDistSq <= 4.0)
            {
                double core = Math.Max(0.0, 1.0 - ioDistSq / 4.0);
                ushort val = (ushort)(32000 * core * sharpness);
                return channel switch
                {
                    0 => val,
                    1 => (ushort)(val * 0.90),
                    _ => (ushort)(val * 0.65)
                };
            }

            // Fond de ciel avec niveau de noir réaliste
            return (ushort)(520 + (channel == 0 ? 30 : 0));
        }

        double norm = Math.Sqrt(distNormSq);
        // Assombrissement au limbe de Minnaert réaliste
        double limbDarkening = Math.Pow(Math.Max(0.0, 1.0 - norm * norm * 0.82), 0.38);
        double lat = dy / ry; // -1 (nord) à +1 (sud)
        double lon = dx / rx; // -1 (est) à +1 (ouest)

        // Bandes joviennes détaillées :
        // EZ (Equatorial Zone) : lat ~ 0.0
        // NEB (North Equatorial Belt) : lat ~ -0.22
        // SEB (South Equatorial Belt) : lat ~ +0.22 (avec split)
        // NTB (North Temperate Belt) : lat ~ -0.45
        // STB (South Temperate Belt) : lat ~ +0.45
        // Calottes polaires (NPR / SPR)
        double neb = Math.Exp(-Math.Pow((lat + 0.22) * 9.5, 2));
        double seb1 = Math.Exp(-Math.Pow((lat - 0.16) * 11.0, 2));
        double seb2 = Math.Exp(-Math.Pow((lat - 0.28) * 11.0, 2));
        double ntb = Math.Exp(-Math.Pow((lat + 0.44) * 12.0, 2));
        double stb = Math.Exp(-Math.Pow((lat - 0.46) * 12.0, 2));
        double polarDarkening = Math.Pow(Math.Abs(lat), 2.2) * 0.35;

        // Festoons et tourbillons turbulents dans la Zone Équatoriale
        double festoons = 0.07 * sharpness * Math.Sin(dx * 0.28 + lat * 4.0) * Math.Cos(lat * 8.0);

        // Grande Tache Rouge (GRS) dans la bande SEB
        double grsDistX = (dx - rx * 0.32) / 16.0;
        double grsDistY = (dy - ry * 0.24) / 10.0;
        double grsDist = Math.Sqrt(grsDistX * grsDistX + grsDistY * grsDistY);

        double grsBoostR = 0.0;
        double grsDropGB = 0.0;
        if (grsDist < 1.0)
        {
            double grsProfile = (1.0 - grsDist * grsDist) * sharpness;
            grsBoostR = 0.32 * grsProfile;
            grsDropGB = 0.28 * grsProfile;
        }

        // Modulation spectrale par bande
        double beltAbsorption = (neb * 0.32 + (seb1 + seb2) * 0.26 + ntb * 0.18 + stb * 0.15) * sharpness;

        double baseR = (0.89 - beltAbsorption * 0.35 - polarDarkening + festoons + grsBoostR) * limbDarkening;
        double baseG = (0.76 - beltAbsorption * 0.65 - polarDarkening + festoons * 0.7 - grsDropGB) * limbDarkening;
        double baseB = (0.58 - beltAbsorption * 0.90 - polarDarkening - festoons * 0.5 - grsDropGB) * limbDarkening;

        double valNorm = channel switch
        {
            0 => Math.Clamp(baseR, 0.03, 1.0),
            1 => Math.Clamp(baseG, 0.03, 1.0),
            _ => Math.Clamp(baseB, 0.03, 1.0)
        };

        return (ushort)(valNorm * 62000);
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

        double t = index * 0.5;
        double rippleAmp = (1.05 - sharpness) * 1.8;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double waveX = Math.Sin(y * 0.09 + t) * rippleAmp;
                double waveY = Math.Cos(x * 0.08 + t * 0.7) * rippleAmp;
                double px = x + waveX;
                double py = y + waveY;

                double baseLum = 0.42 + 0.05 * sharpness * Math.Sin((px - dx) * 0.18) * Math.Cos((py - dy) * 0.18);
                foreach (var (cx, cy, cr) in craters)
                {
                    double d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    if (Math.Abs(d - cr) < 4.5)
                    {
                        double rimSign = (px - cx) / Math.Max(1.0, cr);
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
        double t = index * 0.45;
        double rippleAmp = (1.05 - sharpness) * 2.0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double waveX = Math.Sin(y * 0.08 + t) * rippleAmp;
                double waveY = Math.Cos(x * 0.07 + t * 0.8) * rippleAmp;
                double px = x + waveX;
                double py = y + waveY;

                double tx = px - cx;
                double ty = py - cy;
                double rx = tx * cosA - ty * sinA;
                double ry = tx * sinA + ty * cosA;
                int idx = (y * w + x) * 3;

                double globeDist = Math.Sqrt(rx * rx + (ry * 1.12) * (ry * 1.12));
                double ringEll = Math.Sqrt((rx / 96.0) * (rx / 96.0) + (ry / 28.0) * (ry / 28.0));

                (ushort r, ushort g, ushort b) = globeDist switch
                {
                    <= 34.0 => ComputeSaturnGlobePixel(globeDist, ry, sharpness),
                    _ when ringEll is >= 0.48 and <= 1.0 => ComputeSaturnRingPixel(ringEll, sharpness),
                    _ => ((ushort)450, (ushort)450, (ushort)450)
                };

                frame.Data[idx] = r;
                frame.Data[idx + 1] = g;
                frame.Data[idx + 2] = b;
            }
        }

        return frame;
    }

    private static (ushort R, ushort G, ushort B) ComputeSaturnGlobePixel(double globeDist, double ry, double sharpness)
    {
        double limb = Math.Sqrt(1.0 - (globeDist / 36.0) * (globeDist / 36.0));
        double band = 1.0 - 0.15 * sharpness * Math.Sin(ry * 0.25);
        return ((ushort)(54000 * limb * band), (ushort)(48000 * limb * band), (ushort)(35000 * limb * band));
    }

    private static (ushort R, ushort G, ushort B) ComputeSaturnRingPixel(double ringEll, double sharpness)
    {
        // Cassini division around 0.80
        double cassini = Math.Abs(ringEll - 0.80) < 0.035 ? (1.0 - 0.75 * sharpness) : 1.0;
        return ((ushort)(50000 * cassini), (ushort)(44500 * cassini), (ushort)(34000 * cassini));
    }
}
