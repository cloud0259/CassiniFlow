using Avalonia.Controls;
using Avalonia.Interactivity;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Loc.LocalizeTree(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
