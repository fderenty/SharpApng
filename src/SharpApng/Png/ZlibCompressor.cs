namespace SharpApng;

/// <summary>
/// Compresses an image's filtered scanlines into the zlib stream an <c>IDAT</c> or <c>fdAT</c>
/// chunk carries, in place of the runtime's own zlib.
/// </summary>
/// <param name="data">
/// Every scanline of one image, each prefixed with its filter type byte: the complete data the
/// stream must decompress to.
/// </param>
/// <returns>
/// A complete zlib stream (RFC 1950): the two-byte header, the deflate data and the Adler-32
/// checksum. Raw deflate or gzip output is refused, since a PNG holding it cannot be read.
/// </returns>
/// <remarks>
/// <para>
/// This is how a stronger compressor plugs in without the library taking a dependency on it.
/// With ZopfliSharp, for instance:
/// </para>
/// <code>
/// new ApngWriterOptions { Compressor = data => Zopfli.Compress(data, ZopfliFormat.Zlib) }
/// </code>
/// <para>
/// The compressor is called once per image, with the whole image at once, so it sees all the
/// redundancy there is to find. It may be called from any thread the caller writes on.
/// </para>
/// </remarks>
public delegate byte[] ZlibCompressor(ReadOnlySpan<byte> data);
