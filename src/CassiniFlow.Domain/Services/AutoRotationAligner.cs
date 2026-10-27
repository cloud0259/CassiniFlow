using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Détection automatique de l'orientation d'un objet allongé (ex. Saturne avec ses anneaux)
/// et rotation des frames pour aligner plusieurs fichiers sur la même orientation.
/// L'orientation est l'angle (modulo 180°) de l'axe principal de la tache lumineuse,
/// mesuré dans le repère image (axe Y vers le bas, angle positif = sens horaire à l'écran).
/// </summary>
public static class AutoRotationAligner
{
    /// <summary>Rapport grand axe / petit axe minimum pour considérer l'orientation fiable.</summary>
    public const double MinReliableElongation = 1.08;

    public sealed record OrientationResult(double AngleDegrees, double Elongation);

    public static Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? PreviewFrameLoader { get; set; }

    /// <summary>
    /// Mesure l'orientation médiane (moyenne vectorielle sur 2θ) sur quelques frames du fichier.
    /// Retourne null si aucun objet exploitable n'est trouvé.
    /// </summary>
    public static OrientationResult? MeasureOrientation(string filePath, int frameCount, int samples = 9)
    {
        if (PreviewFrameLoader == null) return null;

        samples = Math.Clamp(samples, 1, 21);
        int total = Math.Max(1, frameCount);
        double sumCos = 0, sumSin = 0, sumElong = 0;
        int n = 0;

        for (int i = 0; i < samples; i++)
        {
            double t = samples == 1 ? 0.5 : 0.1 + 0.8 * i / (samples - 1);
            int idx = Math.Clamp((int)(t * (total - 1)), 0, total - 1);
            try
            {
                var preview = PreviewFrameLoader(filePath, idx, 960);
                var m = ComputeOrientation(preview.Frame);
                if (m is null) continue;
                double a2 = 2.0 * m.AngleDegrees * Math.PI / 180.0;
                // Pondération par l'élongation : les frames mieux contrastées/allongées comptent plus
                double wgt = Math.Max(0.01, m.Elongation - 1.0);
                sumCos += wgt * Math.Cos(a2);
                sumSin += wgt * Math.Sin(a2);
                sumElong += m.Elongation;
                n++;
            }
            catch
            {
                // frame illisible : ignorée
            }
        }

        if (n == 0) return null;
        double angle = 0.5 * Math.Atan2(sumSin, sumCos) * 180.0 / Math.PI;
        return new OrientationResult(angle, sumElong / n);
    }

    /// <summary>Orientation de l'axe principal de la tache lumineuse d'une frame (moments d'ordre 2).</summary>
    public static unsafe OrientationResult? ComputeOrientation(AstroFrame frame)
    {
        int w = frame.Width, h = frame.Height, ch = frame.Channels;
        int count = w * h;
        if (count == 0) return null;

        // Luminance (max des canaux) + histogramme 8 bits pour estimer fond et maximum
        var lum = new byte[count];
        var hist = new int[256];
        int maxL = 0;
        fixed (ushort* p = frame.Data)
        {
            for (int i = 0; i < count; i++)
            {
                int v = ch == 1
                    ? p[i]
                    : Math.Max(p[i * 3], Math.Max(p[i * 3 + 1], p[i * 3 + 2]));
                int b = v >> 8; // Data est normalisée sur 16 bits (8 bits × 257)
                if (b > 255) b = 255;
                lum[i] = (byte)b;
                hist[b]++;
                if (b > maxL) maxL = b;
            }
        }

        if (maxL < 20) return null;

        // Fond = médiane
        int acc = 0, bg = 0, half = count / 2;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= half) { bg = v; break; }
        }

        // Seuil bas pour inclure les anneaux (plus sombres que le globe)
        double thr = bg + 0.14 * (maxL - bg);
        thr = Math.Max(thr, bg + 8);

        double sw = 0, sx = 0, sy = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (lum[row + x] >= thr) { sw += 1; sx += x; sy += y; }
            }
        }

        if (sw < 30) return null;
        double cx = sx / sw, cy = sy / sw;

        double mu20 = 0, mu02 = 0, mu11 = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                if (lum[row + x] < thr) continue;
                double dx = x - cx;
                mu20 += dx * dx;
                mu02 += dy * dy;
                mu11 += dx * dy;
            }
        }
        mu20 /= sw; mu02 /= sw; mu11 /= sw;

        double common = Math.Sqrt(Math.Max(0, (mu20 - mu02) * (mu20 - mu02) / 4.0 + mu11 * mu11));
        double l1 = (mu20 + mu02) / 2.0 + common;
        double l2 = (mu20 + mu02) / 2.0 - common;
        if (l2 <= 1e-6) return null;

        double elong = Math.Sqrt(l1 / l2);
        double theta = 0.5 * Math.Atan2(2.0 * mu11, mu20 - mu02) * 180.0 / Math.PI;
        return new OrientationResult(theta, elong);
    }

    /// <summary>Ramène un angle dans ]-90°, 90°] (l'orientation d'un axe est définie modulo 180°).</summary>
    public static double NormalizeAxisAngle(double deg)
    {
        deg %= 180.0;
        if (deg > 90.0) deg -= 180.0;
        if (deg <= -90.0) deg += 180.0;
        return deg;
    }

    /// <summary>
    /// Fait pivoter la frame autour de son centre de <paramref name="degrees"/> (sens horaire à l'écran
    /// pour un angle positif), interpolation bilinéaire, taille conservée, fond noir.
    /// </summary>
    public static AstroFrame Rotate(AstroFrame src, double degrees)
    {
        if (Math.Abs(degrees) < 0.05) return src;

        int w = src.Width, h = src.Height, ch = src.Channels;
        var dst = new AstroFrame(w, h, ch, src.OriginalBitDepth)
        {
            FrameIndex = src.FrameIndex,
            IsBayerRaw = false,
            BayerPattern = src.BayerPattern,
            TimestampUtc = src.TimestampUtc,
            QualityScore = src.QualityScore,
            DetectedCentroidX = src.DetectedCentroidX,
            DetectedCentroidY = src.DetectedCentroidY
        };

        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        ushort[] s = src.Data;
        ushort[] d = dst.Data;

        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, y =>
        {
            double dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx;
                double sx = cos * dx + sin * dy + cx;
                double sy = -sin * dx + cos * dy + cy;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= w - 1 || y0 >= h - 1) continue;
                double fx = sx - x0, fy = sy - y0;
                int i00 = (y0 * w + x0) * ch;
                int i10 = i00 + ch;
                int i01 = i00 + w * ch;
                int i11 = i01 + ch;
                int o = (y * w + x) * ch;
                for (int c = 0; c < ch; c++)
                {
                    double v = (s[i00 + c] * (1 - fx) + s[i10 + c] * fx) * (1 - fy)
                             + (s[i01 + c] * (1 - fx) + s[i11 + c] * fx) * fy;
                    d[o + c] = (ushort)Math.Clamp((int)(v + 0.5), 0, 65535);
                }
            }
        });

        return dst;
    }
}
