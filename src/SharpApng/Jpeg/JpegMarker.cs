namespace SharpApng;

/// <summary>
/// JPEG marker codes, the byte that follows <c>0xFF</c>.
/// </summary>
internal static class JpegMarker
{
    public const byte Soi = 0xD8;
    public const byte Eoi = 0xD9;
    public const byte Sos = 0xDA;
    public const byte Dqt = 0xDB;
    public const byte Dnl = 0xDC;
    public const byte Dri = 0xDD;
    public const byte Dht = 0xC4;
    public const byte Rst0 = 0xD0;
    public const byte Rst7 = 0xD7;
    public const byte App0 = 0xE0;
    public const byte App1 = 0xE1;
    public const byte App14 = 0xEE;
    public const byte AppLast = 0xEF;
    public const byte Com = 0xFE;

    // Start-of-frame markers. The code identifies the coding process, which is how unsupported
    // variants are recognised and refused rather than mis-decoded.
    public const byte Sof0 = 0xC0;  // baseline sequential, Huffman
    public const byte Sof1 = 0xC1;  // extended sequential, Huffman
    public const byte Sof2 = 0xC2;  // progressive, Huffman
    public const byte Sof3 = 0xC3;  // lossless, Huffman
    public const byte Sof5 = 0xC5;  // differential sequential, Huffman
    public const byte Sof6 = 0xC6;  // differential progressive, Huffman
    public const byte Sof7 = 0xC7;  // differential lossless, Huffman
    public const byte Sof9 = 0xC9;  // extended sequential, arithmetic
    public const byte Sof10 = 0xCA; // progressive, arithmetic
    public const byte Sof11 = 0xCB; // lossless, arithmetic
    public const byte Sof13 = 0xCD; // differential sequential, arithmetic
    public const byte Sof14 = 0xCE; // differential progressive, arithmetic
    public const byte Sof15 = 0xCF; // differential lossless, arithmetic

    /// <summary>A human-readable name for a marker, used in error messages.</summary>
    public static string Describe(byte marker) => marker switch
    {
        Sof0 => "SOF0 (baseline sequential)",
        Sof1 => "SOF1 (extended sequential)",
        Sof2 => "SOF2 (progressive)",
        Sof3 => "SOF3 (lossless)",
        Sof5 => "SOF5 (differential sequential)",
        Sof6 => "SOF6 (differential progressive)",
        Sof7 => "SOF7 (differential lossless)",
        Sof9 => "SOF9 (extended sequential, arithmetic coding)",
        Sof10 => "SOF10 (progressive, arithmetic coding)",
        Sof11 => "SOF11 (lossless, arithmetic coding)",
        Sof13 => "SOF13 (differential sequential, arithmetic coding)",
        Sof14 => "SOF14 (differential progressive, arithmetic coding)",
        Sof15 => "SOF15 (differential lossless, arithmetic coding)",
        _ => $"0xFF{marker:X2}",
    };
}

/// <summary>
/// Thrown when JPEG data is malformed, or uses a coding process this decoder does not implement.
/// </summary>
public sealed class JpegFormatException(string message) : Exception(message);
