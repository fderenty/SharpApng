using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpApng;

/// <summary>
/// Pixel layout of an <see cref="RgbaImage"/>. Both are RGBA and non-premultiplied;
/// they differ only in the width of a channel.
/// </summary>
public enum PixelFormat
{
    /// <summary>8 bits per channel, 4 bytes per pixel. See <see cref="Rgba32"/>.</summary>
    Rgba32 = 0,

    /// <summary>16 bits per channel, 8 bytes per pixel. See <see cref="Rgba64"/>.</summary>
    Rgba64 = 1,
}

/// <summary>
/// An 8-bit-per-channel, non-premultiplied RGBA pixel.
/// </summary>
/// <remarks>
/// Field order matches memory order, so a <c>byte[]</c> of pixel data can be reinterpreted
/// as a span of this struct with no conversion and no endianness question.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba32 : IEquatable<Rgba32>
{
    /// <summary>Red channel.</summary>
    public byte R;

    /// <summary>Green channel.</summary>
    public byte G;

    /// <summary>Blue channel.</summary>
    public byte B;

    /// <summary>Alpha channel. 0 is fully transparent, 255 fully opaque.</summary>
    public byte A;

    /// <summary>Creates a pixel from its four channels.</summary>
    public Rgba32(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>A fully transparent pixel with all channels zeroed.</summary>
    public static Rgba32 Transparent => default;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Rgba32 other)
        => Unsafe.As<Rgba32, uint>(ref Unsafe.AsRef(in this)) == Unsafe.As<Rgba32, uint>(ref other);

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgba32 other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => (int)Unsafe.As<Rgba32, uint>(ref Unsafe.AsRef(in this));

    /// <summary>Compares two pixels channel by channel.</summary>
    public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);

    /// <summary>Compares two pixels channel by channel.</summary>
    public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);

    /// <inheritdoc/>
    public override readonly string ToString() => $"rgba({R}, {G}, {B}, {A})";
}

/// <summary>
/// A 16-bit-per-channel, non-premultiplied RGBA pixel.
/// </summary>
/// <remarks>
/// Channels are stored in machine byte order, not PNG byte order; the codec swaps on the way
/// in and out. Note that 16 bits per channel buys precision, not dynamic range: it is not HDR,
/// and browsers truncate it to 8 bits when compositing.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct Rgba64 : IEquatable<Rgba64>
{
    /// <summary>Red channel.</summary>
    public ushort R;

    /// <summary>Green channel.</summary>
    public ushort G;

    /// <summary>Blue channel.</summary>
    public ushort B;

    /// <summary>Alpha channel. 0 is fully transparent, 65535 fully opaque.</summary>
    public ushort A;

    /// <summary>Creates a pixel from its four channels.</summary>
    public Rgba64(ushort r, ushort g, ushort b, ushort a = ushort.MaxValue)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>A fully transparent pixel with all channels zeroed.</summary>
    public static Rgba64 Transparent => default;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Rgba64 other)
        => Unsafe.As<Rgba64, ulong>(ref Unsafe.AsRef(in this)) == Unsafe.As<Rgba64, ulong>(ref other);

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgba64 other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => Unsafe.As<Rgba64, ulong>(ref Unsafe.AsRef(in this)).GetHashCode();

    /// <summary>Compares two pixels channel by channel.</summary>
    public static bool operator ==(Rgba64 left, Rgba64 right) => left.Equals(right);

    /// <summary>Compares two pixels channel by channel.</summary>
    public static bool operator !=(Rgba64 left, Rgba64 right) => !left.Equals(right);

    /// <inheritdoc/>
    public override readonly string ToString() => $"rgba16({R}, {G}, {B}, {A})";
}
