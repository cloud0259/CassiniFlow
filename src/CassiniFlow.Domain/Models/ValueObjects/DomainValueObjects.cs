namespace CassiniFlow.Domain.Models.ValueObjects;

public readonly record struct Centroid(int X, int Y, bool IsValid)
{
    public static Centroid Invalid => new(-1, -1, false);
    public static Centroid At(int x, int y) => new(x, y, true);
}

public readonly record struct FrameDimensions(int Width, int Height)
{
    public int TotalPixels => Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct CropRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct QualityScore(double Value) : IComparable<QualityScore>
{
    public int CompareTo(QualityScore other) => Value.CompareTo(other.Value);
    public static implicit operator double(QualityScore q) => q.Value;
    public static implicit operator QualityScore(double val) => new(val);
}
