using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Desktop.Theme;
using CassiniFlow.Desktop.ViewModels;

namespace CassiniFlow.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly IThemeService _themeService;

    public MainWindow() : this(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IThemeService>(App.Services))
    {
    }

    public MainWindow(IThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);

        Loaded += (_, _) => UpdateMenuCheckStates();
        Opened += (_, _) =>
        {
            ApplyLanguage();
            UpdateMenuCheckStates();

            // Démarrage automatique du didacticiel au premier lancement
            if (DataContext is MainWindowViewModel vm && !vm.HasSeenTutorial)
            {
                vm.StartTutorial();
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.RequestOpenSettings += OpenSettingsDialog;
                vm.RequestImportSettings += () => OnImportSettingsClick(null, null!);
                vm.RequestExportSettings += () => OnExportSettingsClick(null, null!);
                vm.RequestOpenFrameComparator += OpenFrameComparatorDialog;
            }
        };

        _themeService.ThemeChanged += UpdateMenuCheckStates;
        Loc.LanguageChanged += () =>
        {
            Loc.LocalizeTree(this);
            UpdateMenuCheckStates();
        };

        UpdateMenuCheckStates();
    }

    private void ApplyLanguage()
    {
        Loc.LocalizeTree(this);
        UpdateMenuCheckStates();
        (DataContext as MainWindowViewModel)?.RefreshLocalizedTexts();
    }

    private void UpdateMenuCheckStates()
    {
        if (FrenchMenuItem != null)
        {
            FrenchMenuItem.IsChecked = !Loc.IsEnglish;
        }
        if (EnglishMenuItem != null)
        {
            EnglishMenuItem.IsChecked = Loc.IsEnglish;
        }

        if (DarkThemeMenuItem != null)
        {
            DarkThemeMenuItem.IsChecked = _themeService.CurrentTheme == AppTheme.DeepSpaceDark;
        }
        if (LightThemeMenuItem != null)
        {
            LightThemeMenuItem.IsChecked = _themeService.CurrentTheme == AppTheme.CleanLabLight;
        }
        if (NightVisionMenuItem != null)
        {
            NightVisionMenuItem.IsChecked = _themeService.CurrentTheme == AppTheme.NightVision;
        }

        if (ThemeQuickToggleButton != null)
        {
            ThemeQuickToggleButton.Content = _themeService.CurrentTheme switch
            {
                AppTheme.CleanLabLight => "☀️",
                AppTheme.NightVision => "🔴",
                _ => "🌙"
            };
        }
    }

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        OpenSettingsDialog();
    }

    private void OpenSettingsDialog()
    {
        var settingsWin = new SettingsWindow
        {
            DataContext = this.DataContext
        };
        settingsWin.ShowDialog(this);
    }

    private async void OnImportSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsProcessing) return;

        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = Loc.Get("Dialogs.ImportSettingsTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(Loc.Get("Dialogs.FilterSettingsFiles"))
                {
                    Patterns = ["*.cfset", "*.json", "*.pof"]
                },
                Avalonia.Platform.Storage.FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path && !string.IsNullOrWhiteSpace(path))
        {
            await vm.ImportSettingsFromFileAsync(path);
        }
    }

    private async void OnExportSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = Loc.Get("Dialogs.ExportSettingsTitle"),
            DefaultExtension = "cfset",
            SuggestedFileName = "cassiniflow_preset.cfset",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("CassiniFlow Preset (*.cfset)") { Patterns = ["*.cfset"] },
                new Avalonia.Platform.Storage.FilePickerFileType("JSON Settings (*.json)") { Patterns = ["*.json"] },
                new Avalonia.Platform.Storage.FilePickerFileType("CassiniFlow Options File (*.pof)") { Patterns = ["*.pof"] }
            ]
        });

        if (file?.TryGetLocalPath() is { } path && !string.IsNullOrWhiteSpace(path))
        {
            await vm.ExportSettingsToFileAsync(path);
        }
    }

    private void OpenFrameComparatorDialog()
    {
        if (DataContext is MainWindowViewModel vm && vm.StackedComparisonItems.Count > 0)
        {
            var comparatorWin = new FrameComparatorWindow
            {
                DataContext = new FrameComparatorViewModel(vm.StackedComparisonItems)
            };
            comparatorWin.ShowDialog(this);
        }
    }

    private void OnSetLanguageFrenchClick(object? sender, RoutedEventArgs e)
    {
        Loc.SetLanguage(AppLanguage.French);
        ApplyLanguage();
    }

    private void OnSetLanguageEnglishClick(object? sender, RoutedEventArgs e)
    {
        Loc.SetLanguage(AppLanguage.English);
        ApplyLanguage();
    }

    private void OnSetThemeDarkClick(object? sender, RoutedEventArgs e)
    {
        _themeService.ApplyTheme(AppTheme.DeepSpaceDark);
        UpdateMenuCheckStates();
    }

    private void OnSetThemeLightClick(object? sender, RoutedEventArgs e)
    {
        _themeService.ApplyTheme(AppTheme.CleanLabLight);
        UpdateMenuCheckStates();
    }

    private void OnSetThemeNightVisionClick(object? sender, RoutedEventArgs e)
    {
        _themeService.ApplyTheme(AppTheme.NightVision);
        UpdateMenuCheckStates();
    }

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        _themeService.ToggleNextTheme();
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsProcessing: true } or MainWindowViewModel { IsLoadingFiles: true } or MainWindowViewModel { IsMeasuringPlanetSizes: true })
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsProcessing || vm.IsLoadingFiles || vm.IsMeasuringPlanetSizes) return;

        var files = e.Data.GetFiles();
        if (files is null) return;

        var localPaths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();

        if (localPaths.Count > 0)
        {
            switch (vm.SelectedSourceFileTabIndex)
            {
                case 1:
                    vm.AddDarkFiles(localPaths);
                    break;
                case 2:
                    vm.AddFlatFiles(localPaths);
                    break;
                case 3:
                    vm.AddFlatDarkFiles(localPaths);
                    break;
                default:
                    await vm.AddPhysicalFilesAsync(localPaths);
                    break;
            }
        }
    }

    private async void OnAddVideoFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("Dialogs.SelectSourceFilesTitle"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Get("Dialogs.FilterVideosAndImages"))
                {
                    Patterns = ["*.avi", "*.mp4", "*.mov", "*.mkv", "*.webm", "*.wmv", "*.ser", "*.fit", "*.fits", "*.bmp", "*.png", "*.tif", "*.tiff", "*.jpg", "*.jpeg", "*.dng"]
                },
                FilePickerFileTypes.All
            ]
        });

        var localPaths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();

        if (localPaths.Count > 0)
        {
            await vm.AddPhysicalFilesAsync(localPaths);
        }
    }

    private async System.Threading.Tasks.Task<System.Collections.Generic.List<string>> PickCalibrationFilesAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Astrophotography Calibration (SER, FITS, TIFF, PNG, BMP, AVI)")
                {
                    Patterns = ["*.ser", "*.fit", "*.fits", "*.tif", "*.tiff", "*.png", "*.bmp", "*.avi"]
                },
                FilePickerFileTypes.All
            ]
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();
    }

    private async void OnAddDarkFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;
        var paths = await PickCalibrationFilesAsync(Loc.Get("Dialogs.SelectDarkFiles"));
        if (paths.Count > 0) vm.AddDarkFiles(paths);
    }

    private async void OnAddFlatFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;
        var paths = await PickCalibrationFilesAsync(Loc.Get("Dialogs.SelectFlatFiles"));
        if (paths.Count > 0) vm.AddFlatFiles(paths);
    }

    private async void OnAddFlatDarkFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;
        var paths = await PickCalibrationFilesAsync(Loc.Get("Dialogs.SelectFlatDarkFiles"));
        if (paths.Count > 0) vm.AddFlatDarkFiles(paths);
    }

    private async void OnSelectOutputFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Get("Dialogs.SelectOutputFolderTitle"),
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            string? path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                vm.CustomOutputDirectory = path;
            }
        }
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var aboutWin = new AboutWindow();
        aboutWin.ShowDialog(this);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
