using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Mesure rapide du diamètre apparent de l'objet (planète/Lune) dans une vidéo ou une séquence,
/// afin de pouvoir comparer/classer les fichiers et vérifier que les objets alignés ont la même taille.
/// </summary>
public static class PlanetSizeAnalyzer
{
    public static Func<string, int, int, (AstroFrame Frame, int OriginalWidth, int OriginalHeight)>? PreviewFrameLoader { get; set; }

    /// <summary>
    /// Échantillonne quelques frames réparties dans le fichier et retourne la médiane du diamètre
    /// de l'objet en pixels (résolution d'origine), ou null si aucun objet n'a pu être détecté.
    /// </summary>
    public static double? MeasureDiameterPixels(string filePath, int frameCount, int samples = 7)
    {
        if (PreviewFrameLoader == null) return null;

        samples = Math.Clamp(samples, 1, 15);
        int total = Math.Max(1, frameCount);
        var diameters = new List<double>(samples);

        for (int i = 0; i < samples; i++)
        {
            double t = samples == 1 ? 0.5 : 0.1 + 0.8 * i / (samples - 1);
            int idx = Math.Clamp((int)(t * (total - 1)), 0, total - 1);

            try
            {
                var preview = PreviewFrameLoader(filePath, idx, 960);
                var frame = preview.Frame;
                double scale = preview.OriginalWidth > 0 ? (double)frame.Width / preview.OriginalWidth : 1.0;
                if (scale <= 0) scale = 1.0;

                var det = StabilisationAndQualityEngine.DetectPlanetOrObject(
                    frame,
                    checkObject: true,
                    autoThreshold: true,
                    manualThreshold8Bit: 0,
                    minObjectSizePixels: 4,
                    shadowEdge: LunarShadowEdge.None,
                    checkOverexposed: false,
                    maxOverexposedPixels: 0,
                    rejectClippedObjects: false);

                if (!det.Accepted || det.DetectedPixelCount <= 0) continue;

                double d = PlanetDiskCalculator.CalculateEquivalentDiameter(det.DetectedPixelCount, scale);
                diameters.Add(d);
            }
            catch
            {
                // frame illisible : on ignore cet échantillon
            }
        }

        return PlanetDiskCalculator.ComputeMedian(diameters);
    }
}
