namespace CassiniFlow.Domain.Models;

/// <summary>
/// Maillage régulier de points d'alignement (AP Grid) couvrant l'objet ou la surface.
/// </summary>
public sealed class AlignmentGrid
{
    public int TileSize { get; init; }
    public int MinBrightness { get; init; }
    public IReadOnlyList<AlignmentPoint> Points { get; init; }
    public (int X, int Y) AnchorPoint { get; init; }
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }

    public int ActiveCount => Points.Count(p => p.IsActive);
    public int TotalCount => Points.Count;

    public AlignmentGrid(int tileSize, int minBrightness, IReadOnlyList<AlignmentPoint> points, (int X, int Y) anchorPoint, int frameWidth = 0, int frameHeight = 0)
    {
        TileSize = tileSize;
        MinBrightness = minBrightness;
        Points = points;
        AnchorPoint = anchorPoint;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
    }

    public static AlignmentGrid Empty => new(104, 15, [], (-1, -1), 0, 0);
}
