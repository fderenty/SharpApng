namespace SharpApng;

/// <summary>
/// Loads an animation from any format this library reads, without the caller having to know
/// which one it is.
/// </summary>
/// <remarks>
/// The point of entry for using a GIF as a source: load it and hand the result straight to
/// <see cref="ApngWriter"/>, in memory, with no temporary file in between.
/// <code>
/// AnimationDocument source = AnimationLoader.LoadFile("input.gif");
/// ApngWriter.WriteFile(source, "output.png");
/// </code>
/// </remarks>
public static class AnimationLoader
{
    /// <summary>Loads a GIF, an APNG or a still PNG from a file.</summary>
    public static AnimationDocument LoadFile(string path, GifReaderOptions? gifOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Load(File.ReadAllBytes(path), gifOptions);
    }

    /// <summary>Loads a GIF, an APNG or a still PNG from a stream, consuming it to the end.</summary>
    public static AnimationDocument Load(Stream stream, GifReaderOptions? gifOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Load(buffer.ToArray(), gifOptions);
    }

    /// <summary>
    /// Loads a GIF, an APNG or a still PNG held in memory. The format is decided by the data's
    /// own leading bytes, never by a file extension.
    /// </summary>
    /// <exception cref="NotSupportedException">The data is in none of those formats.</exception>
    public static AnimationDocument Load(ReadOnlyMemory<byte> data, GifReaderOptions? gifOptions = null)
    {
        if (GifReader.IsGif(data.Span))
            return GifReader.Read(data, gifOptions);

        if (ImageLoader.IsPng(data.Span))
            return ApngReader.Read(data);

        if (ImageLoader.IsBmp(data.Span) || JpegCodec.IsJpeg(data.Span))
        {
            // A single still, presented as a one-frame animation so callers need no special case.
            RgbaImage image = ImageLoader.Load(data);
            var document = new AnimationDocument(image.Width, image.Height, image.Format);
            document.Add(image, 0);
            return document;
        }

        throw new NotSupportedException(
            "Unrecognised format. This library reads GIF, PNG, APNG, BMP and JPEG; build an " +
            "AnimationDocument directly for anything else.");
    }
}
