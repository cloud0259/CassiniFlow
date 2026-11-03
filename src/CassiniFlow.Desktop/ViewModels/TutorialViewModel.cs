using System;
using Avalonia;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class TutorialViewModel : ObservableObject
{
    private readonly IAstroLocalization _loc;
    private readonly IUserSettingsService _userSettings;

    [ObservableProperty] private bool _isTutorialActive;
    [ObservableProperty] private int _tutorialStepIndex;
    [ObservableProperty] private string _tutorialStepTitle = string.Empty;
    [ObservableProperty] private string _tutorialStepDescription = string.Empty;
    [ObservableProperty] private string _tutorialStepBadge = string.Empty;
    [ObservableProperty] private string _tutorialTargetBadge = string.Empty;
    [ObservableProperty] private bool _canTutorialGoPrevious;
    [ObservableProperty] private string _tutorialNextButtonText = string.Empty;
    [ObservableProperty] private HorizontalAlignment _tutorialHorizontalAlignment = HorizontalAlignment.Left;
    [ObservableProperty] private VerticalAlignment _tutorialVerticalAlignment = VerticalAlignment.Top;
    [ObservableProperty] private Thickness _tutorialMargin = new(16, 20, 0, 0);

    public event Action<int>? StepChanged;

    public TutorialViewModel(IAstroLocalization localization, IUserSettingsService userSettings)
    {
        _loc = localization ?? throw new ArgumentNullException(nameof(localization));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _tutorialNextButtonText = _loc.Get("Tutorial.Next");
    }

    [RelayCommand]
    public void StartTutorial()
    {
        TutorialStepIndex = 0;
        IsTutorialActive = true;
        UpdateTutorialStep();
    }

    [RelayCommand]
    public void NextTutorialStep()
    {
        if (TutorialStepIndex < 4)
        {
            TutorialStepIndex++;
            UpdateTutorialStep();
        }
        else
        {
            CloseTutorial();
        }
    }

    [RelayCommand]
    public void PreviousTutorialStep()
    {
        if (TutorialStepIndex > 0)
        {
            TutorialStepIndex--;
            UpdateTutorialStep();
        }
    }

    [RelayCommand]
    public void CloseTutorial()
    {
        IsTutorialActive = false;
        _userSettings.MarkTutorialSeen();
    }

    public void UpdateTutorialStep()
    {
        CanTutorialGoPrevious = TutorialStepIndex > 0;
        TutorialStepBadge = string.Format(_loc.Get("Tutorial.StepBadge"), TutorialStepIndex + 1, 5);
        TutorialNextButtonText = TutorialStepIndex == 4
            ? _loc.Get("Tutorial.Finish")
            : _loc.Get("Tutorial.Next");

        switch (TutorialStepIndex)
        {
            case 0:
                TutorialHorizontalAlignment = HorizontalAlignment.Left;
                TutorialVerticalAlignment = VerticalAlignment.Top;
                TutorialMargin = new Thickness(16, 20, 0, 0);
                TutorialTargetBadge = _loc.Get("Messages.TutorialBadgeStep1");
                TutorialStepTitle = _loc.Get("Tutorial.Step1Title");
                TutorialStepDescription = _loc.Get("Tutorial.Step1Desc");
                break;

            case 1:
                TutorialHorizontalAlignment = HorizontalAlignment.Right;
                TutorialVerticalAlignment = VerticalAlignment.Top;
                TutorialMargin = new Thickness(0, 30, 16, 0);
                TutorialTargetBadge = _loc.Get("Messages.TutorialBadgeStep2");
                TutorialStepTitle = _loc.Get("Tutorial.Step2Title");
                TutorialStepDescription = _loc.Get("Tutorial.Step2Desc");
                break;

            case 2:
                TutorialHorizontalAlignment = HorizontalAlignment.Right;
                TutorialVerticalAlignment = VerticalAlignment.Center;
                TutorialMargin = new Thickness(0, 0, 16, 40);
                TutorialTargetBadge = _loc.Get("Messages.TutorialBadgeStep3");
                TutorialStepTitle = _loc.Get("Tutorial.Step3Title");
                TutorialStepDescription = _loc.Get("Tutorial.Step3Desc");
                break;

            case 3:
                TutorialHorizontalAlignment = HorizontalAlignment.Right;
                TutorialVerticalAlignment = VerticalAlignment.Center;
                TutorialMargin = new Thickness(0, 60, 16, 0);
                TutorialTargetBadge = _loc.Get("Messages.TutorialBadgeStep4");
                TutorialStepTitle = _loc.Get("Tutorial.Step4Title");
                TutorialStepDescription = _loc.Get("Tutorial.Step4Desc");
                break;

            case 4:
                TutorialHorizontalAlignment = HorizontalAlignment.Right;
                TutorialVerticalAlignment = VerticalAlignment.Bottom;
                TutorialMargin = new Thickness(0, 0, 16, 20);
                TutorialTargetBadge = _loc.Get("Messages.TutorialBadgeStep5");
                TutorialStepTitle = _loc.Get("Tutorial.Step5Title");
                TutorialStepDescription = _loc.Get("Tutorial.Step5Desc");
                break;
        }

        StepChanged?.Invoke(TutorialStepIndex);
    }
}
