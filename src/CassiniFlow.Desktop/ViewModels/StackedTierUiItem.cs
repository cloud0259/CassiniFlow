using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class StackedTierUiItem : ObservableObject
{
    public StackedTierComparisonItem Model { get; }
    public Bitmap? PreviewBitmap { get; }

    public double Percentage => Model.Percentage;
    public int FramesStacked => Model.FramesStacked;
    public int TotalValidFrames => Model.TotalValidFrames;
    public string OutputFilePath => Model.OutputFilePath;
    public string FileName => Model.FileName;
    public double QualityScore => Model.QualityScore;
    public bool IsBest => Model.IsBest;
    public string TierLabel => Model.TierLabel;
    public string SummaryText => Model.SummaryText;
    public string BadgeText => Model.BadgeText;
    public string ScoreText => $"{Model.QualityScore:F1} / 100";
    public string ResolutionDetails => $"{Model.ActiveApCount} AP ({Model.TileSize}px)";

    public StackedTierUiItem(StackedTierComparisonItem model, Bitmap? previewBitmap = null)
    {
        Model = model;
        PreviewBitmap = previewBitmap;
    }

    [RelayCommand]
    public void OpenFile()
    {
        if (!string.IsNullOrWhiteSpace(OutputFilePath) && File.Exists(OutputFilePath))
        {
            Process.Start(new ProcessStartInfo(OutputFilePath) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    public void OpenContainingFolder()
    {
        string? dir = Path.GetDirectoryName(OutputFilePath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
    }
}
