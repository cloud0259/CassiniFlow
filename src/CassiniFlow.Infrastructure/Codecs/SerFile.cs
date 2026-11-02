using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Infrastructure.Codecs;

public enum SerColorId
{
    Mono = 0,
    BayerRggb = 8,
    BayerGrbg = 9,
    BayerGbrg = 10,
    BayerBggr = 11,
    BayerCmyg = 16,
    BayerCygm = 17,
    BayerRgbc = 18,
    BayerBgrg = 19,
    Rgb = 100,
    Bgr = 101
}

public sealed class SerHeader
{
    public const int HeaderSizeBytes = 178;
    public const string MagicSignature = "LUCAM-RECORDER";

    public string FileId { get; set; } = MagicSignature;
    public int LuId { get; set; }
    public SerColorId ColorId { get; set; } = SerColorId.Mono;
    public int LittleEndian { get; set; } = 0; // Per SER spec quirk, 0 = Little Endian in many cameras, 1 = Big/Little depending on software
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public int PixelDepthPerPlane { get; set; } = 8;
    public int FrameCount { get; set; }
    public string Observer { get; set; } = "New-CassiniFlow";
    public string Instrument { get; set; } = "AstroCam";
    public string Telescope { get; set; } = "Telescope";
    public long DateTimeTicks { get; set; }
    public long DateTimeUtcTicks { get; set; }
    public bool HasTrailerTimestamps { get; set; }

    public int Planes => ColorId is SerColorId.Rgb or SerColorId.Bgr ? 3 : 1;
    public int BytesPerPixelPlane => PixelDepthPerPlane <= 8 ? 1 : 2;
    public long BytesPerFrame => (long)ImageWidth * ImageHeight * Planes * BytesPerPixelPlane;

    public bool IsBayer => ColorId is SerColorId.BayerRggb or SerColorId.BayerGrbg or SerColorId.BayerGbrg or SerColorId.BayerBggr;

    public BayerPattern ToBayerPattern() => ColorId switch
    {
        SerColorId.BayerGbrg => BayerPattern.Gbrg,
        SerColorId.BayerBggr => BayerPattern.Bggr,
        SerColorId.BayerGrbg => BayerPattern.Grbg,
        _ => BayerPattern.Rggb
    };

    public string FormatDetailedReport(string filePath, long fileSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SER File Details: {Path.GetFileName(filePath)}");
        sb.AppendLine($"File Size          : {fileSize:N0} bytes ({fileSize / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine($"File ID            : {FileId}");
        sb.AppendLine($"LuID               : {LuId}");
        sb.AppendLine($"Color ID           : {ColorId} ({(int)ColorId})");
        sb.AppendLine($"Little Endian Flag : {LittleEndian}");
        sb.AppendLine($"Dimensions         : {ImageWidth} x {ImageHeight} px");
        sb.AppendLine($"Pixel Depth        : {PixelDepthPerPlane}-bit ({BytesPerPixelPlane} byte(s)/sample)");
        sb.AppendLine($"Frame Count        : {FrameCount:N0}");
        sb.AppendLine($"Bytes Per Frame    : {BytesPerFrame:N0}");
        sb.AppendLine($"Observer           : {Observer}");
        sb.AppendLine($"Instrument         : {Instrument}");
        sb.AppendLine($"Telescope          : {Telescope}");
        if (DateTimeTicks > 0)
            sb.AppendLine($"Start Time (Local) : {SerTimestampToDateTime(DateTimeTicks):yyyy-MM-dd HH:mm:ss.fff}");
        if (DateTimeUtcTicks > 0)
            sb.AppendLine($"Start Time (UTC)   : {SerTimestampToDateTime(DateTimeUtcTicks):yyyy-MM-dd HH:mm:ss.fff} UTC");
        sb.AppendLine($"Timestamps Trailer : {(HasTrailerTimestamps ? "Present (64-bit UTC per frame)" : "Not present")}");
        return sb.ToString();
    }

    public static DateTime SerTimestampToDateTime(long serSeptaseconds)
    {
        if (serSeptaseconds <= 0 || serSeptaseconds > DateTime.MaxValue.Ticks)
            return DateTime.MinValue;
        return new DateTime(serSeptaseconds, DateTimeKind.Utc);
    }
}

/// <summary>
/// High-performance zero-copy SER v3 reader using MemoryMappedFile and direct unsafe pointer access.
/// </summary>
public sealed unsafe class SerFile : IDisposable
{
    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _basePtr;
    private bool _disposed;
    public string FilePath { get; }
    public SerHeader Header { get; }
    private readonly long[] _frameTimestamps;

