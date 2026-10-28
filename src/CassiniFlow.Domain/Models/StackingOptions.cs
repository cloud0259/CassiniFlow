namespace CassiniFlow.Domain.Models;

/// <summary>
/// Options de configuration pour le moteur d'alignement multi-zones et d'empilement haute résolution.
/// </summary>
public sealed class StackingOptions
{
    public StackingWorkflowMode WorkflowMode { get; set; } = StackingWorkflowMode.PreProcessingOnly;

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
