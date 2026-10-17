namespace SharpApng;

/// <summary>
/// A canonical Huffman table as declared by a <c>DHT</c> marker.
/// </summary>
/// <remarks>
/// Decoding uses an eight-bit lookup for the common case and falls back to a per-length walk for
/// the longer codes, which is the arrangement libjpeg settled on: most symbols in a real image
/// are short, so one array index resolves them.
/// </remarks>
internal sealed class JpegHuffmanTable
{
    private const int LookupBits = 8;

    private readonly byte[] _lookupLength = new byte[1 << LookupBits];
    private readonly byte[] _lookupValue = new byte[1 << LookupBits];
    private readonly int[] _minCode = new int[17];
    private readonly int[] _maxCode = new int[18];
    private readonly int[] _valuePointer = new int[17];
    private readonly byte[] _values;

    /// <summary>
    /// Builds a table from the counts-per-length and the symbol list carried by <c>DHT</c>.
    /// </summary>
    /// <param name="countsPerLength">Sixteen entries: how many codes exist of each length 1 to 16.</param>
    /// <param name="values">The symbols, ordered by increasing code length.</param>
    public JpegHuffmanTable(ReadOnlySpan<byte> countsPerLength, byte[] values)
    {
        _values = values;

        // Canonical code assignment: codes of each length follow on from the previous length,
        // shifted left by one.
        Span<int> codes = stackalloc int[17];
        int code = 0;
        int index = 0;

        for (int length = 1; length <= 16; length++)
        {
            codes[length] = code;
            _valuePointer[length] = index;
            _minCode[length] = code;

            int count = countsPerLength[length - 1];
            index += count;
            code += count;
            _maxCode[length] = count > 0 ? code - 1 : -1;
            code <<= 1;
        }

        _maxCode[17] = int.MaxValue;

        // Flatten the short codes into a direct lookup keyed by the next eight bits.
        Array.Fill(_lookupLength, (byte)0);
        int symbol = 0;
        code = 0;

        for (int length = 1; length <= LookupBits; length++)
        {
            for (int i = 0; i < countsPerLength[length - 1]; i++)
            {
                // Every bit pattern starting with this code maps to the same symbol.
                int shifted = code << (LookupBits - length);
                int span = 1 << (LookupBits - length);

                for (int k = 0; k < span; k++)
                {
                    _lookupLength[shifted + k] = (byte)length;
                    _lookupValue[shifted + k] = values[symbol];
                }

                symbol++;
                code++;
            }

            code <<= 1;
        }
    }

    /// <summary>Resolves an eight-bit peek, or returns 0 when the code is longer than that.</summary>
    public int TryLookup(int peek, out byte value)
    {
        value = _lookupValue[peek];
        return _lookupLength[peek];
    }

    /// <summary>Returns the symbol for a code of the given length, or -1 if it does not match.</summary>
    public int Resolve(int length, int code)
    {
        if (length > 16 || _maxCode[length] < 0 || code > _maxCode[length])
            return -1;

        int offset = _valuePointer[length] + code - _minCode[length];
        return offset >= 0 && offset < _values.Length ? _values[offset] : -1;
    }
}

