using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Desktop.Views;

namespace CassiniFlow.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CassiniFlow.Desktop.Localization.Loc.LoadSettings();
        CassiniFlow.Desktop.Theme.ThemeManager.Initialize();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
