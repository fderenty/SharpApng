namespace SharpApng;

/// <summary>
/// The LZW variant GIF uses for its pixel data.
/// </summary>
/// <remarks>
/// <para>
/// Codes are variable width and packed least-significant-bit first. The dictionary is held as
/// parallel prefix and suffix arrays rather than as strings: an entry is a single byte plus a
/// pointer to the rest, so decoding walks the chain backwards into a scratch buffer and never
/// allocates per code.
/// </para>
/// <para>
/// The Unisys patent that once covered this expired in 2003 in the United States and by July
/// 2004 everywhere else, so LZW has been unencumbered for two decades.
/// </para>
/// </remarks>
internal static class GifLzwDecoder
{
    private const int MaxCodes = 4096;
    private const int MaxCodeSize = 12;

    /// <summary>
    /// Decodes LZW data into palette indices.
    /// </summary>
    /// <param name="compressed">The concatenated data sub-blocks, without their length bytes.</param>
    /// <param name="minimumCodeSize">The LZW minimum code size byte from the image descriptor.</param>
    /// <param name="output">Receives one palette index per pixel; must be exactly the frame's pixel count.</param>
    public static void Decode(ReadOnlySpan<byte> compressed, int minimumCodeSize, Span<byte> output)
    {
        if (minimumCodeSize is < 2 or > 8)
            throw new GifFormatException($"Invalid LZW minimum code size {minimumCodeSize}; it must be between 2 and 8.");

        int clearCode = 1 << minimumCodeSize;
        int endCode = clearCode + 1;

        var prefix = new short[MaxCodes];
        var suffix = new byte[MaxCodes];
        var scratch = new byte[MaxCodes];

        for (int i = 0; i < clearCode; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
        }

        int codeSize = minimumCodeSize + 1;
        int nextCode = clearCode + 2;
        int previous = -1;
        int written = 0;

        int bitBuffer = 0;
        int bitCount = 0;
        int position = 0;

        while (true)
        {
            // Refill from the byte stream. Sub-block boundaries are not code boundaries, which
            // is why the caller concatenates them first.
            while (bitCount < codeSize)
            {
                if (position >= compressed.Length)
                {
                    // Truncated streams are common in the wild; stop cleanly and leave the rest
                    // of the frame at index zero rather than throwing away a mostly good image.
                    return;
                }

                bitBuffer |= compressed[position++] << bitCount;
                bitCount += 8;
            }

            int code = bitBuffer & ((1 << codeSize) - 1);
            bitBuffer >>= codeSize;
            bitCount -= codeSize;

            if (code == endCode)
                return;

            if (code == clearCode)
            {
                codeSize = minimumCodeSize + 1;
                nextCode = clearCode + 2;
                previous = -1;
                continue;
            }

            if (previous == -1)
            {
                if (code >= clearCode)
                    throw new GifFormatException("The first LZW code after a reset is not a root code.");

                if (written < output.Length)
                    output[written++] = suffix[code];

                previous = code;
                continue;
            }

            int current = code;
            int scratchLength = 0;

            // The KwKwK case: a code may refer to the entry currently being built.
            if (code >= nextCode)
            {
                if (code > nextCode)
                    throw new GifFormatException($"LZW code {code} refers past the end of the dictionary.");

                scratch[scratchLength++] = suffix[FirstByteOf(previous, prefix)];
                current = previous;
            }

            while (current >= 0)
            {
                if (scratchLength >= MaxCodes)
                    throw new GifFormatException("Corrupt LZW dictionary: a code chain does not terminate.");

                scratch[scratchLength++] = suffix[current];
                current = prefix[current];
            }

            // The chain was walked backwards, so emit it in reverse.
            for (int i = scratchLength - 1; i >= 0 && written < output.Length; i--)
                output[written++] = scratch[i];

            if (nextCode < MaxCodes)
            {
                prefix[nextCode] = (short)previous;
                suffix[nextCode] = scratch[scratchLength - 1];
                nextCode++;

                if (nextCode == (1 << codeSize) && codeSize < MaxCodeSize)
                    codeSize++;
            }

            previous = code;

            if (written >= output.Length)
                return;
        }
    }

    private static int FirstByteOf(int code, short[] prefix)
    {
        while (prefix[code] >= 0)
            code = prefix[code];

        return code;
    }
}

/// <summary>
/// Thrown when GIF data is malformed or uses a feature this decoder does not handle.
/// </summary>
public sealed class GifFormatException(string message) : Exception(message);
