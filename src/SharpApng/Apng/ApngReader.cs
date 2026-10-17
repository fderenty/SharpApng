using System.Buffers.Binary;

namespace SharpApng;

/// <summary>
/// Reads animated PNGs into an <see cref="AnimationDocument"/>.
/// </summary>
/// <remarks>
/// Frames come back exactly as the file stores them — cropped sub-images with their offsets,
/// disposal and blending intact. Call <see cref="AnimationDocument.RenderFrames"/> to replay them
/// into the full-canvas images a viewer would actually display.
/// </remarks>
public static class ApngReader
{
    /// <summary>Reads an APNG file.</summary>
    public static AnimationDocument ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Read(File.ReadAllBytes(path));
    }

    /// <summary>Reads an APNG from a stream, consuming it to the end.</summary>
    public static AnimationDocument Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    /// <summary>
    /// Reads an APNG held in memory. A still PNG is accepted too and yields a single-frame
    /// document.
    /// </summary>
    /// <exception cref="PngFormatException">The stream is not a well-formed PNG.</exception>
    public static AnimationDocument Read(ReadOnlyMemory<byte> data)
    {
        var reader = new PngChunkReader(data);
        reader.ReadSignature();

        PngHeader? header = null;
        byte[]? palette = null;
        byte[]? transparency = null;
        int loopCount = 0;
        bool sawAnimationControl = false;
        bool sawDefaultImage = false;
        uint expectedSequence = 0;

        using var defaultImageData = new MemoryStream();
        FrameControl? defaultImageControl = null;

        var frames = new List<PendingFrame>();
        PendingFrame? pending = null;

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

                case "acTL":
                    if (sawDefaultImage)
                        throw new PngFormatException("acTL must appear before the first IDAT.");
                    if (chunk.Data.Length < 8)
                        throw new PngFormatException($"acTL must be 8 bytes, found {chunk.Data.Length}.");

                    loopCount = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(chunk.Data.AsSpan(4)), int.MaxValue);
                    sawAnimationControl = true;
                    break;

                case "fcTL":
                {
                    FrameControl frameControl = FrameControl.Parse(chunk.Data);
                    RequireSequence(frameControl.SequenceNumber, ref expectedSequence, "fcTL");

                    if (!sawDefaultImage)
                    {
                        if (defaultImageControl is not null)
                            throw new PngFormatException("The default image carries more than one fcTL chunk.");
                        defaultImageControl = frameControl;
                    }
                    else
                    {
                        if (pending is not null)
                            frames.Add(pending);
                        pending = new PendingFrame(frameControl);
                    }

                    break;
                }

                case "IDAT":
                    if (header is null)
                        throw new PngFormatException("IDAT appeared before IHDR.");
                    sawDefaultImage = true;
                    defaultImageData.Write(chunk.Data);
                    break;

                case "fdAT":
                {
                    if (chunk.Data.Length < 4)
                        throw new PngFormatException("fdAT is too short to hold a sequence number.");
                    if (pending is null)
                        throw new PngFormatException("fdAT appeared before its fcTL chunk.");

                    uint sequence = BinaryPrimitives.ReadUInt32BigEndian(chunk.Data);
                    RequireSequence(sequence, ref expectedSequence, "fdAT");
                    pending.Data.Write(chunk.Data.AsSpan(4));
                    break;
                }

                case "IEND":
                    goto done;
            }
        }

    done:
        if (header is null)
            throw new PngFormatException("The stream contains no IHDR chunk.");
        if (defaultImageData.Length == 0)
            throw new PngFormatException("The stream contains no IDAT chunk.");

        if (pending is not null)
            frames.Add(pending);

        var document = new AnimationDocument(header.Value.Width, header.Value.Height, header.Value.TargetFormat)
        {
            LoopCount = loopCount,
        };

        RgbaImage defaultImage = PngRaster.Decode(defaultImageData.ToArray(), header.Value, palette, transparency);

        if (!sawAnimationControl)
        {
            // A still PNG. Present it as a one-frame animation so callers need no special case.
            document.Add(new AnimationFrame(defaultImage, 0));
            return document;
        }

        if (defaultImageControl is { } control)
            document.Add(BuildFrame(defaultImage, control));
        else
            document.PosterImage = defaultImage;

        foreach (PendingFrame frame in frames)
        {
            var frameHeader = new PngHeader(
                (int)frame.Control.Width,
                (int)frame.Control.Height,
                header.Value.BitDepth,
                header.Value.ColorType,
                header.Value.InterlaceMethod);

            RgbaImage image = PngRaster.Decode(frame.Data.ToArray(), frameHeader, palette, transparency);
            document.Add(BuildFrame(image, frame.Control));
            frame.Data.Dispose();
        }

        return document;
    }

    /// <summary>
    /// True when the data is a PNG carrying an <c>acTL</c> chunk, and therefore animated.
    /// </summary>
    public static bool IsAnimated(ReadOnlyMemory<byte> data)
    {
        try
        {
            var reader = new PngChunkReader(data);
            reader.ReadSignature();

            while (reader.HasMore)
            {
                PngChunk chunk = reader.Read();
                if (chunk.Type == "acTL")
                    return true;
                if (chunk.Type is "IDAT" or "IEND")
                    return false;
            }
        }
        catch (PngFormatException)
        {
            return false;
        }

        return false;
    }

    private static AnimationFrame BuildFrame(RgbaImage image, FrameControl control)
    {
        int delay = control.DelayDenominator == 0
            ? control.DelayNumerator * 1000 / 100   // A zero denominator means 1/100s per the spec.
            : control.DelayNumerator * 1000 / control.DelayDenominator;

        return new AnimationFrame(
            image,
            delay,
            (int)control.XOffset,
            (int)control.YOffset,
            control.DisposeOp,
            control.BlendOp);
    }

    private static void RequireSequence(uint actual, ref uint expected, string chunkType)
    {
        if (actual != expected)
        {
            throw new PngFormatException(
                $"Out-of-order {chunkType}: expected sequence number {expected}, found {actual}.");
        }

        expected++;
    }

    private sealed class PendingFrame(FrameControl control)
    {
        public FrameControl Control { get; } = control;

        public MemoryStream Data { get; } = new();
    }

    private readonly struct FrameControl
    {
        private FrameControl(
            uint sequenceNumber,
            uint width,
            uint height,
            uint xOffset,
            uint yOffset,
            ushort delayNumerator,
            ushort delayDenominator,
            DisposeOp disposeOp,
            BlendOp blendOp)
        {
            SequenceNumber = sequenceNumber;
            Width = width;
            Height = height;
            XOffset = xOffset;
            YOffset = yOffset;
            DelayNumerator = delayNumerator;
            DelayDenominator = delayDenominator;
            DisposeOp = disposeOp;
            BlendOp = blendOp;
        }

        public uint SequenceNumber { get; }

        public uint Width { get; }

        public uint Height { get; }

        public uint XOffset { get; }

        public uint YOffset { get; }

        public ushort DelayNumerator { get; }

        public ushort DelayDenominator { get; }

        public DisposeOp DisposeOp { get; }

        public BlendOp BlendOp { get; }

        public static FrameControl Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length != 26)
                throw new PngFormatException($"fcTL must be 26 bytes, found {data.Length}.");

            byte disposeOp = data[24];
            byte blendOp = data[25];

            if (disposeOp > 2)
                throw new PngFormatException($"Unknown dispose_op value {disposeOp}.");
            if (blendOp > 1)
                throw new PngFormatException($"Unknown blend_op value {blendOp}.");

            return new FrameControl(
                BinaryPrimitives.ReadUInt32BigEndian(data),
                BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
                BinaryPrimitives.ReadUInt32BigEndian(data[16..]),
                BinaryPrimitives.ReadUInt16BigEndian(data[20..]),
                BinaryPrimitives.ReadUInt16BigEndian(data[22..]),
                (DisposeOp)disposeOp,
                (BlendOp)blendOp);
        }
    }
}
