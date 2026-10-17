using System.IO.Compression;

namespace SharpApng;

/// <summary>
/// Reads and writes still PNG images.
/// </summary>
/// <remarks>
/// Decoding accepts any PNG the specification allows — every colour type, every bit depth,
/// palettes, <c>tRNS</c> transparency and Adam7 interlacing — and normalises the result to RGBA.
/// Encoding always emits colour type 6 (truecolour with alpha), non-interlaced, at 8 or 16 bits
/// per channel. Writing exactly one layout is deliberate: it is what makes an animation's frames
/// incapable of disagreeing with the header that governs them.
/// </remarks>
public static class PngCodec
{
    /// <summary>Decodes a PNG file.</summary>
    public static RgbaImage DecodeFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>Decodes a PNG from a stream, reading it to the end.</summary>
    public static RgbaImage Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    /// <summary>
    /// Decodes a PNG held in memory. If the data is an APNG, this returns its default image,
    /// which is what a viewer without animation support would show.
    /// </summary>
    public static RgbaImage Decode(ReadOnlyMemory<byte> data)
    {
        var reader = new PngChunkReader(data);
        reader.ReadSignature();

        PngHeader? header = null;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();

        while (reader.HasMore)
        {
            PngChunk chunk = reader.Read();

            switch (chunk.Type)
            {
                case "IHDR":
                    if (header is not null)
                        throw new PngFormatException("A PNG may contain only one IHDR chunk.");
                    header = PngHeader.Parse(chunk.Data);
                    break;

                case "PLTE":
                    palette = chunk.Data;
                    break;

                case "tRNS":
                    transparency = chunk.Data;
                    break;

                case "IDAT":
                    if (header is null)
                        throw new PngFormatException("IDAT appeared before IHDR.");
                    idat.Write(chunk.Data);
                    break;

                case "IEND":
                    goto done;
            }
        }

    done:
        if (header is null)
            throw new PngFormatException("The stream contains no IHDR chunk.");
        if (idat.Length == 0)
            throw new PngFormatException("The stream contains no IDAT chunk.");

        return PngRaster.Decode(idat.ToArray(), header.Value, palette, transparency);
    }

    /// <summary>Encodes an image to a PNG file.</summary>
    public static void EncodeFile(RgbaImage image, string path, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FileStream file = File.Create(path);
        Encode(image, file, level);
    }

    /// <summary>Encodes an image as a PNG onto a stream.</summary>
    public static void Encode(RgbaImage image, Stream stream, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);

        var writer = new PngChunkWriter(stream);
        writer.WriteSignature();

        var header = new PngHeader(
            image.Width,
            image.Height,
            bitDepth: image.Format == PixelFormat.Rgba64 ? (byte)16 : (byte)8,
            colorType: PngHeader.ColorTypeTruecolorAlpha);

        writer.Write("IHDR", header.ToPayload());
        writer.Write("IDAT", PngRaster.Encode(image, level));
        writer.Write("IEND", []);
    }

    /// <summary>Encodes an image as a PNG into a new byte array.</summary>
    public static byte[] EncodeToArray(RgbaImage image, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var buffer = new MemoryStream();
        Encode(image, buffer, level);
        return buffer.ToArray();
    }
}
