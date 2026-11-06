using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using CassiniFlow.Application.UseCases;
using CassiniFlow.Infrastructure.Adapters;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<StackedTierUiItem> StackedComparisonItems { get; } = [];
    [ObservableProperty] private bool _hasMultipleStackedTiers;
    [ObservableProperty] private string _frameComparatorButtonText = string.Empty;

    public event Action? RequestOpenFrameComparator;

    [RelayCommand]
    public void OpenFrameComparator() => RequestOpenFrameComparator?.Invoke();

    private CancellationTokenSource? _processingCts;

    // 7. Do Processing
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddFiles))]
    [NotifyPropertyChangedFor(nameof(CanStartProcessing))]
    [NotifyPropertyChangedFor(nameof(HasStartValidationIssue))]
    [NotifyPropertyChangedFor(nameof(StartProcessingValidationMessage))]
    private bool _isProcessing;
    [ObservableProperty] private int _processingProgress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(GlobalStatusText))] private string _processingStageText = Loc.Get("UI.Ready");
    [ObservableProperty] private string _lastOutputFilePath = string.Empty;
    partial void OnLastOutputFilePathChanged(string value) => OnPropertyChanged(nameof(PlayOrOpenOutputText));

    public string PlayOrOpenOutputText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LastOutputFilePath))
            {
                return Loc.Get("Messages.OpenResult");
            }
            string ext = Path.GetExtension(LastOutputFilePath).ToLowerInvariant();
            if (ext is ".tif" or ".tiff" or ".png" or ".fit" or ".fits" or ".jpg" or ".jpeg")
            {
                return Loc.Get("Messages.ViewImage");
            }
            return Loc.Get("Messages.PlayVideo");
        }
    }
    [ObservableProperty] private string _lastOutputDirectory = string.Empty;
    [ObservableProperty] private bool _hasCompletedOutput;
    [ObservableProperty] private string _summaryStatsText = Loc.Get("UI.NoProcessingRunYet");
    [ObservableProperty] private string _consoleLogText = Loc.Get("UI.AddSourceFilesToStart");


    // --- Validation d'exécution (blocage du bouton Démarrer) ---
    public bool CanStartProcessing => !IsProcessing && string.IsNullOrEmpty(GetStartProcessingValidationIssue());
    public bool HasStartValidationIssue => !IsProcessing && !string.IsNullOrEmpty(GetStartProcessingValidationIssue());
    public string StartProcessingValidationMessage => GetStartProcessingValidationIssue();

    private string GetStartProcessingValidationIssue()
    {
        if (!SourceFiles.Any(f => f.IsChecked))
        {
            return Loc.Get("Messages.ValidationAddAndCheckSourceFile");
        }

        if (!IsWorkflowPreProcessingOnly)
        {
            if (GetActiveStackPercentages().Count == 0)
            {
                return Loc.Get("Messages.ValidationStackPercentageRequiredNonZero");
            }

            if (CurrentAlignmentGrid.ActiveCount == 0)
            {
                return Loc.Get("Messages.ValidationNoApPointsPlaced");
            }
        }
        else if (EnableQuality && LimitQualityFrames && QualityKeepValue <= 0)
        {
            return Loc.Get("Messages.ValidationQualityKeepCountPositive");
        }

        return string.Empty;
    }

    private void NotifyStartProcessingValidation()
    {
        OnPropertyChanged(nameof(CanStartProcessing));
        OnPropertyChanged(nameof(HasStartValidationIssue));
        OnPropertyChanged(nameof(StartProcessingValidationMessage));
    }
    [ObservableProperty] private int _stackFrameCount = 1000;
    [ObservableProperty] private int _maxAvailableFrames = 999999;

    public void UpdateAvailableFrames()
    {
        var activeFiles = SourceFiles.Where(f => f.IsChecked).ToList();
        if (activeFiles.Count == 0)
        {
            activeFiles = SourceFiles.ToList();
        }

        if (activeFiles.Count == 0)
        {
            MaxAvailableFrames = 999999;
            return;
        }

        int available;
        if (activeFiles.Count > 1 && IsJoinMode)
        {
            available = Math.Max(1, activeFiles.Sum(f => GetFileEffectiveFrames(f)));
        }
        else if (SelectedSourceFile is { IsChecked: true })
        {
            available = Math.Max(1, GetFileEffectiveFrames(SelectedSourceFile));
        }
        else
        {
            available = Math.Max(1, activeFiles.Max(f => GetFileEffectiveFrames(f)));
        }

        MaxAvailableFrames = Math.Max(1, available);
        StackFrameCount = MaxAvailableFrames;
    }

    private int GetFileEffectiveFrames(SourceFileItem file)
    {
        if (file.HasCustomRange)
        {
            return file.EffectiveFrames;
        }
        if (LimitFrameRange)
        {
            int globalCount = Math.Max(1, EndFrame - StartFrame + 1);
            return Math.Min(file.Frames, globalCount);
        }
        return Math.Max(1, file.Frames);
    }
    [ObservableProperty] private int _selectedDrizzleIndex = 0;
    [ObservableProperty] private bool _normalizeBrightness = true;
    [ObservableProperty] private int _normalizeTargetPercent = 75;
    [ObservableProperty] private bool _correctAtmosphericDispersion = true;
    [ObservableProperty] private bool _sharpeningEnabled = true;
    [ObservableProperty] private int _sharpeningBlendPercent = 10;
    [ObservableProperty] private bool _featheringEnabled = true;

    partial void OnNormalizeBrightnessChanged(bool value)
    {
        _userSettings.Current.NormalizeBrightness = value;
        _userSettings.Save();
    }
    partial void OnNormalizeTargetPercentChanged(int value)
    {
        _userSettings.Current.NormalizeTargetPercent = Math.Clamp(value, 10, 100);
        _userSettings.Save();
    }
    partial void OnCorrectAtmosphericDispersionChanged(bool value)
    {
        _userSettings.Current.CorrectAtmosphericDispersion = value;
        _userSettings.Save();
    }
    partial void OnSharpeningEnabledChanged(bool value)
    {
        _userSettings.Current.SharpeningEnabled = value;
        _userSettings.Save();
    }
    partial void OnSharpeningBlendPercentChanged(int value)
    {
        _userSettings.Current.SharpeningBlendPercent = Math.Clamp(value, 0, 100);
        _userSettings.Save();
    }
    partial void OnFeatheringEnabledChanged(bool value)
    {
        _userSettings.Current.FeatheringEnabled = value;
        _userSettings.Save();
    }



    [RelayCommand]
    public async Task RunProcessingAsync()
    {
        if (SourceFiles.Count == 0)
        {
            SelectedTabIndex = 0;
            ConsoleLogText = Loc.Get("Messages.ValidationAddFiles");
            return;
        }

        var selectedFiles = SourceFiles.Where(f => f.IsChecked).ToList();
        if (selectedFiles.Count == 0)
        {
            SelectedTabIndex = 0;
            ConsoleLogText = Loc.Get("Messages.ValidationCheckFiles");
            return;
        }

        if (IsProcessing) return;

        string validationIssue = GetStartProcessingValidationIssue();
        if (!string.IsNullOrEmpty(validationIssue))
        {
            ConsoleLogText = validationIssue;
            return;
        }

        IsSection10Expanded = true;
        IsProcessing = true;
        HasCompletedOutput = false;
        DismissError();
        HasMultipleStackedTiers = false;
        StackedComparisonItems.Clear();
        SyncOptionsFromUi();

        _processingCts?.Cancel();
        _processingCts = new CancellationTokenSource();

        string firstPath = (SelectedSourceFile is { IsChecked: true } ? SelectedSourceFile : selectedFiles[0]).FullPath;
        string baseDir = !string.IsNullOrWhiteSpace(CustomOutputDirectory) && Directory.Exists(CustomOutputDirectory)
            ? CustomOutputDirectory
            : Path.GetDirectoryName(firstPath) ?? Environment.CurrentDirectory;

        string outDir = Path.Combine(baseDir, $"CassiniFlow_{DateTime.Now:yyyyMMdd_HHmmss}");
        LastOutputDirectory = outDir;

        var progress = new Progress<PipelineProgressReport>(r =>
        {
            ProcessingProgress = r.PercentComplete;
            ProcessingStageText = $"{Loc.TranslateRuntime(r.Stage)} ({r.PercentComplete}%)";
            UpdateProgressLog(r);
        });

        try
        {
            var filesToRun = selectedFiles.Select(f => f.FullPath).ToList();
            string modeLabel = IsJoinMode ? Loc.Get("Messages.JoinModeLabelLong") : Loc.Get("Messages.BatchModeLabelLong");
            InitProcessingLog(filesToRun.Count);

            var summary = await Task.Run(() => _pipelineRunner.ExecuteFilesAsync(
                filesToRun,
                Options,
                outDir,
                progress,
                _processingCts.Token));

            LastOutputFilePath = summary.OutputFilePath;
            HasCompletedOutput = summary.Success && File.Exists(summary.OutputFilePath);

            double fpsSpeed = summary.TotalInputFrames / Math.Max(0.001, summary.Elapsed.TotalSeconds);
            SummaryStatsText = Loc.Format("Messages.SummaryStatsFormatted", summary.TotalInputFrames, summary.OutputFramesWritten, summary.DiscardedNoPlanet + summary.DiscardedQuality, summary.Elapsed.TotalSeconds, fpsSpeed);
            FinishProcessingLog(summary);
        }
        catch (OperationCanceledException)
        {
            ProcessingStageText = Loc.Get("Messages.CancelledByUser");
            AppendStatusLog(Loc.Format("Messages.ProcessingCancelledAt", DateTime.Now));
        }
        catch (Exception ex)
        {
            ProcessingStageText = Loc.Get("Messages.Error");
            var errorDetails = CassiniFlowErrorExplainer.Explain(ex, Loc.Localization);
            AppendStatusLog(Loc.Format("Messages.ProcessingErrorAt", DateTime.Now, $"{errorDetails.Title} : {errorDetails.Message}"));
            if (!string.IsNullOrWhiteSpace(errorDetails.SuggestedResolution))
            {
                AppendStatusLog($"💡 {errorDetails.SuggestedResolution.Replace('\n', ' ')}");
            }
            ShowError(errorDetails);
        }
        finally
        {
            IsProcessing = false;
        }
    }


    private readonly List<string> _processingLogLines = [];
    private string _currentProcessingStage = string.Empty;

    private void InitProcessingLog(int fileCount)
    {
        _processingLogLines.Clear();
        _currentProcessingStage = string.Empty;
        string startTime = DateTime.Now.ToString("HH:mm:ss");
        string modeLabel = IsJoinMode
            ? Loc.Get("Messages.JoinModeLabel")
            : Loc.Get("Messages.BatchModeLabel");
        string line1 = Loc.Format("Messages.LaunchTimeHeader", startTime, fileCount, modeLabel);
        _processingLogLines.Add(line1);
        ConsoleLogText = line1;
    }

    private void UpdateProgressLog(PipelineProgressReport r)
    {
        string stage = r.Stage;
        string msg = Loc.TranslateRuntime(r.LogMessage);

        if (string.Equals(stage, _currentProcessingStage, StringComparison.Ordinal) && _processingLogLines.Count > 1)
        {
            _processingLogLines[^1] = msg;
        }
        else
        {
            _currentProcessingStage = stage;
            _processingLogLines.Add(msg);
        }

        ConsoleLogText = string.Join(Environment.NewLine, _processingLogLines);
    }

    private void FinishProcessingLog(PipelineRunSummary summary)
    {
        string finishMsg = Loc.Format("Messages.FinishedSuccessLastFile", DateTime.Now.ToString("HH:mm:ss"), summary.OutputFilePath);
        _processingLogLines.Add(finishMsg);

        if (!string.IsNullOrWhiteSpace(summary.LogFilePath) && File.Exists(summary.LogFilePath))
        {
            string logMsg = Loc.Format("Messages.ExecutionLogGeneratedAt", DateTime.Now.ToString("HH:mm:ss"), summary.LogFilePath);
            _processingLogLines.Add(logMsg);
        }

        if (summary.StackedTiers is { Count: > 1 } tiers)
        {
            StackedComparisonItems.Clear();
            var pairs = new List<(StackedTierSummary Summary, AstroFrame Frame)>();

            foreach (var tier in tiers)
            {
                if (File.Exists(tier.OutputFilePath))
                {
                    try
                    {
                        var frame = UniversalMediaReader.ReadSingleFrame(tier.OutputFilePath, 0);
                        pairs.Add((tier, frame));
                    }
                    catch { }
                }
            }

            if (pairs.Count > 1)
            {
                var evaluatedItems = StackedTierQualityEvaluator.EvaluateTiers(pairs);
                var best = evaluatedItems.FirstOrDefault(it => it.IsBest) ?? evaluatedItems[0];

                foreach (var eval in evaluatedItems)
                {
                    var bmp = AstroBitmapHelper.LoadBitmapFromFile(eval.OutputFilePath);
                    StackedComparisonItems.Add(new StackedTierUiItem(eval, bmp));
                }

                HasMultipleStackedTiers = StackedComparisonItems.Count > 1;
                FrameComparatorButtonText = Loc.Format("FrameComparator.ButtonLabel", StackedComparisonItems.Count, best.Percentage);
                string bestLog = Loc.Format("Messages.BestTierIdentified", best.Percentage, best.QualityScore, best.FileName);
                _processingLogLines.Add($"⭐ {bestLog}");
            }
        }

        ConsoleLogText = string.Join(Environment.NewLine, _processingLogLines);
    }

    private void AppendStatusLog(string message)
    {
        _processingLogLines.Add(message);
        ConsoleLogText = string.Join(Environment.NewLine, _processingLogLines);
    }

    private static string FormatExceptionDetails(Exception ex)
    {
        var messages = new List<string>();
        if (ex is AggregateException ae)
        {
            foreach (var inner in ae.Flatten().InnerExceptions)
            {
                string msg = inner.Message;
                if (inner.InnerException != null && !string.IsNullOrWhiteSpace(inner.InnerException.Message))
                {
                    msg += $" ({inner.InnerException.Message})";
                }
                if (!messages.Contains(msg))
                {
                    messages.Add(msg);
                }
            }
        }
        else
        {
            string msg = ex.Message;
            if (ex.InnerException != null && !string.IsNullOrWhiteSpace(ex.InnerException.Message))
            {
                msg += $" ({ex.InnerException.Message})";
            }
            messages.Add(msg);
        }

        return messages.Count > 0 ? string.Join(" | ", messages) : ex.ToString();
    }

    [RelayCommand]
    public void CancelProcessing()
    {
        _processingCts?.Cancel();
    }


    [RelayCommand]
    public void OpenOutputFolder()
    {
        string dir = !string.IsNullOrWhiteSpace(LastOutputDirectory) && Directory.Exists(LastOutputDirectory)
            ? LastOutputDirectory
            : (SelectedSourceFile is not null ? Path.GetDirectoryName(SelectedSourceFile.FullPath) ?? string.Empty : string.Empty);

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        else
        {
            ShowError(new CassiniFlow.Domain.Exceptions.OutputDirectoryException(
                Loc.Get("Errors.TitleOutputDirectory"),
                Loc.Get("Errors.ExplanationOutputDirectory"),
                Loc.Get("Errors.SuggestionOutputDirectory")));
        }
    }


    [RelayCommand]
    public void PlayLastOutputVideo()
    {
        if (!string.IsNullOrWhiteSpace(LastOutputFilePath) && File.Exists(LastOutputFilePath))
        {
            Process.Start(new ProcessStartInfo(LastOutputFilePath) { UseShellExecute = true });
        }
        else
        {
            ShowError(new CassiniFlow.Domain.Exceptions.CorruptedMediaException(
                Loc.Get("Errors.TitleCorruptMedia"),
                Loc.Get("Errors.ExplanationCorruptMedia"),
                Loc.Get("Errors.SuggestionCorruptMedia")));
        }
    }
}
