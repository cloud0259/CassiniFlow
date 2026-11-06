using System;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Domain.Exceptions;
using CassiniFlow.Domain.Services;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _hasActiveError;
    [ObservableProperty] private string _activeErrorCode = string.Empty;
    [ObservableProperty] private string _activeErrorTitle = string.Empty;
    [ObservableProperty] private string _activeErrorMessage = string.Empty;
    [ObservableProperty] private string? _activeErrorSuggestion;
    [ObservableProperty] private bool _hasActiveErrorSuggestion;
    [ObservableProperty] private string _activeErrorTechnicalDetails = string.Empty;
    [ObservableProperty] private string _errorCopyButtonText = Loc.Get("Errors.CopyDetails");

    public void ShowError(Exception ex)
    {
        var details = CassiniFlowErrorExplainer.Explain(ex, Loc.Localization);
        ShowError(details);
    }

    public void ShowError(CassiniFlowErrorDetails details)
    {
        ActiveErrorCode = details.ErrorCode;
        ActiveErrorTitle = details.Title;
        ActiveErrorMessage = details.Message;
        ActiveErrorSuggestion = details.SuggestedResolution;
        HasActiveErrorSuggestion = !string.IsNullOrWhiteSpace(details.SuggestedResolution);
        ActiveErrorTechnicalDetails = details.TechnicalDetails;
        ErrorCopyButtonText = Loc.Get("Errors.CopyDetails");
        HasActiveError = true;
    }

    [RelayCommand]
    public void DismissError()
    {
        HasActiveError = false;
        ActiveErrorCode = string.Empty;
        ActiveErrorTitle = string.Empty;
        ActiveErrorMessage = string.Empty;
        ActiveErrorSuggestion = null;
        HasActiveErrorSuggestion = false;
        ActiveErrorTechnicalDetails = string.Empty;
    }

    [RelayCommand]
    public async Task CopyErrorDetailsAsync()
    {
        if (string.IsNullOrWhiteSpace(ActiveErrorTechnicalDetails) && string.IsNullOrWhiteSpace(ActiveErrorMessage)) return;

        string fullReport = $"[{ActiveErrorCode}] {ActiveErrorTitle}\n{ActiveErrorMessage}\n\nResolution:\n{ActiveErrorSuggestion}\n\nTechnical details:\n{ActiveErrorTechnicalDetails}";

        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(fullReport);
                ErrorCopyButtonText = Loc.Get("Errors.Copied");
                _ = Task.Delay(2500).ContinueWith(_ =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ErrorCopyButtonText = Loc.Get("Errors.CopyDetails");
                    });
                });
            }
        }
        catch
        {
            // Ignore clipboard errors
        }
    }
}
