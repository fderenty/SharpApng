using System.Buffers.Binary;

namespace SharpApng;

/// <summary>
/// The contents of an <c>IHDR</c> chunk.
/// </summary>
internal readonly struct PngHeader
{
    public const byte ColorTypeGrayscale = 0;
    public const byte ColorTypeTruecolor = 2;
    public const byte ColorTypePalette = 3;
    public const byte ColorTypeGrayscaleAlpha = 4;
    public const byte ColorTypeTruecolorAlpha = 6;

    /// <summary>The payload length of a well-formed IHDR chunk.</summary>
    public const int PayloadLength = 13;

    public PngHeader(int width, int height, byte bitDepth, byte colorType, byte interlaceMethod = 0)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        ColorType = colorType;
        InterlaceMethod = interlaceMethod;
    }

    public int Width { get; }

    public int Height { get; }

    public byte BitDepth { get; }

    public byte ColorType { get; }

    public byte InterlaceMethod { get; }

    /// <summary>Samples per pixel implied by the colour type.</summary>
    public int Channels => ColorType switch
    {
        ColorTypeGrayscale or ColorTypePalette => 1,
        ColorTypeGrayscaleAlpha => 2,
        ColorTypeTruecolor => 3,
        ColorTypeTruecolorAlpha => 4,
        _ => throw new PngFormatException($"Unsupported colour type {ColorType}."),
    };

    /// <summary>
    /// The filter offset in bytes: the distance back to the corresponding byte of the previous
    /// pixel, rounded up to at least one for sub-byte depths.
    /// </summary>
    public int FilterBytesPerPixel => Math.Max(1, Channels * BitDepth / 8);

    /// <summary>Bytes in one unfiltered scanline of the given width.</summary>
    public int BytesPerRow(int width) => ((width * Channels * BitDepth) + 7) / 8;

    /// <summary>The pixel format this header decodes into.</summary>
    public PixelFormat TargetFormat => BitDepth == 16 ? PixelFormat.Rgba64 : PixelFormat.Rgba32;

    /// <summary>Reads and validates an IHDR payload.</summary>
    public static PngHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != PayloadLength)
            throw new PngFormatException($"IHDR must be {PayloadLength} bytes, found {data.Length}.");

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        byte bitDepth = data[8];
        byte colorType = data[9];
        byte compression = data[10];
        byte filter = data[11];
        byte interlace = data[12];

        if (width == 0 || height == 0)
            throw new PngFormatException("IHDR declares a zero dimension.");
        if (width > int.MaxValue || height > int.MaxValue)
            throw new PngFormatException("IHDR declares a dimension larger than this decoder supports.");
        if (compression != 0)
            throw new PngFormatException($"Unsupported compression method {compression}; PNG defines only 0.");
        if (filter != 0)
            throw new PngFormatException($"Unsupported filter method {filter}; PNG defines only 0.");
        if (interlace > 1)
            throw new PngFormatException($"Unsupported interlace method {interlace}.");

        var header = new PngHeader((int)width, (int)height, bitDepth, colorType, interlace);
        header.ValidateDepth();
        return header;
    }

    /// <summary>Serialises the header into an IHDR payload.</summary>
    public byte[] ToPayload()
    {
        var payload = new byte[PayloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)Width);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)Height);
        payload[8] = BitDepth;
        payload[9] = ColorType;
        payload[10] = 0;
        payload[11] = 0;
        payload[12] = InterlaceMethod;
        return payload;
    }

    private void ValidateDepth()
    {
        bool valid = ColorType switch
        {
            ColorTypeGrayscale => BitDepth is 1 or 2 or 4 or 8 or 16,
            ColorTypePalette => BitDepth is 1 or 2 or 4 or 8,
            ColorTypeTruecolor or ColorTypeGrayscaleAlpha or ColorTypeTruecolorAlpha => BitDepth is 8 or 16,
            _ => throw new PngFormatException($"Unsupported colour type {ColorType}."),
        };

        if (!valid)
            throw new PngFormatException($"Bit depth {BitDepth} is not allowed with colour type {ColorType}.");
    }
}
