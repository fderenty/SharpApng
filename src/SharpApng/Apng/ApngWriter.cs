using System.Buffers.Binary;

namespace SharpApng;

/// <summary>
/// Writes animated PNGs, one frame at a time.
/// </summary>
/// <remarks>
/// <para>
/// Frames are handed in at full canvas size; the writer works out for itself which rectangle
/// changed and stores only that. Nothing is ever re-encoded through an intermediate PNG, so the
/// header written up front necessarily describes every frame that follows.
/// </para>
/// <para>
/// Call <see cref="Complete"/> when the last frame is in. Disposing without completing writes a
/// valid file too, but <see cref="Complete"/> is the clearer way to say so.
/// </para>
/// </remarks>
public sealed class ApngWriter : IDisposable
{
    private const int FcTlPayloadLength = 26;

    private readonly Stream _destination;
    private readonly Stream _target;
    private readonly MemoryStream? _spool;
    private readonly PngChunkWriter _writer;
    private readonly ApngWriterOptions _options;
    private readonly RgbaImage _canvas;

    private uint _sequenceNumber;
    private int _frameCount;
    private bool _posterWritten;
    private long _actlPosition = -1;
    private long _lastFcTlPosition = -1;
    private byte[]? _lastFcTlPayload;
    private bool _completed;
    private bool _disposed;

    /// <summary>Creates a writer over a stream.</summary>
    /// <param name="stream">Where the APNG goes. Left open by <see cref="Dispose"/>.</param>
    /// <param name="width">Canvas width.</param>
    /// <param name="height">Canvas height.</param>
    /// <param name="format">Channel width for every frame.</param>
    /// <param name="options">Writer settings, or null for the defaults.</param>
    public ApngWriter(
        Stream stream,
        int width,
        int height,
        PixelFormat format = PixelFormat.Rgba32,
        ApngWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        _destination = stream;
        _options = options ?? new ApngWriterOptions();
        Width = width;
        Height = height;
        Format = format;

        // Frame counts and merged delays are patched after the fact, so the writer needs to seek.
        // When the caller's stream cannot, spool into memory and copy across at the end.
        if (stream.CanSeek)
        {
            _target = stream;
        }
        else
        {
            _spool = new MemoryStream();
            _target = _spool;
        }

        _writer = new PngChunkWriter(_target);
        _canvas = new RgbaImage(width, height, format);

        WritePreamble();
    }

    /// <summary>Canvas width in pixels.</summary>
    public int Width { get; }

    /// <summary>Canvas height in pixels.</summary>
    public int Height { get; }

    /// <summary>Channel width every frame must use.</summary>
    public PixelFormat Format { get; }

    /// <summary>How many animation frames have been written so far.</summary>
    public int FrameCount => _frameCount;

    /// <summary>Encodes a whole document in one call.</summary>
    public static void Write(AnimationDocument document, Stream stream, ApngWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);

        if (document.Frames.Count == 0)
            throw new ArgumentException("The document has no frames.", nameof(document));

        options ??= new ApngWriterOptions();
        options.LoopCount = document.LoopCount;

        using var writer = new ApngWriter(stream, document.Width, document.Height, document.Format, options);

        if (document.PosterImage is { } poster)
            writer.SetPosterImage(poster);

        foreach (AnimationFrame frame in document.Frames)
        {
            if (frame.Bounds == document.Bounds && frame.BlendOp == BlendOp.Source && frame.DisposeOp == DisposeOp.None)
            {
                // A plain full-canvas frame: let the writer do its own change detection.
                writer.AddFrame(frame.Image, frame.DelayMilliseconds);
            }
            else
            {
                writer.AddSubFrame(frame.Image, frame.DelayMilliseconds, frame.X, frame.Y, frame.DisposeOp, frame.BlendOp);
            }
        }

