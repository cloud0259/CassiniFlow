using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.ViewModels;

namespace CassiniFlow.Desktop.Views;

public partial class SourceFilesView : UserControl
{
    private SourceFileItem? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    public SourceFilesView()
    {
        InitializeComponent();
    }

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

    private async void OnAddVideoFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.IsLoadingFiles || vm.IsProcessing || vm.IsMeasuringPlanetSizes) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
}
