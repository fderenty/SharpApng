using System.IO.Compression;

namespace SharpApng;

/// <summary>
/// Converts between <see cref="RgbaImage"/> and the compressed, filtered scanline data that
/// lives inside <c>IDAT</c> and <c>fdAT</c> chunks.
/// </summary>
/// <remarks>
/// The zlib framing itself comes from <see cref="ZLibStream"/> in the BCL, which is exactly the
/// container PNG asks for. That is what lets this library carry no third-party dependency.
/// </remarks>
internal static class PngRaster
{
    /// <summary>
    /// Filters and compresses an image as 8-bit or 16-bit RGBA. The output always describes
    /// colour type 6, non-interlaced.
    /// </summary>
    public static byte[] Encode(RgbaImage image, CompressionLevel level)
    {
        int bpp = image.BytesPerPixel;
        int rowBytes = image.Stride;

        byte[] current = new byte[rowBytes];
        byte[] previous = new byte[rowBytes];
        byte[] filtered = new byte[rowBytes];
        byte[] scratch = new byte[rowBytes];

        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, level, leaveOpen: true))
        {
            for (int y = 0; y < image.Height; y++)
            {
                CopyRowToNetworkOrder(image, y, current);

                int filterType = PngFilter.ChooseAndApply(current, previous, filtered, scratch, bpp);
                deflate.WriteByte((byte)filterType);
                deflate.Write(filtered, 0, filtered.Length);

                (current, previous) = (previous, current);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses, unfilters and expands scanline data into RGBA, handling every colour type,
    /// bit depth and both interlace methods.
    /// </summary>
    public static RgbaImage Decode(
        byte[] compressed,
        in PngHeader header,
        byte[]? palette,
        byte[]? transparency)
    {
        var image = new RgbaImage(header.Width, header.Height, header.TargetFormat);
        byte[] raw = Inflate(compressed, EstimateRawSize(header));

        int consumed = 0;
        if (header.InterlaceMethod == 0)
        {
            DecodePass(raw, ref consumed, header, palette, transparency, image,
                width: header.Width, height: header.Height, xStart: 0, yStart: 0, xStep: 1, yStep: 1);
        }
        else
        {
            ReadOnlySpan<int> xStart = [0, 4, 0, 2, 0, 1, 0];
            ReadOnlySpan<int> yStart = [0, 0, 4, 0, 2, 0, 1];
            ReadOnlySpan<int> xStep = [8, 8, 4, 4, 2, 2, 1];
            ReadOnlySpan<int> yStep = [8, 8, 8, 4, 4, 2, 2];

            for (int pass = 0; pass < 7; pass++)
            {
                int passWidth = CeilDiv(header.Width - xStart[pass], xStep[pass]);
                int passHeight = CeilDiv(header.Height - yStart[pass], yStep[pass]);
                if (passWidth <= 0 || passHeight <= 0)
                    continue;

                DecodePass(raw, ref consumed, header, palette, transparency, image,
                    passWidth, passHeight, xStart[pass], yStart[pass], xStep[pass], yStep[pass]);
            }
        }

        return image;
    }

    private static void DecodePass(
        byte[] raw,
        ref int consumed,
        in PngHeader header,
        byte[]? palette,
        byte[]? transparency,
        RgbaImage target,
        int width,
        int height,
        int xStart,
        int yStart,
        int xStep,
        int yStep)
    {
        int rowBytes = header.BytesPerRow(width);
        int bpp = header.FilterBytesPerPixel;

        byte[] current = new byte[rowBytes];
        byte[] previous = new byte[rowBytes];

        for (int row = 0; row < height; row++)
        {
            if (consumed + 1 + rowBytes > raw.Length)
                throw new PngFormatException("Truncated pixel data: fewer scanlines than IHDR promised.");

            int filterType = raw[consumed++];
            raw.AsSpan(consumed, rowBytes).CopyTo(current);
            consumed += rowBytes;

            PngFilter.Unfilter(filterType, current, previous, bpp);
            ExpandRow(current, header, palette, transparency, target, yStart + (row * yStep), xStart, xStep, width);

            (current, previous) = (previous, current);
        }
    }

    private static void ExpandRow(
        ReadOnlySpan<byte> raw,
        in PngHeader header,
        byte[]? palette,
        byte[]? transparency,
        RgbaImage target,
        int y,
        int xStart,
        int xStep,
        int count)
    {
        int depth = header.BitDepth;
        int channels = header.Channels;

        if (depth == 16)
        {
            Span<Rgba64> pixels = target.AsRgba64();
            int rowOffset = y * target.Width;

            for (int i = 0; i < count; i++)
            {
                int s = i * channels;
                Rgba64 pixel = header.ColorType switch
                {
                    PngHeader.ColorTypeGrayscale => Gray16(Sample16(raw, s), transparency),
                    PngHeader.ColorTypeGrayscaleAlpha =>
                        new Rgba64(Sample16(raw, s), Sample16(raw, s), Sample16(raw, s), Sample16(raw, s + 1)),
                    PngHeader.ColorTypeTruecolor =>
                        Color16(Sample16(raw, s), Sample16(raw, s + 1), Sample16(raw, s + 2), transparency),
                    PngHeader.ColorTypeTruecolorAlpha =>
                        new Rgba64(Sample16(raw, s), Sample16(raw, s + 1), Sample16(raw, s + 2), Sample16(raw, s + 3)),
                    _ => throw new PngFormatException($"Colour type {header.ColorType} cannot use a 16-bit depth."),
                };

                pixels[rowOffset + xStart + (i * xStep)] = pixel;
            }

            return;
        }

        Span<Rgba32> pixels8 = target.AsRgba32();
        int rowOffset8 = y * target.Width;

        for (int i = 0; i < count; i++)
        {
            int s = i * channels;
            Rgba32 pixel;

            switch (header.ColorType)
            {
                case PngHeader.ColorTypeGrayscale:
                {
                    int value = Sample(raw, s, depth);
                    byte gray = Scale8(value, depth);
                    byte alpha = IsTransparentGray(transparency, value) ? (byte)0 : byte.MaxValue;
                    pixel = new Rgba32(gray, gray, gray, alpha);
                    break;
                }

                case PngHeader.ColorTypeGrayscaleAlpha:
                {
                    byte gray = Scale8(Sample(raw, s, depth), depth);
                    pixel = new Rgba32(gray, gray, gray, Scale8(Sample(raw, s + 1, depth), depth));
                    break;
                }

                case PngHeader.ColorTypeTruecolor:
                {
                    int r = Sample(raw, s, depth);
                    int g = Sample(raw, s + 1, depth);
                    int b = Sample(raw, s + 2, depth);
                    byte alpha = IsTransparentColor(transparency, r, g, b) ? (byte)0 : byte.MaxValue;
                    pixel = new Rgba32(Scale8(r, depth), Scale8(g, depth), Scale8(b, depth), alpha);
                    break;
                }

                case PngHeader.ColorTypeTruecolorAlpha:
                {
                    pixel = new Rgba32(
                        Scale8(Sample(raw, s, depth), depth),
                        Scale8(Sample(raw, s + 1, depth), depth),
                        Scale8(Sample(raw, s + 2, depth), depth),
                        Scale8(Sample(raw, s + 3, depth), depth));
                    break;
                }

                case PngHeader.ColorTypePalette:
                {
                    if (palette is null)
                        throw new PngFormatException("A palette image is missing its PLTE chunk.");

                    int index = Sample(raw, s, depth);
                    if ((index * 3) + 2 >= palette.Length)
                        throw new PngFormatException($"Palette index {index} is outside the PLTE chunk.");

                    byte alpha = transparency is not null && index < transparency.Length
                        ? transparency[index]
                        : byte.MaxValue;

                    pixel = new Rgba32(palette[index * 3], palette[(index * 3) + 1], palette[(index * 3) + 2], alpha);
                    break;
                }

                default:
                    throw new PngFormatException($"Unsupported colour type {header.ColorType}.");
            }

            pixels8[rowOffset8 + xStart + (i * xStep)] = pixel;
        }
    }

    private static Rgba64 Gray16(ushort gray, byte[]? transparency)
    {
        ushort alpha = transparency is { Length: >= 2 } && ReadBigEndian16(transparency, 0) == gray
            ? (ushort)0
            : ushort.MaxValue;

        return new Rgba64(gray, gray, gray, alpha);
    }

    private static Rgba64 Color16(ushort r, ushort g, ushort b, byte[]? transparency)
    {
        bool clear = transparency is { Length: >= 6 }
            && ReadBigEndian16(transparency, 0) == r
            && ReadBigEndian16(transparency, 2) == g
            && ReadBigEndian16(transparency, 4) == b;

        return new Rgba64(r, g, b, clear ? (ushort)0 : ushort.MaxValue);
    }

    private static bool IsTransparentGray(byte[]? transparency, int value)
        => transparency is { Length: >= 2 } && ReadBigEndian16(transparency, 0) == value;

    private static bool IsTransparentColor(byte[]? transparency, int r, int g, int b)
        => transparency is { Length: >= 6 }
            && ReadBigEndian16(transparency, 0) == r
            && ReadBigEndian16(transparency, 2) == g
            && ReadBigEndian16(transparency, 4) == b;

    private static ushort ReadBigEndian16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static ushort Sample16(ReadOnlySpan<byte> row, int index)
        => (ushort)((row[index * 2] << 8) | row[(index * 2) + 1]);

    private static int Sample(ReadOnlySpan<byte> row, int index, int bitDepth)
    {
        if (bitDepth == 8)
            return row[index];

        int perByte = 8 / bitDepth;
        int shift = 8 - bitDepth - ((index % perByte) * bitDepth);
        return (row[index / perByte] >> shift) & ((1 << bitDepth) - 1);
    }

    /// <summary>Spreads a sub-8-bit sample across the full 0-255 range.</summary>
    private static byte Scale8(int value, int bitDepth) => bitDepth switch
    {
        8 => (byte)value,
        4 => (byte)(value * 17),
        2 => (byte)(value * 85),
        1 => (byte)(value * 255),
        _ => throw new PngFormatException($"Cannot scale a {bitDepth}-bit sample to 8 bits."),
    };

    private static void CopyRowToNetworkOrder(RgbaImage image, int y, Span<byte> destination)
    {
        ReadOnlySpan<byte> row = image.Row(y);

        // PNG stores 16-bit samples big-endian; RgbaImage keeps them in machine order.
        if (image.Format == PixelFormat.Rgba32 || !BitConverter.IsLittleEndian)
        {
            row.CopyTo(destination);
            return;
        }

        for (int i = 0; i < row.Length; i += 2)
        {
            destination[i] = row[i + 1];
            destination[i + 1] = row[i];
        }
    }

    private static byte[] Inflate(byte[] compressed, int expectedLength)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);

        try
        {
            zlib.CopyTo(output);
        }
        catch (InvalidDataException ex)
        {
            throw new PngFormatException("Pixel data is not a valid zlib stream.", ex);
        }

        return output.ToArray();
    }

    private static int EstimateRawSize(in PngHeader header)
    {
        long size = ((long)header.BytesPerRow(header.Width) + 1) * header.Height;
        return size > Array.MaxLength ? Array.MaxLength : (int)size;
    }

    private static int CeilDiv(int value, int divisor) => value <= 0 ? 0 : ((value + divisor - 1) / divisor);
}
