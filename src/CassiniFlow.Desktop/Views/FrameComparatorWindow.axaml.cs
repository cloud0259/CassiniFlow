using Avalonia.Controls;
using Avalonia.Interactivity;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.ViewModels;

namespace CassiniFlow.Desktop.Views;

public partial class FrameComparatorWindow : Window
{
    public FrameComparatorWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Loc.LocalizeTree(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FrameComparatorViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
