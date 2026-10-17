namespace SharpApng;

/// <summary>
/// How pixels are stored relative to how they should be displayed, as recorded in EXIF tag
/// 0x0112. Values match the TIFF specification.
/// </summary>
/// <remarks>
/// Phone cameras almost always store a photograph in the sensor's own orientation and record
/// the rotation here rather than rotating the pixels. Ignoring this tag displays portrait
/// photographs lying on their side. See <see cref="RgbaImageOps.ApplyOrientation"/>.
/// </remarks>
public enum ExifOrientation
{
    /// <summary>Already upright; no transformation needed.</summary>
    Normal = 1,

    /// <summary>Mirrored left to right.</summary>
    FlipHorizontal = 2,

    /// <summary>Rotated by 180 degrees.</summary>
    Rotate180 = 3,

    /// <summary>Mirrored top to bottom.</summary>
    FlipVertical = 4,

    /// <summary>Mirrored across the main diagonal. Swaps width and height.</summary>
    Transpose = 5,

    /// <summary>Rotated 90 degrees clockwise for display. Swaps width and height.</summary>
    Rotate90 = 6,

    /// <summary>Mirrored across the anti-diagonal. Swaps width and height.</summary>
    Transverse = 7,

    /// <summary>Rotated 270 degrees clockwise for display. Swaps width and height.</summary>
    Rotate270 = 8,
}
