using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<SourceFileItem> SourceFiles { get; } = [];

    public ObservableCollection<string> DarkFiles { get; } = [];
    public ObservableCollection<string> FlatFiles { get; } = [];
    public ObservableCollection<string> FlatDarkFiles { get; } = [];

    [ObservableProperty] private string _darkCountText = Loc.Format("UI.DarkCount", 0);
    [ObservableProperty] private string _flatCountText = Loc.Format("UI.FlatCount", 0);
    [ObservableProperty] private string _flatDarkCountText = Loc.Format("UI.FlatDarkCount", 0);
    [ObservableProperty] private bool _hasCalibrationFiles = false;
    [ObservableProperty] private int _selectedSourceFileTabIndex = 0;



    [ObservableProperty] private SourceFileItem? _selectedSourceFile;

    partial void OnSelectedSourceFileChanged(SourceFileItem? value)
    {
        HasLoadedFiles = SourceFiles.Count > 0;
        if (value is null)
        {
            _cachedPreviewResult = null;
            _cachedRawFilePath = null;
            ViewerTitle = Loc.Get("UI.NoFileSelected");
            UpdateAvailableFrames();
            FrameDimensionsText = "—";
            PreviewReady?.Invoke(null, null, 1.0);
            ResetTimeline();
            return;
        }

        MaxFrameCount = Math.Max(1, value.Frames);
        CurrentFrameIndex = 1;
        OutputFps = Math.Max(1, (int)Math.Round(value.Fps));
        TargetBoxX = Math.Max(16, value.Width / 2);
        TargetBoxY = Math.Max(16, value.Height / 2);

        // Preserve user/preset crop size (e.g. 448x448 for planetary); only clamp if source video is smaller
        if (CropWidth > value.Width)
        {
            CropWidth = Math.Max(64, value.Width & ~3);
        }
        if (CropHeight > value.Height)
        {
            CropHeight = Math.Max(64, value.Height & ~3);
        }

        UpdateAvailableFrames();
        int selIn = value.SelectionIn > 0 ? value.SelectionIn : 1;
        int selOut = value.SelectionOut > 0 ? value.SelectionOut : MaxFrameCount;
        selIn = Math.Clamp(selIn, 1, MaxFrameCount);
        selOut = Math.Clamp(selOut, selIn, MaxFrameCount);

        StartFrame = selIn;
        EndFrame = selOut;
        TimelineSelectionIn = selIn;
        TimelineSelectionOut = selOut;
        TriggerQualityTimelineAnalysis(value.FullPath);
        RequestPreviewUpdate(reloadFromDisk: true);
    }

    partial void OnSelectedPresetIndexChanged(int value)
    {
        if (!_isAutoDetectingPreset)
        {
            _userManuallySelectedPreset = true;
        }

        int maxW = SelectedSourceFile?.Width ?? 4096;
        int maxH = SelectedSourceFile?.Height ?? 4096;

        switch (value)
        {
            case 1: // Planetary
                SelectedStabilisationModeIndex = 1;
                CheckObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                if (SelectedSourceFile?.PlanetDiameterPx is double planetD && planetD > 5)
                {
                    // Multiplicateur 2.5 pour englober confortablement les anneaux (ex: Saturne) et une marge de sécurité
                    int pCrop = Math.Clamp((int)Math.Ceiling(planetD * 2.5), 96, Math.Min(maxW, maxH)) & ~7;
                    CropWidth = pCrop;
                    CropHeight = pCrop;
                }
                else
                {
                    CropWidth = Math.Min(512, maxW);
                    CropHeight = Math.Min(512, maxH);
                }
                EnableQuality = true;
                SelectedQualityLimitModeIndex = 1;
                QualityKeepValue = 25;
                break;
            case 2: // Solar/Lunar Close-up
                SelectedStabilisationModeIndex = 2;
                EnableCropping = false;
                EnableQuality = true;
                SelectedQualityLimitModeIndex = 1;
                QualityKeepValue = 20;
                break;
            case 3: // Solar/Lunar Full Disc
                SelectedStabilisationModeIndex = 1;
                CheckObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                int discCrop;
                if (SelectedSourceFile?.PlanetDiameterPx is double d && d > 10)
                {
                    // Encadrer correctement l'objet avec une marge de 15% à 25% autour du diamètre mesuré
                    discCrop = Math.Clamp((int)Math.Ceiling(d * 1.20), 128, Math.Min(maxW, maxH)) & ~7;
                }
                else
                {
                    discCrop = Math.Clamp((int)(Math.Min(maxW, maxH) * 0.90), 256, Math.Min(maxW, maxH)) & ~7;
                }
                CropWidth = discCrop;
                CropHeight = discCrop;
                SelectedQualityAlgoIndex = 2;
                break;
            case 4: // ISS
                SelectedStabilisationModeIndex = 1;
                CheckObjectPresent = true;
                CentreObjectInFrame = true;
                EnableCropping = true;
                CropWidth = Math.Min(448, maxW);
                CropHeight = Math.Min(448, maxH);
                SelectedQualityAlgoIndex = 3;
                break;
            case 5: // Planetary Animation
                SelectedStabilisationModeIndex = 1;
                CentreObjectInFrame = true;
                EnableCropping = true;
                CropWidth = Math.Min(448, maxW);
                CropHeight = Math.Min(448, maxH);
                EnableQuality = false;
                SelectedOutputFormatIndex = 1;
                break;
        }
        RequestPreviewUpdate(reloadFromDisk: false);
    }

    public async Task AddPhysicalFilesAsync(IEnumerable<string> filePaths)
    {
        if (IsLoadingFiles || IsProcessing) return;

        var paths = filePaths.Where(File.Exists).ToList();
        if (paths.Count == 0) return;

        IsLoadingFiles = true;
        LoadingProgress = 0;
        LoadingStatusText = string.Format(Loc.Get("SourceFiles.LoadingFiles"), 0, paths.Count);

        var newItems = new SourceFileItem?[paths.Count];
        int processedCount = 0;

        try
        {
            await Task.Run(() =>
            {
                int maxWorkers = Math.Clamp(Environment.ProcessorCount, 1, 16);
                Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers }, i =>
                {
                    string path = paths[i];
                    try
                    {
                        var summary = UniversalMediaReader.InspectFile(path);
                        double mb = summary.FileSizeBytes / (1024.0 * 1024.0);
                        string sizeStr = mb >= 1024
                            ? Loc.Format("UI.FileSizeGb", mb / 1024.0)
                            : Loc.Format("UI.FileSizeMb", mb);

                        newItems[i] = new SourceFileItem
                        {
                            FileName = Path.GetFileName(path),
                            FullPath = path,
                            FormatBadge = summary.FormatBadge,
                            Frames = summary.FrameCount,
                            SelectionIn = 1,
                            SelectionOut = Math.Max(1, summary.FrameCount),
                            Width = summary.Width,
                            Height = summary.Height,
                            Fps = summary.Fps,
                            FileSize = sizeStr,
                            DetailedReport = summary.DetailedReport,
                            Metadata = summary.Metadata ?? AstroMediaMetadata.Empty
                        };
                    }
                    catch (Exception ex)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            ConsoleLogText += $"\n{Loc.Get("Messages.ReadError")} {Path.GetFileName(path)} : {ex.Message}";
                        });
                    }

                    int done = Interlocked.Increment(ref processedCount);
                    if (paths.Count > 10 && (done % 20 == 0 || done == paths.Count))
                    {
                        double pct = (double)done / paths.Count * 100.0;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            LoadingProgress = pct;
                            LoadingStatusText = string.Format(Loc.Get("SourceFiles.LoadingFiles"), done, paths.Count);
                        });
                    }
                });
            });
        }
        finally
        {
            IsLoadingFiles = false;
            LoadingProgress = 0;
            LoadingStatusText = string.Empty;
        }

        var validItems = newItems.Where(it => it != null).Cast<SourceFileItem>().ToList();
        if (validItems.Count == 0) return;

        foreach (var item in validItems)
        {
            SourceFiles.Add(item);
        }

        HasLoadedFiles = SourceFiles.Count > 0;
        if (SourceFiles.Count > 1)
        {
            IsJoinMode = true;
            IsBatchMode = false;
        }

        _userManuallySelectedPreset = false;
        var firstNew = validItems[0];
        SelectedSourceFile ??= firstNew;
        if (firstNew.FormatBadge.Contains("Bayer", StringComparison.OrdinalIgnoreCase))
        {
            DebayerEnabled = true;
            if (firstNew.FormatBadge.Contains("BGGR", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 1;
            else if (firstNew.FormatBadge.Contains("RGGB", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 2;
            else if (firstNew.FormatBadge.Contains("GRBG", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 3;
            else if (firstNew.FormatBadge.Contains("GBRG", StringComparison.OrdinalIgnoreCase)) SelectedBayerPatternIndex = 0;
        }
        ConsoleLogText = Loc.Format("Messages.FilesLoaded", SourceFiles.Count, firstNew.FileName, firstNew.Resolution, firstNew.Frames, firstNew.FpsInfo);

        UpdateAvailableFrames();
        await AnalyzePlanetSizes(validItems);
    }


    [RelayCommand]
    public void RemoveSelectedFile()
    {
        var checkedItems = SourceFiles.Where(f => f.IsChecked).ToList();
        if (checkedItems.Count == 0)
        {
            if (SelectedSourceFile is null) return;
            checkedItems.Add(SelectedSourceFile);
        }
        UniversalMediaReader.ClosePreviewCache();
        int idx = SelectedSourceFile is null ? 0 : SourceFiles.IndexOf(SelectedSourceFile);
        foreach (var it in checkedItems) SourceFiles.Remove(it);
        HasLoadedFiles = SourceFiles.Count > 0;
        SelectedSourceFile = SourceFiles.Count > 0
            ? SourceFiles[Math.Clamp(idx, 0, SourceFiles.Count - 1)]
            : null;
        UpdateAvailableFrames();
    }


    private void OnSourceFilesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (SourceFileItem item in e.NewItems)
            {
                item.PropertyChanged += OnSourceFileItemPropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (SourceFileItem item in e.OldItems)
            {
                item.PropertyChanged -= OnSourceFileItemPropertyChanged;
            }
        }
        UpdateAvailableFrames();
        OnPropertyChanged(nameof(AreAllFilesChecked));
        NotifyStartProcessingValidation();
    }

    private void OnSourceFileItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SourceFileItem.IsChecked) or nameof(SourceFileItem.SelectionIn) or nameof(SourceFileItem.SelectionOut))
        {
            UpdateAvailableFrames();
            if (e.PropertyName == nameof(SourceFileItem.IsChecked))
            {
                OnPropertyChanged(nameof(AreAllFilesChecked));
                NotifyStartProcessingValidation();
            }
        }
    }

    public bool? AreAllFilesChecked
    {
        get
        {
            if (SourceFiles.Count == 0) return false;
            if (SourceFiles.All(f => f.IsChecked)) return true;
            if (SourceFiles.All(f => !f.IsChecked)) return false;
            return null;
        }
        set
        {
            bool target = value ?? true;
            foreach (var f in SourceFiles) f.IsChecked = target;
            OnPropertyChanged(nameof(AreAllFilesChecked));
        }
    }

    [RelayCommand]
    public void ToggleCheckAllFiles()
    {
        bool target = SourceFiles.Any(f => !f.IsChecked);
        foreach (var f in SourceFiles) f.IsChecked = target;
        OnPropertyChanged(nameof(AreAllFilesChecked));
    }


    public string DarkFilesTooltip => DarkFiles.Count == 0 ? string.Empty : string.Join(Environment.NewLine, DarkFiles.Select(Path.GetFileName));
    public string FlatFilesTooltip => FlatFiles.Count == 0 ? string.Empty : string.Join(Environment.NewLine, FlatFiles.Select(Path.GetFileName));
    public string FlatDarkFilesTooltip => FlatDarkFiles.Count == 0 ? string.Empty : string.Join(Environment.NewLine, FlatDarkFiles.Select(Path.GetFileName));

    [RelayCommand]
    public void AddDarkFiles(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p) && !DarkFiles.Contains(p)) DarkFiles.Add(p);
        }
        UpdateCalibrationStatus();
        ConsoleLogText = Loc.Format("Messages.DarkRegistered", DarkFiles.Count);
    }

    [RelayCommand]
    public void ClearDarkFiles()
    {
        DarkFiles.Clear();
        UpdateCalibrationStatus();
        ConsoleLogText = Loc.Get("Messages.DarkCleared");
    }

    [RelayCommand]
    public void RemoveDarkFile(string path)
    {
        if (DarkFiles.Remove(path))
        {
            UpdateCalibrationStatus();
            ConsoleLogText = Loc.Format("Messages.DarkRemoved", Path.GetFileName(path));
        }
    }

    [RelayCommand]
    public void AddFlatFiles(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p) && !FlatFiles.Contains(p)) FlatFiles.Add(p);
        }
        UpdateCalibrationStatus();
        ConsoleLogText = Loc.Format("Messages.FlatRegistered", FlatFiles.Count);
    }

    [RelayCommand]
    public void ClearFlatFiles()
    {
        FlatFiles.Clear();
        UpdateCalibrationStatus();
        ConsoleLogText = Loc.Get("Messages.FlatCleared");
    }

    [RelayCommand]
    public void RemoveFlatFile(string path)
    {
        if (FlatFiles.Remove(path))
        {
            UpdateCalibrationStatus();
            ConsoleLogText = Loc.Format("Messages.FlatRemoved", Path.GetFileName(path));
        }
    }

    [RelayCommand]
    public void AddFlatDarkFiles(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p) && !FlatDarkFiles.Contains(p)) FlatDarkFiles.Add(p);
        }
        UpdateCalibrationStatus();
        ConsoleLogText = Loc.Format("Messages.FlatDarkRegistered", FlatDarkFiles.Count);
    }

    [RelayCommand]
    public void ClearFlatDarkFiles()
    {
        FlatDarkFiles.Clear();
        UpdateCalibrationStatus();
        ConsoleLogText = Loc.Get("Messages.FlatDarkCleared");
    }

    [RelayCommand]
    public void RemoveFlatDarkFile(string path)
    {
        if (FlatDarkFiles.Remove(path))
        {
            UpdateCalibrationStatus();
            ConsoleLogText = Loc.Format("Messages.FlatDarkRemoved", Path.GetFileName(path));
        }
    }

    [RelayCommand]
    public void ClearAllCalibrationFiles()
    {
        DarkFiles.Clear();
        FlatFiles.Clear();
        FlatDarkFiles.Clear();
        UpdateCalibrationStatus();
        ConsoleLogText = Loc.Get("Messages.AllCalibrationCleared");
    }

    private void UpdateCalibrationStatus()
    {
        DarkCountText = Loc.Format("UI.DarkCount", DarkFiles.Count);
        FlatCountText = Loc.Format("UI.FlatCount", FlatFiles.Count);
        FlatDarkCountText = Loc.Format("UI.FlatDarkCount", FlatDarkFiles.Count);
        HasCalibrationFiles = DarkFiles.Count > 0 || FlatFiles.Count > 0 || FlatDarkFiles.Count > 0;
        OnPropertyChanged(nameof(DarkFilesTooltip));
        OnPropertyChanged(nameof(FlatFilesTooltip));
        OnPropertyChanged(nameof(FlatDarkFilesTooltip));
        RequestPreviewUpdate(reloadFromDisk: false);
    }

    [RelayCommand]
    public void MoveSelectedFileUp() => MoveSelectedFile(-1);


    [RelayCommand]
    public void MoveSelectedFileDown() => MoveSelectedFile(1);

    private void MoveSelectedFile(int delta)
    {
        var item = SelectedSourceFile;
        if (item is null) return;
        int idx = SourceFiles.IndexOf(item);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= SourceFiles.Count) return;
        SourceFiles.Move(idx, target);
        SelectedSourceFile = item;
    }


    [RelayCommand]
    public async Task AnalyzePlanetSizes()
    {
        var unmeasured = SourceFiles.Where(f => !f.PlanetDiameterPx.HasValue).ToList();
        var targets = unmeasured.Count > 0 ? unmeasured : SourceFiles.ToList();
        await AnalyzePlanetSizesInternal(targets);
    }

    public async Task AnalyzePlanetSizes(IEnumerable<SourceFileItem>? targetItems)
    {
        var items = (targetItems ?? SourceFiles).ToList();
        await AnalyzePlanetSizesInternal(items);
    }

    private async Task AnalyzePlanetSizesInternal(List<SourceFileItem> items)
    {
        if (items.Count == 0) return;

        IsMeasuringPlanetSizes = true;
        MeasuringStatusText = Loc.Format("Messages.MeasuringAndIdentifyingProgress", 0, items.Count);

        foreach (var it in items) it.IsMeasuring = true;

        var detectedList = new List<(SourceFileItem Item, CelestialTargetClassification Classification)>();

        await Task.Run(() =>
        {
            int current = 0;
            foreach (var it in items)
            {
                current++;
                double? d = null;
                CelestialTargetClassification? classification = null;

                try
                {
                    d = PlanetSizeAnalyzer.MeasureDiameterPixels(it.FullPath, it.Frames, samples: 3);
                }
                catch { }

                try
                {
                    classification = _celestialClassifier.ClassifyVideo(
                        it.FullPath,
                        it.Frames,
                        d,
                        detectSatellites: DetectNaturalSatellites,
                        satelliteSensitivity: SatelliteDetectionSensitivity);
                }
                catch { }

                int step = current;
                var classResult = classification;
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    it.PlanetDiameterPx = d;
                    if (classResult is not null && classResult.TargetType != CelestialTargetType.Unknown)
                    {
                        it.IdentifiedObjectText = classResult.ObjectSummary(!Loc.IsEnglish);
                        detectedList.Add((it, classResult));
                    }
                    it.IsMeasuring = false;
                    MeasuringStatusText = Loc.Format("Messages.MeasuringAndIdentifyingProgress", step, items.Count);
                });
            }
        });

        IsMeasuringPlanetSizes = false;

        var sizes = SourceFiles.Where(f => f.PlanetDiameterPx.HasValue).Select(f => f.PlanetDiameterPx!.Value).ToList();
        string sizeLog = sizes.Count switch
        {
            0 => Loc.Get("Messages.ObjectSizeUndetectable"),
            1 => Loc.Format("Messages.ObjectSizeSingle", sizes[0]),
            _ => FormatObjectSizeSpread(sizes)
        };

        var sb = new System.Text.StringBuilder(sizeLog);

        if (detectedList.Count > 0)
        {
            var primaryEntry = detectedList.FirstOrDefault(x => x.Item == SelectedSourceFile);
            if (primaryEntry.Item is null) primaryEntry = detectedList[0];
            var primary = primaryEntry.Classification;
            var primaryItem = primaryEntry.Item;

            string objName = primary.ObjectSummary(!Loc.IsEnglish);
            string objDetails = primary.GetDetails(!Loc.IsEnglish);
            string idLine = Loc.Format("Messages.TargetIdentifiedCertainty", objName, objDetails);
            sb.AppendLine().Append(idLine);

            if (!_userManuallySelectedPreset && SelectedPresetIndex == 0 && primary.RecommendedPresetIndex > 0)
            {
                try
                {
                    _isAutoDetectingPreset = true;
                    SelectedPresetIndex = primary.RecommendedPresetIndex;
                }
                finally
                {
                    _isAutoDetectingPreset = false;
                }
                string presetLine = Loc.Format("Messages.PresetAppliedAuto", GetPresetName(primary.RecommendedPresetIndex));
                sb.AppendLine().Append(presetLine);
            }

            int frameW = EnableCropping ? CropWidth : primaryItem.Width;
            int frameH = EnableCropping ? CropHeight : primaryItem.Height;
            int optimalTile = AlignmentGridGenerator.RecommendOptimalTileSize(
                primaryItem.PlanetDiameterPx,
                primary.TargetType,
                frameW,
                frameH);
            AlignmentTileSize = optimalTile;
            if (!IsWorkflowPreProcessingOnly)
            {
                CalculateAlignmentGrid();
                if (CurrentAlignmentGrid.ActiveCount > 0)
                {
                    string apLine = Loc.Format("Messages.ApGridGeneratedAuto", CurrentAlignmentGrid.ActiveCount, optimalTile);
                    sb.AppendLine().Append(apLine);
                }
            }
        }
        else if (SelectedSourceFile is { } selItem)
        {
            int frameW = EnableCropping ? CropWidth : selItem.Width;
            int frameH = EnableCropping ? CropHeight : selItem.Height;
            AlignmentTileSize = AlignmentGridGenerator.RecommendOptimalTileSize(
                selItem.PlanetDiameterPx,
                CelestialTargetType.Unknown,
                frameW,
                frameH);
            if (!IsWorkflowPreProcessingOnly)
            {
                CalculateAlignmentGrid();
            }
        }

        ConsoleLogText = sb.ToString();
    }

    [RelayCommand]
    public void AutoDetectPreset()
    {
        var item = SelectedSourceFile;
        if (item is null) return;

        try
        {
            var classification = _celestialClassifier.ClassifyVideo(
                item.FullPath,
                item.Frames,
                item.PlanetDiameterPx,
                detectSatellites: DetectNaturalSatellites,
                satelliteSensitivity: SatelliteDetectionSensitivity);
            if (classification is null || classification.TargetType == CelestialTargetType.Unknown) return;
            if (classification.TargetType != CelestialTargetType.Unknown &&
                classification.RecommendedPresetIndex > 0)
            {
                try
                {
                    _isAutoDetectingPreset = true;
                    SelectedPresetIndex = classification.RecommendedPresetIndex;
                    _userManuallySelectedPreset = false;
                }
                finally
                {
                    _isAutoDetectingPreset = false;
                }
                ConsoleLogText = Loc.Format("Messages.TargetIdentifiedCertainty", classification.ObjectSummary(!Loc.IsEnglish), classification.GetDetails(!Loc.IsEnglish));
            }
            else
            {
                ConsoleLogText = Loc.Get("Messages.TargetUncertainManualRetained");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogText = Loc.Format("Messages.RecognitionError", ex.Message);
        }
    }

    private static string GetPresetName(int index) => index switch
    {
        1 => Loc.Get("Messages.PresetPlanetary"),
        2 => Loc.Get("Messages.PresetSolarLunarCloseup"),
        3 => Loc.Get("Messages.PresetSolarLunarFullDisc"),
        4 => Loc.Get("Messages.PresetIss"),
        5 => Loc.Get("Messages.PresetPlanetaryAnimation"),
        _ => Loc.Get("Messages.PresetManual")
    };

    private static string FormatObjectSizeSpread(IReadOnlyList<double> sizes)
    {
        double min = sizes.Min(), max = sizes.Max();
        double spread = (max - min) / max * 100.0;
        return spread > 10.0
            ? Loc.Format("Messages.ObjectSizesVary", min, max, spread)
            : Loc.Format("Messages.ObjectSizesConsistent", min, max, spread);
    }


    [RelayCommand]
    public void SortFilesBySizeAscending() => SortFilesBySize(false);


    [RelayCommand]
    public void SortFilesBySizeDescending() => SortFilesBySize(true);

    private void SortFilesBySize(bool descending)
    {
        var selected = SelectedSourceFile;
        var sorted = (descending
            ? SourceFiles.OrderByDescending(f => f.PlanetDiameterPx ?? double.NegativeInfinity)
            : SourceFiles.OrderBy(f => f.PlanetDiameterPx ?? double.PositiveInfinity)).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = SourceFiles.IndexOf(sorted[i]);
            if (cur != i) SourceFiles.Move(cur, i);
        }
        SelectedSourceFile = selected;
    }


    [RelayCommand]
    public void ClearAllFiles()
    {
        UniversalMediaReader.ClosePreviewCache();
        SourceFiles.Clear();
        HasLoadedFiles = false;
        SelectedSourceFile = null;
        UpdateAvailableFrames();
        ResetTimeline();
    }
}
