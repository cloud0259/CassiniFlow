using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Adapters;

namespace CassiniFlow.Desktop.Services;

public static class AstroBitmapHelper
{
    public static Bitmap? LoadBitmapFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        // 1. Toujours décoder via UniversalMediaReader en priorité :
        // Préserve la dynamique linéaire exacte pour FITS, SER, TIFF et PNG 16-bit / 8-bit astro
        // sans altération par la courbe de transfert sRGB de Skia/Avalonia qui brûle les blancs.
        try
        {
            var frame = UniversalMediaReader.ReadSingleFrame(filePath, 0);
            return BlitToBitmap(frame);
        }
        catch
        {
            // 2. Repli vers le décodeur natif Avalonia pour les formats web ou non astro
            try
            {
                using var fs = File.OpenRead(filePath);
                return new Bitmap(fs);
            }
            catch
            {
                return null;
            }
        }
    }

    public static unsafe WriteableBitmap BlitToBitmap(AstroFrame frame)
    {
        int w = frame.Width;
        int h = frame.Height;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        using var fb = bmp.Lock();
        byte* pDest = (byte*)fb.Address;
        int stride = fb.RowBytes;
        int ch = frame.Channels;

        fixed (ushort* pSrc = frame.Data)
        {
            for (int y = 0; y < h; y++)
            {
                byte* rowDest = pDest + (y * stride);
                int rowSrc = y * w;

                for (int x = 0; x < w; x++)
                {
                    byte b, g, r;
                    if (ch == 1)
                    {
                        byte lum = (byte)(pSrc[rowSrc + x] >> 8);
                        b = g = r = lum;
                    }
                    else
                    {
                        int srcIdx = (rowSrc + x) * 3;
                        r = (byte)(pSrc[srcIdx] >> 8);
                        g = (byte)(pSrc[srcIdx + 1] >> 8);
                        b = (byte)(pSrc[srcIdx + 2] >> 8);
                    }

                    int destIdx = x * 4;
                    rowDest[destIdx] = b;
                    rowDest[destIdx + 1] = g;
                    rowDest[destIdx + 2] = r;
                    rowDest[destIdx + 3] = 255;
                }
            }
        }

        return bmp;
    }
}
