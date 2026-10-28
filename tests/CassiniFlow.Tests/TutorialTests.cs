using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.ViewModels;
using Xunit;

namespace CassiniFlow.Tests;

public class TutorialTests
{
    [Fact]
    public void Tutorial_Workflow_NavigatesThroughAllStepsAndCloses()
    {
        Loc.SetLanguage(AppLanguage.French);
        var vm = new MainWindowViewModel();

        // 1. Start tutorial
        vm.StartTutorial();
        Assert.True(vm.IsTutorialActive);
        Assert.Equal(0, vm.TutorialStepIndex);
        Assert.Equal(0, vm.SelectedTabIndex);
        Assert.False(vm.CanTutorialGoPrevious);
        Assert.Contains("1", vm.TutorialStepBadge);
        Assert.NotEmpty(vm.TutorialStepTitle);
        Assert.NotEmpty(vm.TutorialStepDescription);

        // 2. Step 2 (Input Options)
        vm.NextTutorialStep();
        Assert.Equal(1, vm.TutorialStepIndex);
        Assert.Equal(1, vm.SelectedTabIndex);
        Assert.True(vm.CanTutorialGoPrevious);

        // 3. Step 3 (Processing & Centering)
        vm.NextTutorialStep();
        Assert.Equal(2, vm.TutorialStepIndex);
        Assert.Equal(2, vm.SelectedTabIndex);

        // 4. Step 4 (Quality)
        vm.NextTutorialStep();
        Assert.Equal(3, vm.TutorialStepIndex);
        Assert.Equal(3, vm.SelectedTabIndex);

        // 5. Step 5 (Output & Execution)
        vm.NextTutorialStep();
        Assert.Equal(4, vm.TutorialStepIndex);
        Assert.Equal(6, vm.SelectedTabIndex); // Execution
        Assert.Equal(Loc.Get("Tutorial.Finish"), vm.TutorialNextButtonText);

        // 6. Previous step back to 3
        vm.PreviousTutorialStep();
        Assert.Equal(3, vm.TutorialStepIndex);
        Assert.Equal(3, vm.SelectedTabIndex);

        // 7. Advance and Finish
        vm.NextTutorialStep();
        Assert.Equal(4, vm.TutorialStepIndex);
        vm.NextTutorialStep(); // Finish
        Assert.False(vm.IsTutorialActive);
        Assert.True(UserSettingsService.Current.HasSeenTutorial);
    }

    [Fact]
    public void Tutorial_LanguageSwitch_UpdatesTexts()
    {
        Loc.SetLanguage(AppLanguage.French);
        var vm = new MainWindowViewModel();
        vm.StartTutorial();

        string frTitle = vm.TutorialStepTitle;
        Assert.Contains("Importation", frTitle);

        Loc.SetLanguage(AppLanguage.English);
        vm.RefreshLocalizedTexts();

        string enTitle = vm.TutorialStepTitle;
        Assert.Contains("Import", enTitle);
        Assert.NotEqual(frTitle, enTitle);

        // Reset to French for other tests
        Loc.SetLanguage(AppLanguage.French);
    }
}
