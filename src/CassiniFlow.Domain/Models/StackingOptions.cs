namespace CassiniFlow.Domain.Models;

/// <summary>
/// Options de configuration pour le moteur d'alignement multi-zones et d'empilement haute résolution.
/// </summary>
public sealed class StackingOptions
{
    public StackingWorkflowMode WorkflowMode { get; set; } = StackingWorkflowMode.PreProcessingOnly;

    // Stabilisation & Alignement global (AutoStakkert reverse engineered)
    public StackingStabilisationMode StabilisationMode { get; set; } = StackingStabilisationMode.PlanetCog;
    public bool DisableStabilisation { get; set; } = false;
    public StackingCanvasMode CanvasMode { get; set; } = StackingCanvasMode.Crop;

    // Estimateur de Qualité & Robustesse au bruit (SNR)
    public int NoiseRobust { get; set; } = 2; // 1 (Very high SNR) à 8 (Very low SNR)
    public QualityAnalysisScope QualityScope { get; set; } = QualityAnalysisScope.LocalAp;

    // Trame de Référence & Double Stack
    public bool DoubleStackReference { get; set; } = true;
    public ReferenceFrameMode ReferenceMode { get; set; } = ReferenceFrameMode.Automatic;
    public int ManualReferenceFrameIndex { get; set; } = 0;

    // Points d'alignement
    public int AlignmentTileSize { get; set; } = 104;
    public int MinBrightnessThreshold { get; set; } = 15;
    public bool EnablePruning { get; set; } = true;
    public bool EnableMultiScaleGrid { get; set; } = true;

    // Paliers d'empilement
    public List<double> StackPercentages { get; set; } = [10.0, 25.0];
    public List<int> StackFrameCounts { get; set; } = [200, 500];

    // Restauration & Rendu
    public DrizzleScale Drizzle { get; set; } = DrizzleScale.None;
    public bool NormalizeBrightness { get; set; } = true;
    public int NormalizeTargetPercent { get; set; } = 75;
    public bool CorrectAtmosphericDispersion { get; set; } = true;
    public bool SharpeningEnabled { get; set; } = true;
    public int SharpeningBlendPercent { get; set; } = 10;
    public bool FeatheringEnabled { get; set; } = true;

    public StackingOptions Clone()
    {
        return new StackingOptions
        {
            WorkflowMode = WorkflowMode,
            StabilisationMode = StabilisationMode,
            DisableStabilisation = DisableStabilisation,
            CanvasMode = CanvasMode,
            NoiseRobust = NoiseRobust,
            QualityScope = QualityScope,
            DoubleStackReference = DoubleStackReference,
            ReferenceMode = ReferenceMode,
            ManualReferenceFrameIndex = ManualReferenceFrameIndex,
            AlignmentTileSize = AlignmentTileSize,
            MinBrightnessThreshold = MinBrightnessThreshold,
            EnablePruning = EnablePruning,
            EnableMultiScaleGrid = EnableMultiScaleGrid,
            StackPercentages = new List<double>(StackPercentages),
            StackFrameCounts = new List<int>(StackFrameCounts),
            Drizzle = Drizzle,
            NormalizeBrightness = NormalizeBrightness,
            NormalizeTargetPercent = NormalizeTargetPercent,
            CorrectAtmosphericDispersion = CorrectAtmosphericDispersion,
            SharpeningEnabled = SharpeningEnabled,
            SharpeningBlendPercent = SharpeningBlendPercent,
            FeatheringEnabled = FeatheringEnabled
        };
    }
}
