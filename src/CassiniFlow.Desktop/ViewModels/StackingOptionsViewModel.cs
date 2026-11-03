using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CassiniFlow.Desktop.Services;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class StackingOptionsViewModel : ObservableObject
{
    private readonly IUserSettingsService _userSettings;

    [ObservableProperty] private int _selectedStackingImageFormatIndex = 0;

    [ObservableProperty] private int _stackPercentage1 = 25;
    [ObservableProperty] private int _stackPercentage2 = 0;
    [ObservableProperty] private int _stackPercentage3 = 0;

    [ObservableProperty] private int _stackFrameCount = 1000;
    [ObservableProperty] private int _maxAvailableFrames = 999999;

    [ObservableProperty] private int _selectedDrizzleIndex = 0;
    [ObservableProperty] private bool _normalizeBrightness = true;
    [ObservableProperty] private int _normalizeTargetPercent = 75;
    [ObservableProperty] private bool _correctAtmosphericDispersion = true;
    [ObservableProperty] private bool _sharpeningEnabled = true;
    [ObservableProperty] private int _sharpeningBlendPercent = 10;
    [ObservableProperty] private bool _featheringEnabled = true;

    public event Action? StackingConfigChanged;

    public StackingOptionsViewModel(IUserSettingsService userSettings)
    {
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));

        _selectedStackingImageFormatIndex = Math.Clamp(_userSettings.Current.SelectedStackingImageFormatIndex, 0, 2);
        _stackPercentage1 = Math.Clamp(_userSettings.Current.StackPercentage1, 0, 100);
        _stackPercentage2 = Math.Clamp(_userSettings.Current.StackPercentage2, 0, 100);
        _stackPercentage3 = Math.Clamp(_userSettings.Current.StackPercentage3, 0, 100);

        _normalizeBrightness = _userSettings.Current.NormalizeBrightness;
        if (_userSettings.Current.NormalizeTargetPercent >= 10)
        {
            _normalizeTargetPercent = _userSettings.Current.NormalizeTargetPercent;
        }
        _correctAtmosphericDispersion = _userSettings.Current.CorrectAtmosphericDispersion;
        _sharpeningEnabled = _userSettings.Current.SharpeningEnabled;
        _sharpeningBlendPercent = Math.Clamp(_userSettings.Current.SharpeningBlendPercent, 0, 100);
        _featheringEnabled = _userSettings.Current.FeatheringEnabled;
    }

    partial void OnSelectedStackingImageFormatIndexChanged(int value)
    {
        _userSettings.Current.SelectedStackingImageFormatIndex = value;
        _userSettings.Save();
        StackingConfigChanged?.Invoke();
    }

    partial void OnStackPercentage1Changed(int value)
    {
        _userSettings.Current.StackPercentage1 = Math.Clamp(value, 0, 100);
        _userSettings.Save();
        StackingConfigChanged?.Invoke();
    }

    partial void OnStackPercentage2Changed(int value)
    {
        _userSettings.Current.StackPercentage2 = Math.Clamp(value, 0, 100);
        _userSettings.Save();
        StackingConfigChanged?.Invoke();
    }

    partial void OnStackPercentage3Changed(int value)
    {
        _userSettings.Current.StackPercentage3 = Math.Clamp(value, 0, 100);
        _userSettings.Save();
        StackingConfigChanged?.Invoke();
    }

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

    public int StackPercentage
    {
        get => StackPercentage1 > 0 ? StackPercentage1 : (StackPercentage2 > 0 ? StackPercentage2 : (StackPercentage3 > 0 ? StackPercentage3 : 0));
        set => StackPercentage1 = value;
    }

    public List<double> GetActiveStackPercentages()
    {
        var list = new List<double>();
        if (StackPercentage1 > 0) list.Add(StackPercentage1);
        if (StackPercentage2 > 0) list.Add(StackPercentage2);
        if (StackPercentage3 > 0) list.Add(StackPercentage3);
        return list.Distinct().OrderBy(p => p).ToList();
    }
}

