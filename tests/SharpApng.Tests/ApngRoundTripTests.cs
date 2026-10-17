namespace SharpApng.Tests;

[TestClass]
public sealed class ApngRoundTripTests
{
    [TestMethod]
    public void RoundTrip_MultipleFrames_ReproducesEveryFrameExactly()
    {
        RgbaImage[] source =
        [
            TestImages.Gradient(40, 30, seed: 1),
            TestImages.Gradient(40, 30, seed: 2),
            TestImages.Gradient(40, 30, seed: 3),
        ];

        byte[] encoded = Encode(source, delay: 120);
        AnimationDocument document = ApngReader.Read(encoded);
        RgbaImage[] rendered = [.. document.RenderFrames()];

        Assert.AreEqual(source.Length, rendered.Length);
        for (int i = 0; i < source.Length; i++)
            Assert.IsTrue(RgbaImageOps.ContentEquals(source[i], rendered[i]), $"frame {i} differs after a round trip");
    }

    [TestMethod]
    public void RoundTrip_WhenAlphaDecreases_StillReproducesFrames()
    {
        // The case the source libraries could not express. Blanking unchanged pixels and relying
        // on BlendOp.Over can never lower the alpha already on the canvas, so a frame that erases
        // an opaque shape came out wrong. Cropping plus BlendOp.Source handles it exactly.
        var canvas = new PixelRect(4, 4, 8, 8);
        RgbaImage[] source =
        [
            TestImages.Rectangle(16, 16, canvas, new Rgba32(255, 0, 0)),
            new RgbaImage(16, 16),                                        // everything erased
            TestImages.Rectangle(16, 16, canvas, new Rgba32(0, 0, 255)),
        ];

        byte[] encoded = Encode(source, delay: 50);
        RgbaImage[] rendered = [.. ApngReader.Read(encoded).RenderFrames()];

        Assert.AreEqual(3, rendered.Length);
        Assert.IsTrue(RgbaImageOps.ContentEquals(source[1], rendered[1]), "the erasing frame did not survive the round trip");
        Assert.AreEqual(0, rendered[1].GetPixel(8, 8).A, "the erased pixel should be transparent");
        Assert.IsTrue(RgbaImageOps.ContentEquals(source[2], rendered[2]));
    }

    [TestMethod]
    public void RoundTrip_SixteenBitFrames_AreLossless()
    {
        RgbaImage[] source =
        [
            TestImages.Gradient(24, 18, seed: 1, PixelFormat.Rgba64),
            TestImages.Gradient(24, 18, seed: 4, PixelFormat.Rgba64),
        ];

        AnimationDocument document = ApngReader.Read(Encode(source, delay: 100, PixelFormat.Rgba64));
        RgbaImage[] rendered = [.. document.RenderFrames()];

        Assert.AreEqual(PixelFormat.Rgba64, document.Format);
        Assert.IsTrue(RgbaImageOps.ContentEquals(source[0], rendered[0]));
        Assert.IsTrue(RgbaImageOps.ContentEquals(source[1], rendered[1]));
    }

    [TestMethod]
    public void Optimize_StoresOnlyTheChangedRectangle()
    {
        RgbaImage first = TestImages.Solid(64, 64, new Rgba32(0, 0, 0));
        RgbaImage second = first.Clone();
        second.SetPixel(20, 30, new Rgba32(255, 255, 255));

        AnimationDocument document = ApngReader.Read(Encode([first, second], delay: 100));

        AnimationFrame changed = document.Frames[1];
        Assert.AreEqual(new PixelRect(20, 30, 1, 1), changed.Bounds, "only the single changed pixel should be stored");
        Assert.AreEqual(BlendOp.Source, changed.BlendOp);
    }

    [TestMethod]
    public void Optimize_Disabled_StoresFullFrames()
    {
        RgbaImage first = TestImages.Solid(32, 32, new Rgba32(0, 0, 0));
        RgbaImage second = first.Clone();
        second.SetPixel(1, 1, new Rgba32(255, 255, 255));

        byte[] encoded = Encode([first, second], delay: 100, PixelFormat.Rgba32,
            new ApngWriterOptions { OptimizeFrames = false });

        AnimationDocument document = ApngReader.Read(encoded);

        Assert.AreEqual(new PixelRect(0, 0, 32, 32), document.Frames[1].Bounds);
    }

