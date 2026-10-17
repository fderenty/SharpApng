using System.Runtime.InteropServices;

namespace SharpApng;

/// <summary>
/// A rectangular buffer of non-premultiplied RGBA pixels, tightly packed (no row padding).
/// </summary>
/// <remarks>
/// <para>
/// This is the one image type the whole library speaks. Every decoder normalises into it and
/// every encoder consumes it, which is what keeps the PNG side from ever having to reconcile
/// two frames that disagree about their colour type.
/// </para>
/// <para>
/// Pixel data is exposed directly through <see cref="Pixels"/>, <see cref="AsRgba32"/> and
/// <see cref="AsRgba64"/> so callers can work on it in place at full speed.
/// </para>
/// </remarks>
public sealed class RgbaImage
{
    private RgbaImage(byte[] pixels, int width, int height, PixelFormat format)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Format = format;
    }

    /// <summary>Allocates a fully transparent image.</summary>
    public RgbaImage(int width, int height, PixelFormat format = PixelFormat.Rgba32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        long byteCount = (long)width * height * BytesPerPixelOf(format);
        if (byteCount > Array.MaxLength)
            throw new ArgumentException($"A {width}x{height} {format} image needs {byteCount} bytes, more than one array can hold.");

        Width = width;
        Height = height;
        Format = format;
        Pixels = new byte[byteCount];
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Channel width of the pixel data.</summary>
    public PixelFormat Format { get; }

    /// <summary>
    /// The raw pixel bytes, row-major and tightly packed. Mutating this mutates the image.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>Bytes occupied by one pixel: 4 for <see cref="PixelFormat.Rgba32"/>, 8 for <see cref="PixelFormat.Rgba64"/>.</summary>
    public int BytesPerPixel => BytesPerPixelOf(Format);

    /// <summary>Bytes occupied by one row.</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>The rectangle covering the whole image.</summary>
    public PixelRect Bounds => new(0, 0, Width, Height);

    internal static int BytesPerPixelOf(PixelFormat format) => format == PixelFormat.Rgba64 ? 8 : 4;

    /// <summary>
    /// Wraps an existing buffer without copying it. The image and the caller then share storage.
    /// </summary>
    /// <exception cref="ArgumentException">The buffer length does not match the dimensions.</exception>
    public static RgbaImage Wrap(byte[] pixels, int width, int height, PixelFormat format = PixelFormat.Rgba32)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        long expected = (long)width * height * BytesPerPixelOf(format);
        if (pixels.LongLength != expected)
        {
            throw new ArgumentException(
                $"Expected {expected} bytes for a {width}x{height} {format} image, got {pixels.LongLength}.",
                nameof(pixels));
        }

        return new RgbaImage(pixels, width, height, format);
    }

    /// <summary>
    /// Copies an existing buffer into a new image. Use this when the caller keeps writing to
    /// its own buffer; use <see cref="Wrap"/> to hand ownership over instead.
    /// </summary>
    public static RgbaImage FromRaw(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format = PixelFormat.Rgba32)
    {
        var image = new RgbaImage(width, height, format);
        if (pixels.Length != image.Pixels.Length)
        {
            throw new ArgumentException(
                $"Expected {image.Pixels.Length} bytes for a {width}x{height} {format} image, got {pixels.Length}.",
                nameof(pixels));
        }

        pixels.CopyTo(image.Pixels);
        return image;
    }

    /// <summary>Reinterprets the buffer as 8-bit pixels.</summary>
    /// <exception cref="InvalidOperationException">The image is not <see cref="PixelFormat.Rgba32"/>.</exception>
    public Span<Rgba32> AsRgba32()
    {
        if (Format != PixelFormat.Rgba32)
            throw new InvalidOperationException($"Image is {Format}, not {PixelFormat.Rgba32}.");

        return MemoryMarshal.Cast<byte, Rgba32>(Pixels.AsSpan());
    }

    /// <summary>Reinterprets the buffer as 16-bit pixels.</summary>
    /// <exception cref="InvalidOperationException">The image is not <see cref="PixelFormat.Rgba64"/>.</exception>
    public Span<Rgba64> AsRgba64()
    {
        if (Format != PixelFormat.Rgba64)
            throw new InvalidOperationException($"Image is {Format}, not {PixelFormat.Rgba64}.");

        return MemoryMarshal.Cast<byte, Rgba64>(Pixels.AsSpan());
    }

    /// <summary>Returns the bytes of one row.</summary>
    public Span<byte> Row(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);

        return Pixels.AsSpan(y * Stride, Stride);
    }

    /// <summary>Reads a pixel, widening 8-bit channels to 16-bit if needed.</summary>
    public Rgba64 GetPixel(int x, int y)
    {
        ValidatePoint(x, y);

        if (Format == PixelFormat.Rgba64)
            return AsRgba64()[(y * Width) + x];

        Rgba32 p = AsRgba32()[(y * Width) + x];
        return new Rgba64(Widen(p.R), Widen(p.G), Widen(p.B), Widen(p.A));
    }

    /// <summary>Writes a pixel, narrowing 16-bit channels to 8-bit if needed.</summary>
    public void SetPixel(int x, int y, Rgba64 value)
    {
        ValidatePoint(x, y);

        if (Format == PixelFormat.Rgba64)
        {
            AsRgba64()[(y * Width) + x] = value;
            return;
        }

        AsRgba32()[(y * Width) + x] =
            new Rgba32(Narrow(value.R), Narrow(value.G), Narrow(value.B), Narrow(value.A));
    }

    /// <summary>Writes an 8-bit pixel, widening it if the image is 16-bit.</summary>
    public void SetPixel(int x, int y, Rgba32 value)
    {
        ValidatePoint(x, y);

        if (Format == PixelFormat.Rgba32)
        {
            AsRgba32()[(y * Width) + x] = value;
            return;
        }

        AsRgba64()[(y * Width) + x] =
            new Rgba64(Widen(value.R), Widen(value.G), Widen(value.B), Widen(value.A));
    }

    /// <summary>Returns an independent copy.</summary>
    public RgbaImage Clone()
    {
        var copy = new RgbaImage(Width, Height, Format);
        Pixels.CopyTo(copy.Pixels, 0);
        return copy;
    }

    /// <summary>Zeroes every channel, making the whole image transparent black.</summary>
    public void Clear() => Array.Clear(Pixels);

    /// <summary>Zeroes every channel inside <paramref name="rect"/>.</summary>
    public void Clear(PixelRect rect)
    {
        ValidateRect(rect);

        int bpp = BytesPerPixel;
        for (int y = rect.Y; y < rect.Bottom; y++)
            Array.Clear(Pixels, (y * Stride) + (rect.X * bpp), rect.Width * bpp);
    }

    /// <summary>Returns a new image holding a copy of <paramref name="rect"/>.</summary>
    public RgbaImage Crop(PixelRect rect)
    {
        ValidateRect(rect);
        if (rect.IsEmpty)
            throw new ArgumentException("Cannot crop to an empty rectangle.", nameof(rect));

        var result = new RgbaImage(rect.Width, rect.Height, Format);
        int bpp = BytesPerPixel;
        int rowBytes = rect.Width * bpp;

        for (int y = 0; y < rect.Height; y++)
        {
            Pixels.AsSpan(((rect.Y + y) * Stride) + (rect.X * bpp), rowBytes)
                  .CopyTo(result.Pixels.AsSpan(y * result.Stride, rowBytes));
        }

        return result;
    }

    /// <summary>
    /// Copies <paramref name="source"/> over this image at the given offset, replacing whatever
    /// was there. This is a raw copy, not an alpha blend; see
    /// <see cref="RgbaImageOps.BlendOver"/> for compositing.
    /// </summary>
    public void DrawSource(RgbaImage source, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(source);
        RequireSameFormat(source);
        ValidateRect(new PixelRect(x, y, source.Width, source.Height));

        int bpp = BytesPerPixel;
        int rowBytes = source.Width * bpp;

        for (int row = 0; row < source.Height; row++)
        {
            source.Pixels.AsSpan(row * source.Stride, rowBytes)
                         .CopyTo(Pixels.AsSpan(((y + row) * Stride) + (x * bpp), rowBytes));
        }
    }

    /// <summary>Converts to another channel width, or returns this image if it already matches.</summary>
    public RgbaImage ConvertTo(PixelFormat format)
    {
        if (format == Format)
            return this;

        var result = new RgbaImage(Width, Height, format);

        if (format == PixelFormat.Rgba64)
        {
            ReadOnlySpan<Rgba32> src = AsRgba32();
            Span<Rgba64> dst = result.AsRgba64();
            for (int i = 0; i < src.Length; i++)
            {
                Rgba32 p = src[i];
                dst[i] = new Rgba64(Widen(p.R), Widen(p.G), Widen(p.B), Widen(p.A));
            }
        }
        else
        {
            ReadOnlySpan<Rgba64> src = AsRgba64();
            Span<Rgba32> dst = result.AsRgba32();
            for (int i = 0; i < src.Length; i++)
            {
                Rgba64 p = src[i];
                dst[i] = new Rgba32(Narrow(p.R), Narrow(p.G), Narrow(p.B), Narrow(p.A));
            }
        }

        return result;
    }

    /// <summary>True when no pixel is less than fully opaque.</summary>
    public bool IsOpaque()
    {
        if (Format == PixelFormat.Rgba32)
        {
            foreach (Rgba32 p in AsRgba32())
            {
                if (p.A != byte.MaxValue)
                    return false;
            }
        }
        else
        {
            foreach (Rgba64 p in AsRgba64())
            {
                if (p.A != ushort.MaxValue)
                    return false;
            }
        }

        return true;
    }

    /// <summary>Spreads an 8-bit channel across 16 bits so that 255 maps to 65535.</summary>
    internal static ushort Widen(byte value) => (ushort)(value * 0x0101);

    /// <summary>Rounds a 16-bit channel down to 8 bits, the inverse of <see cref="Widen"/>.</summary>
    internal static byte Narrow(ushort value) => (byte)((value + 128) / 257);

    internal void RequireSameFormat(RgbaImage other)
    {
        if (other.Format != Format)
            throw new ArgumentException($"Pixel format mismatch: {Format} versus {other.Format}.", nameof(other));
    }

    internal void RequireSameSize(RgbaImage other)
    {
        if (other.Width != Width || other.Height != Height)
        {
            throw new ArgumentException(
                $"Size mismatch: {Width}x{Height} versus {other.Width}x{other.Height}.", nameof(other));
        }
    }

    private void ValidatePoint(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside {Width}x{Height}.");
    }

    private void ValidateRect(PixelRect rect)
    {
        if (!Bounds.Contains(rect))
            throw new ArgumentOutOfRangeException(nameof(rect), $"{rect} is outside {Width}x{Height}.");
    }
}
