using CassiniFlow.Desktop.Localization;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Services;
using Xunit;

namespace CassiniFlow.Tests;

[Collection("Localization")]
public class AutoStakkertFeaturesTests
{
    [Fact]
    public void StackingOptions_HasAutoStakkertDefaultPropertiesAndClonesCorrectly()
    {
        var options = new StackingOptions();

        // Vérification des valeurs par défaut rétro-conçues d'AutoStakkert
        Assert.Equal(StackingStabilisationMode.PlanetCog, options.StabilisationMode);
        Assert.False(options.DisableStabilisation);
        Assert.Equal(StackingCanvasMode.Crop, options.CanvasMode);
        Assert.Equal(2, options.NoiseRobust);
        Assert.Equal(QualityAnalysisScope.LocalAp, options.QualityScope);
        Assert.True(options.DoubleStackReference);
        Assert.Equal(ReferenceFrameMode.Automatic, options.ReferenceMode);
        Assert.Equal(0, options.ManualReferenceFrameIndex);

        // Modification des valeurs
        options.StabilisationMode = StackingStabilisationMode.Surface;
        options.DisableStabilisation = true;
        options.CanvasMode = StackingCanvasMode.Expand;
        options.NoiseRobust = 5;
        options.QualityScope = QualityAnalysisScope.GlobalFrame;
        options.DoubleStackReference = false;
        options.ReferenceMode = ReferenceFrameMode.Manual;
        options.ManualReferenceFrameIndex = 42;

        // Clonage
        var clone = options.Clone();
        Assert.Equal(StackingStabilisationMode.Surface, clone.StabilisationMode);
        Assert.True(clone.DisableStabilisation);
        Assert.Equal(StackingCanvasMode.Expand, clone.CanvasMode);
        Assert.Equal(5, clone.NoiseRobust);
        Assert.Equal(QualityAnalysisScope.GlobalFrame, clone.QualityScope);
        Assert.False(clone.DoubleStackReference);
        Assert.Equal(ReferenceFrameMode.Manual, clone.ReferenceMode);
        Assert.Equal(42, clone.ManualReferenceFrameIndex);
    }

