using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CassiniFlow.Desktop.Localization;
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

    // --- Réordonnancement des fichiers sources à la souris (glisser les lignes) ---
    private SourceFileItem? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragItem = null;
        _dragging = false;
        if (sender is not ListBox list) return;
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<CheckBox>() is not null) return;
        var lbi = (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>();
        if (lbi?.DataContext is SourceFileItem item)
        {
            _dragItem = item;
            _dragStart = e.GetPosition(list);
        }
    }

    private void OnFileListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || sender is not ListBox list || DataContext is not MainWindowViewModel vm) return;
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) { _dragItem = null; _dragging = false; return; }

        var pos = e.GetPosition(list);
        if (!_dragging)
        {
            if (Math.Abs(pos.Y - _dragStart.Y) < 6) return;
            _dragging = true;
            list.Cursor = new Cursor(StandardCursorType.SizeAll);
        }

        foreach (var lbi in list.GetVisualDescendants().OfType<ListBoxItem>())
        {
            var p = e.GetPosition(lbi);
            if (p.Y >= 0 && p.Y <= lbi.Bounds.Height && lbi.DataContext is SourceFileItem target && !ReferenceEquals(target, _dragItem))
            {
                int from = vm.SourceFiles.IndexOf(_dragItem);
                int to = vm.SourceFiles.IndexOf(target);
                if (from >= 0 && to >= 0)
                {
                    vm.SourceFiles.Move(from, to);
                    vm.SelectedSourceFile = _dragItem;
                }
                break;
            }
        }
    }

    private void OnFileListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is ListBox list) list.Cursor = Cursor.Default;
        _dragItem = null;
        _dragging = false;
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
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

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
        if (DataContext is not MainWindowViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Pick("Sélectionner des vidéos ou images (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF)", "Select videos or images (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF)"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Pick("Vidéos & Séquences (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF, JPG)", "Videos & Sequences (AVI, MP4, MOV, MKV, SER, FITS, BMP, PNG, TIFF, JPG)"))
                {
                    Patterns = ["*.avi", "*.mp4", "*.mov", "*.mkv", "*.webm", "*.wmv", "*.ser", "*.fit", "*.fits", "*.bmp", "*.png", "*.tif", "*.tiff", "*.jpg", "*.jpeg"]
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

    private async void OnSelectOutputFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

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
