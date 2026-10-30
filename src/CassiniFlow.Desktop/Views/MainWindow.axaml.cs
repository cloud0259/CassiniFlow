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
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);

        Loaded += (_, _) => UpdateMenuCheckStates();
        Opened += (_, _) =>
        {
            ApplyLanguage();
            UpdateMenuCheckStates();

            // Démarrage automatique du didacticiel au premier lancement
            if (DataContext is MainWindowViewModel vm && !UserSettingsService.Current.HasSeenTutorial)
            {
                vm.StartTutorial();
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.RequestOpenSettings += OpenSettingsDialog;
            }
        };

        ThemeManager.ThemeChanged += UpdateMenuCheckStates;
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
            DarkThemeMenuItem.IsChecked = ThemeManager.CurrentTheme == AppTheme.DeepSpaceDark;
        }
        if (LightThemeMenuItem != null)
        {
            LightThemeMenuItem.IsChecked = ThemeManager.CurrentTheme == AppTheme.CleanLabLight;
        }
        if (NightVisionMenuItem != null)
        {
            NightVisionMenuItem.IsChecked = ThemeManager.CurrentTheme == AppTheme.NightVision;
        }

        if (ThemeQuickToggleButton != null)
        {
            ThemeQuickToggleButton.Content = ThemeManager.CurrentTheme switch
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
        ThemeManager.ApplyTheme(AppTheme.DeepSpaceDark);
        UpdateMenuCheckStates();
    }

    private void OnSetThemeLightClick(object? sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyTheme(AppTheme.CleanLabLight);
        UpdateMenuCheckStates();
    }

    private void OnSetThemeNightVisionClick(object? sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyTheme(AppTheme.NightVision);
        UpdateMenuCheckStates();
    }

    private void OnToggleThemeClick(object? sender, RoutedEventArgs e)
    {
        ThemeManager.ToggleNextTheme();
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
            await vm.AddPhysicalFilesAsync(localPaths);
        }
    }

    private async void OnAddVideoFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Pick("Sélectionner des vidéos ou images (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF, DNG)", "Select videos or images (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF, DNG)"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Pick("Vidéos & Séquences (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF, JPG, DNG)", "Videos & Sequences (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF, JPG, DNG)"))
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
        var paths = await PickCalibrationFilesAsync(Loc.Pick("Sélectionner des trames Dark (SER, FITS, TIFF, PNG)", "Select Dark frames (SER, FITS, TIFF, PNG)"));
        if (paths.Count > 0) vm.AddDarkFiles(paths);
    }

    private async void OnAddFlatFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;
        var paths = await PickCalibrationFilesAsync(Loc.Pick("Sélectionner des trames Flat (SER, FITS, TIFF, PNG)", "Select Flat frames (SER, FITS, TIFF, PNG)"));
        if (paths.Count > 0) vm.AddFlatFiles(paths);
    }

    private async void OnAddFlatDarkFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;
        var paths = await PickCalibrationFilesAsync(Loc.Pick("Sélectionner des trames Flat-Dark (SER, FITS, TIFF, PNG)", "Select Flat-Dark frames (SER, FITS, TIFF, PNG)"));
        if (paths.Count > 0) vm.AddFlatDarkFiles(paths);
    }

    private async void OnSelectOutputFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Pick("Choisir le dossier de sortie", "Choose the output folder"),
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
