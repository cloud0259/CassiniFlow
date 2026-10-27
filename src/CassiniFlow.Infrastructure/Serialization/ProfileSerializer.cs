using System.Globalization;
using System.Text;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Outbound;

namespace CassiniFlow.Infrastructure.Serialization;

/// <summary>
/// Reads and writes legacy CassiniFlow Options Files (.pof) with "[CassiniFlow OPTIONS FILE]" header,
/// as well as generating CLI argument lists compatible with CassiniFlow_core.exe.
/// </summary>
public sealed class ProfileSerializer : IProfileSerializer
{
    public const string Header = "[CassiniFlow OPTIONS FILE]";

    string IProfileSerializer.SerializeToPof(ProcessingOptions options) => SerializeToPof(options);
    ProcessingOptions IProfileSerializer.ParseFromPof(string pofText) => ParseFromPof(pofText);
    List<string> IProfileSerializer.GenerateCommandLineArguments(ProcessingOptions options) => GenerateCommandLineArguments(options);

    public static string SerializeToPof(ProcessingOptions o)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);

        void WriteBool(string key, bool val) => sb.AppendLine($"{key} {(val ? 1 : 0)}");
        void WriteInt(string key, int val) => sb.AppendLine($"{key} {val.ToString(CultureInfo.InvariantCulture)}");
        void WriteDec(string key, decimal val) => sb.AppendLine($"{key} {val.ToString("0.00", CultureInfo.InvariantCulture)}");
        void WriteStr(string key, string val) => sb.AppendLine($"{key} {val}");

        WriteBool("BATCH_MODE", o.RunMode == ProcessingRunMode.BatchMode);
        WriteBool("JOIN_MODE", o.RunMode == ProcessingRunMode.JoinMode);
        WriteBool("PLANETARY_MODE", o.ActivePreset == OptimisationPreset.Planetary);
        WriteBool("FULLDISC_MODE", o.ActivePreset == OptimisationPreset.SolarLunarFullDisc);
        WriteBool("CLOSEUP_MODE", o.ActivePreset == OptimisationPreset.SolarLunarCloseUp);
        WriteBool("PLANETARY_ANNIMATION_AVI_MODE", o.ActivePreset == OptimisationPreset.PlanetaryAnimationAvi);
        WriteBool("PLANETARY_ANNIMATION_GIF_MODE", o.ActivePreset == OptimisationPreset.PlanetaryAnimationGif);
        WriteBool("ISS_MODE", o.ActivePreset == OptimisationPreset.Iss);
        WriteBool("AVI_ARCHIVE_MODE", o.ActivePreset == OptimisationPreset.AviArchive);

        WriteBool("FRAME_LIMIT", o.LimitFrameRange);
        WriteInt("FRAME_RANGE_TYPE", (int)o.FrameRangeMode);
        WriteInt("START_FRAME_VALUE", o.StartFrame);
        WriteInt("END_FRAME_VALUE", o.EndFrame);
        WriteInt("NUM_FRAMES_VALUE", o.NumFramesFromCentre);

        WriteBool("DARK_FRAME_SUBTRACT", o.DarkFrameSubtract);
        WriteBool("SAVE_MASTER_DARK", o.SaveMasterDark);
        WriteBool("REPLACE_HOT_PIXELS", o.ReplaceHotPixels);
        WriteInt("HOT_PIXEL_THRESHOLD", o.HotPixelThreshold);
        WriteBool("DARK_DATA_HAS_BAYER", o.DarkDataHasBayer);

        WriteBool("FLAT_FRAME_CALIBRATE", o.FlatFrameCalibrate);
        WriteBool("SAVE_MASTER_FLAT", o.SaveMasterFlat);
        WriteBool("FLAT_DATA_HAS_BAYER", o.FlatDataHasBayer);
        WriteBool("FLAT_DARK_FRAME_SUBTRACT", o.FlatDarkFrameSubtract);
        WriteBool("SAVE_MASTER_FLAT_DARK", o.SaveMasterFlatDark);

        WriteInt("BINNING", (int)o.Binning);
        WriteInt("BIN_CALC", (int)o.BinCalculation);

        WriteBool("DEBAYER_ENABLED", o.DebayerEnabled);
        WriteBool("PROTECT_BAYER", o.ProtectBayerPattern);
        WriteInt("DEBAYER_TYPE", (int)o.DebayerType);
        WriteInt("DEBAYER_PATTERN", (int)o.DebayerPattern);

        WriteBool("RAW_HPF", o.RawHotPixelFilter);
        WriteBool("RAW_DEBAYER", o.RawDebayerEnabled);
        WriteInt("RAW_DEBAYER_ALGORITHM", (int)o.RawDebayerAlgorithm);
        WriteInt("RAW_COLOUR_SPACE", (int)o.RawOutputColourSpace);
        WriteInt("RAW_HIGHLIGHTS", o.RawHighlightRecovery);

        WriteBool("TRUE_Y800", o.TrueY800DecodeStrict);
        WriteInt("SER_BIT_DEPTH", o.SerBitDepthOverride);
        WriteInt("INPUT_FRAME_COLOUR", (int)o.InputFrameColour);
        WriteInt("IP_FRAME_DROP_VALUE", o.InputFrameDrop);

        WriteBool("DATE_TIME_EXTRACT", o.ExtractDateTimeFromFilename);
        WriteBool("WJUPOS_CONV_UT", o.WinJuposConvertLocalToUt);
        WriteInt("WJUPOS_DATETIME_FORMAT", (int)o.WinJuposDateTimeFormat);
        WriteStr("WJUPOS_DATETIME_TEXT", o.WinJuposCustomMask);
        WriteDec("WJUPOS_INPUT_FPS", o.WinJuposInputFps);

        WriteDec("INPUT_GAIN", o.InputGain);
        WriteDec("GAMMA", o.Gamma);
        WriteBool("MEDIAN_FILTER", o.EnableMedianNoiseFilter);
        WriteBool("CHECK_PLANET", o.CheckPlanetObjectPresent);
        WriteInt("MIN_PLANET_SIZE", o.MinimumObjectSizePixels);
        WriteInt("EDGE_IN_SHADOW", (int)o.EdgeInShadow);
        WriteBool("CENTRE_PLANET", o.CentreObjectInFrame);
        WriteBool("AUTO_THRESHOLD", o.AutoDetectionThreshold);
        WriteInt("THRESHOLD", o.ManualDetectionThreshold);
        WriteBool("OVEREXPOSED", o.RejectOverexposedFrames);
        WriteInt("OVEREXPOSED_VALUE", o.MaxOverexposedPixels);

        WriteBool("ENABLE_CROP", o.EnableCropping);
        WriteInt("CROP_WIDTH", o.CropWidth);
        WriteInt("CROP_HEIGHT", o.CropHeight);
        WriteInt("X_OFFSET", o.CropOffsetX);
        WriteInt("Y_OFFSET", o.CropOffsetY);

        WriteBool("HISTO_STRETCH", o.EnableHistogramStretch);
        WriteInt("HISTO_STRETCH_VALUE", o.HistogramStretchWhitePointPercent);
        WriteBool("HISTO_STRETCH_NO_BP", !o.HistogramStretchBlackPointZero);
        WriteBool("RGB_STRETCH", o.HistogramStretchRgbIndividually);

        WriteBool("STABILISE_NONE_MODE", o.StabilisationMode == StabilisationMode.None);
        WriteBool("STABILISE_OBJECT_MODE", o.StabilisationMode == StabilisationMode.ObjectPlanetary);
        WriteBool("STABILISE_SURFACE_MODE", o.StabilisationMode == StabilisationMode.SurfaceFeature);
        WriteBool("SURFACE_FEATURE_TRACKING_EN", o.EnableSurfaceFeatureTracking);
        WriteBool("STABILISE_EN", o.EnableSurfaceStabilisation);
        WriteInt("AFB_X", o.AfbX);
        WriteInt("AFB_Y", o.AfbY);
        WriteInt("AFB_WIDTH", o.AfbWidth);
        WriteInt("AFB_HEIGHT", o.AfbHeight);
        WriteBool("AFB_REJECT", o.AfbRejectFramesWithoutAnchor);

        WriteBool("AOI_ENABLE", o.AoiEnable);
        WriteInt("AOI_X", o.AoiX);
        WriteInt("AOI_Y", o.AoiY);
        WriteInt("AOI_WIDTH", o.AoiWidth);
        WriteInt("AOI_HEIGHT", o.AoiHeight);
        WriteBool("AOI_REJECT", o.AoiRejectFramesWithoutCompleteArea);
        WriteBool("AOI_CROP", o.AoiCropFrames);

        WriteBool("CONV_MONO", o.ConvertToMonochrome);
        WriteInt("CONV_MONO_TYPE", (int)o.MonochromeConversionType);
        WriteBool("FLIP_X", o.FlipHorizontalX);
        WriteBool("FLIP_Y", o.FlipVerticalY);
        WriteInt("ROTATE", (int)o.Rotation);
        WriteBool("RESIZE_FRAMES", o.EnableResizeFrames);
        WriteBool("RESIZE_KEEP_ASPECT_RATIO", o.ResizeKeepAspectRatio);
        WriteInt("RESIZE_WIDTH", o.ResizeWidth);
        WriteInt("RESIZE_HEIGHT", o.ResizeHeight);

        WriteBool("ENABLE_QUALITY", o.EnableQualityEstimation);
        WriteBool("QUALITY_LIMITING", o.EnableQualityLimiting);
        WriteInt("QUALITY__LIMITING_TYPE", (int)o.QualityLimitingMode);
        WriteInt("QUALITY_FRAMES", o.QualityFramesToKeep);
        WriteBool("QUALITY_REORDER", o.QualityReorderFrames);
        WriteBool("DEFAULT_QUALITY", o.QualityAlgorithm == QualityAlgorithm.GradientSharpness);
        WriteBool("NINOX_QUALITY", o.QualityAlgorithm == QualityAlgorithm.OriginalNinox);
        WriteBool("HISTO_QUALITY", o.QualityAlgorithm == QualityAlgorithm.PeakHistogram);
        WriteBool("TOTALPIXEL_QUALITY", o.QualityAlgorithm == QualityAlgorithm.TotalBrightness);
        WriteInt("QMIN", o.QualityMinSubsample);
        WriteInt("QMAX", o.QualityMaxSubsample);
        WriteInt("QINC", o.QualitySubsampleIncrement);
        WriteBool("ENABLE_QWEIGHT", o.EnableQualityWeighting);

        WriteBool("ANIM_FWD_ENABLE", o.AnimationForwardPlay);
        WriteBool("ANIM_REPEAT_FWD_LAST_ENABLE", o.AnimationPauseFinalForwardFrame);
        WriteInt("ANIM_REPEAT_FWD_LAST_VALUE", o.AnimationPauseForwardCount);
        WriteBool("ANIM_REV_ENABLE", o.AnimationReversePlay);
        WriteBool("ANIM_REPEAT_REV_LAST_ENABLE", o.AnimationPauseFinalReverseFrame);
        WriteInt("ANIM_REPEAT_REV_LAST_VALUE", o.AnimationPauseReverseCount);
        WriteBool("ANIM_REPEAT_ALL_ENABLE", o.AnimationRepeatAllSequence);
        WriteInt("ANIM_REPEAT_ALL_VALUE", o.AnimationRepeatAllCount);
        WriteBool("ANIM_REPEAT_REV_DISABLED_LAST", o.AnimationDisableReverseInLastRepeat);

        WriteBool("CREATE_OUTPUT_SUBDIR", o.CreateOutputSubdirectory);
        WriteStr("SUBDIR_NAME", o.OutputSubdirectoryName);
        WriteBool("SUBDIR_APPEND_TIME", o.SubdirectoryAppendDateTime);
        WriteStr("OUTPUT_DIRECTORY", o.CustomOutputDirectory);
        WriteStr("OUTPUT_PREFIX", o.OutputFilenamePrefix);
        WriteStr("OUTPUT_SUFFIX", o.OutputFilenameSuffix);
        WriteBool("OUTPUT_AVI", o.OutputFormat == OutputFormat.Avi);
        WriteBool("OUTPUT_SER", o.OutputFormat == OutputFormat.Ser);
        WriteBool("OUTPUT_BMP", o.OutputFormat == OutputFormat.Bmp);
        WriteBool("OUTPUT_TIF", o.OutputFormat == OutputFormat.Tiff);
        WriteBool("OUTPUT_PNG", o.OutputFormat == OutputFormat.Png);
        WriteBool("OUTPUT_FITS", o.OutputFormat == OutputFormat.Fits);
        WriteBool("OUTPUT_GIF", o.OutputFormat == OutputFormat.AnimatedGif);

        WriteBool("AVI_FPS_AS_INPUT", o.AviFpsSameAsInput);
        WriteInt("AVI_FPS_VALUE", o.AviFpsValue);
        WriteInt("AVI_CODEC", (int)o.AviCodec);
        WriteInt("AVI_CODEC_QUALITY", o.AviCodecQuality);
        WriteBool("AVI_OLD_FORMAT", o.AviOldFormat);
        WriteInt("AVI_OLD_FORMAT_MAX_SIZE", o.AviOldFormatMaxSizeGb);
        WriteBool("AVI_SAFE_WIDTH", o.AviSafeFrameWidth);

        WriteBool("GIF_REPEAT_ALL_FOREVER_VALUE", o.GifRepeatForever);
        WriteBool("GIF_FPS_AS_INPUT", o.GifFpsSameAsInput);
        WriteInt("GIF_FPS_VALUE", o.GifFpsValue);
        WriteInt("GIF_COLOUR_QUANT_TYPE", (int)o.GifQuantisation);
        WriteInt("GIF_ADV_OPTIONS", o.GifAdvancedPreset);

        WriteBool("RGB_SPLIT", o.SplitRgbChannels);
        WriteInt("FRAME_DROP_VALUE", o.OutputFrameDrop);
        WriteBool("WJUPOS_NAME_ENABLE", o.GenerateWinJuposFilenames);
        WriteInt("WJUPOS_TIME", (int)o.WinJuposTimeMode);
        WriteBool("SAVE_REJECTED", o.SaveRejectedFrames);

        return sb.ToString();
    }

    public static ProcessingOptions ParseFromPof(string content)
    {
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].Trim().Equals(Header, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Not a valid CassiniFlow Options File (missing [CassiniFlow OPTIONS FILE] header).");
        }

        var o = new ProcessingOptions();
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            int spaceIdx = line.IndexOf(' ');
            string key = spaceIdx > 0 ? line[..spaceIdx].Trim() : line;
            string val = spaceIdx > 0 ? line[(spaceIdx + 1)..].Trim() : string.Empty;

            bool AsBool() => val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
            int AsInt(int def = 0) => int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : def;
            decimal AsDec(decimal def = 1.0m) => decimal.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal r) ? r : def;

            switch (key)
            {
                case "BATCH_MODE": if (AsBool()) o.RunMode = ProcessingRunMode.BatchMode; break;
                case "JOIN_MODE": if (AsBool()) o.RunMode = ProcessingRunMode.JoinMode; break;
                case "PLANETARY_MODE": if (AsBool()) o.ActivePreset = OptimisationPreset.Planetary; break;
                case "FULLDISC_MODE": if (AsBool()) o.ActivePreset = OptimisationPreset.SolarLunarFullDisc; break;
                case "CLOSEUP_MODE": if (AsBool()) o.ActivePreset = OptimisationPreset.SolarLunarCloseUp; break;
                case "ISS_MODE": if (AsBool()) o.ActivePreset = OptimisationPreset.Iss; break;
                case "PLANETARY_ANNIMATION_AVI_MODE": if (AsBool()) o.ActivePreset = OptimisationPreset.PlanetaryAnimationAvi; break;
                case "PLANETARY_ANNIMATION_GIF_MODE": if (AsBool()) o.ActivePreset = OptimisationPreset.PlanetaryAnimationGif; break;
                case "AVI_ARCHIVE_MODE": if (AsBool()) o.ActivePreset = OptimisationPreset.AviArchive; break;

                case "FRAME_LIMIT": o.LimitFrameRange = AsBool(); break;
                case "FRAME_RANGE_TYPE": o.FrameRangeMode = (FrameRangeType)AsInt(); break;
                case "START_FRAME_VALUE": o.StartFrame = AsInt(1); break;
                case "END_FRAME_VALUE": o.EndFrame = AsInt(999999); break;
                case "NUM_FRAMES_VALUE": o.NumFramesFromCentre = AsInt(1000); break;

                case "DARK_FRAME_SUBTRACT": o.DarkFrameSubtract = AsBool(); break;
                case "SAVE_MASTER_DARK": o.SaveMasterDark = AsBool(); break;
                case "REPLACE_HOT_PIXELS": o.ReplaceHotPixels = AsBool(); break;
                case "HOT_PIXEL_THRESHOLD": o.HotPixelThreshold = AsInt(35); break;
                case "DARK_DATA_HAS_BAYER": o.DarkDataHasBayer = AsBool(); break;

                case "FLAT_FRAME_CALIBRATE": o.FlatFrameCalibrate = AsBool(); break;
                case "SAVE_MASTER_FLAT": o.SaveMasterFlat = AsBool(); break;
                case "FLAT_DATA_HAS_BAYER": o.FlatDataHasBayer = AsBool(); break;
                case "FLAT_DARK_FRAME_SUBTRACT": o.FlatDarkFrameSubtract = AsBool(); break;
                case "SAVE_MASTER_FLAT_DARK": o.SaveMasterFlatDark = AsBool(); break;

                case "BINNING": o.Binning = (BinningMode)Math.Clamp(AsInt(1), 1, 5); break;
                case "BIN_CALC": o.BinCalculation = (BinningCalculation)AsInt(); break;

                case "DEBAYER_ENABLED": o.DebayerEnabled = AsBool(); break;
                case "PROTECT_BAYER": o.ProtectBayerPattern = AsBool(); break;
                case "DEBAYER_TYPE": o.DebayerType = (DebayerAlgorithm)AsInt(); break;
                case "DEBAYER_PATTERN": o.DebayerPattern = (BayerPattern)AsInt(); break;

                case "TRUE_Y800": o.TrueY800DecodeStrict = AsBool(); break;
                case "SER_BIT_DEPTH": o.SerBitDepthOverride = AsInt(); break;
                case "INPUT_FRAME_COLOUR": o.InputFrameColour = (InputFrameColourMode)AsInt(); break;
                case "IP_FRAME_DROP_VALUE": o.InputFrameDrop = AsInt(); break;

                case "INPUT_GAIN": o.InputGain = AsDec(1.0m); break;
                case "GAMMA": o.Gamma = AsDec(1.0m); break;
                case "MEDIAN_FILTER": o.EnableMedianNoiseFilter = AsBool(); break;
                case "CHECK_PLANET": o.CheckPlanetObjectPresent = AsBool(); break;
                case "MIN_PLANET_SIZE": o.MinimumObjectSizePixels = AsInt(25); break;
                case "EDGE_IN_SHADOW": o.EdgeInShadow = (LunarShadowEdge)AsInt(); break;
                case "CENTRE_PLANET": o.CentreObjectInFrame = AsBool(); break;
                case "AUTO_THRESHOLD": o.AutoDetectionThreshold = AsBool(); break;
                case "THRESHOLD": o.ManualDetectionThreshold = AsInt(40); break;
                case "OVEREXPOSED": o.RejectOverexposedFrames = AsBool(); break;
                case "OVEREXPOSED_VALUE": o.MaxOverexposedPixels = AsInt(60); break;

                case "ENABLE_CROP": o.EnableCropping = AsBool(); break;
                case "CROP_WIDTH": o.CropWidth = AsInt(448); break;
                case "CROP_HEIGHT": o.CropHeight = AsInt(448); break;
                case "X_OFFSET": o.CropOffsetX = AsInt(); break;
                case "Y_OFFSET": o.CropOffsetY = AsInt(); break;

                case "HISTO_STRETCH": o.EnableHistogramStretch = AsBool(); break;
                case "HISTO_STRETCH_VALUE": o.HistogramStretchWhitePointPercent = AsInt(75); break;
                case "HISTO_STRETCH_NO_BP": o.HistogramStretchBlackPointZero = !AsBool(); break;
                case "RGB_STRETCH": o.HistogramStretchRgbIndividually = AsBool(); break;

                case "STABILISE_NONE_MODE": if (AsBool()) o.StabilisationMode = StabilisationMode.None; break;
                case "STABILISE_OBJECT_MODE": if (AsBool()) o.StabilisationMode = StabilisationMode.ObjectPlanetary; break;
                case "STABILISE_SURFACE_MODE": if (AsBool()) o.StabilisationMode = StabilisationMode.SurfaceFeature; break;
                case "SURFACE_FEATURE_TRACKING_EN": o.EnableSurfaceFeatureTracking = AsBool(); break;
                case "STABILISE_EN": o.EnableSurfaceStabilisation = AsBool(); break;
                case "AFB_X": o.AfbX = AsInt(200); break;
                case "AFB_Y": o.AfbY = AsInt(200); break;
                case "AFB_WIDTH": o.AfbWidth = AsInt(64); break;
                case "AFB_HEIGHT": o.AfbHeight = AsInt(64); break;
                case "AFB_REJECT": o.AfbRejectFramesWithoutAnchor = AsBool(); break;

                case "AOI_ENABLE": o.AoiEnable = AsBool(); break;
                case "AOI_X": o.AoiX = AsInt(100); break;
                case "AOI_Y": o.AoiY = AsInt(100); break;
                case "AOI_WIDTH": o.AoiWidth = AsInt(400); break;
                case "AOI_HEIGHT": o.AoiHeight = AsInt(400); break;
                case "AOI_REJECT": o.AoiRejectFramesWithoutCompleteArea = AsBool(); break;
                case "AOI_CROP": o.AoiCropFrames = AsBool(); break;

                case "CONV_MONO": o.ConvertToMonochrome = AsBool(); break;
                case "CONV_MONO_TYPE": o.MonochromeConversionType = (MonochromeConversionMode)AsInt(); break;
                case "FLIP_X": o.FlipHorizontalX = AsBool(); break;
                case "FLIP_Y": o.FlipVerticalY = AsBool(); break;
                case "ROTATE": o.Rotation = (RotationAngle)AsInt(); break;

                case "ENABLE_QUALITY": o.EnableQualityEstimation = AsBool(); break;
                case "QUALITY_LIMITING": o.EnableQualityLimiting = AsBool(); break;
                case "QUALITY__LIMITING_TYPE": o.QualityLimitingMode = (QualityLimitingType)AsInt(); break;
                case "QUALITY_FRAMES": o.QualityFramesToKeep = AsInt(25); break;
                case "QUALITY_REORDER": o.QualityReorderFrames = AsBool(); break;
                case "DEFAULT_QUALITY": if (AsBool()) o.QualityAlgorithm = QualityAlgorithm.GradientSharpness; break;
                case "NINOX_QUALITY": if (AsBool()) o.QualityAlgorithm = QualityAlgorithm.OriginalNinox; break;
                case "HISTO_QUALITY": if (AsBool()) o.QualityAlgorithm = QualityAlgorithm.PeakHistogram; break;
                case "TOTALPIXEL_QUALITY": if (AsBool()) o.QualityAlgorithm = QualityAlgorithm.TotalBrightness; break;
                case "QMIN": o.QualityMinSubsample = AsInt(3); break;
                case "QMAX": o.QualityMaxSubsample = AsInt(5); break;
                case "QINC": o.QualitySubsampleIncrement = AsInt(1); break;
                case "ENABLE_QWEIGHT": o.EnableQualityWeighting = AsBool(); break;

                case "OUTPUT_AVI": if (AsBool()) o.OutputFormat = OutputFormat.Avi; break;
                case "OUTPUT_SER": if (AsBool()) o.OutputFormat = OutputFormat.Ser; break;
                case "OUTPUT_BMP": if (AsBool()) o.OutputFormat = OutputFormat.Bmp; break;
                case "OUTPUT_TIF": if (AsBool()) o.OutputFormat = OutputFormat.Tiff; break;
                case "OUTPUT_PNG": if (AsBool()) o.OutputFormat = OutputFormat.Png; break;
                case "OUTPUT_FITS": if (AsBool()) o.OutputFormat = OutputFormat.Fits; break;
                case "OUTPUT_GIF": if (AsBool()) o.OutputFormat = OutputFormat.AnimatedGif; break;

                case "OUTPUT_PREFIX": o.OutputFilenamePrefix = val; break;
                case "OUTPUT_SUFFIX": o.OutputFilenameSuffix = val; break;
                case "SUBDIR_NAME": o.OutputSubdirectoryName = val; break;
                case "WJUPOS_NAME_ENABLE": o.GenerateWinJuposFilenames = AsBool(); break;
                case "RGB_SPLIT": o.SplitRgbChannels = AsBool(); break;
                case "SAVE_REJECTED": o.SaveRejectedFrames = AsBool(); break;
            }
        }

        return o;
    }

    /// <summary>
    /// Generates the equivalent command-line arguments for CassiniFlow_core.exe / CassiniFlow.Cli.
    /// </summary>
    public static List<string> GenerateCommandLineArguments(ProcessingOptions o)
    {
        var args = new List<string>();

        if (o.RunMode == ProcessingRunMode.JoinMode) args.Add("-join");
        if (o.SaveRejectedFrames) args.Add("-sr");
        if (!o.UseRamBuffersForQualitySorting) args.Add("-no_ram_buff");

        if (o.DarkFrameSubtract)
        {
            args.Add("-dark");
            if (o.SaveMasterDark) args.Add("-save_dark");
            if (o.ReplaceHotPixels) args.Add($"-hot_pixel={o.HotPixelThreshold}");
            if (o.DarkDataHasBayer) args.Add("-dark_bayer");
        }
        if (o.FlatFrameCalibrate)
        {
            args.Add("-flat");
            if (o.SaveMasterFlat) args.Add("-save_flat");
            if (o.FlatDataHasBayer) args.Add("-flat_bayer");
        }
        if (o.FlatDarkFrameSubtract)
        {
            args.Add("-flat_dark");
            if (o.SaveMasterFlatDark) args.Add("-save_flat_dark");
        }

        if (o.LimitFrameRange)
        {
            if (o.FrameRangeMode == FrameRangeType.StartToEnd)
            {
                args.Add($"-sf={o.StartFrame}");
                args.Add($"-f={o.EndFrame - o.StartFrame + 1}");
            }
            else
            {
                args.Add("-ctrf");
                args.Add($"-f={o.NumFramesFromCentre}");
            }
        }

        if (o.Binning != BinningMode.Disabled)
        {
            args.Add($"-bin={(int)o.Binning}");
            if (o.BinCalculation == BinningCalculation.Sum) args.Add("-bin_sum");
        }

        if (o.DebayerEnabled)
        {
            args.Add($"-db={(int)o.DebayerPattern}");
            args.Add($"-db_type={(int)o.DebayerType}");
        }
        else if (o.ProtectBayerPattern)
        {
            args.Add("-pb");
        }

        if (o.InputGain != 1.00m) args.Add($"-input_gain={o.InputGain.ToString("0.00", CultureInfo.InvariantCulture)}");
        if (o.Gamma != 1.00m) args.Add($"-gamma={o.Gamma.ToString("0.00", CultureInfo.InvariantCulture)}");
        if (o.EnableMedianNoiseFilter) args.Add("-median");

        if (o.ConvertToMonochrome)
        {
            args.Add($"-convmono={(int)o.MonochromeConversionType}");
        }

        if (o.EnableHistogramStretch)
        {
            args.Add($"-hs={o.HistogramStretchWhitePointPercent}%");
            if (o.HistogramStretchBlackPointZero) args.Add("-hs_bp");
            if (o.HistogramStretchRgbIndividually) args.Add("-hsrgb");
        }

        if (o.RejectOverexposedFrames) args.Add($"-oe={o.MaxOverexposedPixels}");

        switch (o.StabilisationMode)
        {
            case StabilisationMode.ObjectPlanetary:
                AddPlanetaryCliArgs(args, o);
                break;

            case StabilisationMode.SurfaceFeature when o.EnableSurfaceStabilisation:
                AddSurfaceFeatureCliArgs(args, o);
                break;
        }

        if (o.EnableCropping)
        {
            args.Add($"-cx={o.CropWidth}");
            args.Add($"-cy={o.CropHeight}");
            if (o.CropOffsetX != 0) args.Add($"-ox={o.CropOffsetX}");
            if (o.CropOffsetY != 0) args.Add($"-oy={o.CropOffsetY}");
        }

        if (o.FlipHorizontalX) args.Add("-flip_x");
        if (o.FlipVerticalY) args.Add("-flip_y");
        switch (o.Rotation)
        {
            case RotationAngle.Rotate90Clockwise: args.Add("-rotate90"); break;
            case RotationAngle.Rotate90Anticlockwise: args.Add("-rotate270"); break;
            case RotationAngle.Rotate180: args.Add("-rotate180"); break;
        }

        if (o.EnableQualityEstimation)
        {
            if (o.EnableQualityLimiting)
            {
                args.Add(o.QualityLimitingMode == QualityLimitingType.PercentageOfFrames
                    ? $"-qpcnt={o.QualityFramesToKeep}"
                    : $"-q={o.QualityFramesToKeep}");
            }
            else
            {
                args.Add("-q");
            }

            switch (o.QualityAlgorithm)
            {
                case QualityAlgorithm.OriginalNinox: args.Add("-qold"); break;
                case QualityAlgorithm.PeakHistogram: args.Add("-qhisto"); break;
                case QualityAlgorithm.TotalBrightness: args.Add("-qtotal"); break;
            }
            args.Add($"-qmin={o.QualityMinSubsample}");
            args.Add($"-qmax={o.QualityMaxSubsample}");
            args.Add($"-qinc={o.QualitySubsampleIncrement}");
            if (o.EnableQualityWeighting) args.Add("-qw");
            if (!o.QualityReorderFrames) args.Add("-nr");
        }

        switch (o.OutputFormat)
        {
            case OutputFormat.Avi: args.Add("-avi"); break;
            case OutputFormat.Ser: args.Add("-ser"); break;
            case OutputFormat.Bmp: args.Add("-bmp"); break;
            case OutputFormat.Tiff: args.Add("-tif"); break;
            case OutputFormat.Png: args.Add("-png"); break;
            case OutputFormat.Fits: args.Add("-fits"); break;
            case OutputFormat.AnimatedGif: args.Add("-gif"); break;
        }

        if (!string.IsNullOrEmpty(o.OutputFilenamePrefix)) args.Add($"-op={o.OutputFilenamePrefix}");
        if (!string.IsNullOrEmpty(o.OutputFilenameSuffix)) args.Add($"-os={o.OutputFilenameSuffix}");
        if (o.SplitRgbChannels) args.Add("-rgb");
        if (o.GenerateWinJuposFilenames) args.Add($"-wjpos_time={(int)o.WinJuposTimeMode}");

        return args;
    }

    private static void AddPlanetaryCliArgs(List<string> args, ProcessingOptions o)
    {
        if (o.CheckPlanetObjectPresent)
        {
            args.Add("-chk");
            args.Add($"-ms={o.MinimumObjectSizePixels}");
            if (!o.AutoDetectionThreshold) args.Add($"-mp={o.ManualDetectionThreshold}");
            if (o.EdgeInShadow != LunarShadowEdge.None) args.Add($"-shdw={(int)o.EdgeInShadow}");
        }
        if (o.CentreObjectInFrame) args.Add("-ctr");
    }

    private static void AddSurfaceFeatureCliArgs(List<string> args, ProcessingOptions o)
    {
        args.Add("-en_ss");
        if (o.EnableSurfaceFeatureTracking) args.Add("-en_surface_feature_tracking");
        args.Add($"-afb_x={o.AfbX}");
        args.Add($"-afb_y={o.AfbY}");
        args.Add($"-afb_width={o.AfbWidth}");
        args.Add($"-afb_height={o.AfbHeight}");
        if (o.AfbRejectFramesWithoutAnchor) args.Add("-freject");

        if (o.AoiEnable)
        {
            args.Add("-aoi");
            args.Add($"-aoi_x={o.AoiX}");
            args.Add($"-aoi_y={o.AoiY}");
            args.Add($"-aoi_width={o.AoiWidth}");
            args.Add($"-aoi_height={o.AoiHeight}");
            if (o.AoiRejectFramesWithoutCompleteArea) args.Add("-aoi_reject");
            if (o.AoiCropFrames) args.Add("-aoi_crop");
        }
    }
}
