namespace SharpApng;

/// <summary>
/// An animation: a fixed-size canvas, an ordered list of frames, and a loop count.
/// </summary>
/// <remarks>
/// This is the format-neutral model. <see cref="ApngReader"/> produces one, and
/// <see cref="ApngWriter"/> consumes one. Keeping the model separate from the container is what
/// would let a second container be added later without touching the animation logic.
/// </remarks>
public sealed class AnimationDocument
{
    private readonly List<AnimationFrame> _frames = [];

    /// <summary>Creates an empty animation with the given canvas size.</summary>
    public AnimationDocument(int width, int height, PixelFormat format = PixelFormat.Rgba32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        Format = format;
    }

    /// <summary>Canvas width in pixels.</summary>
    public int Width { get; }

    /// <summary>Canvas height in pixels.</summary>
    public int Height { get; }

    /// <summary>Channel width shared by every frame.</summary>
    public PixelFormat Format { get; }

    /// <summary>How many times the animation plays. Zero means forever.</summary>
    public int LoopCount { get; set; }

    /// <summary>
    /// A still image shown only by viewers that ignore the animation, and not itself part of the
    /// loop. Null when the first frame doubles as the still fallback, which is the usual case.
    /// </summary>
    /// <remarks>
    /// APNG allows the default image — the one in <c>IDAT</c> — to sit outside the animation.
    /// Setting this holds a poster that animation-aware viewers skip entirely.
    /// </remarks>
    public RgbaImage? PosterImage { get; set; }

    /// <summary>The frames, in playback order.</summary>
    public IReadOnlyList<AnimationFrame> Frames => _frames;

    /// <summary>The canvas rectangle.</summary>
    public PixelRect Bounds => new(0, 0, Width, Height);

    /// <summary>Appends a frame, checking that it fits the canvas and matches its pixel format.</summary>
    public void Add(AnimationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Image.Format != Format)
        {
            throw new ArgumentException(
                $"Frame is {frame.Image.Format} but the document is {Format}.", nameof(frame));
        }

        if (!Bounds.Contains(frame.Bounds))
        {
            throw new ArgumentException(
                $"Frame {frame.Bounds} does not fit inside the {Width}x{Height} canvas.", nameof(frame));
        }

        _frames.Add(frame);
    }

    /// <summary>Appends a full-canvas frame.</summary>
    public AnimationFrame Add(RgbaImage image, int delayMilliseconds)
    {
        var frame = new AnimationFrame(image, delayMilliseconds);
        Add(frame);
        return frame;
    }

    /// <summary>Removes every frame.</summary>
    public void Clear() => _frames.Clear();

    /// <summary>Total playback time of a single loop, in milliseconds.</summary>
    public long TotalDurationMilliseconds
    {
        get
        {
            long total = 0;
            foreach (AnimationFrame frame in _frames)
                total += frame.DelayMilliseconds;
            return total;
        }
    }

    /// <summary>
    /// Replays the animation and returns the fully composited canvas at each step, which is
    /// what a viewer actually shows.
    /// </summary>
    /// <remarks>
    /// Each yielded image is a fresh copy, so callers may keep or mutate them freely.
    /// </remarks>
    public IEnumerable<RgbaImage> RenderFrames()
    {
        var canvas = new RgbaImage(Width, Height, Format);
        RgbaImage? saved = null;

        foreach (AnimationFrame frame in _frames)
        {
            if (frame.DisposeOp == DisposeOp.Previous)
                saved = canvas.Clone();

            if (frame.BlendOp == BlendOp.Source)
                canvas.DrawSource(frame.Image, frame.X, frame.Y);
            else
                RgbaImageOps.BlendOver(canvas, frame.Image, frame.X, frame.Y);

            yield return canvas.Clone();

            switch (frame.DisposeOp)
            {
                case DisposeOp.Background:
                    canvas.Clear(frame.Bounds);
                    break;

                case DisposeOp.Previous when saved is not null:
                    canvas = saved;
                    saved = null;
                    break;
            }
        }
    }

    /// <summary>
    /// Builds a document from a sequence of same-sized full-canvas images sharing one delay.
    /// </summary>
    public static AnimationDocument FromImages(IEnumerable<RgbaImage> images, int delayMilliseconds, int loopCount = 0)
    {
        ArgumentNullException.ThrowIfNull(images);

        AnimationDocument? document = null;
        foreach (RgbaImage image in images)
        {
            document ??= new AnimationDocument(image.Width, image.Height, image.Format) { LoopCount = loopCount };
            document.Add(image, delayMilliseconds);
        }

        return document ?? throw new ArgumentException("At least one image is required.", nameof(images));
    }
}