    public SerFile(string filePath)
    {
        FilePath = filePath;
        _fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (_fileStream.Length < SerHeader.HeaderSizeBytes)
        {
            throw new InvalidDataException($"File '{filePath}' is too short to contain SER header.");
        }

        Span<byte> hdr = stackalloc byte[SerHeader.HeaderSizeBytes];
        _fileStream.ReadExactly(hdr);

        Header = ParseHeader(hdr, _fileStream.Length);
        _frameTimestamps = new long[Header.FrameCount];

        if (Header.HasTrailerTimestamps && Header.FrameCount > 0)
        {
            long trailerOffset = SerHeader.HeaderSizeBytes + Header.BytesPerFrame * Header.FrameCount;
            _fileStream.Seek(trailerOffset, SeekOrigin.Begin);
            byte[] tsBytes = new byte[Header.FrameCount * 8];
            _fileStream.ReadExactly(tsBytes);
            for (int i = 0; i < Header.FrameCount; i++)
            {
                _frameTimestamps[i] = BinaryPrimitives.ReadInt64LittleEndian(tsBytes.AsSpan(i * 8, 8));
            }
        }

        _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _accessor = _mmf.CreateViewAccessor(0, _fileStream.Length, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _basePtr = ptr + _accessor.PointerOffset;
    }

    public static SerHeader ParseHeader(ReadOnlySpan<byte> hdr, long totalFileLength)
    {
        string fileId = Encoding.ASCII.GetString(hdr[..14]).TrimEnd('\0', ' ');
        int luId = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(14, 4));
        int colorId = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(18, 4));
        int littleEndian = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(22, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(26, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(30, 4));
        int depth = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(34, 4));
        int frameCount = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(38, 4));
        string observer = Encoding.ASCII.GetString(hdr.Slice(42, 40)).TrimEnd('\0', ' ');
        string instrument = Encoding.ASCII.GetString(hdr.Slice(82, 40)).TrimEnd('\0', ' ');
        string telescope = Encoding.ASCII.GetString(hdr.Slice(122, 40)).TrimEnd('\0', ' ');
        long dtLocal = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(162, 8));
        long dtUtc = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(170, 8));

        var result = new SerHeader
        {
            FileId = fileId,
            LuId = luId,
            ColorId = (SerColorId)colorId,
            LittleEndian = littleEndian,
            ImageWidth = width,
            ImageHeight = height,
            PixelDepthPerPlane = depth,
            FrameCount = frameCount,
            Observer = observer,
            Instrument = instrument,
            Telescope = telescope,
            DateTimeTicks = dtLocal,
            DateTimeUtcTicks = dtUtc
        };

        long expectedDataEnd = SerHeader.HeaderSizeBytes + result.BytesPerFrame * frameCount;
        long expectedTrailerEnd = expectedDataEnd + (long)frameCount * 8;
        result.HasTrailerTimestamps = frameCount > 0 && totalFileLength >= expectedTrailerEnd;

        return result;
    }

    public AstroFrame ReadFrame(int frameIndex, int serBitDepthOverride = 0)
    {
        if (frameIndex < 0 || frameIndex >= Header.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        int effectiveBitDepth = serBitDepthOverride is >= 8 and <= 16 ? serBitDepthOverride : Header.PixelDepthPerPlane;
        int channels = Header.Planes;
        var frame = new AstroFrame(Header.ImageWidth, Header.ImageHeight, channels, effectiveBitDepth)
        {
            FrameIndex = frameIndex,
            IsBayerRaw = Header.IsBayer,
            BayerPattern = Header.ToBayerPattern(),
            TimestampUtc = Header.HasTrailerTimestamps && _frameTimestamps[frameIndex] > 0
                ? SerHeader.SerTimestampToDateTime(_frameTimestamps[frameIndex])
                : null
        };

        long offset = SerHeader.HeaderSizeBytes + frameIndex * Header.BytesPerFrame;
        int sampleCount = Header.ImageWidth * Header.ImageHeight * channels;
        byte* srcPtr = _basePtr + offset;

        fixed (ushort* pDst = frame.Data)
        {
            if (Header.BytesPerPixelPlane == 1)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    pDst[i] = (ushort)(srcPtr[i] * 257); // Scale 0..255 -> 0..65535
                }
            }
            else
            {
                int shift = Math.Clamp(16 - effectiveBitDepth, 0, 8);
                ushort* src16 = (ushort*)srcPtr;
                for (int i = 0; i < sampleCount; i++)
                {
                    ushort val = src16[i];
                    pDst[i] = (ushort)Math.Min(65535, val << shift);
                }
            }

            // Convert BGR to RGB if ColorId == Bgr
            if (Header.ColorId == SerColorId.Bgr)
            {
                for (int i = 0; i < sampleCount; i += 3)
                {
                    (pDst[i], pDst[i + 2]) = (pDst[i + 2], pDst[i]);
                }
            }
        }

        return frame;
    }

    /// <summary>
    /// Lit directement une sous-fenêtre recadrée (cropWidth × cropHeight) centrée sur (centerX, centerY)
    /// depuis la vue MemoryMappedFile, en préservant la phase de la matrice de Bayer (pas pairs).
    /// Évite de lire et d'allouer toute la trame plein capteur avant le recadrage en Passe 2.
    /// </summary>
    public AstroFrame ReadFrameCropped(
        int frameIndex,
        int centerX,
        int centerY,
        int cropWidth,
        int cropHeight,
        int offsetX = 0,
        int offsetY = 0,
        int serBitDepthOverride = 0)
    {
        if (frameIndex < 0 || frameIndex >= Header.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        cropWidth = Math.Max(16, cropWidth);
        cropHeight = Math.Max(16, cropHeight);

        int startX = centerX - (cropWidth / 2) - offsetX;
        int startY = centerY - (cropHeight / 2) - offsetY;

        if (Header.IsBayer)
        {
            startX &= ~1;
            startY &= ~1;
        }

        int effectiveBitDepth = serBitDepthOverride is >= 8 and <= 16 ? serBitDepthOverride : Header.PixelDepthPerPlane;
        int channels = Header.Planes;
        int srcW = Header.ImageWidth;
        int srcH = Header.ImageHeight;

        var frame = new AstroFrame(cropWidth, cropHeight, channels, effectiveBitDepth)
        {
            FrameIndex = frameIndex,
            IsBayerRaw = Header.IsBayer,
            BayerPattern = Header.ToBayerPattern(),
            DetectedCentroidX = cropWidth / 2,
            DetectedCentroidY = cropHeight / 2,
            TimestampUtc = Header.HasTrailerTimestamps && _frameTimestamps[frameIndex] > 0
                ? SerHeader.SerTimestampToDateTime(_frameTimestamps[frameIndex])
                : null
        };

        long offset = SerHeader.HeaderSizeBytes + frameIndex * Header.BytesPerFrame;
        byte* srcPtr = _basePtr + offset;
        bool is8Bit = Header.BytesPerPixelPlane == 1;
        int shift = is8Bit ? 0 : Math.Clamp(16 - effectiveBitDepth, 0, 8);
        bool isBgr = Header.ColorId == SerColorId.Bgr && channels == 3;

        fixed (ushort* pDst = frame.Data)
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

                int srcRowIdx = (sy * srcW + srcXStart) * channels;
                int dstRowIdx = (y * cropWidth + dstXStart) * channels;
                int count = copyPixels * channels;

                if (is8Bit)
                {
                    byte* sRow = srcPtr + srcRowIdx;
                    ushort* dRow = pDst + dstRowIdx;
                    for (int i = 0; i < count; i++)
                    {
                        dRow[i] = (ushort)(sRow[i] * 257);
                    }
                }
                else
                {
                    ushort* sRow = (ushort*)srcPtr + srcRowIdx;
                    ushort* dRow = pDst + dstRowIdx;
                    for (int i = 0; i < count; i++)
                    {
                        ushort val = sRow[i];
                        dRow[i] = (ushort)Math.Min(65535, val << shift);
                    }
                }

                if (isBgr)
                {
                    ushort* dRow = pDst + dstRowIdx;
                    for (int i = 0; i < count; i += 3)
                    {
                        (dRow[i], dRow[i + 2]) = (dRow[i + 2], dRow[i]);
                    }
                }
            }
        }

        return frame;
    }

    /// <summary>
    /// Analyse ultra-rapide Passe 1 (0 allocation Heap/LOH, 0 copie mémoire, 0 débayérisation complète)
    /// directement sur la vue MemoryMappedFile.
    /// Évalue le centroïde de l'objet et la netteté locale dans la boîte de recadrage (ROI).
    /// </summary>
    public (bool Accepted, int CentroidX, int CentroidY, double QualityScore) AnalyzeFrameZeroAlloc(
        int frameIndex,
        ProcessingOptions options,
        int serBitDepthOverride = 0)
    {
        if (frameIndex < 0 || frameIndex >= Header.FrameCount)
        {
            return (false, 0, 0, 0.0);
        }

        int w = Header.ImageWidth;
        int h = Header.ImageHeight;
        if (w < 8 || h < 8)
        {
            return (false, w / 2, h / 2, 0.0);
        }

        long offset = SerHeader.HeaderSizeBytes + frameIndex * Header.BytesPerFrame;
        byte* pFrame = _basePtr + offset;
        int planes = Header.Planes;
        bool is8Bit = Header.BytesPerPixelPlane == 1;
        int effectiveBitDepth = serBitDepthOverride is >= 8 and <= 16 ? serBitDepthOverride : Header.PixelDepthPerPlane;
        int shift16 = is8Bit ? 0 : Math.Clamp(16 - effectiveBitDepth, 0, 8);

        int cx = w / 2;
        int cy = h / 2;
        bool checkObject = options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne &&
                           options.CheckPlanetObjectPresent &&
                           options.StabilisationMode == StabilisationMode.ObjectPlanetary;
        bool centreObject = options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne &&
                            options.CentreObjectInFrame &&
                            options.StabilisationMode == StabilisationMode.ObjectPlanetary;

        if (checkObject || centreObject)
        {
            int detectStep = (w > 640 || h > 640) ? 4 : 2;
            int maxVal8 = 0;
            int skyLevel = 0;

            if (options.AutoDetectionThreshold)
            {
                if (w >= 16 && h >= 16)
                {
                    Span<int> edgeSamples = stackalloc int[32];
                    int sIdx = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        int sx = (w * (i + 1)) / 9;
                        int sy = (h * (i + 1)) / 9;
                        edgeSamples[sIdx++] = ReadPixel8Bit(pFrame, (4 * w + sx) * planes, planes, is8Bit, shift16);
                        edgeSamples[sIdx++] = ReadPixel8Bit(pFrame, ((h - 5) * w + sx) * planes, planes, is8Bit, shift16);
                        edgeSamples[sIdx++] = ReadPixel8Bit(pFrame, (sy * w + 4) * planes, planes, is8Bit, shift16);
                        edgeSamples[sIdx++] = ReadPixel8Bit(pFrame, (sy * w + (w - 5)) * planes, planes, is8Bit, shift16);
                    }
                    edgeSamples.Sort();
                    skyLevel = edgeSamples[16];
                }

                int sampleStep = Math.Max(4, detectStep * 2);
                for (int y = 0; y < h; y += sampleStep)
                {
                    int row = y * w * planes;
                    for (int x = 0; x < w; x += sampleStep)
                    {
                        int v8 = ReadPixel8Bit(pFrame, row + x * planes, planes, is8Bit, shift16);
                        if (v8 > maxVal8) maxVal8 = v8;
                    }
                }
            }

            int thresh8;
            if (!options.AutoDetectionThreshold)
            {
                thresh8 = options.ManualDetectionThreshold;
            }
            else if (skyLevel <= 4)
            {
                thresh8 = Math.Clamp((int)(maxVal8 * 0.14), 8, 180);
            }
            else
            {
                int dynamicRange = maxVal8 - skyLevel;
                thresh8 = dynamicRange <= 0
                    ? Math.Clamp(skyLevel + 8, 8, 180)
                    : Math.Clamp(skyLevel + Math.Max(8, (int)(dynamicRange * 0.14)), 8, 220);
            }

            Span<int> rowCounts = h <= 4096 ? stackalloc int[h] : new int[h];
            Span<int> colCounts = w <= 4096 ? stackalloc int[w] : new int[w];
            rowCounts.Clear();
            colCounts.Clear();

            long sumX = 0, sumY = 0, totalWeight = 0;
            int detectedPixels = 0;
            int stepArea = detectStep * detectStep;
            int minX = w, maxX = 0, minY = h, maxY = 0;

            for (int y = 0; y < h; y += detectStep)
            {
                int row = y * w * planes;
                for (int x = 0; x < w; x += detectStep)
                {
                    int v8 = ReadPixel8Bit(pFrame, row + x * planes, planes, is8Bit, shift16);
                    if (v8 >= thresh8)
                    {
                        int weight = v8 - thresh8 + 1;
                        sumX += (long)x * weight;
                        sumY += (long)y * weight;
                        totalWeight += weight;
                        detectedPixels += stepArea;
                        rowCounts[y]++;
                        colCounts[x]++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            int minArea = (int)(Math.PI * (options.MinimumObjectSizePixels * 0.5) * (options.MinimumObjectSizePixels * 0.5));
            if (checkObject && detectedPixels < Math.Max(4, minArea / 2))
            {
                return (false, w / 2, h / 2, 0.0);
            }

            int baryX = totalWeight > 0 ? (int)(sumX / totalWeight) : w / 2;
            int baryY = totalWeight > 0 ? (int)(sumY / totalWeight) : h / 2;

            int estRadius = Math.Max(options.MinimumObjectSizePixels / 2, (int)Math.Sqrt(detectedPixels / Math.PI));
            int searchRadius = (int)(estRadius * 1.45) + 8;
            int lastStepY = ((h - 1) / detectStep) * detectStep;
            int lastStepX = ((w - 1) / detectStep) * detectStep;

            bool spansWholeScene = detectedPixels > (w * h / 3) && ((maxX - minX) > (w * 3 / 4) || (maxY - minY) > (h * 3 / 4));
            int searchMinX = spansWholeScene ? 0 : Math.Max(0, ((baryX - searchRadius) / detectStep) * detectStep);
            int searchMaxX = spansWholeScene ? lastStepX : Math.Min(lastStepX, ((baryX + searchRadius) / detectStep) * detectStep);
            int searchMinY = spansWholeScene ? 0 : Math.Max(0, ((baryY - searchRadius) / detectStep) * detectStep);
            int searchMaxY = spansWholeScene ? lastStepY : Math.Min(lastStepY, ((baryY + searchRadius) / detectStep) * detectStep);

            int minChord = Math.Clamp(options.MinimumObjectSizePixels / (detectStep * 4), 2, 16);
            int cleanMinY = searchMinY; while (cleanMinY <= searchMaxY && rowCounts[cleanMinY] < minChord) cleanMinY += detectStep;
            int cleanMaxY = searchMaxY; while (cleanMaxY >= searchMinY && rowCounts[cleanMaxY] < minChord) cleanMaxY -= detectStep;
            int cleanMinX = searchMinX; while (cleanMinX <= searchMaxX && colCounts[cleanMinX] < minChord) cleanMinX += detectStep;
            int cleanMaxX = searchMaxX; while (cleanMaxX >= searchMinX && colCounts[cleanMaxX] < minChord) cleanMaxX -= detectStep;

            int finalMinX = (cleanMinX <= cleanMaxX) ? cleanMinX : Math.Max(minX, searchMinX);
            int finalMaxX = (cleanMinX <= cleanMaxX) ? cleanMaxX : Math.Min(maxX, searchMaxX);
            int finalMinY = (cleanMinY <= cleanMaxY) ? cleanMinY : Math.Max(minY, searchMinY);
            int finalMaxY = (cleanMinY <= cleanMaxY) ? cleanMinY : Math.Min(maxY, searchMaxY);

            int geomCenterX = (finalMinX + finalMaxX) / 2;
            int geomCenterY = (finalMinY + finalMaxY) / 2;

            cx = (Math.Abs(geomCenterX - baryX) <= estRadius / 2) ? geomCenterX : baryX;
            cy = (Math.Abs(geomCenterY - baryY) <= estRadius / 2) ? geomCenterY : baryY;

            // Détermination de la ROI d'évaluation de qualité resserrée autour de la planète isolée
            // pour éviter que le bruit de fond de ciel (95% des pixels en 640x480) ne domine le score de netteté.
            int planetSpan = Math.Max(finalMaxX - finalMinX, finalMaxY - finalMinY);
            if (!spansWholeScene && planetSpan > 8)
            {
                int boxSize = Math.Clamp(planetSpan + 32, 48, Math.Min(w, h));
                int qx0 = Math.Clamp(cx - boxSize / 2, 0, Math.Max(0, w - boxSize));
                int qy0 = Math.Clamp(cy - boxSize / 2, 0, Math.Max(0, h - boxSize));
                int qx1 = Math.Min(w, qx0 + boxSize);
                int qy1 = Math.Min(h, qy0 + boxSize);
                int noiseFloor8 = Math.Max(skyLevel + 3, thresh8 / 2);

                double planetScore = ComputeRoiQualityZeroAlloc(
                    pFrame,
                    w,
                    planes,
                    is8Bit,
                    shift16,
                    Header.IsBayer,
                    qx0,
                    qy0,
                    qx1,
                    qy1,
                    options.QualityAlgorithm,
                    options.Stacking.NoiseRobust,
                    noiseFloor8);

                return (true, cx, cy, planetScore);
            }
        }

        // Détermination de la ROI d'évaluation de qualité (zone recadrée autour de la planète ou image entière)
        int roiX0 = 0, roiY0 = 0, roiX1 = w, roiY1 = h;
        if (options.Stacking.WorkflowMode == StackingWorkflowMode.DirectStreamAllInOne && options.EnableCropping)
        {
            int cropW = Math.Clamp(options.CropWidth, 16, w);
            int cropH = Math.Clamp(options.CropHeight, 16, h);
            int targetX = (centreObject ? cx : w / 2) - options.CropOffsetX;
            int targetY = (centreObject ? cy : h / 2) - options.CropOffsetY;

            roiX0 = Math.Clamp(targetX - cropW / 2, 0, Math.Max(0, w - cropW));
            roiY0 = Math.Clamp(targetY - cropH / 2, 0, Math.Max(0, h - cropH));
            roiX1 = Math.Min(w, roiX0 + cropW);
            roiY1 = Math.Min(h, roiY0 + cropH);
        }

        int roiW = roiX1 - roiX0;
        int roiH = roiY1 - roiY0;
        if (roiW < 8 || roiH < 8)
        {
            return (true, cx, cy, 0.0);
        }

        // Calcul du score de qualité directement sur le pointeur de la ROI
        double score = ComputeRoiQualityZeroAlloc(
            pFrame,
            w,
            planes,
            is8Bit,
            shift16,
            Header.IsBayer,
            roiX0,
            roiY0,
            roiX1,
            roiY1,
            options.QualityAlgorithm,
            options.Stacking.NoiseRobust,
            noiseFloor8: 0);

        return (true, cx, cy, score);
    }

    private static int ReadPixel8Bit(byte* pFrame, int idx, int planes, bool is8Bit, int shift16)
    {
        if (is8Bit)
        {
            if (planes == 1) return pFrame[idx];
            return Math.Max(pFrame[idx], Math.Max(pFrame[idx + 1], pFrame[idx + 2]));
        }
        else
        {
            ushort* p16 = (ushort*)pFrame;
            if (planes == 1) return Math.Min(65535, p16[idx] << shift16) >> 8;
            int r = Math.Min(65535, p16[idx] << shift16) >> 8;
            int g = Math.Min(65535, p16[idx + 1] << shift16) >> 8;
            int b = Math.Min(65535, p16[idx + 2] << shift16) >> 8;
            return Math.Max(r, Math.Max(g, b));
        }
    }

    private static double ComputeRoiQualityZeroAlloc(
        byte* pFrame,
        int strideWidth,
        int planes,
        bool is8Bit,
        int shift16,
        bool isBayer,
        int x0,
        int y0,
        int x1,
        int y1,
        QualityAlgorithm algorithm,
        int noiseRobust,
        int noiseFloor8 = 0)
    {
        int roiW = x1 - x0;
        int roiH = y1 - y0;

        if (algorithm == QualityAlgorithm.TotalBrightness)
        {
            long sum = 0;
            for (int y = y0; y < y1; y += 2)
            {
                int row = y * strideWidth * planes;
                for (int x = x0; x < x1; x += 2)
                {
                    sum += ReadPixel8Bit(pFrame, row + x * planes, planes, is8Bit, shift16) * 257;
                }
            }
            return (double)sum / ((roiW / 2) * (roiH / 2));
        }

        if (algorithm == QualityAlgorithm.PeakHistogram)
        {
            Span<int> hist = stackalloc int[256];
            for (int y = y0; y < y1; y += 2)
            {
                int row = y * strideWidth * planes;
                for (int x = x0; x < x1; x += 2)
                {
                    int v8 = ReadPixel8Bit(pFrame, row + x * planes, planes, is8Bit, shift16);
                    hist[v8]++;
                }
            }
            double weightedHigh = 0;
            for (int i = 16; i < 256; i++)
            {
                weightedHigh += hist[i] * (i * i);
            }
            return weightedHigh / (roiW * roiH);
        }

        // GradientSharpness & OriginalNinox :
        // Sur matrice de Bayer brute (1 canal), on force des pas pairs (2, 4, 6) pour comparer uniquement
        // les pixels de même filtre couleur (G1-G1, G2-G2, R-R, B-B) sans jamais avoir à débayériser !
        int robustOffset = Math.Clamp(noiseRobust, 1, 8) - 2;
        int minSub = Math.Max(2, 3 + robustOffset);
        int maxSub = Math.Max(minSub, 5 + robustOffset * 2);

        double totalGrad = 0;

        if (is8Bit && planes == 1)
        {
            int stepInc = isBayer ? 2 : 1;
            int startStep = isBayer ? ((minSub + 1) & ~1) : minSub;
            int endStep = isBayer ? ((maxSub + 1) & ~1) : maxSub;
            if (endStep < startStep) endStep = startStep;

            for (int step = startStep; step <= endStep; step += stepInc)
            {
                long stepGrad = 0;
                int limitY = y1 - step;
                int limitX = x1 - step;

                for (int y = y0; y < limitY; y += step)
                {
                    byte* pRow = pFrame + y * strideWidth;
                    byte* pDown = pRow + step * strideWidth;
                    for (int x = x0; x < limitX; x += step)
                    {
                        int c = pRow[x];
                        if (c < noiseFloor8) continue;

                        int right = pRow[x + step];
                        int down = pDown[x];

                        int dx = c - right;
                        int dy = c - down;
                        if (dx < 0) dx = -dx;
                        if (dy < 0) dy = -dy;
                        stepGrad += dx + dy;
                    }
                }
                totalGrad += stepGrad * 257.0;
            }
        }
        else if (is8Bit && planes == 3)
        {
            for (int step = minSub; step <= maxSub; step++)
            {
                long stepGrad = 0;
                int limitY = y1 - step;
                int limitX = x1 - step;

                for (int y = y0; y < limitY; y += step)
                {
                    byte* pRow = pFrame + y * strideWidth * 3;
                    byte* pDown = pRow + step * strideWidth * 3;
                    for (int x = x0; x < limitX; x += step)
                    {
                        int c = pRow[x * 3 + 1]; // Green channel
                        if (c < noiseFloor8) continue;

                        int right = pRow[(x + step) * 3 + 1];
                        int down = pDown[x * 3 + 1];

                        int dx = c - right;
                        int dy = c - down;
                        if (dx < 0) dx = -dx;
                        if (dy < 0) dy = -dy;
                        stepGrad += dx + dy;
                    }
                }
                totalGrad += stepGrad * 257.0;
            }
        }
        else
        {
            ushort* p16 = (ushort*)pFrame;
            int stepInc = isBayer ? 2 : 1;
            int startStep = isBayer ? ((minSub + 1) & ~1) : minSub;
            int endStep = isBayer ? ((maxSub + 1) & ~1) : maxSub;
            if (endStep < startStep) endStep = startStep;
            int noiseFloor16 = noiseFloor8 * 257;

            for (int step = startStep; step <= endStep; step += stepInc)
            {
                long stepGrad = 0;
                int limitY = y1 - step;
                int limitX = x1 - step;

                for (int y = y0; y < limitY; y += step)
                {
                    int rowOffset = y * strideWidth * planes;
                    int downOffset = (y + step) * strideWidth * planes;
                    for (int x = x0; x < limitX; x += step)
                    {
                        int chOffset = planes == 3 ? 1 : 0;
                        int c = Math.Min(65535, p16[rowOffset + x * planes + chOffset] << shift16);
                        if (c < noiseFloor16) continue;

                        int right = Math.Min(65535, p16[rowOffset + (x + step) * planes + chOffset] << shift16);
                        int down = Math.Min(65535, p16[downOffset + x * planes + chOffset] << shift16);

                        int dx = c - right;
                        int dy = c - down;
                        if (dx < 0) dx = -dx;
                        if (dy < 0) dy = -dy;
                        stepGrad += dx + dy;
                    }
                }
                totalGrad += stepGrad;
            }
        }

        return totalGrad / (roiW * roiH);
    }

    public long GetRawTimestamp(int frameIndex) =>
        (frameIndex >= 0 && frameIndex < _frameTimestamps.Length) ? _frameTimestamps[frameIndex] : 0;

    public static void WriteSerFile(string outputPath, SerHeader templateHeader, IReadOnlyList<AstroFrame> frames)
    {
        if (frames.Count == 0) return;

        using var writer = new SerStreamWriter(
            outputPath,
            frames[0].Width,
            frames[0].Height,
            frames[0].Channels,
            templateHeader.PixelDepthPerPlane,
            frames[0].IsBayerRaw,
            frames[0].BayerPattern,
            templateHeader.Observer,
            templateHeader.Instrument,
            templateHeader.Telescope,
            templateHeader.DateTimeTicks,
            templateHeader.DateTimeUtcTicks);

        for (int f = 0; f < frames.Count; f++)
        {
            writer.WriteFrame(frames[f]);
        }
        writer.Finish();
    }

    public static void WriteFixedAscii(Span<byte> dest, string text)
    {
        dest.Clear();
        if (string.IsNullOrEmpty(text)) return;
        int len = Math.Min(dest.Length, text.Length);
        Encoding.ASCII.GetBytes(text.AsSpan(0, len), dest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_basePtr != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePtr = null;
        }
        _accessor.Dispose();
        _mmf.Dispose();
        _fileStream.Dispose();
    }
}

/// <summary>
/// Streaming SER v3 file writer that writes frames sequentially to disk using a single reused buffer
/// and patches the final FrameCount and optional UTC timestamp trailer upon Finish().
/// </summary>
public sealed class SerStreamWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly int _pixelDepth;
    private readonly byte[] _frameBuffer;
    private readonly List<long> _timestamps = [];
    private bool _hasAnyTimestamps;
    private int _framesWritten;
    private bool _finished;

    public SerStreamWriter(
        string outputPath,
        int width,
        int height,
        int channels,
        int pixelDepthPerPlane = 8,
        bool isBayerRaw = false,
        BayerPattern bayerPattern = BayerPattern.Rggb,
        string observer = "cassiniflow",
        string instrument = "Camera",
        string telescope = "Telescope",
        long dateTimeTicks = 0,
        long dateTimeUtcTicks = 0)
    {
        _width = width;
        _height = height;
        _channels = channels;
        _pixelDepth = pixelDepthPerPlane <= 8 ? 8 : 16;

        int bytesPerSample = _pixelDepth <= 8 ? 1 : 2;
        _frameBuffer = new byte[width * height * channels * bytesPerSample];

        _fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 262144);

        SerColorId outColorId = channels == 3
            ? SerColorId.Rgb
            : isBayerRaw
                ? bayerPattern switch
                {
                    BayerPattern.Gbrg => SerColorId.BayerGbrg,
                    BayerPattern.Bggr => SerColorId.BayerBggr,
                    BayerPattern.Grbg => SerColorId.BayerGrbg,
                    _ => SerColorId.BayerRggb
                }
                : SerColorId.Mono;

        Span<byte> hdr = stackalloc byte[SerHeader.HeaderSizeBytes];
        hdr.Clear();
        SerFile.WriteFixedAscii(hdr[..14], SerHeader.MagicSignature);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(14, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(18, 4), (int)outColorId);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(22, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(26, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(30, 4), height);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(34, 4), _pixelDepth);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(38, 4), 0); // Patched on Finish()
        SerFile.WriteFixedAscii(hdr.Slice(42, 40), observer);
        SerFile.WriteFixedAscii(hdr.Slice(82, 40), instrument);
        SerFile.WriteFixedAscii(hdr.Slice(122, 40), telescope);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(162, 8), dateTimeTicks);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(170, 8), dateTimeUtcTicks);

        _fs.Write(hdr);
    }

    public unsafe void WriteFrame(AstroFrame frame)
    {
        if (frame.TimestampUtc.HasValue)
        {
            _hasAnyTimestamps = true;
            _timestamps.Add(frame.TimestampUtc.Value.ToUniversalTime().Ticks);
        }
        else
        {
            _timestamps.Add(0);
        }

        int count = Math.Min(frame.Data.Length, _width * _height * _channels);
        fixed (ushort* pSrc = frame.Data)
        fixed (byte* pDst = _frameBuffer)
        {
            if (_pixelDepth <= 8)
            {
                for (int i = 0; i < count; i++)
                {
                    pDst[i] = (byte)(pSrc[i] >> 8);
                }
            }
            else
            {
                Buffer.MemoryCopy(pSrc, pDst, _frameBuffer.Length, count * sizeof(ushort));
            }
        }

        _fs.Write(_frameBuffer);
        _framesWritten++;
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        if (_hasAnyTimestamps && _framesWritten > 0)
        {
            byte[] tsBuf = new byte[_framesWritten * 8];
            for (int i = 0; i < _framesWritten; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(tsBuf.AsSpan(i * 8, 8), _timestamps[i]);
            }
            _fs.Write(tsBuf);
        }

        // Patch FrameCount at offset 38 in SER header
        Span<byte> fcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(fcBytes, _framesWritten);
        _fs.Seek(38, SeekOrigin.Begin);
        _fs.Write(fcBytes);
        _fs.Flush();
    }

    public void Dispose()
    {
        Finish();
        _fs.Dispose();
    }
}
