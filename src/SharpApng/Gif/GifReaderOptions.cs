namespace SharpApng;

/// <summary>
/// Knobs for <see cref="GifReader"/>.
/// </summary>
public sealed class GifReaderOptions
{
    /// <summary>
    /// Raise frame delays of 0 or 1 hundredths of a second to 100 ms, which is what browsers do.
    /// On by default.
    /// </summary>
    /// <remarks>
    /// Countless GIFs in the wild declare a delay of 0 or 1. Every browser has clamped those to
    /// 100 ms for compatibility since the 1990s, so the delay a file states and the speed anyone
    /// actually sees are different numbers. Converting literally produces an animation that runs
    /// several times faster than the original appeared to. Turn this off to copy the stated
    /// delays byte for byte instead.
    /// </remarks>
    public bool ClampShortDelays { get; set; } = true;

    /// <summary>
    /// A colour to turn transparent on the way in, on top of any transparency the GIF already
    /// declares. Null to keep the file's own transparency untouched.
    /// </summary>
    /// <remarks>
    /// Useful for the common case of a GIF drawn on a flat background — white, magenta, a chroma
    /// key — that was never marked transparent because the format's single transparent index was
    /// spent elsewhere or simply unused. See <see cref="GifReader"/> for what this does to frame
    /// composition.
    /// </remarks>
    public Rgba32? TransparentColor { get; set; }

    /// <summary>
    /// Use the background colour the GIF itself declares as the colour to turn transparent.
    /// Ignored when <see cref="TransparentColor"/> is set.
    /// </summary>
    public bool TreatBackgroundAsTransparent { get; set; }

    /// <summary>
    /// How far a channel may differ from <see cref="TransparentColor"/> and still be erased.
    /// Zero, an exact match, by default.
    /// </summary>
    /// <remarks>
    /// GIF pixels come from a palette, so colours are exact and zero is normally right. Raise it
    /// for files whose background was anti-aliased or dithered before being quantised.
    /// </remarks>
    public int ColorTolerance { get; set; }

    /// <summary>
    /// Composite every frame against the canvas and return full-size frames, rather than the
    /// cropped sub-images the file stores.
    /// </summary>
    /// <remarks>
    /// Forced on when a colour is being made transparent, since that changes what the frames
    /// mean. Leaving it off preserves the file's own frame layout, which round-trips more
    /// faithfully and converts faster.
    /// </remarks>
    public bool FlattenFrames { get; set; }
}
