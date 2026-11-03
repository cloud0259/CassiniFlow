using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    // Interactive Tutorial Wizard
    public TutorialViewModel TutorialVM { get; }

    public bool HasSeenTutorial => _userSettings.Current.HasSeenTutorial;

    public bool IsTutorialActive
    {
        get => TutorialVM.IsTutorialActive;
        set => TutorialVM.IsTutorialActive = value;
    }

    public int TutorialStepIndex
    {
        get => TutorialVM.TutorialStepIndex;
        set => TutorialVM.TutorialStepIndex = value;
    }

    public string TutorialStepTitle
    {
        get => TutorialVM.TutorialStepTitle;
        set => TutorialVM.TutorialStepTitle = value;
    }

    public string TutorialStepDescription
    {
        get => TutorialVM.TutorialStepDescription;
        set => TutorialVM.TutorialStepDescription = value;
    }

    public string TutorialStepBadge
    {
        get => TutorialVM.TutorialStepBadge;
        set => TutorialVM.TutorialStepBadge = value;
    }

    public string TutorialTargetBadge
    {
        get => TutorialVM.TutorialTargetBadge;
        set => TutorialVM.TutorialTargetBadge = value;
    }

    public bool CanTutorialGoPrevious
    {
        get => TutorialVM.CanTutorialGoPrevious;
        set => TutorialVM.CanTutorialGoPrevious = value;
    }

    public string TutorialNextButtonText
    {
        get => TutorialVM.TutorialNextButtonText;
        set => TutorialVM.TutorialNextButtonText = value;
    }

    public Avalonia.Layout.HorizontalAlignment TutorialHorizontalAlignment
    {
        get => TutorialVM.TutorialHorizontalAlignment;
        set => TutorialVM.TutorialHorizontalAlignment = value;
    }

    public Avalonia.Layout.VerticalAlignment TutorialVerticalAlignment
    {
        get => TutorialVM.TutorialVerticalAlignment;
        set => TutorialVM.TutorialVerticalAlignment = value;
    }

    public Avalonia.Thickness TutorialMargin
    {
        get => TutorialVM.TutorialMargin;
        set => TutorialVM.TutorialMargin = value;
    }

    [RelayCommand]
    public void StartTutorial() => TutorialVM.StartTutorial();

    [RelayCommand]
    public void NextTutorialStep() => TutorialVM.NextTutorialStep();

    [RelayCommand]
    public void PreviousTutorialStep() => TutorialVM.PreviousTutorialStep();

    [RelayCommand]
    public void CloseTutorial() => TutorialVM.CloseTutorial();

    public void UpdateTutorialStep() => TutorialVM.UpdateTutorialStep();
    public event Action? RequestOpenSettings;

    [RelayCommand]
    public void OpenSettings() => RequestOpenSettings?.Invoke();

    [RelayCommand]
    public void OpenSettingsTab() => RequestOpenSettings?.Invoke();

    [RelayCommand]
    public async Task SaveUserSettings()
    {
        _userSettings.UpdateCpuSettings(MaxCpuUsagePercent, CpuWarningThresholdPercent);
        _userSettings.UpdateProcessingSettings(
            WinJuposNaming,
            SelectedOutputFormatIndex,
            SelectedStackingImageFormatIndex,
            OutputSuffix,
            CustomOutputDirectory,
            AutoRotateAlign,
            CropWidth,
            CropHeight,
            EnableCropping,
            EnableQuality,
            QualityKeepValue,
            StackPercentage1,
            StackPercentage2,
            StackPercentage3,
            AlignmentTileSize,
            MinBrightnessThreshold,
            NormalizeBrightness,
            NormalizeTargetPercent,
            CorrectAtmosphericDispersion,
            SharpeningEnabled,
            SharpeningBlendPercent,
            FeatheringEnabled);
        IsSettingsSavedFeedbackVisible = true;
        await Task.Delay(2500);
        IsSettingsSavedFeedbackVisible = false;
    }


}
