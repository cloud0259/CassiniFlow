namespace CassiniFlow.Domain.Services;

public static class PlanetDiskCalculator
{
    public static double CalculateEquivalentDiameter(int detectedPixelCount, double scale = 1.0)
    {
        if (detectedPixelCount <= 0) return 0.0;
        if (scale <= 0) scale = 1.0;
        double dPreview = 2.0 * Math.Sqrt(detectedPixelCount / Math.PI);
        return dPreview / scale;
    }

    public static double? ComputeMedian(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
