namespace CassiniFlow.Domain.Models;

/// <summary>
/// Point d'alignement local (Alignment Point - AP) pour le recalage multi-zones.
/// </summary>
public sealed class AlignmentPoint
{
    public int Id { get; init; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Size { get; init; }
    public bool IsActive { get; set; }
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double ShiftX { get; set; }
    public double ShiftY { get; set; }

    public AlignmentPoint(int id, int x, int y, int size, bool isActive = true)
    {
        Id = id;
        X = x;
        Y = y;
        Size = size;
        IsActive = isActive;
    }
}
