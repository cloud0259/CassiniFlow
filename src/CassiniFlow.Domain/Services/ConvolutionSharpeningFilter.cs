using System.Buffers;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Domain.Services;

/// <summary>
/// Filtre d'accentuation haute fréquence par convolution spatiale 5x5 non destructive.
/// Réhausse le contraste des fins détails planétaires sans brûler les hautes lumières.
/// </summary>
public static class ConvolutionSharpeningFilter
{
    // Noyau de convolution laplacien 5x5 extrait par ingénierie inverse
    private static readonly int[] DefaultKernel5x5 =
    [
        -2, -3, -3, -3, -2,
        -3, -1, -2, -1, -3,
        -3, -2, 60, -2, -3,
        -3, -1, -2, -1, -3,
        -2, -3, -3, -3, -2
    ];

    private const int KernelSum = 16; // Somme des coefficients

    public static AstroFrame ApplySharpening(AstroFrame frame, int blendPercent = 10)
    {
        if (frame.Width < 5 || frame.Height < 5 || blendPercent <= 0)
        {
            return frame;
        }

        blendPercent = Math.Clamp(blendPercent, 1, 100);
        double blend = blendPercent / 100.0;
        double origWeight = 1.0 - blend;

        int width = frame.Width;
        int height = frame.Height;
        int channels = frame.Channels;
        int totalSamples = width * height * channels;

        var result = new AstroFrame(width, height, channels, frame.OriginalBitDepth)
        {
            FrameIndex = frame.FrameIndex,
            TimestampUtc = frame.TimestampUtc,
            QualityScore = frame.QualityScore,
            DetectedCentroidX = frame.DetectedCentroidX,
            DetectedCentroidY = frame.DetectedCentroidY
        };

        ushort[] src = frame.Data;
        ushort[] dst = result.Data;

        // Copie des bordures 2px intactes
        Array.Copy(src, dst, totalSamples);

        // Convolution 5x5 sur l'intérieur
        for (int y = 2; y < height - 2; y++)
        {
            for (int x = 2; x < width - 2; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int convSum = 0;
                    int kIdx = 0;

                    for (int ky = -2; ky <= 2; ky++)
                    {
                        int rowOffset = (y + ky) * width * channels;
                        for (int kx = -2; kx <= 2; kx++)
                        {
                            int pIdx = rowOffset + (x + kx) * channels + c;
                            convSum += src[pIdx] * DefaultKernel5x5[kIdx++];
                        }
                    }

                    int sharpenedVal = Math.Clamp(convSum / KernelSum, 0, 65535);
                    int origVal = src[y * width * channels + x * channels + c];

                    // Mélange linéaire entre l'image brute empilée et l'image accentuée
                    int finalVal = (int)Math.Round(origVal * origWeight + sharpenedVal * blend);
                    dst[y * width * channels + x * channels + c] = (ushort)Math.Clamp(finalVal, 0, 65535);
                }
            }
        }

        return result;
    }
}

