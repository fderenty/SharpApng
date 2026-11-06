using System.IO.Compression;
using ZopfliSharp;

namespace SharpApng.Tests;

/// <summary>
/// The <see cref="ZlibCompressor"/> extension point. ZopfliSharp is referenced by this test project
/// only, to exercise the case the extension point exists for; the library itself takes no dependency.
/// </summary>
[TestClass]
public sealed class CompressorTests
{
    [TestMethod]
    public void ApngWriter_PutsExactlyWhatTheCompressorReturnsInEveryIdatAndFdat()
    {
        var produced = new List<byte[]>();
        ZlibCompressor compressor = data =>
        {
            byte[] zlib = Zlib(data, CompressionLevel.Optimal);
            produced.Add(zlib);
            return zlib;
        };

        // A poster is an IDAT of its own, and the three frames then all go into fdAT.
        byte[] file = EncodeApng(
            [TestImages.Gradient(30, 20, seed: 1), TestImages.Gradient(30, 20, seed: 2), TestImages.Gradient(30, 20, seed: 3)],
            new ApngWriterOptions { Compressor = compressor },
            poster: TestImages.Solid(30, 20, new Rgba32(10, 20, 30)));

        List<byte[]> stored = ImageDataChunks(file);

        Assert.AreEqual(4, produced.Count, "one call for the poster and one per frame");
        Assert.AreEqual(produced.Count, stored.Count, "one IDAT or fdAT per call");
        for (int i = 0; i < stored.Count; i++)
            CollectionAssert.AreEqual(produced[i], stored[i], $"chunk {i} does not hold what the compressor returned");
    }

    [TestMethod]
    public void ApngWriter_WithoutAPoster_CallsTheCompressorForTheIdatAndEachFdat()
    {
        int calls = 0;
        ZlibCompressor compressor = data => { calls++; return Zlib(data, CompressionLevel.Optimal); };

        byte[] file = EncodeApng(
            [TestImages.Gradient(30, 20, seed: 1), TestImages.Gradient(30, 20, seed: 2), TestImages.Gradient(30, 20, seed: 3)],
            new ApngWriterOptions { Compressor = compressor });

        Assert.AreEqual(3, calls);
        Assert.AreEqual(3, ImageDataChunks(file).Count);
    }

