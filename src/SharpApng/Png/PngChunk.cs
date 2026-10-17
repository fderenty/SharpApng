using System.Buffers.Binary;
using System.Text;

namespace SharpApng;

/// <summary>
/// A single PNG chunk, held in memory with its payload already separated from its framing.
/// </summary>
internal readonly struct PngChunk(string type, byte[] data)
{
    /// <summary>The four-character chunk type, for example <c>IHDR</c> or <c>fcTL</c>.</summary>
    public string Type { get; } = type;

    /// <summary>The chunk payload, without length, type or CRC.</summary>
    public byte[] Data { get; } = data;

    /// <inheritdoc/>
    public override string ToString() => $"{Type} ({Data.Length} bytes)";
}

/// <summary>
/// Walks a PNG byte stream chunk by chunk, following the length/type/data/CRC framing.
/// </summary>
/// <remarks>
/// Reading the structure rather than searching for type names is the whole point: the four
/// bytes spelling <c>IDAT</c> occur by chance inside compressed pixel data, so any scan-based
/// parser eventually latches onto garbage.
/// </remarks>
internal sealed class PngChunkReader(ReadOnlyMemory<byte> data)
{
    private readonly ReadOnlyMemory<byte> _data = data;
    private int _position;

    /// <summary>The PNG file signature, which must open every stream.</summary>
    public static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Consumes and validates the eight-byte signature.</summary>
    /// <exception cref="PngFormatException">The signature is missing or malformed.</exception>
    public void ReadSignature()
    {
        if (_data.Length < Signature.Length || !_data.Span[..Signature.Length].SequenceEqual(Signature))
            throw new PngFormatException("Not a PNG stream: the file signature is missing.");

        _position = Signature.Length;
    }

    /// <summary>True while chunks remain.</summary>
    public bool HasMore => _position < _data.Length;

    /// <summary>
    /// Reads the next chunk and verifies its CRC.
    /// </summary>
    /// <exception cref="PngFormatException">The stream is truncated or a CRC does not match.</exception>
    public PngChunk Read()
    {
        ReadOnlySpan<byte> span = _data.Span;

        if (_position + 8 > span.Length)
            throw new PngFormatException("Truncated PNG: a chunk header runs past the end of the stream.");

        uint length = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(_position, 4));
        if (length > int.MaxValue - 12)
            throw new PngFormatException($"Chunk declares an impossible length of {length} bytes.");

        int dataStart = _position + 8;
        int crcStart = dataStart + (int)length;
        if (crcStart + 4 > span.Length)
            throw new PngFormatException("Truncated PNG: a chunk payload runs past the end of the stream.");

        string type = Encoding.ASCII.GetString(span.Slice(_position + 4, 4));
        ReadOnlySpan<byte> payload = span.Slice(dataStart, (int)length);

        uint expected = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(crcStart, 4));
        uint actual = Crc32.Compute(span.Slice(_position + 4, 4), payload);
        if (expected != actual)
            throw new PngFormatException($"CRC mismatch in chunk '{type}': expected {expected:X8}, computed {actual:X8}.");

        _position = crcStart + 4;
        return new PngChunk(type, payload.ToArray());
    }
}

/// <summary>
/// Emits PNG chunks with correct length framing and CRC.
/// </summary>
internal sealed class PngChunkWriter(Stream stream)
{
    private readonly Stream _stream = stream;
    private readonly byte[] _scratch = new byte[8];

    /// <summary>Writes the eight-byte PNG signature.</summary>
    public void WriteSignature() => _stream.Write(PngChunkReader.Signature);

    /// <summary>Writes a complete chunk.</summary>
    public void Write(string type, ReadOnlySpan<byte> data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);

        BinaryPrimitives.WriteUInt32BigEndian(_scratch, (uint)data.Length);
        _stream.Write(_scratch.AsSpan(0, 4));
        _stream.Write(typeBytes);
        _stream.Write(data);

        BinaryPrimitives.WriteUInt32BigEndian(_scratch, Crc32.Compute(typeBytes, data));
        _stream.Write(_scratch.AsSpan(0, 4));
    }

    /// <summary>
    /// Writes a chunk whose payload is a prefix followed by a body, without joining them into
    /// one buffer first. Used for <c>fdAT</c>, whose payload is a sequence number plus a large
    /// block of compressed data.
    /// </summary>
    public void Write(string type, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> body)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);

        BinaryPrimitives.WriteUInt32BigEndian(_scratch, (uint)(prefix.Length + body.Length));
        _stream.Write(_scratch.AsSpan(0, 4));
        _stream.Write(typeBytes);
        _stream.Write(prefix);
        _stream.Write(body);

        uint crc = Crc32.Begin();
        crc = Crc32.Update(crc, typeBytes);
        crc = Crc32.Update(crc, prefix);
        crc = Crc32.Update(crc, body);

        BinaryPrimitives.WriteUInt32BigEndian(_scratch, Crc32.Finish(crc));
        _stream.Write(_scratch.AsSpan(0, 4));
    }
}

/// <summary>
/// Thrown when a PNG or APNG stream does not conform to the specification.
/// </summary>
public sealed class PngFormatException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public PngFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public PngFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
