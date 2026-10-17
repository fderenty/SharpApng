namespace SharpApng;

/// <summary>
/// Reconstruction filter used when resampling an image.
/// </summary>
public enum ResampleFilter
{
    /// <summary>Box, averaging the source pixels that fall under each destination pixel. Fast, and a good fit for downscaling.</summary>
    Box = 0,

    /// <summary>Triangle, also known as bilinear.</summary>
    Triangle = 1,

    /// <summary>Three-lobe Lanczos. Sharpest, and the most expensive.</summary>
    Lanczos3 = 2,
}

/// <summary>
/// Pixel-level operations shared by the reader, the writer and the frame optimiser.
/// </summary>
public static class RgbaImageOps
{
    /// <summary>True when both images have the same format, size and pixel values.</summary>
    public static bool ContentEquals(RgbaImage a, RgbaImage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (ReferenceEquals(a, b))
            return true;

        return a.Format == b.Format
            && a.Width == b.Width
            && a.Height == b.Height
            && a.Pixels.AsSpan().SequenceEqual(b.Pixels);
    }

    /// <summary>
    /// Returns the smallest rectangle enclosing every pixel where the two images differ,
    /// or <see cref="PixelRect.Empty"/> when they are identical.
    /// </summary>
    /// <remarks>
    /// Rows are rejected wholesale with a vectorised compare before any per-pixel scan, so
    /// the common case of a small moving region over a static background stays cheap.
    /// </remarks>
    public static PixelRect FindDifference(RgbaImage baseline, RgbaImage target)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(target);
        baseline.RequireSameFormat(target);
        baseline.RequireSameSize(target);

        int bpp = baseline.BytesPerPixel;
        int stride = baseline.Stride;
        int top = -1;
        int bottom = -1;
        int left = int.MaxValue;
        int right = -1;

        for (int y = 0; y < baseline.Height; y++)
        {
            ReadOnlySpan<byte> a = baseline.Pixels.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> b = target.Pixels.AsSpan(y * stride, stride);

            if (a.SequenceEqual(b))
                continue;

            if (top < 0)
                top = y;
            bottom = y + 1;

            int rowLeft = 0;
            while (rowLeft < baseline.Width && PixelEquals(a, b, rowLeft, bpp))
                rowLeft++;

            int rowRight = baseline.Width;
            while (rowRight > rowLeft && PixelEquals(a, b, rowRight - 1, bpp))
                rowRight--;

            if (rowLeft < left)
                left = rowLeft;
            if (rowRight > right)
                right = rowRight;
        }

