namespace SharpApng;

/// <summary>
/// What happens to the frame's region of the canvas once the frame's display time is over.
/// Values match the APNG <c>fcTL</c> encoding.
/// </summary>
public enum DisposeOp : byte
{
    /// <summary>Leave the canvas as it is. The next frame draws on top.</summary>
    None = 0,

    /// <summary>Clear the frame's region to transparent black before the next frame.</summary>
    Background = 1,

    /// <summary>Restore the frame's region to what it held before this frame was drawn.</summary>
    Previous = 2,
}

/// <summary>
/// How the frame's pixels combine with what is already on the canvas.
/// Values match the APNG <c>fcTL</c> encoding.
/// </summary>
public enum BlendOp : byte
{
    /// <summary>Overwrite the region, alpha included. Always exact, and the library's default.</summary>
    Source = 0,

    /// <summary>Alpha-composite over the existing contents.</summary>
    Over = 1,
}

/// <summary>
/// One frame of an animation: a sub-image, where to put it, how long to show it, and how it
/// combines with the canvas.
/// </summary>
public sealed class AnimationFrame
{
    /// <summary>Creates a frame positioned at the canvas origin.</summary>
    public AnimationFrame(RgbaImage image, int delayMilliseconds)
        : this(image, delayMilliseconds, 0, 0, DisposeOp.None, BlendOp.Source)
    {
    }

    /// <summary>Creates a fully specified frame.</summary>
    public AnimationFrame(
        RgbaImage image,
        int delayMilliseconds,
        int x,
        int y,
        DisposeOp disposeOp = DisposeOp.None,
        BlendOp blendOp = BlendOp.Source)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);

        Image = image;
        DelayMilliseconds = delayMilliseconds;
        X = x;
        Y = y;
        DisposeOp = disposeOp;
        BlendOp = blendOp;
    }

    /// <summary>The frame's pixels. May be smaller than the canvas.</summary>
    public RgbaImage Image { get; }

    /// <summary>How long the frame stays on screen, in milliseconds.</summary>
    /// <remarks>
    /// Zero means "as fast as possible"; viewers generally clamp it to their own floor rather
    /// than honouring it literally.
    /// </remarks>
    public int DelayMilliseconds { get; set; }

    /// <summary>Horizontal offset of the frame within the canvas.</summary>
    public int X { get; }

    /// <summary>Vertical offset of the frame within the canvas.</summary>
    public int Y { get; }

    /// <summary>Disposal applied after the frame's display time.</summary>
    public DisposeOp DisposeOp { get; set; }

    /// <summary>Blending applied when the frame is drawn.</summary>
    public BlendOp BlendOp { get; set; }

    /// <summary>The frame's placement within the canvas.</summary>
    public PixelRect Bounds => new(X, Y, Image.Width, Image.Height);

    /// <inheritdoc/>
    public override string ToString()
        => $"{Bounds} {DelayMilliseconds}ms {BlendOp}/{DisposeOp}";
}