    [TestMethod]
    public void DuplicateFrames_AreMergedIntoOneLongerFrame()
    {
        RgbaImage image = TestImages.Gradient(16, 16);

        AnimationDocument document = ApngReader.Read(Encode([image, image.Clone(), image.Clone()], delay: 100));

        Assert.AreEqual(1, document.Frames.Count, "three identical frames should collapse into one");
        Assert.AreEqual(300, document.Frames[0].DelayMilliseconds, "the delays should have been added together");
    }

    [TestMethod]
    public void LoopCount_SurvivesTheRoundTrip()
    {
        byte[] encoded = Encode([TestImages.Gradient(8, 8, 1), TestImages.Gradient(8, 8, 2)],
            delay: 100, PixelFormat.Rgba32, new ApngWriterOptions { LoopCount = 7 });

        Assert.AreEqual(7, ApngReader.Read(encoded).LoopCount);
    }

    [TestMethod]
    public void PosterImage_StaysOutOfTheAnimation()
    {
        RgbaImage poster = TestImages.Solid(16, 16, new Rgba32(255, 255, 0));
        RgbaImage frameOne = TestImages.Gradient(16, 16, seed: 5);
        RgbaImage frameTwo = TestImages.Gradient(16, 16, seed: 6);

        using var stream = new MemoryStream();
        using (var writer = new ApngWriter(stream, 16, 16))
        {
            writer.SetPosterImage(poster);
            writer.AddFrame(frameOne, 100);
            writer.AddFrame(frameTwo, 100);
            writer.Complete();
        }

        byte[] encoded = stream.ToArray();
        AnimationDocument document = ApngReader.Read(encoded);

        Assert.IsNotNull(document.PosterImage, "the poster should come back as a separate image");
        Assert.AreEqual(2, document.Frames.Count, "the poster must not count as an animation frame");
        Assert.IsTrue(RgbaImageOps.ContentEquals(poster, document.PosterImage));

        // Viewers without APNG support fall back to the default image, so it must still decode.
        Assert.IsTrue(RgbaImageOps.ContentEquals(poster, PngCodec.Decode(encoded)));

        RgbaImage[] rendered = [.. document.RenderFrames()];
        Assert.IsTrue(RgbaImageOps.ContentEquals(frameOne, rendered[0]), "the animation should start from the first frame, not the poster");
        Assert.IsTrue(RgbaImageOps.ContentEquals(frameTwo, rendered[1]));
    }

    [TestMethod]
    public void PosterImage_SurvivesADocumentRoundTrip()
    {
        var document = new AnimationDocument(12, 12)
        {
            PosterImage = TestImages.Solid(12, 12, new Rgba32(0, 128, 255)),
        };
        document.Add(TestImages.Gradient(12, 12, 1), 60);
        document.Add(TestImages.Gradient(12, 12, 2), 60);

        using var stream = new MemoryStream();
        ApngWriter.Write(document, stream);

        AnimationDocument reloaded = ApngReader.Read(stream.ToArray());

        Assert.IsNotNull(reloaded.PosterImage);
        Assert.IsTrue(RgbaImageOps.ContentEquals(document.PosterImage, reloaded.PosterImage));
        Assert.AreEqual(2, reloaded.Frames.Count);
    }

    [TestMethod]
    public void StillPng_ReadsAsASingleFrameAnimation()
    {
        RgbaImage image = TestImages.Gradient(12, 9);

        AnimationDocument document = ApngReader.Read(PngCodec.EncodeToArray(image));

        Assert.AreEqual(1, document.Frames.Count);
        Assert.IsTrue(RgbaImageOps.ContentEquals(image, document.Frames[0].Image));
    }

    [TestMethod]
    public void IsAnimated_DistinguishesApngFromPng()
    {
        byte[] still = PngCodec.EncodeToArray(TestImages.Gradient(8, 8));
        byte[] animated = Encode([TestImages.Gradient(8, 8, 1), TestImages.Gradient(8, 8, 2)], delay: 100);

        Assert.IsFalse(ApngReader.IsAnimated(still));
        Assert.IsTrue(ApngReader.IsAnimated(animated));
    }

