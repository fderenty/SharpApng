using System.Buffers.Binary;

namespace SharpApng;

/// <summary>
/// Metadata read from a JPEG's headers without decoding its pixels.
/// </summary>
public readonly struct JpegInfo
{
    internal JpegInfo(int width, int height, int componentCount, bool isProgressive, ExifOrientation orientation)
    {
        Width = width;
        Height = height;
        ComponentCount = componentCount;
        IsProgressive = isProgressive;
        Orientation = orientation;
    }

    /// <summary>Image width in pixels, as stored, before any orientation is applied.</summary>
    public int Width { get; }

    /// <summary>Image height in pixels, as stored, before any orientation is applied.</summary>
    public int Height { get; }

    /// <summary>Number of colour components: 1 for greyscale, 3 for colour, 4 for CMYK.</summary>
    public int ComponentCount { get; }

    /// <summary>True when the file uses progressive coding.</summary>
    public bool IsProgressive { get; }

    /// <summary>Orientation declared in EXIF, or <see cref="ExifOrientation.Normal"/> when absent.</summary>
    public ExifOrientation Orientation { get; }
}

/// <summary>
/// Options for <see cref="JpegCodec"/>.
/// </summary>
public sealed class JpegDecoderOptions
{
    /// <summary>
    /// Rotate the decoded image according to the EXIF orientation tag. On by default.
    /// </summary>
    /// <remarks>
    /// Browsers and image viewers all honour this tag. A photograph taken in portrait is stored
    /// in the sensor's landscape orientation with a rotation flag, so ignoring the tag displays
    /// it lying on its side.
    /// </remarks>
    public bool ApplyExifOrientation { get; set; } = true;
}

/// <summary>
/// Decodes JPEG images.
/// </summary>
/// <remarks>
/// <para>
/// Covers baseline and progressive Huffman coding, greyscale and YCbCr, and every chroma
/// subsampling expressible through the sampling factors in <c>SOF</c>. Output is always opaque
/// <see cref="PixelFormat.Rgba32"/>, since JPEG has no alpha channel.
/// </para>
/// <para>
/// Coding processes that are not implemented — arithmetic, lossless, hierarchical, 12-bit and
/// CMYK — are refused with a message naming what was found. A JPEG is never decoded
/// approximately: an Adobe CMYK file, for instance, stores its channels inverted, and guessing
/// at it would produce a plausible-looking image in the wrong colours.
/// </para>
/// <para>
/// Encoding is not provided.
/// </para>
/// </remarks>
public static class JpegCodec
{
    /// <summary>Decodes a JPEG file.</summary>
    public static RgbaImage DecodeFile(string path, JpegDecoderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Decode(File.ReadAllBytes(path), options);
    }

