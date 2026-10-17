using System.Buffers.Binary;
using System.Text;

namespace SharpApng.Tests;

/// <summary>One frame to be written into a synthetic GIF.</summary>
internal sealed class GifTestFrame
{
    public required byte[] Indices { get; init; }

    public int Left { get; init; }

    public int Top { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public int DelayCentiseconds { get; init; }

    /// <summary>0 unspecified, 1 do not dispose, 2 restore to background, 3 restore to previous.</summary>
    public int Disposal { get; init; }

    /// <summary>Palette index to treat as transparent, or -1 for none.</summary>
    public int TransparentIndex { get; init; } = -1;

    public bool Interlaced { get; init; }
}

/// <summary>
/// Builds GIF files by hand so the reader can be tested without binary fixtures in the
/// repository.
/// </summary>
internal static class TestGifs
{
    /// <summary>Assembles a GIF87a/89a file from a palette and a list of frames.</summary>
    public static byte[] Build(
        int width,
        int height,
        Rgba32[] palette,
        GifTestFrame[] frames,
        int loopCount = -1,
        int backgroundIndex = 0)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("GIF89a"));

        Span<byte> word = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)width);
        output.Write(word);
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)height);
        output.Write(word);

        // Global colour table present, 8-bit colour resolution, 256 entries.
        output.WriteByte(0x80 | 0x70 | 0x07);
        output.WriteByte((byte)backgroundIndex);
        output.WriteByte(0);

        for (int i = 0; i < 256; i++)
        {
            Rgba32 c = i < palette.Length ? palette[i] : default;
            output.WriteByte(c.R);
            output.WriteByte(c.G);
            output.WriteByte(c.B);
        }

        if (loopCount >= 0)
        {
            output.WriteByte(0x21);
            output.WriteByte(0xFF);
            output.WriteByte(11);
            output.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            output.WriteByte(3);
            output.WriteByte(1);
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)loopCount);
            output.Write(word);
            output.WriteByte(0);
        }

        foreach (GifTestFrame frame in frames)
        {
            output.WriteByte(0x21);
            output.WriteByte(0xF9);
            output.WriteByte(4);
            output.WriteByte((byte)(((frame.Disposal & 0x07) << 2) | (frame.TransparentIndex >= 0 ? 1 : 0)));
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)frame.DelayCentiseconds);
            output.Write(word);
            output.WriteByte((byte)Math.Max(0, frame.TransparentIndex));
            output.WriteByte(0);

            output.WriteByte(0x2C);
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)frame.Left);
            output.Write(word);
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)frame.Top);
            output.Write(word);
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)frame.Width);
            output.Write(word);
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)frame.Height);
            output.Write(word);
            output.WriteByte((byte)(frame.Interlaced ? 0x40 : 0x00)); // no local colour table

            output.WriteByte(8); // LZW minimum code size
            WriteSubBlocks(output, CompressStored(frame.Indices));
        }

        output.WriteByte(0x3B);
        return output.ToArray();
    }

    /// <summary>
    /// Produces a valid LZW stream that never actually compresses: a Clear Code followed by
    /// literal codes, re-cleared often enough that the code width never has to grow.
    /// </summary>
    /// <remarks>
    /// This is the well-known "uncompressed GIF" trick. It exercises the decoder's bit unpacking,
    /// clear-code handling and sub-block reassembly without needing an encoder. Real-world LZW,
    /// with a growing dictionary, is covered separately by decoding files produced by Pillow.
    /// </remarks>
    private static byte[] CompressStored(byte[] indices)
    {
        const int ClearCode = 256;
        const int EndCode = 257;
        const int CodeSize = 9;

        using var bits = new MemoryStream();
        int buffer = 0;
        int count = 0;

        void Emit(int code)
        {
            buffer |= code << count;
            count += CodeSize;
            while (count >= 8)
            {
                bits.WriteByte((byte)(buffer & 0xFF));
                buffer >>= 8;
                count -= 8;
            }
        }

        Emit(ClearCode);
        int sinceClear = 0;

        foreach (byte index in indices)
        {
            // After a clear the next free code is 258; emitting 254 literals keeps it below the
            // 512 that would force a tenth bit.
            if (sinceClear >= 254)
            {
                Emit(ClearCode);
                sinceClear = 0;
            }

            Emit(index);
            sinceClear++;
        }

        Emit(EndCode);

        if (count > 0)
            bits.WriteByte((byte)(buffer & 0xFF));

        return bits.ToArray();
    }

    private static void WriteSubBlocks(Stream output, byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int length = Math.Min(255, data.Length - offset);
            output.WriteByte((byte)length);
            output.Write(data, offset, length);
            offset += length;
        }

        output.WriteByte(0);
    }

    /// <summary>Fills an index buffer with a solid value.</summary>
    public static byte[] Solid(int width, int height, byte index)
    {
        var data = new byte[width * height];
        Array.Fill(data, index);
        return data;
    }

    /// <summary>Fills an index buffer, then paints a rectangle with another index.</summary>
    public static byte[] WithRect(int width, int height, byte background, PixelRect rect, byte index)
    {
        byte[] data = Solid(width, height, background);
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
                data[(y * width) + x] = index;
        }

        return data;
    }
}