        writer.Complete();
    }

    /// <summary>Encodes a whole document to a file.</summary>
    public static void WriteFile(AnimationDocument document, string path, ApngWriterOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FileStream file = File.Create(path);
        Write(document, file, options);
    }

    /// <summary>
    /// Writes a still image that only viewers without APNG support will show, and which takes no
    /// part in the animation.
    /// </summary>
    /// <remarks>
    /// Must be called before the first <see cref="AddFrame"/>. Without it, the first frame serves
    /// as both the still fallback and the opening frame of the loop, which is what most files do.
    /// </remarks>
    public void SetPosterImage(RgbaImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        if (_posterWritten)
            throw new InvalidOperationException("A poster image has already been written.");
        if (_frameCount > 0)
            throw new InvalidOperationException("The poster image must be written before the first frame.");

        RequireCanvasSized(image);

        // No fcTL, so this image is the default image only. The canvas stays transparent because
        // animation-aware viewers never composite it.
        _writer.Write("IDAT", PngRaster.Encode(image, _options.CompressionLevel));
        _posterWritten = true;
    }

    /// <summary>
    /// Adds a full-canvas frame. The writer compares it with what is already on the canvas and
    /// stores only the rectangle that changed.
    /// </summary>
    /// <param name="image">A frame the size of the canvas.</param>
    /// <param name="delayMilliseconds">How long to show it.</param>
    public void AddFrame(RgbaImage image, int delayMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);

        if (_completed)
            throw new InvalidOperationException("The animation has already been completed.");

        RequireCanvasSized(image);

        if (_frameCount == 0 && !_posterWritten)
        {
            WriteFirstFrame(image, delayMilliseconds);
            return;
        }

        PixelRect changed = _options.OptimizeFrames
            ? RgbaImageOps.FindDifference(_canvas, image)
            : Bounds();

        if (changed.IsEmpty)
        {
            if (_options.MergeDuplicateFrames && TryExtendLastDelay(delayMilliseconds))
                return;

            // Nothing changed and the delay could not be folded in, so store a minimal frame.
            changed = new PixelRect(0, 0, 1, 1);
        }

        AddSubFrame(image.Crop(changed), delayMilliseconds, changed.X, changed.Y, DisposeOp.None, BlendOp.Source);
    }

    /// <summary>
    /// Adds a frame at an explicit position with explicit disposal and blending, bypassing the
    /// writer's own change detection.
    /// </summary>
    public void AddSubFrame(
        RgbaImage image,
        int delayMilliseconds,
        int x,
        int y,
        DisposeOp disposeOp = DisposeOp.None,
        BlendOp blendOp = BlendOp.Source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);

        if (_completed)
            throw new InvalidOperationException("The animation has already been completed.");
        if (image.Format != Format)
            throw new ArgumentException($"Expected a {Format} frame, got {image.Format}.", nameof(image));
        if (!Bounds().Contains(new PixelRect(x, y, image.Width, image.Height)))
            throw new ArgumentException("The frame does not fit inside the canvas at that offset.", nameof(image));

        if (_frameCount == 0 && !_posterWritten)
        {
            if (x != 0 || y != 0 || image.Width != Width || image.Height != Height)
            {
                throw new ArgumentException(
                    "The first frame must cover the whole canvas, because it is also the default image. " +
                    "Call SetPosterImage first if the animation should start from a partial frame.",
                    nameof(image));
            }

            WriteFirstFrame(image, delayMilliseconds);
            return;
        }

        WriteFrameControl(image.Width, image.Height, x, y, delayMilliseconds, disposeOp, blendOp);
        _writer.Write("fdAT", SequenceBytes(NextSequence()), PngRaster.Encode(image, _options.CompressionLevel));

        ApplyToCanvas(image, x, y, disposeOp, blendOp);
        _frameCount++;
    }

    /// <summary>
    /// Finishes the file: writes <c>IEND</c> and patches the frame count into <c>acTL</c>.
    /// </summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
            return;

        if (_frameCount == 0)
            throw new InvalidOperationException("An APNG needs at least one frame.");

        _writer.Write("IEND", []);
        PatchFrameCount();

        if (_spool is not null)
        {
            _spool.Position = 0;
            _spool.CopyTo(_destination);
        }

        _completed = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (!_completed && _frameCount > 0)
            Complete();

        _spool?.Dispose();
        _disposed = true;
    }

    private PixelRect Bounds() => new(0, 0, Width, Height);

    private void WritePreamble()
    {
        _writer.WriteSignature();

        var header = new PngHeader(
            Width,
            Height,
            bitDepth: Format == PixelFormat.Rgba64 ? (byte)16 : (byte)8,
            colorType: PngHeader.ColorTypeTruecolorAlpha);

        _writer.Write("IHDR", header.ToPayload());

        // acTL must precede every IDAT and fdAT. The frame count is unknown at this point, so
        // reserve the chunk now and patch it in Complete().
        _actlPosition = _target.Position;
        _writer.Write("acTL", BuildAnimationControl(0, _options.LoopCount));
    }

    private void WriteFirstFrame(RgbaImage image, int delayMilliseconds)
    {
        WriteFrameControl(image.Width, image.Height, 0, 0, delayMilliseconds, DisposeOp.None, BlendOp.Source);
        _writer.Write("IDAT", PngRaster.Encode(image, _options.CompressionLevel));

        image.Pixels.CopyTo(_canvas.Pixels, 0);
        _frameCount++;
    }

    private void RequireCanvasSized(RgbaImage image)
    {
        if (image.Width != Width || image.Height != Height)
            throw new ArgumentException($"Expected a {Width}x{Height} image, got {image.Width}x{image.Height}.", nameof(image));
        if (image.Format != Format)
            throw new ArgumentException($"Expected a {Format} image, got {image.Format}.", nameof(image));
    }

    private void WriteFrameControl(
        int width,
        int height,
        int x,
        int y,
        int delayMilliseconds,
        DisposeOp disposeOp,
        BlendOp blendOp)
    {
        (ushort delayNumerator, ushort delayDenominator) = EncodeDelay(delayMilliseconds);

        var payload = new byte[FcTlPayloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(payload, NextSequence());
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12), (uint)x);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), (uint)y);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(20), delayNumerator);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(22), delayDenominator);
        payload[24] = (byte)disposeOp;
        payload[25] = (byte)blendOp;

        _lastFcTlPosition = _target.Position;
        _lastFcTlPayload = payload;
        _writer.Write("fcTL", payload);
    }

    /// <summary>
    /// Adds <paramref name="extraMilliseconds"/> to the frame already written, by rewriting its
    /// fcTL in place. Returns false when the sum will not fit the delay encoding.
    /// </summary>
    private bool TryExtendLastDelay(int extraMilliseconds)
    {
        if (_lastFcTlPayload is null || _lastFcTlPosition < 0)
            return false;

        ushort numerator = BinaryPrimitives.ReadUInt16BigEndian(_lastFcTlPayload.AsSpan(20));
        ushort denominator = BinaryPrimitives.ReadUInt16BigEndian(_lastFcTlPayload.AsSpan(22));
        if (denominator == 0)
            return false;

        long currentMs = (long)numerator * 1000 / denominator;
        long total = currentMs + extraMilliseconds;
        if (total > int.MaxValue)
            return false;

        (ushort newNumerator, ushort newDenominator) = EncodeDelay((int)total);

        // Round-trip the encoding: if it cannot represent the sum, keep the frame instead.
        if ((long)newNumerator * 1000 / newDenominator != total)
            return false;

        BinaryPrimitives.WriteUInt16BigEndian(_lastFcTlPayload.AsSpan(20), newNumerator);
        BinaryPrimitives.WriteUInt16BigEndian(_lastFcTlPayload.AsSpan(22), newDenominator);

        long resume = _target.Position;
        _target.Position = _lastFcTlPosition;
        new PngChunkWriter(_target).Write("fcTL", _lastFcTlPayload);
        _target.Position = resume;
        return true;
    }

    private void PatchFrameCount()
    {
        if (_actlPosition < 0)
            return;

        long resume = _target.Position;
        _target.Position = _actlPosition;
        new PngChunkWriter(_target).Write("acTL", BuildAnimationControl(_frameCount, _options.LoopCount));
        _target.Position = resume;
    }

    private void ApplyToCanvas(RgbaImage image, int x, int y, DisposeOp disposeOp, BlendOp blendOp)
    {
        RgbaImage? saved = disposeOp == DisposeOp.Previous ? _canvas.Clone() : null;

        if (blendOp == BlendOp.Source)
            _canvas.DrawSource(image, x, y);
        else
            RgbaImageOps.BlendOver(_canvas, image, x, y);

        switch (disposeOp)
        {
            case DisposeOp.Background:
                _canvas.Clear(new PixelRect(x, y, image.Width, image.Height));
                break;

            case DisposeOp.Previous when saved is not null:
                saved.Pixels.CopyTo(_canvas.Pixels, 0);
                break;
        }
    }

    private uint NextSequence() => _sequenceNumber++;

    private static byte[] SequenceBytes(uint sequence)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, sequence);
        return bytes;
    }

    private static byte[] BuildAnimationControl(int frameCount, int loopCount)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)frameCount);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)loopCount);
        return payload;
    }

    /// <summary>
    /// Expresses a millisecond delay as the fraction APNG stores. Both parts are 16-bit, so long
    /// delays trade precision for range rather than overflowing.
    /// </summary>
    internal static (ushort Numerator, ushort Denominator) EncodeDelay(int milliseconds)
    {
        if (milliseconds <= ushort.MaxValue)
            return ((ushort)milliseconds, 1000);

        if (milliseconds / 10 <= ushort.MaxValue)
            return ((ushort)(milliseconds / 10), 100);

        if (milliseconds / 100 <= ushort.MaxValue)
            return ((ushort)(milliseconds / 100), 10);

        int seconds = Math.Min(milliseconds / 1000, ushort.MaxValue);
        return ((ushort)seconds, 1);
    }
}
