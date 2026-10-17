namespace SharpApng;

/// <summary>
/// An axis-aligned rectangle in pixel coordinates. Exists so the library needs no
/// reference to System.Drawing.
/// </summary>
public readonly struct PixelRect : IEquatable<PixelRect>
{
    /// <summary>Creates a rectangle. Negative extents are rejected.</summary>
    public PixelRect(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Left edge.</summary>
    public int X { get; }

    /// <summary>Top edge.</summary>
    public int Y { get; }

    /// <summary>Horizontal extent.</summary>
    public int Width { get; }

    /// <summary>Vertical extent.</summary>
    public int Height { get; }

    /// <summary>Right edge, exclusive.</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge, exclusive.</summary>
    public int Bottom => Y + Height;

    /// <summary>True when the rectangle covers no pixels.</summary>
    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>The empty rectangle at the origin.</summary>
    public static PixelRect Empty => default;

    /// <summary>Builds a rectangle from two corners, the second exclusive.</summary>
    public static PixelRect FromBounds(int left, int top, int right, int bottom)
        => new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>
    /// Grows the rectangle so that its origin lands on even coordinates and its extents stay
    /// inside <paramref name="bounds"/>. Kept for formats that cannot express odd offsets.
    /// </summary>
    public PixelRect SnapToEven(PixelRect bounds)
    {
        if (IsEmpty)
            return this;

        int left = X & ~1;
        int top = Y & ~1;
        return FromBounds(
            Math.Max(bounds.X, left),
            Math.Max(bounds.Y, top),
            Math.Min(bounds.Right, Right),
            Math.Min(bounds.Bottom, Bottom));
    }

    /// <summary>Returns the smallest rectangle containing both operands.</summary>
    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty)
            return other;
        if (other.IsEmpty)
            return this;

        return FromBounds(
            Math.Min(X, other.X),
            Math.Min(Y, other.Y),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    /// <summary>True when <paramref name="other"/> lies entirely within this rectangle.</summary>
    public bool Contains(PixelRect other)
        => other.IsEmpty || (other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom);

    /// <inheritdoc/>
    public bool Equals(PixelRect other)
        => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PixelRect other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    /// <summary>Compares two rectangles field by field.</summary>
    public static bool operator ==(PixelRect left, PixelRect right) => left.Equals(right);

    /// <summary>Compares two rectangles field by field.</summary>
    public static bool operator !=(PixelRect left, PixelRect right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => $"({X}, {Y}) {Width}x{Height}";
}
