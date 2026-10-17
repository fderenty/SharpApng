using System.Buffers.Binary;

namespace SharpApng;

/// <summary>
/// Reads the orientation tag out of a JPEG <c>APP1</c> segment.
/// </summary>
/// <remarks>
/// Deliberately not a general EXIF parser, and it should not become one. It walks IFD0 looking
/// for tag 0x0112 and stops, which keeps a format notorious for malformed files from becoming a
/// source of decoding failures.
/// </remarks>
internal static class JpegExif
{
    private const ushort OrientationTag = 0x0112;
    private const ushort TypeShort = 3;

    /// <summary>
    /// Returns the orientation declared in an <c>APP1</c> segment, or null when the segment is
    /// not EXIF, carries no orientation, or is malformed.
    /// </summary>
    public static ExifOrientation? ReadOrientation(ReadOnlySpan<byte> segment)
    {
        // "Exif\0\0" then a TIFF header.
        if (segment.Length < 14 || !segment[..6].SequenceEqual("Exif\0\0"u8))
            return null;

        ReadOnlySpan<byte> tiff = segment[6..];

        bool bigEndian;
        if (tiff[0] == 'M' && tiff[1] == 'M')
            bigEndian = true;
        else if (tiff[0] == 'I' && tiff[1] == 'I')
            bigEndian = false;
        else
            return null;

        if (ReadUInt16(tiff[2..], bigEndian) != 42)
            return null;

        uint ifdOffset = ReadUInt32(tiff[4..], bigEndian);
        if (ifdOffset > int.MaxValue || ifdOffset + 2 > (uint)tiff.Length)
            return null;

        int offset = (int)ifdOffset;
        int entryCount = ReadUInt16(tiff[offset..], bigEndian);
        offset += 2;

        for (int i = 0; i < entryCount; i++)
        {
            int entry = offset + (i * 12);
            if (entry + 12 > tiff.Length)
                return null;

            ushort tag = ReadUInt16(tiff[entry..], bigEndian);
            if (tag != OrientationTag)
                continue;

            ushort type = ReadUInt16(tiff[(entry + 2)..], bigEndian);
            if (type != TypeShort)
                return null;

            // A SHORT fits in the value field, which is padded to four bytes; the value sits in
            // the first two, in the file's own byte order.
            ushort value = ReadUInt16(tiff[(entry + 8)..], bigEndian);

            return value is >= 1 and <= 8 ? (ExifOrientation)value : null;
        }

        return null;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data) : BinaryPrimitives.ReadUInt16LittleEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
}
