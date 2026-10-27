using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Ports.Outbound;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.UseCases;

public sealed class MeasurePlanetUseCase : IMeasurePlanetUseCase
{
    private readonly IMediaReaderFactory _readerFactory;

    public MeasurePlanetUseCase(IMediaReaderFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    public Task<double?> MeasureDiameterPixelsAsync(string filePath, int frameCount, int samples = 7)
    {
        return Task.Run(() =>
        {
            samples = Math.Clamp(samples, 1, 15);
            int total = Math.Max(1, frameCount);
            var diameters = new List<double>(samples);

            for (int i = 0; i < samples; i++)
            {
                double t = samples == 1 ? 0.5 : 0.1 + 0.8 * i / (samples - 1);
                int idx = Math.Clamp((int)(t * (total - 1)), 0, total - 1);

                try
                {
                    var preview = _readerFactory.ReadPreviewFrame(filePath, idx, 960);
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
        });
    }
}

public sealed class MeasureOrientationUseCase : IMeasureOrientationUseCase
{
    private readonly IMediaReaderFactory _readerFactory;

    public MeasureOrientationUseCase(IMediaReaderFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    public Task<(double AngleDegrees, double Elongation)?> MeasureOrientationAsync(string filePath, int frameCount, int samples = 9)
    {
        return Task.Run<(double AngleDegrees, double Elongation)?>(() =>
        {
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
                    var preview = _readerFactory.ReadPreviewFrame(filePath, idx, 960);
                    var m = AutoRotationAligner.ComputeOrientation(preview.Frame);
                    if (m is null) continue;
                    double a2 = 2.0 * m.AngleDegrees * Math.PI / 180.0;
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
            return (angle, sumElong / n);
        });
    }
}
