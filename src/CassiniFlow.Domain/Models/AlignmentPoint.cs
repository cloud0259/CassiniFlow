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

/// <summary>
/// Maillage régulier de points d'alignement (AP Grid) couvrant l'objet ou la surface.
/// </summary>
public sealed class AlignmentGrid
{
    public int TileSize { get; init; }
    public int MinBrightness { get; init; }
    public IReadOnlyList<AlignmentPoint> Points { get; init; }
    public (int X, int Y) AnchorPoint { get; init; }

    public int ActiveCount => Points.Count(p => p.IsActive);
    public int TotalCount => Points.Count;

    public AlignmentGrid(int tileSize, int minBrightness, IReadOnlyList<AlignmentPoint> points, (int X, int Y) anchorPoint)
    {
        TileSize = tileSize;
        MinBrightness = minBrightness;
        Points = points;
        AnchorPoint = anchorPoint;
    }

    public static AlignmentGrid Empty => new(104, 15, [], (-1, -1));
}