/// <summary>
/// Reads the entropy-coded segment of a scan: bit unpacking, byte unstuffing and Huffman
/// symbol decoding.
/// </summary>
/// <remarks>
/// Inside entropy-coded data, <c>FF 00</c> encodes a literal <c>FF</c> byte, and <c>FF</c>
/// followed by anything else is a marker that ends the segment. That unstuffing belongs here, in
/// the bit reader, rather than in a pre-pass over the buffer: a pre-pass cannot tell where the
/// segment ends, which is exactly the mistake of scanning for byte sequences that this library
/// avoids everywhere else.
/// </remarks>
internal ref struct JpegBitReader(ReadOnlySpan<byte> data, int position)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = position;
    private int _bitBuffer = 0;
    private int _bitCount = 0;
    private bool _atMarker = false;
    private bool _corrupt = false;

    /// <summary>Where the reader has consumed up to, byte-aligned.</summary>
    public readonly int Position => _position;

    /// <summary>True once a marker has been reached and no more entropy data is available.</summary>
    public readonly bool AtMarker => _atMarker;

    /// <summary>
    /// True when the entropy data did not decode cleanly, so the rest of the scan was abandoned.
    /// </summary>
    public readonly bool HitCorruptData => _corrupt;

    /// <summary>
    /// Steps over a restart marker if one is present, discarding the partial byte that precedes
    /// it.
    /// </summary>
    /// <returns>True when a restart marker was consumed.</returns>
    /// <remarks>
    /// Nothing is modified when no marker is found. Some encoders declare a restart interval in
    /// <c>DRI</c> and then never write the markers; the entropy stream simply runs on, so the bit
    /// position has to survive the failed probe intact. Clearing the bit buffer first would
    /// destroy the alignment and corrupt everything after the first interval.
    /// </remarks>
    public bool TryConsumeRestartMarker()
    {
        ReadOnlySpan<byte> span = _data;

        // The marker does not necessarily begin exactly at _position. Bytes already pulled into
        // the bit buffer sit behind it, while the padding bits that fill the last byte of an
        // interval, plus any stuffed 0xFF00 pair, can leave it a little short. So probe a small
        // window on both sides.
        //
        // Searching is safe rather than a guess: inside entropy-coded data every 0xFF is
        // followed by 0x00, so an 0xFF followed by a restart code can only be a real marker.
        int start = Math.Max(0, _position - 4);
        int end = Math.Min(span.Length - 2, _position + 8);

        for (int probe = start; probe <= end; probe++)
        {
            if (span[probe] != 0xFF)
                continue;

            byte next = span[probe + 1];
            if (next < JpegMarker.Rst0 || next > JpegMarker.Rst7)
                continue;

            _position = probe + 2;
            _bitBuffer = 0;
            _bitCount = 0;
            _atMarker = false;
            return true;
        }

        return false;
    }

    /// <summary>Reads one bit, returning 0 once the segment has run out.</summary>
    public int ReadBit()
    {
        if (_bitCount == 0 && !Fill())
            return 0;

        _bitCount--;
        return (_bitBuffer >> _bitCount) & 1;
    }

    /// <summary>Reads <paramref name="count"/> bits, most significant first.</summary>
    public int ReadBits(int count)
    {
        int result = 0;
        for (int i = 0; i < count; i++)
            result = (result << 1) | ReadBit();

        return result;
    }

    /// <summary>Decodes one Huffman symbol.</summary>
    public int DecodeSymbol(JpegHuffmanTable table)
    {
        // The lookup needs eight bits; near the end of a segment there may be fewer, so fall
        // through to the slow path rather than reading past the marker.
        if (_bitCount >= 8 || FillTo(8))
        {
            int peek = (_bitBuffer >> (_bitCount - 8)) & 0xFF;
            int length = table.TryLookup(peek, out byte value);
            if (length > 0)
            {
                _bitCount -= length;
                return value;
            }
        }

        int code = 0;
        for (int bits = 1; bits <= 16; bits++)
        {
            code = (code << 1) | ReadBit();
            int symbol = table.Resolve(bits, code);
            if (symbol >= 0)
                return symbol;
        }

        throw new JpegFormatException("Entropy-coded data contains a Huffman code longer than 16 bits.");
    }

    /// <summary>
    /// Applies the sign extension of the JPEG specification, which stores a magnitude and
    /// derives the sign from the leading bit.
    /// </summary>
    public static int Extend(int value, int magnitude)
        => magnitude == 0 ? 0 : value < (1 << (magnitude - 1)) ? value - (1 << magnitude) + 1 : value;

    /// <summary>Reads a magnitude-coded value of the given bit length and sign-extends it.</summary>
    public int ReceiveAndExtend(int magnitude)
        => magnitude == 0 ? 0 : Extend(ReadBits(magnitude), magnitude);

    private bool FillTo(int bits)
    {
        while (_bitCount < bits)
        {
            if (!Fill())
                return false;
        }

        return true;
    }

    private bool Fill()
    {
        if (_atMarker)
            return false;

        ReadOnlySpan<byte> span = _data;
        if (_position >= span.Length)
        {
            _atMarker = true;
            return false;
        }

        byte b = span[_position];

        if (b == 0xFF)
        {
            // Skip any run of fill bytes to reach the real second byte.
            int next = _position + 1;
            while (next < span.Length && span[next] == 0xFF)
                next++;

            if (next >= span.Length)
            {
                _atMarker = true;
                return false;
            }

            if (span[next] == 0x00)
            {
                // Stuffed: this is a literal FF in the entropy stream.
                _position = next + 1;
            }
            else
            {
                // A genuine marker ends the segment. Leave the position on the FF so the caller
                // can identify it.
                _position = next - 1;
                _atMarker = true;
                return false;
            }
        }
        else
        {
            _position++;
        }

        // Callers never let the count exceed 15 before consuming, so a 32-bit buffer has room.
        _bitBuffer = (_bitBuffer << 8) | b;
        _bitCount += 8;
        return true;
    }
}