    [TestMethod]
    public void Writer_WorksOnAStreamThatCannotSeek()
    {
        using var inner = new MemoryStream();
        using (var forwardOnly = new ForwardOnlyStream(inner))
        using (var writer = new ApngWriter(forwardOnly, 16, 16))
        {
            writer.AddFrame(TestImages.Gradient(16, 16, 1), 100);
            writer.AddFrame(TestImages.Gradient(16, 16, 2), 100);
            writer.Complete();
        }

        AnimationDocument document = ApngReader.Read(inner.ToArray());
        Assert.AreEqual(2, document.Frames.Count, "the frame count must still be patched when spooling");
    }

    [TestMethod]
    public void Writer_RejectsAFrameOfTheWrongSize()
    {
        using var stream = new MemoryStream();
        using var writer = new ApngWriter(stream, 16, 16);
        writer.AddFrame(TestImages.Gradient(16, 16), 100);

        Assert.ThrowsExactly<ArgumentException>(() => writer.AddFrame(TestImages.Gradient(8, 8), 100));
    }

    [TestMethod]
    public void Writer_RejectsAFrameOfTheWrongPixelFormat()
    {
        using var stream = new MemoryStream();
        using var writer = new ApngWriter(stream, 16, 16);

        Assert.ThrowsExactly<ArgumentException>(
            () => writer.AddFrame(TestImages.Gradient(16, 16, 0, PixelFormat.Rgba64), 100));
    }

    [TestMethod]
    public void Reader_RejectsOutOfOrderSequenceNumbers()
    {
        byte[] encoded = Encode([TestImages.Gradient(8, 8, 1), TestImages.Gradient(8, 8, 2)], delay: 100);

        // Find the second fcTL and corrupt its sequence number, then repair the chunk CRC so the
        // failure is attributed to the sequence check rather than to the checksum.
        int index = FindChunk(encoded, "fdAT");
        encoded[index + 8] = 0xFF;
        RepairCrc(encoded, index);

        Assert.ThrowsExactly<PngFormatException>(() => ApngReader.Read(encoded));
    }

    [TestMethod]
    public void Delays_AreRepresentedExactly()
    {
        foreach (int delay in new[] { 0, 1, 33, 100, 1000, 65535 })
        {
            (ushort numerator, ushort denominator) = ApngWriter.EncodeDelay(delay);
            Assert.AreEqual(delay, numerator * 1000 / denominator, $"delay {delay} ms was not encoded exactly");
        }
    }

    private static byte[] Encode(
        RgbaImage[] frames,
        int delay,
        PixelFormat format = PixelFormat.Rgba32,
        ApngWriterOptions? options = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new ApngWriter(stream, frames[0].Width, frames[0].Height, format, options))
        {
            foreach (RgbaImage frame in frames)
                writer.AddFrame(frame, delay);

            writer.Complete();
        }

        return stream.ToArray();
    }

    private static int FindChunk(byte[] data, string type)
    {
        int position = 8;
        while (position + 8 <= data.Length)
        {
            int length = (data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
            string current = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            if (current == type)
                return position;

            position += length + 12;
        }

        throw new InvalidOperationException($"No '{type}' chunk in the stream.");
    }

    private static void RepairCrc(byte[] data, int chunkStart)
    {
        int length = (data[chunkStart] << 24) | (data[chunkStart + 1] << 16) | (data[chunkStart + 2] << 8) | data[chunkStart + 3];
        uint crc = Crc32.Compute(data.AsSpan(chunkStart + 4, 4 + length));
        int crcOffset = chunkStart + 8 + length;
        data[crcOffset] = (byte)(crc >> 24);
        data[crcOffset + 1] = (byte)(crc >> 16);
        data[crcOffset + 2] = (byte)(crc >> 8);
        data[crcOffset + 3] = (byte)crc;
    }

    /// <summary>A stream that reports CanSeek = false, to exercise the writer's spooling path.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
