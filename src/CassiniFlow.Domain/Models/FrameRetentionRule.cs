using System;
using System.Globalization;

namespace CassiniFlow.Domain.Models;

/// <summary>
/// Unité de sélection du nombre de trames retenues.
/// </summary>
public enum FrameRetentionUnit
{
    Percentage = 0,
    FrameCount = 1
}

/// <summary>
/// Value Object immuable : source unique de vérité pour exprimer et résoudre une rétention de frames.
/// </summary>
public readonly record struct FrameRetentionRule
{
    public FrameRetentionUnit Unit { get; }
    public double Value { get; }

    private FrameRetentionRule(FrameRetentionUnit unit, double value)
    {
        Unit = unit;
        Value = value;
    }

    public static FrameRetentionRule FromPercentage(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent <= 0 || percent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "Le pourcentage doit être dans ]0, 100].");
        }

        return new FrameRetentionRule(FrameRetentionUnit.Percentage, percent);
    }

    public static FrameRetentionRule FromFrameCount(int frameCount)
    {
        if (frameCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Le nombre de frames doit être >= 1.");
        }

        return new FrameRetentionRule(FrameRetentionUnit.FrameCount, frameCount);
    }

    /// <summary>
    /// Résolution déterministe unique du nombre de trames à retenir.
    /// </summary>
    public int ResolveFrameCount(int availableFrames)
    {
        if (availableFrames <= 0)
        {
            return 0;
        }

        double raw = Unit == FrameRetentionUnit.Percentage
            ? availableFrames * (Value / 100.0)
            : Value;

        return Math.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero), 1, availableFrames);
    }

    public string FormatShortTag()
    {
        return Unit == FrameRetentionUnit.Percentage
            ? $"p{Value.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', '_')}"
            : $"f{(int)Math.Round(Value, MidpointRounding.AwayFromZero)}";
    }
}

/// <summary>
/// Palier d'empilement résolu pour un nombre donné de trames scorées.
/// </summary>
public readonly record struct ResolvedStackingTier(
    FrameRetentionRule Rule,
    int FramesToStack,
    double EffectivePercentage);
