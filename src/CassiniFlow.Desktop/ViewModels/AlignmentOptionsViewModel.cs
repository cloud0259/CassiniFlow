using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Domain.Models;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class AlignmentOptionsViewModel : ObservableObject
{
    private readonly IUserSettingsService _userSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTileSize48))]
    [NotifyPropertyChangedFor(nameof(IsTileSize104))]
    [NotifyPropertyChangedFor(nameof(IsTileSize140))]
    [NotifyPropertyChangedFor(nameof(IsTileSize200))]
    private int _alignmentTileSize = 48;

    [ObservableProperty] private int _minBrightnessThreshold = 15;
    [ObservableProperty] private AlignmentGrid _currentAlignmentGrid = AlignmentGrid.Empty;

    public event Action? GridRecalculationRequested;
    public event Action? GridChanged;

    public bool IsTileSize48 => AlignmentTileSize == 48;
    public bool IsTileSize104 => AlignmentTileSize == 104;
    public bool IsTileSize140 => AlignmentTileSize == 140;
    public bool IsTileSize200 => AlignmentTileSize == 200;

    public string ActivePointsCountText => CurrentAlignmentGrid.ActiveCount > 0
        ? $"{CurrentAlignmentGrid.ActiveCount} AP"
        : "-";

    public AlignmentOptionsViewModel(IUserSettingsService userSettings)
    {
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        if (_userSettings.Current.AlignmentTileSize >= 16)
        {
            _alignmentTileSize = _userSettings.Current.AlignmentTileSize;
        }
        if (_userSettings.Current.MinBrightnessThreshold > 0)
        {
            _minBrightnessThreshold = _userSettings.Current.MinBrightnessThreshold;
        }
    }

    partial void OnAlignmentTileSizeChanged(int value)
    {
        _userSettings.Current.AlignmentTileSize = Math.Clamp(value, 16, 512);
        _userSettings.Save();
        GridRecalculationRequested?.Invoke();
    }

    partial void OnMinBrightnessThresholdChanged(int value)
    {
        _userSettings.Current.MinBrightnessThreshold = Math.Clamp(value, 1, 254);
        _userSettings.Save();
        GridRecalculationRequested?.Invoke();
    }

    partial void OnCurrentAlignmentGridChanged(AlignmentGrid value)
    {
        OnPropertyChanged(nameof(ActivePointsCountText));
        GridChanged?.Invoke();
    }
}
