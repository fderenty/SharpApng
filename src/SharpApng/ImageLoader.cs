namespace SharpApng;

/// <summary>
/// Loads an image without the caller having to know which format it is in.
/// </summary>
/// <remarks>
/// The format is decided by the data's own leading bytes, never by the file extension.
/// </remarks>
public static class ImageLoader
{
    /// <summary>Loads a PNG or BMP file.</summary>
    public static RgbaImage LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Loads a PNG, BMP or GIF from memory. Animated sources yield their first frame; use
    /// <see cref="AnimationLoader"/> to keep the whole animation.
    /// </summary>
    /// <exception cref="NotSupportedException">The data is in none of those formats.</exception>
    public static RgbaImage Load(ReadOnlyMemory<byte> data)
    {
        if (IsPng(data.Span))
            return PngCodec.Decode(data);

        if (IsBmp(data.Span))
            return BmpCodec.Decode(data.Span);

        if (JpegCodec.IsJpeg(data.Span))
            return JpegCodec.Decode(data.Span);

        if (GifReader.IsGif(data.Span))
        {
            // Composited, not the stored sub-image: a GIF's first frame may be smaller than the
            // canvas and positioned within it.
            using IEnumerator<RgbaImage> frames = GifReader.Read(data).RenderFrames().GetEnumerator();
            return frames.MoveNext() ? frames.Current : throw new NotSupportedException("The GIF contains no frames.");
        }

        throw new NotSupportedException(
            "Unrecognised image format. This library decodes PNG, BMP, GIF and JPEG; build an " +
            "RgbaImage directly with RgbaImage.Wrap or RgbaImage.FromRaw for anything else.");
    }

    /// <summary>True when the data starts with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> data)
        => data.Length >= 8 && data[..8].SequenceEqual(PngChunkReader.Signature);

    /// <summary>True when the data starts with the BMP signature.</summary>
    public static bool IsBmp(ReadOnlySpan<byte> data)
        => data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M';
}