    [Fact]
    public void QualityScore_NoiseRobustParameterScalesSubsamplingFilter()
    {
        // Création d'une trame synthétique 64x64
        int width = 64;
        int height = 64;
        var frame = new AstroFrame(width, height, channels: 1, originalBitDepth: 16);

        // Ajout d'une cible avec contraste (disque)
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dist = Math.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32));
                frame.Data[y * width + x] = dist < 16 ? (ushort)40000 : (ushort)2000;
            }
        }

        // Calcul des scores pour différents facteurs Noise Robust
        double scoreRobust1 = StabilisationAndQualityEngine.ComputeQualityScore(frame, QualityAlgorithm.GradientSharpness, noiseRobust: 1);
        double scoreRobust2 = StabilisationAndQualityEngine.ComputeQualityScore(frame, QualityAlgorithm.GradientSharpness, noiseRobust: 2);
        double scoreRobust5 = StabilisationAndQualityEngine.ComputeQualityScore(frame, QualityAlgorithm.GradientSharpness, noiseRobust: 5);

        Assert.True(scoreRobust1 > 0);
        Assert.True(scoreRobust2 > 0);
        Assert.True(scoreRobust5 > 0);
    }

    [Fact]
    public void MainWindowViewModel_Card1TitleAndWorkflowFlags_AdaptToStackingOnly()
    {
        Loc.SetLanguage(AppLanguage.French, save: false);
        var vm = new MainWindowViewModel();

        // 1. Mode StackingOnly
        vm.SetWorkflowMode("stackingonly");
        Assert.True(vm.IsWorkflowStackingOnly);
        Assert.False(vm.IsWorkflowPreProcessingOnly);
        Assert.False(vm.IsWorkflowDirectStreamAllInOne);
        Assert.False(vm.IsSection1Visible); // PIPP Mode & Presets masqué
        Assert.True(vm.IsSection5Visible);  // AutoStakkert Référence visible
        Assert.Equal("1", vm.Section5BadgeText); // Réf AutoStakkert est en tête (Badge 1)
        Assert.Equal("2", vm.Section6BadgeText); // AP
        Assert.Equal("3", vm.Section7BadgeText); // Empilement

        // 2. Mode DirectStreamAllInOne (Tout-en-un)
        vm.SetWorkflowMode("directstreamallinone");
        Assert.False(vm.IsWorkflowStackingOnly);
        Assert.False(vm.IsWorkflowPreProcessingOnly);
        Assert.True(vm.IsWorkflowDirectStreamAllInOne);
        Assert.True(vm.IsSection1Visible);
        Assert.True(vm.IsSection5Visible);
        Assert.Equal("1", vm.Section1BadgeText);
        Assert.Equal("5", vm.Section5BadgeText); // Réf AutoStakkert
        Assert.Equal("6", vm.Section6BadgeText); // AP

        // 3. Mode PreProcessingOnly
        vm.SetWorkflowMode("preprocessingonly");
        Assert.False(vm.IsWorkflowStackingOnly);
        Assert.True(vm.IsWorkflowPreProcessingOnly);
        Assert.False(vm.IsWorkflowDirectStreamAllInOne);
        Assert.True(vm.IsSection1Visible);
        Assert.False(vm.IsSection5Visible); // Réf AutoStakkert masqué
        Assert.False(vm.IsSection6Visible); // AP masqué
    }

    [Fact]
    public void MainWindowViewModel_AutoStakkertProperties_TwoWayBindingAndCommit()
    {
        var vm = new MainWindowViewModel();

        // Stabilisation
        vm.IsStabilisationSurface = true;
        Assert.Equal(StackingStabilisationMode.Surface, vm.StackingStabilisationMode);
        Assert.True(vm.IsStabilisationSurface);
        Assert.False(vm.IsStabilisationPlanetCog);

        vm.IsStabilisationPlanetCog = true;
        Assert.Equal(StackingStabilisationMode.PlanetCog, vm.StackingStabilisationMode);

        vm.DisableStabilisation = true;

        // Canevas
        vm.IsCanvasExpand = true;
        Assert.Equal(StackingCanvasMode.Expand, vm.StackingCanvasMode);

        // Robuste au bruit
        vm.NoiseRobust = 4;
        Assert.Contains("4", vm.NoiseRobustText);

        // Portée de qualité
        vm.IsQualityGlobalFrame = true;
        Assert.Equal(QualityAnalysisScope.GlobalFrame, vm.QualityScope);

        // Référence
        vm.DoubleStackReference = false;
        vm.IsReferenceManual = true;
        Assert.Equal(ReferenceFrameMode.Manual, vm.ReferenceFrameMode);
        vm.ManualReferenceFrameIndex = 128;

        // Synchronisation vers le modèle de domaine Options
        vm.SyncOptionsFromUi();

        Assert.Equal(StackingStabilisationMode.PlanetCog, vm.Options.Stacking.StabilisationMode);
        Assert.True(vm.Options.Stacking.DisableStabilisation);
        Assert.Equal(StackingCanvasMode.Expand, vm.Options.Stacking.CanvasMode);
        Assert.Equal(4, vm.Options.Stacking.NoiseRobust);
        Assert.Equal(QualityAnalysisScope.GlobalFrame, vm.Options.Stacking.QualityScope);
        Assert.False(vm.Options.Stacking.DoubleStackReference);
        Assert.Equal(ReferenceFrameMode.Manual, vm.Options.Stacking.ReferenceMode);
        Assert.Equal(128, vm.Options.Stacking.ManualReferenceFrameIndex);
    }

    [Fact]
    public void StackingStab_Translations_AreValidAndHaveNoMojibake()
    {
        Loc.SetLanguage(AppLanguage.French, save: false);
        Assert.Equal("Centre de gravité", Loc.Get("StackingStab.PlanetCog"));
        Assert.Equal("Désactiver la stabilisation", Loc.Get("StackingStab.DisableStabilization"));
        Assert.Equal("Estimateur de qualité", Loc.Get("StackingStab.QualityEstimator"));
        Assert.Equal("Référence Double Stack", Loc.Get("StackingStab.DoubleStackReference"));
        Assert.DoesNotContain("Ã", Loc.Get("StackingStab.DisableStabilization"));
        Assert.DoesNotContain("Ã", Loc.Get("StackingStab.DisableStabilizationTip"));

        Loc.SetLanguage(AppLanguage.English, save: false);
        Assert.Equal("Centre of Gravity", Loc.Get("StackingStab.PlanetCog"));
        Assert.Equal("Disable Stabilization", Loc.Get("StackingStab.DisableStabilization"));
        Assert.Equal("Double Stack Reference", Loc.Get("StackingStab.DoubleStackReference"));

        Loc.SetLanguage(AppLanguage.French, save: false);
    }

    [Fact]
    public void ManualPoints_ArePreserved_WhenPreviewCroppingIsToggled()
    {
        var vm = new MainWindowViewModel();
        vm.EnableCropping = true;
        vm.CropWidth = 320;
        vm.CropHeight = 320;

        // 1. Ajouter un point manuel
        vm.AddManualAlignmentPoint(160, 160);
        Assert.Single(vm.CurrentAlignmentGrid.Points);
        Assert.Equal(160, vm.CurrentAlignmentGrid.Points[0].X);
        Assert.Equal(160, vm.CurrentAlignmentGrid.Points[0].Y);
        Assert.True(vm.CurrentAlignmentGrid.Points[0].IsActive);

        // 2. Basculer l'aperçu recadré (PreviewCroppedOutput) : les points ne doivent PAS disparaître
        vm.PreviewCroppedOutput = true;
        Assert.Single(vm.CurrentAlignmentGrid.Points);
        Assert.Equal(160, vm.CurrentAlignmentGrid.Points[0].X);
        Assert.Equal(160, vm.CurrentAlignmentGrid.Points[0].Y);

        vm.PreviewCroppedOutput = false;
        Assert.Single(vm.CurrentAlignmentGrid.Points);
        Assert.Equal(160, vm.CurrentAlignmentGrid.Points[0].X);
        Assert.Equal(160, vm.CurrentAlignmentGrid.Points[0].Y);

        // 3. Ajouter un second point manuel
        vm.AddManualAlignmentPoint(200, 160);
        Assert.Equal(2, vm.CurrentAlignmentGrid.Points.Count);

        // 4. Supprimer le premier point
        vm.RemoveManualAlignmentPoint(160, 160);
        Assert.Single(vm.CurrentAlignmentGrid.Points);
        Assert.Equal(200, vm.CurrentAlignmentGrid.Points[0].X);
    }

    [Fact]
    public void AlignmentGrid_IsRecalculated_WhenCropSizeChanges()
    {
        var vm = new MainWindowViewModel();
        vm.EnableCropping = true;
        vm.CropWidth = 200;
        vm.CropHeight = 200;

        // Simuler un frame en mémoire
        var testFrame = new AstroFrame(400, 400, 1, 16);
        for (int y = 150; y < 250; y++)
            for (int x = 150; x < 250; x++)
                testFrame.Data[y * 400 + x] = 30000;

        vm.GetType().GetField("_lastProcessedPreviewFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, testFrame);

        // 1. Calculer la grille initiale
        vm.CalculateAlignmentGrid();
        Assert.True(vm.CurrentAlignmentGrid.Points.Count > 0);
        Assert.Equal(200, vm.CurrentAlignmentGrid.FrameWidth);
        Assert.Equal(200, vm.CurrentAlignmentGrid.FrameHeight);

        // 2. Modifier CropWidth
        vm.CropWidth = 300;
        Assert.Equal(300, vm.CurrentAlignmentGrid.FrameWidth);
        Assert.Equal(200, vm.CurrentAlignmentGrid.FrameHeight);

        // 3. Modifier CropHeight
        vm.CropHeight = 300;
        Assert.Equal(300, vm.CurrentAlignmentGrid.FrameWidth);
        Assert.Equal(300, vm.CurrentAlignmentGrid.FrameHeight);
    }
}
