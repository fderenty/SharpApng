namespace SharpApng;

/// <summary>
/// CRC-32 (IEEE 802.3), the variant PNG uses to checksum every chunk.
/// </summary>
internal static class Crc32
{
    private const uint Polynomial = 0xEDB88320u;
    private const uint Seed = 0xFFFFFFFFu;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>Computes the CRC of a single buffer.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Seed, data));

    /// <summary>Computes the CRC of two buffers as if they were concatenated.</summary>
    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        => Finish(Update(Update(Seed, first), second));

    /// <summary>Starts a running CRC. Feed it to <see cref="Update"/>, close it with <see cref="Finish"/>.</summary>
    public static uint Begin() => Seed;

    /// <summary>Folds more bytes into a running CRC.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = Table;
        foreach (byte b in data)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Closes a running CRC into its final value.</summary>
    public static uint Finish(uint crc) => crc ^ Seed;
}
