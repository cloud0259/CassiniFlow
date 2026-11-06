using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Desktop.ViewModels;

public sealed partial class FrameComparatorViewModel : ObservableObject
{
    public ObservableCollection<StackedTierUiItem> Tiers { get; } = [];

    [ObservableProperty] private StackedTierUiItem? _bestTier;
    [ObservableProperty] private StackedTierUiItem? _selectedTier;
    [ObservableProperty] private string _summaryBannerText = string.Empty;

    public event Action? RequestClose;

    public FrameComparatorViewModel(IEnumerable<StackedTierUiItem> items)
    {
        foreach (var item in items)
        {
            Tiers.Add(item);
        }

        BestTier = Tiers.FirstOrDefault(t => t.IsBest) ?? Tiers.FirstOrDefault();
        SelectedTier = BestTier;

        if (BestTier != null)
        {
            SummaryBannerText = Loc.Format("FrameComparator.SummaryBanner", BestTier.Percentage, BestTier.QualityScore);
        }
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke();

    [RelayCommand]
    public void OpenOutputFolder()
    {
        if (SelectedTier != null)
        {
            SelectedTier.OpenContainingFolder();
        }
        else if (BestTier != null)
        {
            BestTier.OpenContainingFolder();
        }
    }
}
