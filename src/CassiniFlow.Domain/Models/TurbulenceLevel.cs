namespace CassiniFlow.Domain.Models;

/// <summary>
/// Turbulence severity level evaluated from seeing and score variance.
/// </summary>
public enum TurbulenceLevel
{
    Low = 0,      // Calm seeing, optimal sharpness (green)
    Moderate = 1, // Medium stability (yellow/orange)
    High = 2      // Severe turbulence, blurry or drifting (red)
}
