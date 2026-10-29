using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Codecs;
using CassiniFlow.Infrastructure.WinJupos;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Portable, CPU-agnostic FFmpeg/FFprobe pipe backend used automatically on Linux (WSL), macOS, ARM64,
/// or older x86/x64 processors where native OpenCvSharpExtern binaries are unavailable.
/// Requires only standard 'ffmpeg' and 'ffprobe' on PATH (e.g. 'sudo apt install ffmpeg' on Linux/WSL).
/// </summary>
public static class FfmpegCliBackend
{
    private static bool? _isFfmpegInstalled;

    public static bool IsAvailable()
    {
        if (_isFfmpegInstalled.HasValue) return _isFfmpegInstalled.Value;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(2000);
            _isFfmpegInstalled = p is { ExitCode: 0 };
        }
        catch
        {
            _isFfmpegInstalled = false;
        }
        return _isFfmpegInstalled.Value;
    }

    public static MediaFileSummary InspectVideo(string filePath)
    {
        var fi = new FileInfo(filePath);
        string ext = fi.Extension.ToLowerInvariant();

        int width = 640, height = 480, frames = 100;
        double fps = 30.0;
        string codec = ext.TrimStart('.').ToUpperInvariant();

        if (ext == ".avi" && BmpAndAviWriter.TryReadAviHeader(filePath, out int aviW, out int aviH, out int aviFrames, out double aviFps))
        {
            width = aviW;
            height = aviH;
            frames = aviFrames;
            fps = aviFps;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,nb_frames,duration,codec_name:stream_tags=rotate:stream_side_data=rotation -show_entries format=duration -of json \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (p is not null)
            {
                string json = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);

                using var doc = JsonDocument.Parse(json);
                double durationSec = 0;
                if (doc.RootElement.TryGetProperty("format", out var fmt) &&
                    fmt.TryGetProperty("duration", out var durEl) &&
                    double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    durationSec = d;
                }

                if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
                {
                    var s = streams[0];
                    if (s.TryGetProperty("width", out var wEl)) width = Math.Max(1, wEl.GetInt32());
                    if (s.TryGetProperty("height", out var hEl)) height = Math.Max(1, hEl.GetInt32());
                    if (s.TryGetProperty("codec_name", out var cEl)) codec = (cEl.GetString() ?? codec).ToUpperInvariant();
                    if (durationSec <= 0 && s.TryGetProperty("duration", out var sDurEl) &&
                        double.TryParse(sDurEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double streamDurVal) &&
                        streamDurVal > 0)
                    {
                        durationSec = streamDurVal;
                    }

                    int rotationDeg = ExtractRotation(s);

                    // ffmpeg auto-rotates 90°/270° smartphone videos during decoding, so swap width & height to match decoded output
                    if (Math.Abs(rotationDeg) % 180 == 90)
                    {
                        (width, height) = (height, width);
                    }

                    if (s.TryGetProperty("r_frame_rate", out var fpsEl))
                    {
                        string fpsStr = fpsEl.GetString() ?? "30/1";
                        string[] parts = fpsStr.Split('/');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
                            den > 0)
                        {
                            fps = num / den;
                        }
                    }

                    frames = ExtractFrameCount(s, durationSec, fps, frames);
                }
            }
        }
        catch
        {
            // Keep safe defaults if ffprobe fails
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Fichier Vidéo : {fi.Name}");
        sb.AppendLine(new string('=', 56));
        sb.AppendLine($"Chemin      : {filePath}");
        sb.AppendLine($"Taille      : {fi.Length:N0} octets ({fi.Length / (1024.0 * 1024.0):F2} Mo)");
        sb.AppendLine($"Conteneur   : {ext.ToUpperInvariant()} (Codec: {codec})");
        sb.AppendLine($"Dimensions  : {width} × {height} px");
        sb.AppendLine($"Nb Frames   : {frames}");
        sb.AppendLine($"Cadence     : {fps:F2} fps");

        return new MediaFileSummary(
            filePath,
            $"{ext.TrimStart('.').ToUpperInvariant()} ({codec})",
            frames,
            width,
            height,
            fps,
            fi.Length,
            sb.ToString());
    }

    private static int ExtractRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("rotate", out var rotTag) &&
            int.TryParse(rotTag.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rTag))
        {
            return rTag;
        }

        if (stream.TryGetProperty("side_data_list", out var sideList) && sideList.ValueKind == JsonValueKind.Array)
        {
            foreach (var sd in sideList.EnumerateArray())
            {
                if (sd.TryGetProperty("rotation", out var rotEl))
                {
                    switch (rotEl.ValueKind)
                    {
                        case JsonValueKind.Number when rotEl.TryGetDouble(out double rNum):
                            return (int)Math.Round(rNum);

                        case JsonValueKind.String when double.TryParse(rotEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rStr):
                            return (int)Math.Round(rStr);
                    }
                }
            }
        }

        return 0;
    }

    private static int ExtractFrameCount(JsonElement stream, double durationSec, double fps, int defaultFrames)
    {
        if (stream.TryGetProperty("nb_frames", out var nbEl) &&
            int.TryParse(nbEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFrames) &&
            parsedFrames > 0)
        {
            return parsedFrames;
        }

        if (durationSec > 0 && fps > 0)
        {
            return Math.Max(1, (int)Math.Round(durationSec * fps));
        }

        return defaultFrames;
    }

    public static UniversalPreviewFrame ReadPreviewFrame(string filePath, int frameIndex, int maxPreviewDim = 960)
    {
        var summary = InspectVideo(filePath);
        int origW = summary.Width;
        int origH = summary.Height;
        double fps = Math.Max(1.0, summary.Fps);
        int target = Math.Clamp(frameIndex, 0, Math.Max(0, summary.FrameCount - 1));

        double scale = 1.0;
        int outW = origW;
        int outH = origH;
        int maxDim = Math.Max(origW, origH);
        if (maxDim > maxPreviewDim)
        {
            scale = (double)maxPreviewDim / maxDim;
            outW = Math.Max(16, ((int)Math.Round(origW * scale)) & ~1);
            outH = Math.Max(16, ((int)Math.Round(origH * scale)) & ~1);
        }

        double timestampSec = target / fps;
        string timeArg = timestampSec.ToString("F3", CultureInfo.InvariantCulture);

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-ss {timeArg} -i \"{filePath}\" -frames:v 1 -vf scale={outW}:{outH} -f rawvideo -pix_fmt rgb24 -v quiet -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Impossible de démarrer ffmpeg. Installez ffmpeg ('sudo apt install ffmpeg').");

        int byteCount = outW * outH * 3;
        byte[] buf = new byte[byteCount];
        int readTotal = 0;
        var stream = p.StandardOutput.BaseStream;
        while (readTotal < byteCount)
        {
            int r = stream.Read(buf, readTotal, byteCount - readTotal);
            if (r <= 0) break;
            readTotal += r;
        }
        p.WaitForExit(3000);

        var frame = new AstroFrame(outW, outH, 3, 8) { FrameIndex = target };
        ushort[] dst = frame.Data;
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = (ushort)(buf[i] * 257);
        }

        return new UniversalPreviewFrame(frame, origW, origH, (double)outW / origW);
    }

    public static IEnumerable<AstroFrame> EnumerateFrames(string filePath, int startIndex, int endIndex)
    {
        var summary = InspectVideo(filePath);
        int w = summary.Width;
        int h = summary.Height;
        double fps = Math.Max(1.0, summary.Fps);
        int start = Math.Max(0, startIndex);
        int end = Math.Min(summary.FrameCount - 1, endIndex);
        int count = Math.Max(0, end - start + 1);
        if (count == 0) yield break;

        double startSec = start / fps;
        string ssArg = start > 0 ? $"-ss {startSec.ToString("F3", CultureInfo.InvariantCulture)} " : string.Empty;

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"{ssArg}-threads 1 -i \"{filePath}\" -frames:v {count} -vf scale={w}:{h} -f rawvideo -pix_fmt rgb24 -v quiet -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (p is null) yield break;

        int frameBytes = w * h * 3;
        byte[] buf = new byte[frameBytes];
        var stream = p.StandardOutput.BaseStream;
        int idx = start;

        while (idx <= end)
        {
            int readTotal = 0;
            while (readTotal < frameBytes)
            {
                int r = stream.Read(buf, readTotal, frameBytes - readTotal);
                if (r <= 0) break;
                readTotal += r;
            }
            if (readTotal < frameBytes) break;

            var frame = new AstroFrame(w, h, 3, 8) { FrameIndex = idx };
            ushort[] dst = frame.Data;
            for (int i = 0; i < frameBytes; i++)
            {
                dst[i] = (ushort)(buf[i] * 257);
            }

            yield return frame;
            idx++;
        }

        try
        {
            if (!p.HasExited) p.Kill();
        }
        catch
        {
            // Ignore process cleanup errors
        }
    }

    public static unsafe void ScanSegmentIntoTopKHeapFast(
        string filePath,
        int startIdx,
        int endIdx,
        int globalFrameBase,
        ProcessingOptions options,
        bool enforcePlanetRejection,
        bool centrePlanet,
        int maxKeepTarget,
        PriorityQueue<AstroFrame, double> topKHeap,
        object heapLock,
        ref double currentMinScoreInHeap,
        ref int scannedCount,
        ref int acceptedBeforeLimit,
        ref int discardedNoPlanet,
        CancellationToken cancellationToken,
        Action<int> onStep)
    {
        var summary = InspectVideo(filePath);
        int w = summary.Width;
        int h = summary.Height;
        double fps = Math.Max(1.0, summary.Fps);
        int start = Math.Max(0, startIdx);
        int end = Math.Min(summary.FrameCount - 1, endIdx);
        int count = Math.Max(0, end - start + 1);
        if (count == 0) return;

        bool hasExtractedStart = WinJuposHelper.TryExtractDateTime(filePath, false, out var startUtc);
        if (!hasExtractedStart) startUtc = File.GetCreationTimeUtc(filePath);

        double startSec = start / fps;
        string ssArg = start > 0 ? $"-ss {startSec.ToString("F3", CultureInfo.InvariantCulture)} " : string.Empty;

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"{ssArg}-threads 1 -i \"{filePath}\" -frames:v {count} -vf scale={w}:{h} -f rawvideo -pix_fmt rgb24 -v quiet -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (p is null) return;

        int frameBytes = w * h * 3;
        byte[] buf = new byte[frameBytes];
        var stream = p.StandardOutput.BaseStream;
        int idx = start;
        int roiW = options.EnableCropping ? Math.Max(16, options.CropWidth) : 512;
        int roiH = options.EnableCropping ? Math.Max(16, options.CropHeight) : 512;
        int hintX = -1, hintY = -1, cachedThresh = -1;
        AstroFrame? spareFrame = null;

        try
        {
            while (idx <= end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int readTotal = 0;
                while (readTotal < frameBytes)
                {
                    int r = stream.Read(buf, readTotal, frameBytes - readTotal);
                    if (r <= 0) break;
                    readTotal += r;
                }
                if (readTotal < frameBytes) break;

                int done = Interlocked.Increment(ref scannedCount);
                if ((idx - start) % 45 == 0) cachedThresh = -1;

                var det = DetectOnRgb24FastTracked(
                    buf,
                    w,
                    h,
                    enforcePlanetRejection,
                    options.AutoDetectionThreshold,
                    options.ManualDetectionThreshold,
                    options.MinimumObjectSizePixels,
                    ref hintX,
                    ref hintY,
                    ref cachedThresh);

                if (enforcePlanetRejection && !det.Accepted)
                {
                    Interlocked.Increment(ref discardedNoPlanet);
                    onStep(done);
                    idx++;
                    continue;
                }

                Interlocked.Increment(ref acceptedBeforeLimit);
                int targetX = centrePlanet ? det.CentroidX : w / 2;
                int targetY = centrePlanet ? det.CentroidY : h / 2;

                double score = options.EnableQualityEstimation
                    ? ComputeQualityOnRgb24RoiFast(
                        buf,
                        w,
                        h,
                        targetX - options.CropOffsetX,
                        targetY + options.CropOffsetY,
                        roiW,
                        roiH,
                        options.QualityAlgorithm,
                        options.QualityMinSubsample,
                        options.QualityMaxSubsample,
                        options.QualitySubsampleIncrement)
                    : 100.0;

                bool shouldExtract;
                lock (heapLock)
                {
                    shouldExtract = topKHeap.Count < maxKeepTarget || score > currentMinScoreInHeap;
                }

                if (shouldExtract)
                {
                    int globalIdx = globalFrameBase + (idx - start);
                    AstroFrame work = options.EnableCropping
                        ? ExtractCroppedAstroFrameFromRgb24(
                            buf,
                            w,
                            h,
                            globalIdx,
                            targetX,
                            targetY,
                            options.CropWidth,
                            options.CropHeight,
                            options.CropOffsetX,
                            options.CropOffsetY,
                            reusableDst: spareFrame)
                        : Rgb24ToAstroFrame(buf, w, h, globalIdx);

                    work.QualityScore = score;
                    work.TimestampUtc = startUtc.AddSeconds(idx / fps);

                    lock (heapLock)
                    {
                        if (topKHeap.Count < maxKeepTarget)
                        {
                            topKHeap.Enqueue(work, score);
                            topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                            spareFrame = null;
                        }
                        else if (score > currentMinScoreInHeap)
                        {
                            var evicted = topKHeap.Dequeue();
                            topKHeap.Enqueue(work, score);
                            topKHeap.TryPeek(out _, out currentMinScoreInHeap);
                            spareFrame = evicted;
                        }
                        else
                        {
                            spareFrame = work;
                        }
                    }
                }

                onStep(done);
                idx++;
            }
        }
        finally
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private static unsafe DetectionResult DetectOnRgb24FastTracked(
        byte[] rgbBuf,
        int w,
        int h,
        bool checkObject,
        bool autoThreshold,
        int manualThreshold8Bit,
        int minObjectSizePixels,
        ref int hintX,
        ref int hintY,
        ref int cachedThreshold8)
    {
        int stride = w * 3;
        int minArea = Math.Max(4, (int)(Math.PI * (minObjectSizePixels * 0.5) * (minObjectSizePixels * 0.5)) / 2);

        fixed (byte* pBase = rgbBuf)
        {
            if (hintX >= 0 && hintY >= 0 && cachedThreshold8 > 0)
            {
                const int radius = 240;
                int rx0 = Math.Max(0, hintX - radius);
                int ry0 = Math.Max(0, hintY - radius);
                int rx1 = Math.Min(w, hintX + radius);
                int ry1 = Math.Min(h, hintY + radius);
                const int localStep = 4;

                long lSumX = 0, lSumY = 0, lWeight = 0;
                int lDet = 0;
                int thresh = cachedThreshold8;
                int lMinX = rx1, lMaxX = rx0, lMinY = ry1, lMaxY = ry0;

                for (int y = ry0; y < ry1; y += localStep)
                {
                    byte* row = pBase + y * stride;
                    for (int x = rx0; x < rx1; x += localStep)
                    {
                        int idx = x * 3;
                        byte r = row[idx], g = row[idx + 1], b = row[idx + 2];
                        byte lum = r >= g ? (r >= b ? r : b) : (g >= b ? g : b);
                        if (lum >= thresh)
                        {
                            lDet += localStep * localStep;
                            long wt = lum - thresh + 1;
                            lSumX += (long)x * wt;
                            lSumY += (long)y * wt;
                            lWeight += wt;
                            if (x < lMinX) lMinX = x;
                            if (x > lMaxX) lMaxX = x;
                            if (y < lMinY) lMinY = y;
                            if (y > lMaxY) lMaxY = y;
                        }
                    }
                }

                if (lDet >= minArea && lWeight > 0 && lMinX <= lMaxX && lMinY <= lMaxY)
                {
                    int lbaryX = (int)(lSumX / lWeight);
                    int lbaryY = (int)(lSumY / lWeight);
                    int lestRadius = Math.Max(minObjectSizePixels / 2, (int)Math.Sqrt(lDet / Math.PI));
                    int ldiscRadius = (int)(lestRadius * 1.35) + 6;

                    int ldiscMinX = Math.Max(lMinX, lbaryX - ldiscRadius);
                    int ldiscMaxX = Math.Min(lMaxX, lbaryX + ldiscRadius);
                    int ldiscMinY = Math.Max(lMinY, lbaryY - ldiscRadius);
                    int ldiscMaxY = Math.Min(lMaxY, lbaryY + ldiscRadius);

                    int lgeomX = (ldiscMinX <= ldiscMaxX) ? (ldiscMinX + ldiscMaxX) / 2 : lbaryX;
                    int lgeomY = (ldiscMinY <= ldiscMaxY) ? (ldiscMinY + ldiscMaxY) / 2 : lbaryY;

                    int lcx = (Math.Abs(lgeomX - lbaryX) <= lestRadius / 2) ? lgeomX : lbaryX;
                    int lcy = (Math.Abs(lgeomY - lbaryY) <= lestRadius / 2) ? lgeomY : lbaryY;
                    hintX = lcx;
                    hintY = lcy;
                    return new DetectionResult(true, null, lcx, lcy, lDet, 0, thresh);
                }
            }

            int step = (w > 1920 || h > 1920) ? 10 : ((w > 960 || h > 960) ? 6 : 2);
            int threshold8 = manualThreshold8Bit;
            if (autoThreshold)
            {
                if (cachedThreshold8 > 0)
                {
                    threshold8 = cachedThreshold8;
                }
                else
                {
                    byte maxVal = 0;
                    int tStep = step * 2;
                    for (int y = 0; y < h; y += tStep)
                    {
                        byte* row = pBase + y * stride;
                        for (int x = 0; x < w; x += tStep)
                        {
                            int idx = x * 3;
                            byte lum = Math.Max(row[idx], Math.Max(row[idx + 1], row[idx + 2]));
                            if (lum > maxVal) maxVal = lum;
                        }
                    }
                    threshold8 = Math.Clamp((int)(maxVal * 0.14), 8, 180);
                    cachedThreshold8 = threshold8;
                }
            }
            else
            {
                cachedThreshold8 = manualThreshold8Bit;
            }

            long sumX = 0, sumY = 0, totalWeight = 0;
            int detectedPixels = 0;
            int minX = w, maxX = 0, minY = h, maxY = 0;

            for (int y = 0; y < h; y += step)
            {
                byte* row = pBase + y * stride;
                for (int x = 0; x < w; x += step)
                {
                    int idx = x * 3;
                    byte r = row[idx], g = row[idx + 1], b = row[idx + 2];
                    byte lum = r >= g ? (r >= b ? r : b) : (g >= b ? g : b);
                    if (lum >= threshold8)
                    {
                        detectedPixels += step * step;
                        long weight = lum - threshold8 + 1;
                        sumX += (long)x * weight;
                        sumY += (long)y * weight;
                        totalWeight += weight;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            int baryX = totalWeight > 0 ? (int)(sumX / totalWeight) : w / 2;
            int baryY = totalWeight > 0 ? (int)(sumY / totalWeight) : h / 2;
            int estRadius = Math.Max(minObjectSizePixels / 2, (int)Math.Sqrt(detectedPixels / Math.PI));
            int discRadius = (int)(estRadius * 1.35) + 6;

            int discMinX = Math.Max(minX, baryX - discRadius);
            int discMaxX = Math.Min(maxX, baryX + discRadius);
            int discMinY = Math.Max(minY, baryY - discRadius);
            int discMaxY = Math.Min(maxY, baryY + discRadius);

            int geomX = (discMinX <= discMaxX) ? (discMinX + discMaxX) / 2 : baryX;
            int geomY = (discMinY <= discMaxY) ? (discMinY + discMaxY) / 2 : baryY;

            int cx = (Math.Abs(geomX - baryX) <= estRadius / 2) ? geomX : baryX;
            int cy = (Math.Abs(geomY - baryY) <= estRadius / 2) ? geomY : baryY;

            if (!checkObject)
            {
                hintX = cx;
                hintY = cy;
                return new DetectionResult(true, null, cx, cy, detectedPixels, 0, threshold8);
            }

            bool accepted = detectedPixels >= minArea;
            if (accepted)
            {
                hintX = cx;
                hintY = cy;
            }
            return new DetectionResult(accepted, accepted ? null : "No object detected", cx, cy, detectedPixels, 0, threshold8);
        }
    }
    private static unsafe double ComputeQualityOnRgb24RoiFast(
        byte[] rgbBuf,
        int srcW,
        int srcH,
        int centerX,
        int centerY,
        int roiWidth,
        int roiHeight,
        QualityAlgorithm algorithm,
        int minSubsample,
        int maxSubsample,
        int subsampleInc)
    {
        int stride = srcW * 3;
        int x0 = Math.Clamp(centerX - roiWidth / 2, 0, Math.Max(0, srcW - 8));
        int y0 = Math.Clamp(centerY - roiHeight / 2, 0, Math.Max(0, srcH - 8));
        int x1 = Math.Clamp(x0 + roiWidth, x0 + 8, srcW);
        int y1 = Math.Clamp(y0 + roiHeight, y0 + 8, srcH);
        int w = x1 - x0;
        int h = y1 - y0;
        if (w < 8 || h < 8) return 0.0;

        fixed (byte* pBase = rgbBuf)
        {
            if (algorithm == QualityAlgorithm.TotalBrightness)
            {
                long sum = 0;
                for (int y = y0; y < y1; y += 2)
                {
                    byte* row = pBase + y * stride;
                    for (int x = x0; x < x1; x += 2)
                    {
                        sum += row[x * 3 + 1];
                    }
                }
                return (double)(sum * 4) / (w * h);
            }

            if (algorithm == QualityAlgorithm.PeakHistogram)
            {
                Span<int> hist = stackalloc int[256];
                for (int y = y0; y < y1; y += 2)
                {
                    byte* row = pBase + y * stride;
                    for (int x = x0; x < x1; x += 2)
                    {
                        hist[row[x * 3 + 1]]++;
                    }
                }
                double weighted = 0;
                for (int i = 16; i < 256; i++) weighted += hist[i] * (i * i);
                return weighted / (w * h);
            }

            int minSub = Math.Max(2, minSubsample);
            int maxSub = Math.Max(minSub, maxSubsample);
            int inc = Math.Max(1, subsampleInc);

            double totalGrad = 0;
            for (int step = minSub; step <= maxSub; step += inc)
            {
                long stepGrad = 0;
                for (int y = y0; y < y1 - step; y += step)
                {
                    byte* row = pBase + y * stride;
                    byte* downRow = pBase + (y + step) * stride;
                    for (int x = x0; x < x1 - step; x += step)
                    {
                        int idx = x * 3 + 1;
                        int c = row[idx];
                        int dx = c - row[(x + step) * 3 + 1];
                        int dy = c - downRow[idx];
                        if (dx < 0) dx = -dx;
                        if (dy < 0) dy = -dy;
                        stepGrad += dx + dy;
                    }
                }
                totalGrad += stepGrad * 257.0;
            }

            return totalGrad / (w * h);
        }
    }

    private static unsafe AstroFrame ExtractCroppedAstroFrameFromRgb24(
        byte[] rgbBuf,
        int srcW,
        int srcH,
        int frameIndex,
        int centerX,
        int centerY,
        int cropWidth,
        int cropHeight,
        int offsetX,
        int offsetY,
        AstroFrame? reusableDst)
    {
        cropWidth = Math.Max(16, cropWidth);
        cropHeight = Math.Max(16, cropHeight);

        int startX = centerX - (cropWidth / 2) - offsetX;
        int startY = centerY - (cropHeight / 2) + offsetY;

        AstroFrame dst;
        if (reusableDst is not null && reusableDst.Width == cropWidth && reusableDst.Height == cropHeight && reusableDst.Channels == 3)
        {
            dst = reusableDst;
            dst.FrameIndex = frameIndex;
            dst.DetectedCentroidX = cropWidth / 2;
            dst.DetectedCentroidY = cropHeight / 2;
            if (startX < 0 || startY < 0 || startX + cropWidth > srcW || startY + cropHeight > srcH)
            {
                Array.Clear(dst.Data, 0, dst.Data.Length);
            }
        }
        else
        {
            dst = new AstroFrame(cropWidth, cropHeight, 3, 8)
            {
                FrameIndex = frameIndex,
                DetectedCentroidX = cropWidth / 2,
                DetectedCentroidY = cropHeight / 2
            };
        }

        int stride = srcW * 3;
        fixed (byte* pSrcBase = rgbBuf)
        fixed (ushort* pDstBase = dst.Data)
        {
            for (int y = 0; y < cropHeight; y++)
            {
                int sy = startY + y;
                if ((uint)sy >= (uint)srcH) continue;

                int dstXStart = 0;
                int srcXStart = startX;
                int copyPixels = cropWidth;

                if (srcXStart < 0)
                {
                    dstXStart = -srcXStart;
                    copyPixels -= dstXStart;
                    srcXStart = 0;
                }
                if (srcXStart + copyPixels > srcW)
                {
                    copyPixels = srcW - srcXStart;
                }
                if (copyPixels <= 0) continue;

                byte* srcRow = pSrcBase + sy * stride;
                ushort* dstRow = pDstBase + y * cropWidth * 3;
                for (int i = 0; i < copyPixels; i++)
                {
                    int sIdx = (srcXStart + i) * 3;
                    int dIdx = (dstXStart + i) * 3;
                    dstRow[dIdx] = (ushort)(srcRow[sIdx] * 257);         // R
                    dstRow[dIdx + 1] = (ushort)(srcRow[sIdx + 1] * 257); // G
                    dstRow[dIdx + 2] = (ushort)(srcRow[sIdx + 2] * 257); // B
                }
            }
        }

        return dst;
    }

    private static AstroFrame Rgb24ToAstroFrame(byte[] rgbBuf, int w, int h, int frameIndex)
    {
        var frame = new AstroFrame(w, h, 3, 8) { FrameIndex = frameIndex };
        ushort[] dst = frame.Data;
        int len = dst.Length;
        for (int i = 0; i < len; i++)
        {
            dst[i] = (ushort)(rgbBuf[i] * 257);
        }
        return frame;
    }

    public static void WriteVideoFile(string outputPath, IReadOnlyList<AstroFrame> frames, double fps, bool isMp4)
    {
        if (frames.Count == 0) return;
        int w = (frames[0].Width + 1) & ~1;
        int h = (frames[0].Height + 1) & ~1;
        string fpsStr = Math.Max(1.0, fps).ToString("F2", CultureInfo.InvariantCulture);
        string codecArgs = isMp4 ? "-c:v libx264 -pix_fmt yuv420p -crf 18" : "-c:v mjpeg -q:v 2";

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -loglevel error -f rawvideo -pix_fmt rgb24 -s {w}x{h} -r {fpsStr} -i - {codecArgs} \"{outputPath}\"",
            RedirectStandardInput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (p is null) return;

        int frameBytes = w * h * 3;
        byte[] buf = new byte[frameBytes];
        var stdin = p.StandardInput.BaseStream;

        foreach (var frame in frames)
        {
            Array.Clear(buf, 0, frameBytes);
            int copyW = Math.Min(w, frame.Width);
            int copyH = Math.Min(h, frame.Height);
            int ch = frame.Channels;
            ushort[] src = frame.Data;

            for (int y = 0; y < copyH; y++)
            {
                for (int x = 0; x < copyW; x++)
                {
                    int dIdx = (y * w + x) * 3;
                    if (ch == 1)
                    {
                        byte v = (byte)(src[y * frame.Width + x] >> 8);
                        buf[dIdx] = v;
                        buf[dIdx + 1] = v;
                        buf[dIdx + 2] = v;
                    }
                    else
                    {
                        int sIdx = (y * frame.Width + x) * 3;
                        buf[dIdx] = (byte)(src[sIdx] >> 8);
                        buf[dIdx + 1] = (byte)(src[sIdx + 1] >> 8);
                        buf[dIdx + 2] = (byte)(src[sIdx + 2] >> 8);
                    }
                }
            }
            stdin.Write(buf, 0, frameBytes);
        }

        stdin.Flush();
        stdin.Close();
        p.WaitForExit(120000);
    }
}
