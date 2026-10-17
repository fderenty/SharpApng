using System.IO.Compression;

namespace SharpApng;

/// <summary>
/// Knobs for <see cref="ApngWriter"/>.
/// </summary>
public sealed class ApngWriterOptions
{
    /// <summary>How hard zlib works on each frame. Defaults to <see cref="CompressionLevel.Optimal"/>.</summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

    /// <summary>
    /// How many times the animation plays; zero means forever. Defaults to zero.
    /// </summary>
    public int LoopCount { get; set; }

    /// <summary>
    /// Shrink each frame to the rectangle that actually changed, instead of storing the whole
    /// canvas every time. On by default.
    /// </summary>
    /// <remarks>
    /// The cropped frame is written with <see cref="BlendOp.Source"/>, which overwrites the
    /// region outright. That stays exact even when a pixel becomes more transparent than the one
    /// beneath it — the case <see cref="BlendOp.Over"/> cannot express at all.
    /// </remarks>
    public bool OptimizeFrames { get; set; } = true;

    /// <summary>
    /// Fold a frame identical to the one before it into its predecessor's delay rather than
    /// storing it again. On by default, and ignored when <see cref="OptimizeFrames"/> is off.
    /// </summary>
    public bool MergeDuplicateFrames { get; set; } = true;
}
