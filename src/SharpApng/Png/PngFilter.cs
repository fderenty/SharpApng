namespace SharpApng;

/// <summary>
/// The five per-scanline filters defined by the PNG specification, in both directions.
/// </summary>
/// <remarks>
/// Filters operate on bytes, never on pixels, and reference the byte <c>bpp</c> positions to
/// the left. That parameter is the pixel stride in bytes (4 for RGBA8, 8 for RGBA16), which is
/// why 16-bit support costs nothing here.
/// </remarks>
internal static class PngFilter
{
    public const int TypeNone = 0;
    public const int TypeSub = 1;
    public const int TypeUp = 2;
    public const int TypeAverage = 3;
    public const int TypePaeth = 4;

    /// <summary>
    /// Reverses a filter in place. <paramref name="current"/> arrives filtered and leaves as
    /// raw bytes; <paramref name="previous"/> is the already-unfiltered row above, or all
    /// zeroes for the first row.
    /// </summary>
    public static void Unfilter(int filterType, Span<byte> current, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filterType)
        {
            case TypeNone:
                break;

            case TypeSub:
                for (int i = bpp; i < current.Length; i++)
                    current[i] = (byte)(current[i] + current[i - bpp]);
                break;

            case TypeUp:
                for (int i = 0; i < current.Length; i++)
                    current[i] = (byte)(current[i] + previous[i]);
                break;

            case TypeAverage:
                for (int i = 0; i < current.Length; i++)
                {
                    int left = i >= bpp ? current[i - bpp] : 0;
                    current[i] = (byte)(current[i] + ((left + previous[i]) >> 1));
                }

                break;

            case TypePaeth:
                for (int i = 0; i < current.Length; i++)
                {
                    int left = i >= bpp ? current[i - bpp] : 0;
                    int upLeft = i >= bpp ? previous[i - bpp] : 0;
                    current[i] = (byte)(current[i] + Paeth(left, previous[i], upLeft));
                }

                break;

            default:
                throw new PngFormatException($"Unknown scanline filter type {filterType}.");
        }
    }

    /// <summary>
    /// Applies a filter, writing the result to <paramref name="destination"/> and leaving
    /// <paramref name="current"/> untouched.
    /// </summary>
    public static void Apply(
        int filterType,
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> previous,
        Span<byte> destination,
        int bpp)
    {
        switch (filterType)
        {
            case TypeNone:
                current.CopyTo(destination);
                break;

            case TypeSub:
                for (int i = 0; i < current.Length; i++)
                {
                    int left = i >= bpp ? current[i - bpp] : 0;
                    destination[i] = (byte)(current[i] - left);
                }

                break;

            case TypeUp:
                for (int i = 0; i < current.Length; i++)
                    destination[i] = (byte)(current[i] - previous[i]);
                break;

            case TypeAverage:
                for (int i = 0; i < current.Length; i++)
                {
                    int left = i >= bpp ? current[i - bpp] : 0;
                    destination[i] = (byte)(current[i] - ((left + previous[i]) >> 1));
                }

                break;

            case TypePaeth:
                for (int i = 0; i < current.Length; i++)
                {
                    int left = i >= bpp ? current[i - bpp] : 0;
                    int upLeft = i >= bpp ? previous[i - bpp] : 0;
                    destination[i] = (byte)(current[i] - Paeth(left, previous[i], upLeft));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(filterType));
        }
    }

    /// <summary>
    /// Picks the filter for a row using the minimum-sum-of-absolute-differences heuristic
    /// recommended by the PNG specification, and leaves the filtered bytes in
    /// <paramref name="destination"/>.
    /// </summary>
    /// <returns>The filter type that was chosen and applied.</returns>
    public static int ChooseAndApply(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> previous,
        Span<byte> destination,
        Span<byte> scratch,
        int bpp)
    {
        int bestType = TypeNone;
        long bestScore = long.MaxValue;

        for (int type = TypeNone; type <= TypePaeth; type++)
        {
            Apply(type, current, previous, scratch, bpp);

            long score = 0;
            foreach (byte b in scratch)
                score += (sbyte)b < 0 ? 256 - b : b;

            if (score < bestScore)
            {
                bestScore = score;
                bestType = type;
                scratch.CopyTo(destination);
            }
        }

        return bestType;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int distLeft = Math.Abs(estimate - left);
        int distUp = Math.Abs(estimate - up);
        int distUpLeft = Math.Abs(estimate - upLeft);

        if (distLeft <= distUp && distLeft <= distUpLeft)
            return left;

        return distUp <= distUpLeft ? up : upLeft;
    }
}
