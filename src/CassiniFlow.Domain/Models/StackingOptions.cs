using System.Text.Json.Serialization;
using System.Xml.Serialization;

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
    public int AlignmentTileSize { get; set; } = 48;
    public int MinBrightnessThreshold { get; set; } = 15;
    public bool EnablePruning { get; set; } = true;
    public bool EnableMultiScaleGrid { get; set; } = true;

    [JsonIgnore]
    [XmlIgnore]
    public AlignmentGrid? CustomAlignmentGrid { get; set; }

    [JsonIgnore]
    [XmlIgnore]
    public Dictionary<string, AlignmentGrid> FileAlignmentGrids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Paliers d'empilement
    public FrameRetentionUnit RetentionUnit { get; set; } = FrameRetentionUnit.Percentage;
    public List<double> StackPercentages { get; set; } = [25.0];
    public List<int> StackFixedFrameCounts { get; set; } = [1000];

    [JsonIgnore]
    [XmlIgnore]
    public List<int> StackFrameCounts { get; set; } = [];

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
            CustomAlignmentGrid = CustomAlignmentGrid,
            FileAlignmentGrids = new Dictionary<string, AlignmentGrid>(FileAlignmentGrids, StringComparer.OrdinalIgnoreCase),
            RetentionUnit = RetentionUnit,
            StackPercentages = new List<double>(StackPercentages),
            StackFixedFrameCounts = new List<int>(StackFixedFrameCounts),
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

    /// <summary>
    /// Construit la liste ordonnée des règles de rétention actives selon l'unité configurée.
    /// </summary>
    public IReadOnlyList<FrameRetentionRule> GetActiveRetentionRules()
    {
        if (RetentionUnit == FrameRetentionUnit.FrameCount)
        {
            var counts = StackFixedFrameCounts
                .Where(c => c > 0)
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            if (counts.Count > 0)
            {
                return counts.Select(FrameRetentionRule.FromFrameCount).ToList();
            }
        }

        var pcts = StackPercentages
            .Where(p => p > 0 && p <= 100)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        if (pcts.Count == 0)
        {
            pcts = [25.0];
        }

        return pcts.Select(FrameRetentionRule.FromPercentage).ToList();
    }

    /// <summary>
    /// Résout les paliers d'empilement pour un nombre de trames scorées donné, en dédupliquant
    /// les paliers qui aboutissent au même nombre effectif de trames.
    /// </summary>
    public IReadOnlyList<ResolvedStackingTier> ResolveTiers(int scoredFrameCount)
    {
        if (scoredFrameCount <= 0)
        {
            return [];
        }

        var rules = GetActiveRetentionRules();
        var resolved = new List<ResolvedStackingTier>(rules.Count);
        var seenCounts = new HashSet<int>();

        foreach (var rule in rules)
        {
            int count = rule.ResolveFrameCount(scoredFrameCount);
            if (seenCounts.Add(count))
            {
                double effectivePct = rule.Unit == FrameRetentionUnit.Percentage
                    ? rule.Value
                    : Math.Round((count * 100.0) / scoredFrameCount, 1);

                resolved.Add(new ResolvedStackingTier(rule, count, effectivePct));
            }
        }

        resolved.Sort((a, b) => a.FramesToStack.CompareTo(b.FramesToStack));
        return resolved;
    }
}
