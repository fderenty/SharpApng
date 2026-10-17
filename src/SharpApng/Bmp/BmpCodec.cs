using System.Buffers.Binary;
using System.Numerics;

namespace SharpApng;

/// <summary>
/// Decodes Windows BMP files into <see cref="RgbaImage"/>.
/// </summary>
/// <remarks>
/// Covers what a BMP produced by a screen capture or an image editor actually contains: 8-bit
/// palette, 24-bit BGR and 32-bit BGRA, uncompressed or with colour masks, stored either
/// bottom-up or top-down. Run-length encoded and 1/4-bit variants are rejected with a clear
/// message rather than decoded incorrectly.
/// </remarks>
public static class BmpCodec
{
    private const int FileHeaderSize = 14;
    private const uint CompressionRgb = 0;
    private const uint CompressionRle8 = 1;
    private const uint CompressionRle4 = 2;
    private const uint CompressionBitFields = 3;

    /// <summary>Decodes a BMP file.</summary>
    public static RgbaImage DecodeFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>Decodes a BMP from a stream, reading it to the end.</summary>
    public static RgbaImage Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    /// <summary>Decodes a BMP held in memory.</summary>
    /// <exception cref="BmpFormatException">The data is not a BMP this decoder handles.</exception>
    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < FileHeaderSize + 12)
            throw new BmpFormatException("The data is too short to be a BMP.");
        if (data[0] != (byte)'B' || data[1] != (byte)'M')
            throw new BmpFormatException("Not a BMP: the file signature is missing.");

        int pixelOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[FileHeaderSize..]);

        if (headerSize < 40)
            throw new BmpFormatException($"Unsupported DIB header size {headerSize}; this decoder needs at least 40.");
        if (FileHeaderSize + headerSize > data.Length)
            throw new BmpFormatException("Truncated BMP: the DIB header runs past the end of the file.");

        ReadOnlySpan<byte> dib = data.Slice(FileHeaderSize, headerSize);
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        int paletteEntries = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);

        if (width <= 0)
            throw new BmpFormatException($"Invalid BMP width {width}.");
        if (rawHeight == 0)
            throw new BmpFormatException("Invalid BMP height 0.");

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);

        if (compression is CompressionRle8 or CompressionRle4)
            throw new BmpFormatException("Run-length encoded BMP files are not supported.");
        if (compression is not (CompressionRgb or CompressionBitFields))
            throw new BmpFormatException($"Unsupported BMP compression method {compression}.");
        if (bitCount is not (8 or 24 or 32))
            throw new BmpFormatException($"Unsupported BMP bit depth {bitCount}; supported depths are 8, 24 and 32.");

        (uint redMask, uint greenMask, uint blueMask, uint alphaMask) =
            ReadMasks(data, dib, headerSize, bitCount, compression);

        byte[]? palette = null;
        if (bitCount == 8)
        {
            if (paletteEntries == 0)
                paletteEntries = 256;

            int paletteOffset = FileHeaderSize + headerSize;
            int paletteBytes = paletteEntries * 4;
            if (paletteOffset + paletteBytes > data.Length)
                throw new BmpFormatException("Truncated BMP: the colour table runs past the end of the file.");

            palette = data.Slice(paletteOffset, paletteBytes).ToArray();
        }

        int bytesPerPixel = bitCount / 8;
        int rowSize = ((width * bitCount) + 31) / 32 * 4;

        if (pixelOffset < 0 || pixelOffset + ((long)rowSize * height) > data.Length)
            throw new BmpFormatException("Truncated BMP: the pixel data runs past the end of the file.");

        var image = new RgbaImage(width, height);
        Span<Rgba32> pixels = image.AsRgba32();
        bool sawAlpha = false;

        for (int y = 0; y < height; y++)
        {
            int sourceRow = topDown ? y : height - 1 - y;
            ReadOnlySpan<byte> row = data.Slice(pixelOffset + (sourceRow * rowSize), rowSize);
            int destination = y * width;

            for (int x = 0; x < width; x++)
            {
                Rgba32 pixel;

                if (bitCount == 8)
                {
                    int index = row[x] * 4;
                    if (index + 2 >= palette!.Length)
                        throw new BmpFormatException($"Palette index {row[x]} is outside the colour table.");

                    pixel = new Rgba32(palette[index + 2], palette[index + 1], palette[index]);
                }
                else if (bitCount == 24)
                {
                    int offset = x * 3;
                    pixel = new Rgba32(row[offset + 2], row[offset + 1], row[offset]);
                }
                else
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(row[(x * bytesPerPixel)..]);
                    byte alpha = alphaMask == 0 ? byte.MaxValue : Extract(value, alphaMask);
                    pixel = new Rgba32(
                        Extract(value, redMask),
                        Extract(value, greenMask),
                        Extract(value, blueMask),
                        alpha);

                    if (alpha != 0)
                        sawAlpha = true;
                }

                pixels[destination + x] = pixel;
            }
        }

        // A 32-bit BI_RGB bitmap often leaves its fourth byte at zero without meaning
        // "transparent". Treating that literally would decode the whole image to nothing, so
        // fall back to opaque when no pixel carries any alpha at all.
        if (bitCount == 32 && !sawAlpha)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i].A = byte.MaxValue;
        }

        return image;
    }

    private static (uint Red, uint Green, uint Blue, uint Alpha) ReadMasks(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> dib,
        int headerSize,
        int bitCount,
        uint compression)
    {
        if (bitCount != 32)
            return (0x00FF0000, 0x0000FF00, 0x000000FF, 0);

        if (compression == CompressionRgb)
            return (0x00FF0000, 0x0000FF00, 0x000000FF, headerSize >= 108 ? 0xFF000000 : 0);

        // BI_BITFIELDS: V4 and later carry the masks inside the header, version 3 puts them
        // immediately after it.
        if (headerSize >= 56)
        {
            return (
                BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]),
                BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]),
                BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]));
        }

        int offset = FileHeaderSize + headerSize;
        if (offset + 12 > data.Length)
            throw new BmpFormatException("Truncated BMP: the colour masks run past the end of the file.");

        return (
            BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]),
            offset + 16 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]) : 0);
    }

    /// <summary>Pulls one channel out of a packed pixel and rescales it to 0-255.</summary>
    private static byte Extract(uint value, uint mask)
    {
        if (mask == 0)
            return 0;

        int shift = BitOperations.TrailingZeroCount(mask);
        int bits = BitOperations.PopCount(mask);
        uint channel = (value & mask) >> shift;
        uint max = (1u << bits) - 1;

        return max == 0 ? (byte)0 : (byte)(channel * 255 / max);
    }
}

/// <summary>
/// Thrown when BMP data is malformed or uses a variant this decoder does not handle.
/// </summary>
public sealed class BmpFormatException(string message) : Exception(message);