    [TestMethod]
    public void PngCodec_EveryEncodeOverload_UsesTheCompressor()
    {
        RgbaImage image = TestImages.Gradient(25, 17, seed: 5);
        int calls = 0;
        ZlibCompressor compressor = data => { calls++; return Zlib(data, CompressionLevel.SmallestSize); };

        byte[] fromArray = PngCodec.EncodeToArray(image, compressor);

        using var stream = new MemoryStream();
        PngCodec.Encode(image, stream, compressor);

        string path = Path.Combine(Path.GetTempPath(), $"sharpapng-compressor-{Guid.NewGuid():N}.png");
        try
        {
            PngCodec.EncodeFile(image, path, compressor);
            CollectionAssert.AreEqual(fromArray, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.AreEqual(3, calls);
        CollectionAssert.AreEqual(fromArray, stream.ToArray());
        Assert.IsTrue(RgbaImageOps.ContentEquals(image, PngCodec.Decode(fromArray)));
    }

    [TestMethod]
    public void Compressor_ChangesNothingButWhoCompresses()
    {
        // The compressor receives exactly the scanlines the built-in path deflates, filter bytes
        // included, and every other chunk comes out identical. The compressed bytes themselves
        // are not compared: the runtime's zlib does not promise the same output for data handed
        // over row by row and in one piece, and at its default level it does differ.
        RgbaImage[] frames =
        [
            TestImages.Gradient(40, 30, seed: 1),
            TestImages.Gradient(40, 30, seed: 7),
            TestImages.Rectangle(40, 30, new PixelRect(3, 4, 10, 6), new Rgba32(9, 8, 7)),
        ];
        RgbaImage poster = TestImages.Solid(40, 30, new Rgba32(1, 2, 3));

        var received = new List<byte[]>();
        byte[] builtIn = EncodeApng(frames, new ApngWriterOptions(), poster);
        byte[] supplied = EncodeApng(
            frames,
            new ApngWriterOptions { Compressor = data => { received.Add(data.ToArray()); return Zlib(data, CompressionLevel.Optimal); } },
            poster);

        List<byte[]> deflatedByTheRuntime = ImageDataChunks(builtIn);
        Assert.AreEqual(deflatedByTheRuntime.Count, received.Count);
        for (int i = 0; i < received.Count; i++)
            CollectionAssert.AreEqual(Inflate(deflatedByTheRuntime[i]), received[i], $"image data {i}");

        CollectionAssert.AreEqual(OtherChunks(builtIn), OtherChunks(supplied));

        RgbaImage sixteen = TestImages.Gradient(20, 12, seed: 3, PixelFormat.Rgba64);
        byte[]? stillReceived = null;
        byte[] still = PngCodec.EncodeToArray(sixteen, data => { stillReceived = data.ToArray(); return Zlib(data, CompressionLevel.Optimal); });

        CollectionAssert.AreEqual(Inflate(ImageDataChunks(PngCodec.EncodeToArray(sixteen))[0]), stillReceived, "16-bit still image");
        CollectionAssert.AreEqual(OtherChunks(PngCodec.EncodeToArray(sixteen)), OtherChunks(still));
    }

    [TestMethod]
    public void Zopfli_RoundTripsExactly()
    {
        RgbaImage[] source =
        [
            TestImages.Gradient(48, 32, seed: 1),
            TestImages.Rectangle(48, 32, new PixelRect(5, 5, 20, 10), new Rgba32(200, 40, 40)),
            new RgbaImage(48, 32),
        ];

        byte[] file = EncodeApng(source, new ApngWriterOptions { Compressor = ZopfliZlib });
        RgbaImage[] rendered = [.. ApngReader.Read(file).RenderFrames()];

        Assert.AreEqual(source.Length, rendered.Length);
        for (int i = 0; i < source.Length; i++)
            Assert.IsTrue(RgbaImageOps.ContentEquals(source[i], rendered[i]), $"frame {i} differs after a Zopfli round trip");

        RgbaImage sixteen = TestImages.Gradient(20, 12, seed: 3, PixelFormat.Rgba64);
        Assert.IsTrue(RgbaImageOps.ContentEquals(sixteen, PngCodec.Decode(PngCodec.EncodeToArray(sixteen, ZopfliZlib))));
    }

    [TestMethod]
    public void Zopfli_IsSmallerThanTheRuntimesSmallestSize()
    {
        RgbaImage[] frames = [TestImages.Gradient(96, 64, seed: 1), TestImages.Gradient(96, 64, seed: 2)];

        int smallest = EncodeApng(frames, new ApngWriterOptions { CompressionLevel = CompressionLevel.SmallestSize }).Length;
        int zopfli = EncodeApng(frames, new ApngWriterOptions { Compressor = ZopfliZlib }).Length;

        Assert.IsTrue(zopfli < smallest, $"Zopfli gave {zopfli:N0} bytes, SmallestSize {smallest:N0}");
    }

    [TestMethod]
    public void Compressor_ReturningRawDeflate_IsRefused()
    {
        ZlibCompressor deflate = data =>
        {
            using var output = new MemoryStream();
            using (var stream = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                stream.Write(data);
            return output.ToArray();
        };

        var ex = Assert.ThrowsException<InvalidOperationException>(
            () => PngCodec.EncodeToArray(TestImages.Gradient(10, 10), deflate));
        StringAssert.Contains(ex.Message, "zlib stream");
    }

    [TestMethod]
    public void Compressor_ReturningGzip_IsRefused()
    {
        ZlibCompressor gzip = data => Zopfli.Compress(data, ZopfliFormat.Gzip);

        Assert.ThrowsException<InvalidOperationException>(
            () => EncodeApng([TestImages.Gradient(10, 10)], new ApngWriterOptions { Compressor = gzip }));
    }

    [TestMethod]
    public void Compressor_ReturningNothing_IsRefused()
    {
        Assert.ThrowsException<InvalidOperationException>(() => PngCodec.EncodeToArray(TestImages.Gradient(4, 4), _ => []));
        Assert.ThrowsException<InvalidOperationException>(() => PngCodec.EncodeToArray(TestImages.Gradient(4, 4), _ => null!));
    }

    [TestMethod]
    public void PngCodec_EncodeFile_LeavesNoFileWhenTheCompressorFails()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sharpapng-refused-{Guid.NewGuid():N}.png");

        Assert.ThrowsException<InvalidOperationException>(
            () => PngCodec.EncodeFile(TestImages.Gradient(8, 8), path, data => Zopfli.Compress(data, ZopfliFormat.Deflate)));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void PngCodec_RejectsANullCompressor()
    {
        Assert.ThrowsException<ArgumentNullException>(() => PngCodec.EncodeToArray(TestImages.Gradient(4, 4), (ZlibCompressor)null!));
    }

    private static readonly ZlibCompressor ZopfliZlib = data => Zopfli.Compress(data, ZopfliFormat.Zlib);

    private static byte[] Zlib(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var stream = new ZLibStream(output, level, leaveOpen: true))
            stream.Write(data);
        return output.ToArray();
    }

    private static byte[] EncodeApng(RgbaImage[] frames, ApngWriterOptions options, RgbaImage? poster = null)
    {
        using var output = new MemoryStream();
        using (var writer = new ApngWriter(output, frames[0].Width, frames[0].Height, frames[0].Format, options))
        {
            if (poster is not null)
                writer.SetPosterImage(poster);

            foreach (RgbaImage frame in frames)
                writer.AddFrame(frame, 100);

            writer.Complete();
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var input = new MemoryStream(zlib);
        using var stream = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Every chunk but the image data, as type and hex payload, plus each fdAT's sequence number.</summary>
    private static List<string> OtherChunks(byte[] file)
    {
        var reader = new PngChunkReader(file);
        reader.ReadSignature();

        var chunks = new List<string>();
        while (reader.HasMore)
        {
            PngChunk chunk = reader.Read();
            chunks.Add(chunk.Type switch
            {
                "IDAT" => "IDAT",
                "fdAT" => $"fdAT {Convert.ToHexString(chunk.Data, 0, 4)}",
                _ => $"{chunk.Type} {Convert.ToHexString(chunk.Data)}",
            });
        }

        return chunks;
    }

    /// <summary>The zlib data of every IDAT and fdAT, in file order, without fdAT's sequence number.</summary>
    private static List<byte[]> ImageDataChunks(byte[] file)
    {
        var reader = new PngChunkReader(file);
        reader.ReadSignature();

        var chunks = new List<byte[]>();
        while (reader.HasMore)
        {
            PngChunk chunk = reader.Read();
            if (chunk.Type == "IDAT")
                chunks.Add(chunk.Data);
            else if (chunk.Type == "fdAT")
                chunks.Add(chunk.Data[4..]);
        }

        return chunks;
    }
}
