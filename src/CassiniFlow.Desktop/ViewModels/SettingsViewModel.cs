using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.Theme;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAstroLocalization _loc;
    private readonly IUserSettingsService _userSettings;
    private readonly IThemeService _themeService;

    [ObservableProperty] private int _maxCpuUsagePercent = 80;
    [ObservableProperty] private int _cpuWarningThresholdPercent = 85;
    [ObservableProperty] private string _cpuStatusSummary = string.Empty;
    [ObservableProperty] private int _cpuCoreCount = Environment.ProcessorCount;

    [ObservableProperty] private int _selectedThemeIndex;
    [ObservableProperty] private int _selectedLanguageIndex;

    [ObservableProperty] private bool _winJuposNaming;
    [ObservableProperty] private int _selectedOutputFormatIndex;
    [ObservableProperty] private string _outputSuffix = "_cassiniflow";
    [ObservableProperty] private string _customOutputDirectory = string.Empty;

    [ObservableProperty] private bool _isSettingsSavedFeedbackVisible;

    public SettingsViewModel(
        IAstroLocalization localization,
        IUserSettingsService userSettings,
        IThemeService themeService)
    {
        _loc = localization ?? throw new ArgumentNullException(nameof(localization));
        _userSettings = userSettings ?? throw new ArgumentNullException(nameof(userSettings));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));

        _maxCpuUsagePercent = _userSettings.Current.MaxCpuUsagePercent;
        _cpuWarningThresholdPercent = _userSettings.Current.CpuWarningThresholdPercent;
        _selectedThemeIndex = _userSettings.Current.Theme?.ToLowerInvariant() switch
        {
            "light" => 1,
            "night_vision" or "nightvision" or "red" => 2,
            _ => 0
        };
        _selectedLanguageIndex = string.Equals(_userSettings.Current.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _winJuposNaming = _userSettings.Current.WinJuposNaming;
        _selectedOutputFormatIndex = Math.Clamp(_userSettings.Current.SelectedOutputFormatIndex, 0, 6);
        if (!string.IsNullOrEmpty(_userSettings.Current.OutputSuffix))
        {
            _outputSuffix = _userSettings.Current.OutputSuffix;
        }
        _customOutputDirectory = _userSettings.Current.CustomOutputDirectory;

        _themeService.ThemeChanged += OnThemeChanged;
        UpdateCpuStatus();
    }

    partial void OnMaxCpuUsagePercentChanged(int value)
    {
        UpdateCpuStatus();
        _userSettings.UpdateCpuSettings(value, CpuWarningThresholdPercent);
    }

    partial void OnCpuWarningThresholdPercentChanged(int value)
    {
        UpdateCpuStatus();
        _userSettings.UpdateCpuSettings(MaxCpuUsagePercent, value);
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            1 => AppTheme.CleanLabLight,
            2 => AppTheme.NightVision,
            _ => AppTheme.DeepSpaceDark
        };
        _themeService.ApplyTheme(theme, save: true);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var lang = value == 1 ? AppLanguage.English : AppLanguage.French;
        Loc.SetLanguage(lang, save: true);
    }

    private void OnThemeChanged()
    {
        int targetIdx = _themeService.CurrentTheme switch
        {
            AppTheme.CleanLabLight => 1,
            AppTheme.NightVision => 2,
            _ => 0
        };
        if (SelectedThemeIndex != targetIdx)
        {
            SelectedThemeIndex = targetIdx;
        }
    }

    private void UpdateCpuStatus()
    {
        int totalCores = Environment.ProcessorCount;
        int activeCores = Math.Max(1, (int)Math.Round(totalCores * (MaxCpuUsagePercent / 100.0)));
        CpuStatusSummary = _loc.Format("UI.CpuStatusSummary", activeCores, totalCores, MaxCpuUsagePercent);
    }

    [RelayCommand]
    public async Task SaveUserSettings()
    {
        _userSettings.UpdateCpuSettings(MaxCpuUsagePercent, CpuWarningThresholdPercent);
        _userSettings.Save();

        IsSettingsSavedFeedbackVisible = true;
        await Task.Delay(2500);
        IsSettingsSavedFeedbackVisible = false;
    }
}
