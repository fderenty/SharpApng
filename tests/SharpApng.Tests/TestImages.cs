using System.Buffers.Binary;
using System.IO.Compression;

namespace SharpApng.Tests;

/// <summary>
/// Builders for the images and hand-rolled PNG streams the tests work against.
/// </summary>
internal static class TestImages
{
    /// <summary>
    /// A deterministic image whose pixels vary with position and with <paramref name="seed"/>,
    /// so two different seeds never coincide.
    /// </summary>
    public static RgbaImage Gradient(int width, int height, int seed = 0, PixelFormat format = PixelFormat.Rgba32)
    {
        var image = new RgbaImage(width, height, format);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int r = (x * 7) + (seed * 13);
                int g = (y * 11) + (seed * 5);
                int b = (x + y + seed) * 3;
                int a = 255 - ((x + y) % 64);

                if (format == PixelFormat.Rgba32)
                    image.SetPixel(x, y, new Rgba32((byte)r, (byte)g, (byte)b, (byte)a));
                else
                    image.SetPixel(x, y, new Rgba64((ushort)(r * 257), (ushort)(g * 257), (ushort)(b * 257), (ushort)(a * 257)));
            }
        }

        return image;
    }

    /// <summary>A transparent canvas with one opaque rectangle painted on it.</summary>
    public static RgbaImage Rectangle(int width, int height, PixelRect rect, Rgba32 color)
    {
        var image = new RgbaImage(width, height);

        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
                image.SetPixel(x, y, color);
        }

        return image;
    }

    /// <summary>A uniformly filled image.</summary>
    public static RgbaImage Solid(int width, int height, Rgba32 color)
        => Rectangle(width, height, new PixelRect(0, 0, width, height), color);

    /// <summary>
    /// Assembles a PNG by hand so the decoder can be tested against colour types this library
    /// never writes.
    /// </summary>
    /// <param name="scanlines">Unfiltered rows, without their per-row filter byte.</param>
    public static byte[] BuildPng(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte[][] scanlines,
        byte[]? palette = null,
        byte[]? transparency = null)
    {
        using var body = new MemoryStream();
        using (var zlib = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (byte[] row in scanlines)
            {
                zlib.WriteByte(0); // filter type: None
                zlib.Write(row, 0, row.Length);
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = bitDepth;
        header[9] = colorType;

        WriteChunk(output, "IHDR", header);

        if (palette is not null)
            WriteChunk(output, "PLTE", palette);
        if (transparency is not null)
            WriteChunk(output, "tRNS", transparency);

        WriteChunk(output, "IDAT", body.ToArray());
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    /// <summary>
    /// Assembles an uncompressed 32-bit BMP with a BITMAPV4HEADER, which is the smallest header
    /// that can declare an alpha channel.
    /// </summary>
    public static byte[] BuildBmp32(int width, int height, Rgba32[] pixels, bool topDown = false)
    {
        const int HeaderSize = 108; // BITMAPV4HEADER
        int rowSize = width * 4;
        int pixelDataSize = rowSize * height;
        const int Offset = 14 + HeaderSize;

        var output = new byte[Offset + pixelDataSize];
        output[0] = (byte)'B';
        output[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2), (uint)output.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(10), Offset);

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(14), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(28), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(30), 3); // BI_BITFIELDS

        // Channel masks live inside a V4 header, at offsets 40..55 from its start.
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(14 + 40), 0x00FF0000); // red
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(14 + 44), 0x0000FF00); // green
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(14 + 48), 0x000000FF); // blue
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(14 + 52), 0xFF000000); // alpha

        for (int y = 0; y < height; y++)
        {
            int sourceRow = topDown ? y : height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                Rgba32 p = pixels[(y * width) + x];
                int index = Offset + (sourceRow * rowSize) + (x * 4);
                output[index] = p.B;
                output[index + 1] = p.G;
                output[index + 2] = p.R;
                output[index + 3] = p.A;
            }
        }

        return output;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(typeBytes, data));
        stream.Write(crc);
    }
}
