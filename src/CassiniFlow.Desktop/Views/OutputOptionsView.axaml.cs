using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.ViewModels;

namespace CassiniFlow.Desktop.Views;

public partial class OutputOptionsView : UserControl
{
    public OutputOptionsView()
    {
        InitializeComponent();
    }

    private async void OnSelectOutputFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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
}
