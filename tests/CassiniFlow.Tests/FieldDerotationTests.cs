using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using Xunit;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class FieldDerotationTests
{
    [Fact]
    public void ComputeMaxExposureSeconds_MatchesExcelDobsonTableExample1()
    {
        // Données du tableau Excel rotation-champ_dobson.xlsx - Colonne B (Exemple)
        double latitude = 49.62;
        double altitude = 20.0;
        double azimuth = 160.0;
        double sensorRadius = 1936.0 / 2.0; // 968 px
        double admissibleBlur = 1.5;

        double maxSeconds = FieldDerotationEngine.ComputeMaxExposureSeconds(
            latitude,
            altitude,
            azimuth,
            sensorRadius,
            admissibleBlur);

        // Dans le tableau Excel B29 = 33.01 s
        Assert.InRange(maxSeconds, 32.8, 33.2);
    }

    [Fact]
    public void ComputeMaxExposureSeconds_MatchesExcelDobsonTableExampleMars()
    {
        // Données du tableau Excel rotation-champ_dobson.xlsx - Colonne C (Mars 14/2/25)
        double latitude = 49.62;
        double altitude = 65.0;
        double azimuth = 159.0;
        double sensorRadius = 240.0 / 2.0; // 120 px
        double admissibleBlur = 1.5;

        double maxSeconds = FieldDerotationEngine.ComputeMaxExposureSeconds(
            latitude,
            altitude,
            azimuth,
            sensorRadius,
            admissibleBlur);

        // Dans le tableau Excel C29 = 120.53 s
        Assert.InRange(maxSeconds, 120.0, 121.0);
    }

    [Fact]
    public void ComputeMaxExposureSeconds_MatchesExcelDobsonTableHighAltitude()
    {
        // Données du tableau Excel rotation-champ_dobson.xlsx - Colonne D (Alt 81°, Az 180°)
        double latitude = 49.62;
        double altitude = 81.0;
        double azimuth = 180.0;
        double sensorRadius = 1936.0 / 2.0; // 968 px
        double admissibleBlur = 1.5;

        double maxSeconds = FieldDerotationEngine.ComputeMaxExposureSeconds(
            latitude,
            altitude,
            azimuth,
            sensorRadius,
            admissibleBlur);

        // Dans le tableau Excel D29 = 5.16 s
        Assert.InRange(maxSeconds, 5.0, 5.3);
    }

    [Fact]
    public void ComputeFieldRotationRate_ReturnsNonZeroRate()
    {
        double rateDegPerSec = FieldDerotationEngine.ComputeFieldRotationRateDegPerSec(45.0, 45.0, 180.0);
        Assert.NotEqual(0.0, rateDegPerSec);

        double rateDegPerMin = FieldDerotationEngine.ComputeFieldRotationRateDegPerMin(45.0, 45.0, 180.0);
        Assert.Equal(rateDegPerSec * 60.0, rateDegPerMin, precision: 4);
    }

    [Fact]
    public void ComputeFrameDerotationAngle_CenterFrameHasZeroAngle()
    {
        double angleCenter = FieldDerotationEngine.ComputeFrameDerotationAngle(50, 100, 30.0, 49.62, 45.0, 180.0);
        Assert.Equal(0.0, angleCenter, precision: 4);

        double angleFirst = FieldDerotationEngine.ComputeFrameDerotationAngle(0, 100, 30.0, 49.62, 45.0, 180.0);
        double angleLast = FieldDerotationEngine.ComputeFrameDerotationAngle(100, 100, 30.0, 49.62, 45.0, 180.0);
        Assert.True(Math.Abs(angleFirst) > 0.0);
        Assert.Equal(-angleFirst, angleLast, precision: 4);
    }

    [Fact]
    public void ApplyDerotation_RotatesFrameWithoutChangingDimensions()
    {
        var frame = new AstroFrame(64, 64, 3);
        frame.Data[0] = 5000;

        var rotated = FieldDerotationEngine.ApplyDerotation(frame, 5.0);

        Assert.Equal(64, rotated.Width);
        Assert.Equal(64, rotated.Height);
        Assert.Equal(3, rotated.Channels);
    }

    [Fact]
    public void MainWindowViewModel_DerotationProperties_UpdateInfoText()
    {
        var vm = new MainWindowViewModel();
        vm.ObserverLatitudeDegrees = 49.62;
        vm.TargetAltitudeDegrees = 20.0;
        vm.TargetAzimuthDegrees = 160.0;

        string info = vm.DerotationInfoText;
        Assert.Contains("°/min", info);
        Assert.DoesNotContain("Â", info);
        Assert.DoesNotContain("Ã", info);
        Assert.True(info.Contains("sans d") || info.Contains("without derotation"));
    }

    [Fact]
    public void Derotation_Accordion_MutualExclusionAndVisibility()
    {
        var vm = new MainWindowViewModel();

        // Visible par défaut (DirectStreamAllInOne)
        Assert.True(vm.IsSectionDerotationVisible);
        Assert.Equal("⟳", vm.SectionDerotationBadgeText);

        // Ouverture de l'accordéon Dérotation -> ferme les autres
        vm.IsSection1Expanded = true;
        Assert.False(vm.IsSectionDerotationExpanded);

        vm.IsSectionDerotationExpanded = true;
        Assert.False(vm.IsSection1Expanded);
        Assert.False(vm.IsSection2Expanded);
        Assert.False(vm.IsSection3Expanded);
        Assert.False(vm.IsSection4Expanded);
        Assert.True(vm.IsSectionDerotationExpanded);

        // Ouverture d'une autre section -> ferme Dérotation
        vm.IsSection3Expanded = true;
        Assert.False(vm.IsSectionDerotationExpanded);

        // Masqué en mode StackingOnly
        vm.SetWorkflowMode("stack");
        Assert.False(vm.IsSectionDerotationVisible);
        Assert.False(vm.IsSectionDerotationExpanded);

        // Réactivé en mode PreProcessingOnly
        vm.SetWorkflowMode("prep");
        Assert.True(vm.IsSectionDerotationVisible);
    }

    [Fact]
    public void Derotation_Translations_NoMojibakeAndNoUselessParentheses()
    {
        // Test Français
        Loc.SetLanguage(AppLanguage.French);
        string frEnable = Loc.Get("Derotation.Enable");
        string frLat = Loc.Get("Derotation.Latitude");
        string frAlt = Loc.Get("Derotation.Altitude");
        string frAz = Loc.Get("Derotation.Azimuth");
        string frTab = Loc.Get("Tab.DerotationOptions");

        Assert.DoesNotContain("(", frEnable);
        Assert.DoesNotContain(")", frEnable);
        Assert.DoesNotContain("(", frLat);
        Assert.DoesNotContain("(", frAlt);
        Assert.DoesNotContain("(", frAz);
        Assert.DoesNotContain("Ã", frEnable);
        Assert.DoesNotContain("Â", frEnable);
        Assert.DoesNotContain("Ã", frTab);
        Assert.NotEmpty(Loc.Get("Derotation.EnableTip"));
        Assert.NotEmpty(Loc.Get("Derotation.LatitudeTip"));
        Assert.NotEmpty(Loc.Get("Derotation.AltitudeTip"));
        Assert.NotEmpty(Loc.Get("Derotation.AzimuthTip"));

        // Test Anglais
        Loc.SetLanguage(AppLanguage.English);
        string enEnable = Loc.Get("Derotation.Enable");
        string enLat = Loc.Get("Derotation.Latitude");
        string enAlt = Loc.Get("Derotation.Altitude");
        string enAz = Loc.Get("Derotation.Azimuth");
        string enTab = Loc.Get("Tab.DerotationOptions");

        Assert.DoesNotContain("(", enEnable);
        Assert.DoesNotContain(")", enEnable);
        Assert.DoesNotContain("(", enLat);
        Assert.DoesNotContain("(", enAlt);
        Assert.DoesNotContain("(", enAz);
        Assert.DoesNotContain("Ã", enEnable);
        Assert.DoesNotContain("Â", enEnable);
        Assert.DoesNotContain("Ã", enTab);
        Assert.NotEmpty(Loc.Get("Derotation.EnableTip"));
        Assert.NotEmpty(Loc.Get("Derotation.LatitudeTip"));
        Assert.NotEmpty(Loc.Get("Derotation.AltitudeTip"));
        Assert.NotEmpty(Loc.Get("Derotation.AzimuthTip"));
    }
}