    /// <summary>Decodes a JPEG from a stream, reading it to the end.</summary>
    public static RgbaImage Decode(Stream stream, JpegDecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), options);
    }

    /// <summary>Decodes a JPEG held in memory.</summary>
    /// <exception cref="JpegFormatException">The data is malformed or uses an unsupported coding process.</exception>
    public static RgbaImage Decode(ReadOnlySpan<byte> data, JpegDecoderOptions? options = null)
    {
        options ??= new JpegDecoderOptions();

        var state = new DecoderState();
        Parse(data, state, headersOnly: false);

        if (state.Frame is null)
            throw new JpegFormatException("The stream contains no SOF marker, so there is no image to decode.");

        RgbaImage image = Reconstruct(state);

        return options.ApplyExifOrientation && state.Orientation != ExifOrientation.Normal
            ? RgbaImageOps.ApplyOrientation(image, state.Orientation)
            : image;
    }

    /// <summary>True when the data starts with the JPEG signature.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == 0xFF && data[1] == JpegMarker.Soi && data[2] == 0xFF;

    /// <summary>
    /// Reads dimensions, component count and orientation without decoding any pixels.
    /// </summary>
    public static JpegInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        var state = new DecoderState();
        Parse(data, state, headersOnly: true);

        if (state.Frame is null)
            throw new JpegFormatException("The stream contains no SOF marker.");

        return new JpegInfo(
            state.Frame.Width,
            state.Frame.Height,
            state.Frame.Components.Length,
            state.Frame.IsProgressive,
            state.Orientation);
    }

    private static void Parse(ReadOnlySpan<byte> data, DecoderState state, bool headersOnly)
    {
        if (!IsJpeg(data))
            throw new JpegFormatException("Not a JPEG: the stream does not begin with an SOI marker.");

        int position = 2;

        while (position < data.Length)
        {
            // Markers are structural: read the code and its declared length, never search for a
            // byte pattern. FFD8 and friends occur by chance inside entropy-coded data.
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            while (position < data.Length && data[position] == 0xFF)
                position++;

            if (position >= data.Length)
                break;

            byte marker = data[position++];

            if (marker == JpegMarker.Eoi)
                break;

            if (marker is JpegMarker.Soi || (marker >= JpegMarker.Rst0 && marker <= JpegMarker.Rst7))
                continue;

            if (position + 2 > data.Length)
                throw new JpegFormatException($"Truncated JPEG: marker {JpegMarker.Describe(marker)} has no length field.");

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || position + length > data.Length)
                throw new JpegFormatException($"Marker {JpegMarker.Describe(marker)} declares an impossible length of {length}.");

            ReadOnlySpan<byte> segment = data.Slice(position + 2, length - 2);
            int nextPosition = position + length;

            switch (marker)
            {
                case JpegMarker.Dqt:
                    ReadQuantisationTables(segment, state);
                    break;

                case JpegMarker.Dht:
                    ReadHuffmanTables(segment, state);
                    break;

                case JpegMarker.Dri:
                    if (segment.Length < 2)
                        throw new JpegFormatException("DRI segment is too short.");
                    state.RestartInterval = BinaryPrimitives.ReadUInt16BigEndian(segment);
                    break;

                case JpegMarker.Sof0:
                case JpegMarker.Sof1:
                case JpegMarker.Sof2:
                    ReadFrameHeader(segment, state, progressive: marker == JpegMarker.Sof2);
                    break;

                case JpegMarker.Sof3:
                case JpegMarker.Sof5:
                case JpegMarker.Sof6:
                case JpegMarker.Sof7:
                    throw new JpegFormatException(
                        $"{JpegMarker.Describe(marker)} is not supported. This decoder implements baseline and " +
                        "progressive Huffman coding only; lossless and hierarchical JPEG are not handled.");

                case JpegMarker.Sof9:
                case JpegMarker.Sof10:
                case JpegMarker.Sof11:
                case JpegMarker.Sof13:
                case JpegMarker.Sof14:
                case JpegMarker.Sof15:
                    throw new JpegFormatException(
                        $"{JpegMarker.Describe(marker)} is not supported. Arithmetic coding is virtually unused in " +
                        "practice, so this decoder implements Huffman coding only.");

                case JpegMarker.App1:
                    state.Orientation = JpegExif.ReadOrientation(segment) ?? state.Orientation;
                    break;

                case JpegMarker.App14:
                    ReadAdobeSegment(segment, state);
                    break;

                case JpegMarker.Dnl:
                    // A frame may declare height 0 and supply it here, after the first scan.
                    if (segment.Length >= 2 && state.Frame is { Height: 0 } frame)
                    {
                        frame.Height = BinaryPrimitives.ReadUInt16BigEndian(segment);
                        frame.PrepareComponents();
                    }

                    break;

                case JpegMarker.Sos:
                {
                    if (headersOnly)
                        return;

                    nextPosition = ReadScan(data, segment, position + length, state);
                    break;
                }
            }

            position = nextPosition;
        }
    }

    private static void ReadFrameHeader(ReadOnlySpan<byte> segment, DecoderState state, bool progressive)
    {
        if (state.Frame is not null)
            throw new JpegFormatException("The stream contains more than one SOF marker; hierarchical JPEG is not supported.");
        if (segment.Length < 6)
            throw new JpegFormatException("SOF segment is too short.");

        int precision = segment[0];
        int height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
        int componentCount = segment[5];

        if (precision != 8)
        {
            throw new JpegFormatException(
                $"{precision}-bit samples are not supported; this decoder handles 8-bit JPEG. " +
                "12-bit files come almost exclusively from medical imaging.");
        }

        if (width == 0)
            throw new JpegFormatException("SOF declares a width of zero.");
        if (componentCount == 0)
            throw new JpegFormatException("SOF declares no components.");
        if (segment.Length < 6 + (componentCount * 3))
            throw new JpegFormatException("SOF segment is shorter than its component list.");

        if (componentCount == 4)
        {
            throw new JpegFormatException(
                "Four-component JPEG (CMYK or YCCK) is not supported. Adobe writes these with inverted " +
                "channels, so decoding one without handling that inversion would silently produce an " +
                "image in complementary colours.");
        }

        if (componentCount is not (1 or 3))
            throw new JpegFormatException($"A JPEG with {componentCount} components is not supported.");

        var components = new JpegComponent[componentCount];
        for (int i = 0; i < componentCount; i++)
        {
            int offset = 6 + (i * 3);
            int sampling = segment[offset + 1];
            int horizontal = sampling >> 4;
            int vertical = sampling & 0x0F;

            if (horizontal is < 1 or > 4 || vertical is < 1 or > 4)
                throw new JpegFormatException($"Component {segment[offset]} declares invalid sampling factors {horizontal}x{vertical}.");

            components[i] = new JpegComponent
            {
                Id = segment[offset],
                HorizontalSampling = horizontal,
                VerticalSampling = vertical,
                QuantisationTableIndex = segment[offset + 2],
            };
        }

        state.Frame = new JpegFrame
        {
            Width = width,
            Height = height,
            Precision = precision,
            IsProgressive = progressive,
            Components = components,
        };

        // Height zero means DNL will supply it later; allocation waits until then.
        if (height > 0)
            state.Frame.PrepareComponents();
    }

    private static int ReadScan(ReadOnlySpan<byte> data, ReadOnlySpan<byte> segment, int entropyStart, DecoderState state)
    {
        JpegFrame frame = state.Frame
            ?? throw new JpegFormatException("SOS appeared before SOF.");

        if (frame.Height == 0)
            throw new JpegFormatException("SOS appeared before the image height was known; DNL must precede the first scan here.");
        if (segment.Length < 1)
            throw new JpegFormatException("SOS segment is too short.");

        int count = segment[0];
        if (segment.Length < 1 + (count * 2) + 3)
            throw new JpegFormatException("SOS segment is shorter than its component list.");

        var scanComponents = new JpegComponent[count];
        for (int i = 0; i < count; i++)
        {
            int id = segment[1 + (i * 2)];
            int tables = segment[2 + (i * 2)];

            JpegComponent component = Array.Find(frame.Components, c => c.Id == id)
                ?? throw new JpegFormatException($"The scan references component {id}, which SOF did not declare.");

            component.DcTableIndex = tables >> 4;
            component.AcTableIndex = tables & 0x0F;
            scanComponents[i] = component;
        }

        int tail = 1 + (count * 2);
        int spectralStart = segment[tail];
        int spectralEnd = segment[tail + 1];
        int approximation = segment[tail + 2];

        if (spectralStart > 63 || spectralEnd > 63 || spectralStart > spectralEnd)
            throw new JpegFormatException($"The scan declares an invalid spectral range {spectralStart}..{spectralEnd}.");

        return JpegScanDecoder.Decode(
            data,
            entropyStart,
            frame,
            scanComponents,
            state.DcTables,
            state.AcTables,
            state.RestartInterval,
            spectralStart,
            spectralEnd,
            approximation >> 4,
            approximation & 0x0F);
    }

    private static void ReadQuantisationTables(ReadOnlySpan<byte> segment, DecoderState state)
    {
        int offset = 0;
        while (offset < segment.Length)
        {
            int precisionAndIndex = segment[offset++];
            int index = precisionAndIndex & 0x0F;
            bool sixteenBit = (precisionAndIndex >> 4) != 0;

            if (index > 3)
                throw new JpegFormatException($"DQT declares table index {index}; only 0 to 3 exist.");

            int needed = sixteenBit ? 128 : 64;
            if (offset + needed > segment.Length)
                throw new JpegFormatException("DQT segment is shorter than the table it declares.");

            var table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                // Stored in zigzag order; de-zigzag now so everything downstream, coefficients
                // included, works in natural order.
                int natural = JpegScanDecoder.ZigZag[i];
                table[natural] = sixteenBit
                    ? BinaryPrimitives.ReadUInt16BigEndian(segment[(offset + (i * 2))..])
                    : segment[offset + i];
            }

            offset += needed;
            state.QuantisationTables[index] = table;
        }
    }

    private static void ReadHuffmanTables(ReadOnlySpan<byte> segment, DecoderState state)
    {
        int offset = 0;
        while (offset < segment.Length)
        {
            int classAndIndex = segment[offset++];
            int tableClass = classAndIndex >> 4;
            int index = classAndIndex & 0x0F;

            if (index > 3)
                throw new JpegFormatException($"DHT declares table index {index}; only 0 to 3 exist.");
            if (tableClass > 1)
                throw new JpegFormatException($"DHT declares table class {tableClass}; only 0 (DC) and 1 (AC) exist.");
            if (offset + 16 > segment.Length)
                throw new JpegFormatException("DHT segment is shorter than its code-length counts.");

            ReadOnlySpan<byte> counts = segment.Slice(offset, 16);
            offset += 16;

            int total = 0;
            foreach (byte c in counts)
                total += c;

            if (total > 256)
                throw new JpegFormatException($"DHT declares {total} codes, more than the 256 symbols that exist.");
            if (offset + total > segment.Length)
                throw new JpegFormatException("DHT segment is shorter than its symbol list.");

            byte[] values = segment.Slice(offset, total).ToArray();
            offset += total;

            var table = new JpegHuffmanTable(counts, values);
            if (tableClass == 0)
                state.DcTables[index] = table;
            else
                state.AcTables[index] = table;
        }
    }

    private static void ReadAdobeSegment(ReadOnlySpan<byte> segment, DecoderState state)
    {
        if (segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8))
            state.AdobeColorTransform = segment[11];
    }

    /// <summary>
    /// Turns decoded coefficients into pixels: inverse transform, chroma upsampling, colour
    /// conversion.
    /// </summary>
    private static RgbaImage Reconstruct(DecoderState state)
    {
        JpegFrame frame = state.Frame!;
        var planes = new byte[frame.Components.Length][];
        var planeWidths = new int[frame.Components.Length];
        var planeHeights = new int[frame.Components.Length];

        for (int i = 0; i < frame.Components.Length; i++)
        {
            JpegComponent component = frame.Components[i];
            ushort[] quantisation = state.QuantisationTables[component.QuantisationTableIndex]
                ?? throw new JpegFormatException(
                    $"Component {component.Id} uses quantisation table {component.QuantisationTableIndex}, which no DQT marker defined.");

            // The entropy data covers whole MCUs, so the transformed plane is padded; crop to
            // the component's real extent afterwards.
            int paddedWidth = component.BlocksPerLine * 8;
            int paddedHeight = component.BlocksPerColumn * 8;
            var plane = new byte[(long)paddedWidth * paddedHeight];

            for (int blockRow = 0; blockRow < component.BlocksPerColumn; blockRow++)
            {
                for (int blockColumn = 0; blockColumn < component.BlocksPerLine; blockColumn++)
                {
                    JpegIdct.Transform(
                        component.Coefficients.AsSpan(component.BlockOffset(blockRow, blockColumn), 64),
                        quantisation,
                        plane,
                        (blockRow * 8 * paddedWidth) + (blockColumn * 8),
                        paddedWidth);
                }
            }

            int activeWidth = CeilDiv(frame.Width * component.HorizontalSampling, frame.MaxHorizontalSampling);
            int activeHeight = CeilDiv(frame.Height * component.VerticalSampling, frame.MaxVerticalSampling);

            planes[i] = Upsample(
                plane, paddedWidth, activeWidth, activeHeight,
                component.HorizontalSampling, component.VerticalSampling,
                frame.MaxHorizontalSampling, frame.MaxVerticalSampling,
                frame.Width, frame.Height);

            planeWidths[i] = frame.Width;
            planeHeights[i] = frame.Height;
        }

        var image = new RgbaImage(frame.Width, frame.Height);
        Span<Rgba32> pixels = image.AsRgba32();

        if (frame.Components.Length == 1)
        {
            byte[] grey = planes[0];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Rgba32(grey[i], grey[i], grey[i]);

            return image;
        }

        // Three components are YCbCr unless Adobe says otherwise, or unless the component ids
        // spell R, G, B — both conventions occur in the wild.
        bool isRgb = state.AdobeColorTransform == 0
            || (frame.Components[0].Id == 'R' && frame.Components[1].Id == 'G' && frame.Components[2].Id == 'B');

        byte[] c0 = planes[0];
        byte[] c1 = planes[1];
        byte[] c2 = planes[2];

        if (isRgb)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Rgba32(c0[i], c1[i], c2[i]);

            return image;
        }

        for (int i = 0; i < pixels.Length; i++)
        {
            int y = c0[i];
            int cb = c1[i] - 128;
            int cr = c2[i] - 128;

            pixels[i] = new Rgba32(
                Clamp(y + ((91881 * cr) >> 16)),
                Clamp(y - (((22554 * cb) + (46802 * cr)) >> 16)),
                Clamp(y + ((116130 * cb) >> 16)));
        }

        return image;
    }

    /// <summary>
    /// Expands a component plane to full image resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scale comes from the component's own sampling factors, not from the ratio of plane
    /// size to image size. Those differ whenever the image is not a whole number of MCUs: a
    /// 37-pixel-wide 4:2:0 image stores 19 chroma samples, which cover 38 pixels and are then
    /// cropped. Stretching 19 onto 37 instead would drift by a growing fraction of a pixel across
    /// the row, which is exactly what a diagonal error gradient towards the far corner looks
    /// like.
    /// </para>
    /// <para>
    /// Sample centres are aligned, so a half-resolution plane interpolates with the 3:1 weights
    /// libjpeg calls fancy upsampling. Nearest-neighbour would leave visible colour steps along
    /// edges.
    /// </para>
    /// </remarks>
    private static byte[] Upsample(
        byte[] plane,
        int planeStride,
        int activeWidth,
        int activeHeight,
        int samplingHorizontal,
        int samplingVertical,
        int maxHorizontal,
        int maxVertical,
        int targetWidth,
        int targetHeight)
    {
        var result = new byte[(long)targetWidth * targetHeight];

        if (samplingHorizontal == maxHorizontal && samplingVertical == maxVertical)
        {
            for (int y = 0; y < targetHeight; y++)
                Array.Copy(plane, y * planeStride, result, y * targetWidth, targetWidth);

            return result;
        }

        // 16.16 fixed point. One source sample spans maxSampling/sampling destination pixels.
        long scaleX = ((long)samplingHorizontal << 16) / maxHorizontal;
        long scaleY = ((long)samplingVertical << 16) / maxVertical;

        for (int y = 0; y < targetHeight; y++)
        {
            long sourceY = ((((y << 16) + 32768) * scaleY) >> 16) - 32768;
            int y0 = (int)(sourceY >> 16);
            int fy = (int)(sourceY & 0xFFFF);

            if (sourceY < 0)
            {
                y0 = 0;
                fy = 0;
            }

            y0 = Math.Min(y0, activeHeight - 1);
            int y1 = Math.Min(y0 + 1, activeHeight - 1);

            int row0 = y0 * planeStride;
            int row1 = y1 * planeStride;
            int destination = y * targetWidth;

            for (int x = 0; x < targetWidth; x++)
            {
                long sourceX = ((((x << 16) + 32768) * scaleX) >> 16) - 32768;
                int x0 = (int)(sourceX >> 16);
                int fx = (int)(sourceX & 0xFFFF);

                if (sourceX < 0)
                {
                    x0 = 0;
                    fx = 0;
                }

                x0 = Math.Min(x0, activeWidth - 1);
                int x1 = Math.Min(x0 + 1, activeWidth - 1);

                // Weighted sums rather than base-plus-delta: both operands stay positive, so the
                // rounding term is unbiased. Truncating here would darken every interpolated
                // sample by about half a level, twice over.
                int top = ((plane[row0 + x0] * (65536 - fx)) + (plane[row0 + x1] * fx) + 32768) >> 16;
                int bottom = ((plane[row1 + x0] * (65536 - fx)) + (plane[row1 + x1] * fx) + 32768) >> 16;
                result[destination + x] = (byte)(((top * (65536 - fy)) + (bottom * fy) + 32768) >> 16);
            }
        }

        return result;
    }

    private static byte Clamp(int value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)value;

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    private sealed class DecoderState
    {
        public JpegFrame? Frame { get; set; }

        public ushort[]?[] QuantisationTables { get; } = new ushort[4][];

        public JpegHuffmanTable?[] DcTables { get; } = new JpegHuffmanTable?[4];

        public JpegHuffmanTable?[] AcTables { get; } = new JpegHuffmanTable?[4];

        public int RestartInterval { get; set; }

        public ExifOrientation Orientation { get; set; } = ExifOrientation.Normal;

        /// <summary>The Adobe APP14 colour transform byte, or -1 when the segment is absent.</summary>
        public int AdobeColorTransform { get; set; } = -1;
    }
}
