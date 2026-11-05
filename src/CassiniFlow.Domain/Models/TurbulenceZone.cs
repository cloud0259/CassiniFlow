namespace CassiniFlow.Domain.Models;

/// <summary>
/// Represents a contiguous sequence of frames exhibiting a common turbulence level.
/// </summary>
public readonly record struct TurbulenceZone(int StartFrame, int EndFrame, TurbulenceLevel Level);
