using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.Services;
using CassiniFlow.Domain.Ports.Inbound;
using Microsoft.Extensions.DependencyInjection;

namespace CassiniFlow.Desktop.Views;

public partial class AboutWindow : Window
{
    private readonly IUpdateCheckerService? _updateChecker;
    private readonly IUserSettingsService? _userSettings;
    private string? _downloadUrl;

    public AboutWindow()
    {
        InitializeComponent();

        if (App.Services != null)
        {
            _updateChecker = App.Services.GetService<IUpdateCheckerService>();
            _userSettings = App.Services.GetService<IUserSettingsService>();
        }

        if (_userSettings != null)
        {
            ChkAutoUpdate.IsChecked = _userSettings.Current.CheckUpdatesOnStartup;
        }
    }

    private void OnAutoUpdateCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (_userSettings != null)
        {
            _userSettings.Current.CheckUpdatesOnStartup = ChkAutoUpdate.IsChecked == true;
            _userSettings.Save();
        }
    }

    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e)
    {
        if (_updateChecker == null) return;

        BtnCheckUpdates.IsEnabled = false;
        TxtUpdateStatus.IsVisible = true;
        TxtUpdateStatus.Text = Loc.Get("About.CheckingUpdates");
        BtnDownloadUpdate.IsVisible = false;

        try
        {
            var info = await _updateChecker.CheckForUpdateAsync();
            if (info.IsUpdateAvailable)
            {
                TxtUpdateStatus.Text = Loc.Format("About.UpdateAvailable", info.LatestVersion);
                _downloadUrl = info.HtmlUrl;
                BtnDownloadUpdate.IsVisible = true;
            }
            else
            {
                TxtUpdateStatus.Text = Loc.Get("About.AppIsUpToDate");
                BtnDownloadUpdate.IsVisible = false;
            }
        }
        catch
        {
            TxtUpdateStatus.Text = Loc.Get("About.CheckUpdateError");
        }
        finally
        {
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    private void OnDownloadUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_downloadUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true });
            }
            catch
            {
                // Fallback silencieux
            }
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
