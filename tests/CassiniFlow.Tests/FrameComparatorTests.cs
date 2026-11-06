using System;
using System.Collections.Generic;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using Xunit;

namespace CassiniFlow.Tests;

public class FrameComparatorTests
{
    [Fact]
    public void EvaluateScore_ReturnsValidScore_ForSynthesizedFrames()
    {
        // 1. Sharp frame with high gradient transitions
        var sharpFrame = new AstroFrame(64, 64, 1);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                sharpFrame.Data[y * 64 + x] = (ushort)(((x / 4) % 2 == 0 ? 50000 : 10000));
            }
        }

        // 2. Blurry / flat frame
        var blurFrame = new AstroFrame(64, 64, 1);
        for (int i = 0; i < blurFrame.Data.Length; i++)
        {
            blurFrame.Data[i] = 25000;
        }

        double sharpScore = StackedTierQualityEvaluator.EvaluateScore(sharpFrame);
        double blurScore = StackedTierQualityEvaluator.EvaluateScore(blurFrame);

        Assert.True(sharpScore > blurScore);
        Assert.True(sharpScore > 0);
    }

    [Fact]
    public void EvaluateTiers_CorrectlyIdentifies_BestRenderingTier()
    {
        var frame15 = new AstroFrame(32, 32, 1);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                frame15.Data[y * 32 + x] = (ushort)((x % 2 == 0) ? 60000 : 5000);
            }
        }

        var frame50 = new AstroFrame(32, 32, 1);
        for (int i = 0; i < frame50.Data.Length; i++)
        {
            frame50.Data[i] = 10000;
        }

        var tiers = new List<(StackedTierSummary Summary, AstroFrame Frame)>
        {
            (new StackedTierSummary(15.0, 150, 1000, 48, 32, "C:\\Out\\tier_15.tif"), frame15),
            (new StackedTierSummary(50.0, 500, 1000, 48, 32, "C:\\Out\\tier_50.tif"), frame50)
        };

        var evaluated = StackedTierQualityEvaluator.EvaluateTiers(tiers);

        Assert.Equal(2, evaluated.Count);
        Assert.True(evaluated[0].IsBest);
        Assert.False(evaluated[1].IsBest);
        Assert.Equal("15%", evaluated[0].TierLabel);
        Assert.Contains("MEILLEUR", evaluated[0].BadgeText);
    }

    [Fact]
    public void FrameComparatorViewModel_InitializesCorrectly_WithBestTier()
    {
        var item1 = new StackedTierComparisonItem
        {
            Percentage = 15.0,
            FramesStacked = 150,
            TotalValidFrames = 1000,
            QualityScore = 95.5,
            IsBest = true,
            TierLabel = "15%",
            OutputFilePath = "tier15.png"
        };
        var item2 = new StackedTierComparisonItem
        {
            Percentage = 30.0,
            FramesStacked = 300,
            TotalValidFrames = 1000,
            QualityScore = 82.0,
            IsBest = false,
            TierLabel = "30%",
            OutputFilePath = "tier30.png"
        };

        var uiList = new List<StackedTierUiItem>
        {
            new(item1, null),
            new(item2, null)
        };

        var vm = new FrameComparatorViewModel(uiList);

        Assert.Equal(2, vm.Tiers.Count);
        Assert.NotNull(vm.BestTier);
        Assert.Equal(15.0, vm.BestTier.Percentage);
        Assert.True(vm.BestTier.IsBest);
    }
}