        return top < 0 ? PixelRect.Empty : PixelRect.FromBounds(left, top, right, bottom);
    }

    /// <summary>
    /// Composites <paramref name="source"/> onto <paramref name="canvas"/> at the given offset
    /// using non-premultiplied source-over, the operation APNG calls
    /// <see cref="BlendOp.Over"/>.
    /// </summary>
    public static void BlendOver(RgbaImage canvas, RgbaImage source, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(source);
        canvas.RequireSameFormat(source);

        if (!canvas.Bounds.Contains(new PixelRect(x, y, source.Width, source.Height)))
            throw new ArgumentOutOfRangeException(nameof(source), "Source does not fit inside the canvas at that offset.");

        if (canvas.Format == PixelFormat.Rgba32)
        {
            Span<Rgba32> dst = canvas.AsRgba32();
            ReadOnlySpan<Rgba32> src = source.AsRgba32();

            for (int row = 0; row < source.Height; row++)
            {
                int dstRow = ((y + row) * canvas.Width) + x;
                int srcRow = row * source.Width;
                for (int col = 0; col < source.Width; col++)
                    dst[dstRow + col] = Over(src[srcRow + col], dst[dstRow + col]);
            }
        }
        else
        {
            Span<Rgba64> dst = canvas.AsRgba64();
            ReadOnlySpan<Rgba64> src = source.AsRgba64();

            for (int row = 0; row < source.Height; row++)
            {
                int dstRow = ((y + row) * canvas.Width) + x;
                int srcRow = row * source.Width;
                for (int col = 0; col < source.Width; col++)
                    dst[dstRow + col] = Over(src[srcRow + col], dst[dstRow + col]);
            }
        }
    }

    /// <summary>
    /// Non-premultiplied source-over for one 8-bit pixel, following the integer formula in the
    /// APNG specification.
    /// </summary>
    public static Rgba32 Over(Rgba32 source, Rgba32 destination)
    {
        if (source.A == byte.MaxValue || destination.A == 0)
            return source;
        if (source.A == 0)
            return destination;

        const int Max = byte.MaxValue;
        int sa = source.A;
        int da = destination.A;
        int carried = da * (Max - sa) / Max;
        int outA = sa + carried;

        if (outA == 0)
            return default;

        return new Rgba32(
            (byte)(((source.R * sa) + (destination.R * carried)) / outA),
            (byte)(((source.G * sa) + (destination.G * carried)) / outA),
            (byte)(((source.B * sa) + (destination.B * carried)) / outA),
            (byte)outA);
    }

    /// <summary>Non-premultiplied source-over for one 16-bit pixel.</summary>
    public static Rgba64 Over(Rgba64 source, Rgba64 destination)
    {
        if (source.A == ushort.MaxValue || destination.A == 0)
            return source;
        if (source.A == 0)
            return destination;

        const long Max = ushort.MaxValue;
        long sa = source.A;
        long da = destination.A;
        long carried = da * (Max - sa) / Max;
        long outA = sa + carried;

        if (outA == 0)
            return default;

        return new Rgba64(
            (ushort)(((source.R * sa) + (destination.R * carried)) / outA),
            (ushort)(((source.G * sa) + (destination.G * carried)) / outA),
            (ushort)(((source.B * sa) + (destination.B * carried)) / outA),
            (ushort)outA);
    }

    /// <summary>
    /// True when every pixel of <paramref name="image"/> inside <paramref name="rect"/> is either
    /// fully transparent or fully opaque.
    /// </summary>
    /// <remarks>
    /// The optimiser uses this to decide whether <see cref="BlendOp.Over"/> is safe. Over cannot
    /// lower the alpha already on the canvas, and decoders round the intermediate arithmetic
    /// slightly differently; restricting Over to all-or-nothing alpha sidesteps both problems.
    /// </remarks>
    public static bool HasBinaryAlpha(RgbaImage image, PixelRect rect)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Format == PixelFormat.Rgba32)
        {
            ReadOnlySpan<Rgba32> pixels = image.AsRgba32();
            for (int y = rect.Y; y < rect.Bottom; y++)
            {
                int row = y * image.Width;
                for (int x = rect.X; x < rect.Right; x++)
                {
                    byte a = pixels[row + x].A;
                    if (a != 0 && a != byte.MaxValue)
                        return false;
                }
            }
        }
        else
        {
            ReadOnlySpan<Rgba64> pixels = image.AsRgba64();
            for (int y = rect.Y; y < rect.Bottom; y++)
            {
                int row = y * image.Width;
                for (int x = rect.X; x < rect.Right; x++)
                {
                    ushort a = pixels[row + x].A;
                    if (a != 0 && a != ushort.MaxValue)
                        return false;
                }
            }
        }

        return true;
    }

    private static bool PixelEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int index, int bytesPerPixel)
    {
        int offset = index * bytesPerPixel;
        return a.Slice(offset, bytesPerPixel).SequenceEqual(b.Slice(offset, bytesPerPixel));
    }

    /// <summary>
    /// Resamples an image to an exact size.
    /// </summary>
    /// <param name="source">The image to resample.</param>
    /// <param name="width">Target width, at least 1.</param>
    /// <param name="height">Target height, at least 1.</param>
    /// <param name="filter">Reconstruction filter. <see cref="ResampleFilter.Box"/> by default.</param>
    /// <returns>A new image in the same pixel format as <paramref name="source"/>.</returns>
    /// <remarks>
    /// Filtering happens on premultiplied alpha internally, then the result is unpremultiplied
    /// again. Averaging non-premultiplied channels would let the colour of transparent pixels
    /// bleed into their opaque neighbours, which is what produces the dark fringe around
    /// resized cut-outs. Callers still see non-premultiplied pixels throughout.
    /// </remarks>
    public static RgbaImage Resize(RgbaImage source, int width, int height, ResampleFilter filter = ResampleFilter.Box)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        if (width == source.Width && height == source.Height)
            return source.Clone();

        // Two separable passes: horizontal into an intermediate, then vertical. That costs
        // O(w*h*support) instead of the O(w*h*support²) a single two-dimensional pass would.
        float[] premultiplied = ToPremultiplied(source);
        float[] horizontal = ResampleAxis(premultiplied, source.Width, source.Height, width, horizontally: true, filter);
        float[] vertical = ResampleAxis(horizontal, width, source.Height, height, horizontally: false, filter);

        return FromPremultiplied(vertical, width, height, source.Format);
    }

    /// <summary>
    /// Shrinks an image to fit inside a square of the given size, preserving its aspect ratio.
    /// An image already within the bound is returned unchanged; this never enlarges.
    /// </summary>
    /// <param name="source">The image to resample.</param>
    /// <param name="maxWidthOrHeight">Bound on the longer side, at least 1.</param>
    /// <param name="filter">Reconstruction filter. <see cref="ResampleFilter.Box"/> by default.</param>
    public static RgbaImage ResizeToFit(RgbaImage source, int maxWidthOrHeight, ResampleFilter filter = ResampleFilter.Box)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidthOrHeight, 1);

        int longest = Math.Max(source.Width, source.Height);
        if (longest <= maxWidthOrHeight)
            return source.Clone();

        double scale = (double)maxWidthOrHeight / longest;
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));

        return Resize(source, width, height, filter);
    }

    /// <summary>
    /// Reorients an image according to an EXIF orientation value, swapping width and height for
    /// the four transposing cases.
    /// </summary>
    /// <param name="source">The image to reorient.</param>
    /// <param name="orientation">The orientation the pixels are currently stored in.</param>
    /// <returns>
    /// A new upright image, or a copy when <paramref name="orientation"/> is
    /// <see cref="ExifOrientation.Normal"/>.
    /// </returns>
    public static RgbaImage ApplyOrientation(RgbaImage source, ExifOrientation orientation)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (orientation == ExifOrientation.Normal)
            return source.Clone();

        bool transposes = orientation is ExifOrientation.Transpose or ExifOrientation.Rotate90
                                      or ExifOrientation.Transverse or ExifOrientation.Rotate270;

        int width = transposes ? source.Height : source.Width;
        int height = transposes ? source.Width : source.Height;
        var result = new RgbaImage(width, height, source.Format);

        int bpp = source.BytesPerPixel;
        ReadOnlySpan<byte> src = source.Pixels;
        Span<byte> dst = result.Pixels;

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                (int tx, int ty) = orientation switch
                {
                    ExifOrientation.FlipHorizontal => (source.Width - 1 - x, y),
                    ExifOrientation.Rotate180 => (source.Width - 1 - x, source.Height - 1 - y),
                    ExifOrientation.FlipVertical => (x, source.Height - 1 - y),
                    ExifOrientation.Transpose => (y, x),
                    ExifOrientation.Rotate90 => (source.Height - 1 - y, x),
                    ExifOrientation.Transverse => (source.Height - 1 - y, source.Width - 1 - x),
                    ExifOrientation.Rotate270 => (y, source.Width - 1 - x),
                    _ => (x, y),
                };

                src.Slice(((y * source.Width) + x) * bpp, bpp)
                   .CopyTo(dst[(((ty * width) + tx) * bpp)..]);
            }
        }

        return result;
    }

    /// <summary>
    /// Expands an image into premultiplied floating-point channels, one contiguous RGBA
    /// quadruple per pixel.
    /// </summary>
    private static float[] ToPremultiplied(RgbaImage source)
    {
        long count = (long)source.Width * source.Height * 4;
        if (count > Array.MaxLength)
            throw new ArgumentException("Image is too large to resample in one pass.", nameof(source));

        var result = new float[count];

        if (source.Format == PixelFormat.Rgba32)
        {
            ReadOnlySpan<Rgba32> pixels = source.AsRgba32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba32 p = pixels[i];
                float a = p.A / 255f;
                int o = i * 4;
                result[o] = p.R * a;
                result[o + 1] = p.G * a;
                result[o + 2] = p.B * a;
                result[o + 3] = p.A;
            }
        }
        else
        {
            ReadOnlySpan<Rgba64> pixels = source.AsRgba64();
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba64 p = pixels[i];
                float a = p.A / 65535f;
                int o = i * 4;
                result[o] = p.R * a;
                result[o + 1] = p.G * a;
                result[o + 2] = p.B * a;
                result[o + 3] = p.A;
            }
        }

        return result;
    }

    private static RgbaImage FromPremultiplied(float[] data, int width, int height, PixelFormat format)
    {
        var result = new RgbaImage(width, height, format);

        if (format == PixelFormat.Rgba32)
        {
            Span<Rgba32> pixels = result.AsRgba32();
            for (int i = 0; i < pixels.Length; i++)
            {
                int o = i * 4;
                float alpha = data[o + 3];
                byte a = ClampToByte(alpha);

                if (a == 0)
                {
                    pixels[i] = default;
                    continue;
                }

                float inverse = 255f / alpha;
                pixels[i] = new Rgba32(
                    ClampToByte(data[o] * inverse),
                    ClampToByte(data[o + 1] * inverse),
                    ClampToByte(data[o + 2] * inverse),
                    a);
            }
        }
        else
        {
            Span<Rgba64> pixels = result.AsRgba64();
            for (int i = 0; i < pixels.Length; i++)
            {
                int o = i * 4;
                float alpha = data[o + 3];
                ushort a = ClampToUInt16(alpha);

                if (a == 0)
                {
                    pixels[i] = default;
                    continue;
                }

                float inverse = 65535f / alpha;
                pixels[i] = new Rgba64(
                    ClampToUInt16(data[o] * inverse),
                    ClampToUInt16(data[o + 1] * inverse),
                    ClampToUInt16(data[o + 2] * inverse),
                    a);
            }
        }

        return result;
    }

    /// <summary>
    /// Resamples one axis. Weights are computed once per destination position and reused down
    /// the whole row or column.
    /// </summary>
    private static float[] ResampleAxis(
        float[] source,
        int sourceWidth,
        int sourceHeight,
        int targetLength,
        bool horizontally,
        ResampleFilter filter)
    {
        int sourceLength = horizontally ? sourceWidth : sourceHeight;
        int otherLength = horizontally ? sourceHeight : sourceWidth;
        int targetWidth = horizontally ? targetLength : sourceWidth;

        if (sourceLength == targetLength)
            return source;

        (int[] starts, int[] counts, float[] weights, int stride) =
            BuildContributions(sourceLength, targetLength, filter);

        var result = new float[(long)targetWidth * (horizontally ? sourceHeight : targetLength) * 4];

        for (int other = 0; other < otherLength; other++)
        {
            for (int target = 0; target < targetLength; target++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f;
                int start = starts[target];
                int count = counts[target];
                int weightBase = target * stride;

                for (int k = 0; k < count; k++)
                {
                    float w = weights[weightBase + k];
                    int index = horizontally
                        ? ((other * sourceWidth) + start + k) * 4
                        : (((start + k) * sourceWidth) + other) * 4;

                    r += source[index] * w;
                    g += source[index + 1] * w;
                    b += source[index + 2] * w;
                    a += source[index + 3] * w;
                }

                int destination = horizontally
                    ? ((other * targetWidth) + target) * 4
                    : ((target * targetWidth) + other) * 4;

                result[destination] = r;
                result[destination + 1] = g;
                result[destination + 2] = b;
                result[destination + 3] = a;
            }
        }

        return result;
    }

    private static (int[] Starts, int[] Counts, float[] Weights, int Stride) BuildContributions(
        int sourceLength,
        int targetLength,
        ResampleFilter filter)
    {
        double scale = (double)targetLength / sourceLength;

        // When shrinking, the kernel has to widen in source space so that every source pixel
        // contributes. A fixed-width kernel would sample a few pixels and alias badly.
        double filterScale = scale < 1.0 ? 1.0 / scale : 1.0;
        double support = Support(filter) * filterScale;
        int maxCount = Math.Min(sourceLength, (int)Math.Ceiling(support * 2) + 2);

        var starts = new int[targetLength];
        var counts = new int[targetLength];
        var weights = new float[(long)targetLength * maxCount <= Array.MaxLength
            ? targetLength * maxCount
            : throw new ArgumentException("Resampling this image would need too much scratch space.")];

        for (int i = 0; i < targetLength; i++)
        {
            double center = ((i + 0.5) / scale) - 0.5;
            int start = Math.Max(0, (int)Math.Ceiling(center - support));
            int end = Math.Min(sourceLength - 1, (int)Math.Floor(center + support));
            int count = Math.Min(maxCount, end - start + 1);

            double total = 0;
            for (int k = 0; k < count; k++)
            {
                double w = Kernel(filter, (start + k - center) / filterScale);
                weights[(i * maxCount) + k] = (float)w;
                total += w;
            }

            // A destination pixel that lands between kernel zeroes can total zero; fall back to
            // nearest so the row never comes out black.
            if (total == 0)
            {
                Array.Clear(weights, i * maxCount, count);
                int nearest = Math.Clamp((int)Math.Round(center), start, start + count - 1);
                weights[(i * maxCount) + (nearest - start)] = 1f;
            }
            else
            {
                float inverse = (float)(1.0 / total);
                for (int k = 0; k < count; k++)
                    weights[(i * maxCount) + k] *= inverse;
            }

            starts[i] = start;
            counts[i] = count;
        }

        return (starts, counts, weights, maxCount);
    }

    private static double Support(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Box => 0.5,
        ResampleFilter.Triangle => 1.0,
        ResampleFilter.Lanczos3 => 3.0,
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private static double Kernel(ResampleFilter filter, double x)
    {
        x = Math.Abs(x);

        return filter switch
        {
            ResampleFilter.Box => x <= 0.5 ? 1.0 : 0.0,
            ResampleFilter.Triangle => x < 1.0 ? 1.0 - x : 0.0,
            ResampleFilter.Lanczos3 => x < 3.0 ? Sinc(x) * Sinc(x / 3.0) : 0.0,
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
            return 1.0;

        double pix = Math.PI * x;
        return Math.Sin(pix) / pix;
    }

    private static byte ClampToByte(float value)
        => value <= 0f ? (byte)0 : value >= 255f ? (byte)255 : (byte)(value + 0.5f);

    private static ushort ClampToUInt16(float value)
        => value <= 0f ? (ushort)0 : value >= 65535f ? (ushort)65535 : (ushort)(value + 0.5f);
}
