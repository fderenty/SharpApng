using System.Buffers.Binary;
using System.Text;

namespace SharpApng;

/// <summary>
/// Reads GIF files, animated or not, into an <see cref="AnimationDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// GIF's animation model maps almost exactly onto APNG's: its three disposal methods are
/// <see cref="DisposeOp"/>, its index-based transparency behaves as <see cref="BlendOp.Over"/>
/// with all-or-nothing alpha, and its hundredths-of-a-second delays land on
/// <c>delay_den = 100</c> with no rounding. A converted file therefore keeps the timing and
/// layering of the original rather than approximating them.
/// </para>
/// <para>
/// The result is RGBA, so the 256-colour ceiling and the single transparent index are gone. That
/// is what makes <see cref="GifReaderOptions.TransparentColor"/> worth having: a background that
/// GIF could only paint flat can become genuinely transparent on the way out.
/// </para>
/// </remarks>
public static class GifReader
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationLabel = 0xFF;

    /// <summary>Reads a GIF file.</summary>
    public static AnimationDocument ReadFile(string path, GifReaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Read(File.ReadAllBytes(path), options);
    }

    /// <summary>Reads a GIF from a stream, consuming it to the end.</summary>
    public static AnimationDocument Read(Stream stream, GifReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray(), options);
    }

    /// <summary>Reads a GIF held in memory.</summary>
    /// <exception cref="GifFormatException">The data is not a well-formed GIF.</exception>
    public static AnimationDocument Read(ReadOnlyMemory<byte> data, GifReaderOptions? options = null)
    {
        options ??= new GifReaderOptions();
        var reader = new Cursor(data.Span);

        string signature = Encoding.ASCII.GetString(reader.Take(6));
        if (signature is not ("GIF87a" or "GIF89a"))
            throw new GifFormatException($"Not a GIF: expected the signature GIF87a or GIF89a, found '{signature}'.");

        int canvasWidth = reader.UInt16();
        int canvasHeight = reader.UInt16();
        byte packed = reader.Byte();
        byte backgroundIndex = reader.Byte();
        reader.Byte(); // pixel aspect ratio, of no use here

        if (canvasWidth == 0 || canvasHeight == 0)
            throw new GifFormatException("The GIF declares a zero-sized canvas.");

        Rgba32[]? globalPalette = (packed & 0x80) != 0
            ? ReadPalette(ref reader, 1 << ((packed & 0x07) + 1))
            : null;

        var document = new AnimationDocument(canvasWidth, canvasHeight);

        // State carried by a Graphic Control Extension onto the image that follows it.
        int delayCentiseconds = 0;
        var disposal = DisposeOp.None;
        int transparentIndex = -1;

        while (!reader.AtEnd)
        {
            byte block = reader.Byte();

            if (block == Trailer)
                break;

            switch (block)
            {
                case 0x00:
                    // Stray padding. Several encoders emit it; skipping is what viewers do.
                    continue;

                case ExtensionIntroducer:
                {
                    byte label = reader.Byte();
                    if (label == GraphicControlLabel)
                    {
                        reader.Byte(); // block size, always 4
                        byte flags = reader.Byte();
                        delayCentiseconds = reader.UInt16();
                        byte index = reader.Byte();
                        reader.Byte(); // block terminator

                        transparentIndex = (flags & 0x01) != 0 ? index : -1;
                        disposal = ToDisposeOp((flags >> 2) & 0x07);
                    }
                    else if (label == ApplicationLabel)
                    {
                        ReadApplicationExtension(ref reader, document);
                    }
                    else
                    {
                        reader.SkipSubBlocks();
                    }

                    continue;
                }

                case ImageSeparator:
                {
                    AnimationFrame? frame = ReadFrame(
                        ref reader,
                        document,
                        globalPalette,
                        transparentIndex,
                        DelayToMilliseconds(delayCentiseconds, options.ClampShortDelays),
                        disposal);

                    if (frame is not null)
                        document.Add(frame);

                    // Graphic control state applies to one image only.
                    delayCentiseconds = 0;
                    disposal = DisposeOp.None;
                    transparentIndex = -1;
                    continue;
                }

                default:
                    throw new GifFormatException($"Unexpected block introducer 0x{block:X2}.");
            }
        }

        if (document.Frames.Count == 0)
            throw new GifFormatException("The GIF contains no image data.");

        Rgba32? key = ResolveColorKey(options, globalPalette, backgroundIndex);
        if (key is not null || options.FlattenFrames)
            document = Flatten(document, key, options.ColorTolerance);

        return document;
    }

    /// <summary>True when the data starts with a GIF signature.</summary>
    public static bool IsGif(ReadOnlySpan<byte> data)
        => data.Length >= 6
        && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F'
        && data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a';

    private static AnimationFrame? ReadFrame(
        ref Cursor reader,
        AnimationDocument document,
        Rgba32[]? globalPalette,
        int transparentIndex,
        int delayMilliseconds,
        DisposeOp disposal)
    {
        int left = reader.UInt16();
        int top = reader.UInt16();
        int width = reader.UInt16();
        int height = reader.UInt16();
        byte packed = reader.Byte();

        Rgba32[]? palette = (packed & 0x80) != 0
            ? ReadPalette(ref reader, 1 << ((packed & 0x07) + 1))
            : globalPalette;

        bool interlaced = (packed & 0x40) != 0;

        byte minimumCodeSize = reader.Byte();
        byte[] compressed = reader.ReadSubBlocks();

        if (width == 0 || height == 0)
            return null;

        if (palette is null)
            throw new GifFormatException("A frame has no colour table, neither local nor global.");

        var indices = new byte[width * height];
        GifLzwDecoder.Decode(compressed, minimumCodeSize, indices);

        if (interlaced)
            indices = Deinterlace(indices, width, height);

        // Frames may hang off the canvas; viewers clip rather than grow the canvas, so do that.
        int clippedWidth = Math.Min(width, document.Width - left);
        int clippedHeight = Math.Min(height, document.Height - top);
        if (left >= document.Width || top >= document.Height || clippedWidth <= 0 || clippedHeight <= 0)
            return null;

        var image = new RgbaImage(clippedWidth, clippedHeight);
        Span<Rgba32> pixels = image.AsRgba32();

        for (int y = 0; y < clippedHeight; y++)
        {
            int source = y * width;
            int destination = y * clippedWidth;

            for (int x = 0; x < clippedWidth; x++)
            {
                int index = indices[source + x];
                pixels[destination + x] = index == transparentIndex || index >= palette.Length
                    ? default
                    : palette[index];
            }
        }

        // Over is exact here: GIF transparency is all-or-nothing, so a transparent pixel shows
        // what is beneath it and an opaque one replaces it, which is precisely what Over does.
        return new AnimationFrame(image, delayMilliseconds, left, top, disposal, BlendOp.Over);
    }

    private static void ReadApplicationExtension(ref Cursor reader, AnimationDocument document)
    {
        byte size = reader.Byte();
        ReadOnlySpan<byte> identifier = reader.Take(size);

        if (size == 11 && identifier.StartsWith("NETSCAPE2.0"u8))
        {
            // Sub-block: [length=3][1][loop count, little endian]
            byte length = reader.Byte();
            if (length >= 3)
            {
                ReadOnlySpan<byte> payload = reader.Take(length);
                if (payload[0] == 1)
                    document.LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
            }
            else if (length > 0)
            {
                reader.Take(length);
            }
        }

        reader.SkipSubBlocks();
    }

    private static Rgba32[] ReadPalette(ref Cursor reader, int entries)
    {
        ReadOnlySpan<byte> raw = reader.Take(entries * 3);
        var palette = new Rgba32[entries];

        for (int i = 0; i < entries; i++)
            palette[i] = new Rgba32(raw[i * 3], raw[(i * 3) + 1], raw[(i * 3) + 2]);

        return palette;
    }

    /// <summary>GIF stores interlaced rows in four passes; put them back in order.</summary>
    private static byte[] Deinterlace(byte[] source, int width, int height)
    {
        ReadOnlySpan<int> starts = [0, 4, 2, 1];
        ReadOnlySpan<int> steps = [8, 8, 4, 2];

        var result = new byte[source.Length];
        int row = 0;

        for (int pass = 0; pass < 4; pass++)
        {
            for (int y = starts[pass]; y < height; y += steps[pass])
            {
                source.AsSpan(row * width, width).CopyTo(result.AsSpan(y * width, width));
                row++;
            }
        }

        return result;
    }

    private static DisposeOp ToDisposeOp(int method) => method switch
    {
        // 0 (unspecified) and 1 (do not dispose) both leave the canvas alone.
        0 or 1 => DisposeOp.None,

        // The specification says "restore to background colour", but every browser clears to
        // transparent instead. Matching the behaviour everyone sees beats matching the text.
        2 => DisposeOp.Background,

        3 => DisposeOp.Previous,

        // 4-7 are undefined; treating them as "leave it" is what viewers do.
        _ => DisposeOp.None,
    };

    private static int DelayToMilliseconds(int centiseconds, bool clampShortDelays)
    {
        if (clampShortDelays && centiseconds <= 1)
            centiseconds = 10;

        return centiseconds * 10;
    }

    private static Rgba32? ResolveColorKey(GifReaderOptions options, Rgba32[]? globalPalette, byte backgroundIndex)
    {
        if (options.TransparentColor is { } explicitColor)
            return explicitColor;

        if (options.TreatBackgroundAsTransparent && globalPalette is not null && backgroundIndex < globalPalette.Length)
            return globalPalette[backgroundIndex];

        return null;
    }

    /// <summary>
    /// Replays the animation and rebuilds it from full-canvas frames, optionally erasing a
    /// colour along the way.
    /// </summary>
    /// <remarks>
    /// Applying a colour key to the stored sub-frames directly would be wrong. Those frames use
    /// <see cref="BlendOp.Over"/>, and Over cannot lower the alpha already on the canvas — a
    /// pixel that was opaque in the GIF and becomes transparent here would simply fail to erase
    /// what is beneath it. Compositing first and re-emitting whole frames with
    /// <see cref="BlendOp.Source"/> sidesteps that entirely; <see cref="ApngWriter"/> then crops
    /// them back down, so nothing is lost in file size.
    /// </remarks>
    private static AnimationDocument Flatten(AnimationDocument source, Rgba32? colorKey, int tolerance)
    {
        var result = new AnimationDocument(source.Width, source.Height, source.Format)
        {
            LoopCount = source.LoopCount,
        };

        int index = 0;
        foreach (RgbaImage rendered in source.RenderFrames())
        {
            if (colorKey is { } key)
                EraseColor(rendered, key, tolerance);

            result.Add(new AnimationFrame(rendered, source.Frames[index].DelayMilliseconds));
            index++;
        }

        return result;
    }

    private static void EraseColor(RgbaImage image, Rgba32 key, int tolerance)
    {
        Span<Rgba32> pixels = image.AsRgba32();

        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            if (p.A == 0)
                continue;

            if (tolerance == 0)
            {
                if (p.R == key.R && p.G == key.G && p.B == key.B)
                    pixels[i] = default;
            }
            else if (Math.Abs(p.R - key.R) <= tolerance
                  && Math.Abs(p.G - key.G) <= tolerance
                  && Math.Abs(p.B - key.B) <= tolerance)
            {
                pixels[i] = default;
            }
        }
    }

    /// <summary>A forward-only cursor over the file, with GIF's little-endian conventions.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly bool AtEnd => _position >= _data.Length;

        public byte Byte()
        {
            if (_position >= _data.Length)
                throw new GifFormatException("Truncated GIF: the file ends mid-structure.");

            return _data[_position++];
        }

        public int UInt16()
        {
            ReadOnlySpan<byte> bytes = Take(2);
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }

        public ReadOnlySpan<byte> Take(int count)
        {
            if (_position + count > _data.Length)
                throw new GifFormatException($"Truncated GIF: {count} bytes were expected but only {_data.Length - _position} remain.");

            ReadOnlySpan<byte> slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }

        /// <summary>
        /// Reads a chain of length-prefixed sub-blocks and joins them. LZW codes straddle
        /// sub-block boundaries, so they have to be concatenated before decoding.
        /// </summary>
        public byte[] ReadSubBlocks()
        {
            using var buffer = new MemoryStream();

            while (true)
            {
                byte length = Byte();
                if (length == 0)
                    return buffer.ToArray();

                buffer.Write(Take(length));
            }
        }

        public void SkipSubBlocks()
        {
            while (true)
            {
                byte length = Byte();
                if (length == 0)
                    return;

                Take(length);
            }
        }
    }
}
