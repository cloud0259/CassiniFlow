using CommunityToolkit.Mvvm.ComponentModel;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class SourceFileItem : ObservableObject
{
    [ObservableProperty] private bool _isChecked = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanetSizeText))]
    private double? _planetDiameterPx;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanetSizeText))]
    private bool _isMeasuring;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(PlanetSizeText));
        OnPropertyChanged(nameof(IdentifiedObjectBadge));
        OnPropertyChanged(nameof(IdentifiedObjectSummary));
    }

    public string PlanetSizeText => IsMeasuring ? Loc.Get("Messages.Measuring") : PlanetDiameterPx is double d ? $"{d:F0} px" : "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentifiedObjectSummary))]
    [NotifyPropertyChangedFor(nameof(IdentifiedObjectBadge))]
    [NotifyPropertyChangedFor(nameof(HasIdentifiedObject))]
    private string _identifiedObjectText = string.Empty;

    public bool HasIdentifiedObject => !string.IsNullOrWhiteSpace(IdentifiedObjectText);
    public string IdentifiedObjectBadge => HasIdentifiedObject ? $"🪐 {IdentifiedObjectText}" : string.Empty;
    public string IdentifiedObjectSummary => HasIdentifiedObject ? IdentifiedObjectText : "—";

    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string FormatBadge { get; init; } = string.Empty;
    public int Frames { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Resolution => $"{Width} × {Height}";
    public double Fps { get; init; }
    public string FpsInfo => $"{Fps:F1} fps";
    public string FileSize { get; init; } = string.Empty;
    public string DetailedReport { get; init; } = string.Empty;

    public AstroMediaMetadata Metadata { get; init; } = AstroMediaMetadata.Empty;
    public string ExposureInfo => Metadata.EffectiveExposureText;
    public string ShutterSpeedInfo => Metadata.EffectiveShutterSpeedText;
    public string IsoOrGainInfo => Metadata.IsoOrGainText;
    public string CameraInfo => Metadata.Camera ?? "-";
    public string TelescopeInfo => Metadata.Telescope ?? "-";
    public bool HasCameraInfo => !string.IsNullOrEmpty(Metadata.Camera);
    public bool HasTelescopeInfo => !string.IsNullOrEmpty(Metadata.Telescope);
    public bool HasAstroDetails => Metadata.ExposureTimeSeconds.HasValue || Metadata.Iso.HasValue || Metadata.Gain.HasValue;

    // Découpage temporel individuel par vidéo (IN et OUT)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomRange))]
    [NotifyPropertyChangedFor(nameof(RangeSummaryText))]
    [NotifyPropertyChangedFor(nameof(EffectiveFrames))]
    private int _selectionIn = 1;

    private int _selectionOut;
    public int SelectionOut
    {
        get => _selectionOut > 0 ? _selectionOut : Math.Max(1, Frames);
        set
        {
            if (SetProperty(ref _selectionOut, value))
            {
                OnPropertyChanged(nameof(HasCustomRange));
                OnPropertyChanged(nameof(RangeSummaryText));
                OnPropertyChanged(nameof(EffectiveFrames));
            }
        }
    }

    public bool HasCustomRange => SelectionIn > 1 || (SelectionOut > 0 && SelectionOut < Frames);
    public int EffectiveFrames => HasCustomRange ? Math.Max(0, SelectionOut - SelectionIn + 1) : Math.Max(1, Frames);
    public string RangeSummaryText => HasCustomRange
        ? $"Plage : {SelectionIn} – {SelectionOut} ({Math.Max(0, SelectionOut - SelectionIn + 1)} fr)"
        : $"{Frames} fr";
}

