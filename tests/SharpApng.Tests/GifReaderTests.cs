namespace SharpApng.Tests;

[TestClass]
public sealed class GifReaderTests
{
    private static readonly Rgba32[] Palette =
    [
        new(255, 255, 255), // 0 white
        new(255, 0, 0),     // 1 red
        new(0, 128, 255),   // 2 blue
        new(0, 0, 0),       // 3 black
    ];

    [TestMethod]
    public void Read_SingleFrame_DecodesPixelsThroughThePalette()
    {
        byte[] gif = TestGifs.Build(4, 2, Palette,
        [
            new GifTestFrame { Indices = [0, 1, 2, 3, 3, 2, 1, 0], Width = 4, Height = 2 },
        ]);

        AnimationDocument document = GifReader.Read(gif);
        RgbaImage image = document.Frames[0].Image;

        Assert.AreEqual(1, document.Frames.Count);
        Assert.AreEqual(4, image.Width);
        Assert.AreEqual(2, image.Height);
        Assert.AreEqual(255 * 257, image.GetPixel(0, 0).R, "index 0 should decode to white");
        Assert.AreEqual(255 * 257, image.GetPixel(1, 0).R, "index 1 should decode to red");
        Assert.AreEqual(0, image.GetPixel(1, 0).G);
        Assert.AreEqual(128 * 257, image.GetPixel(2, 0).G, "index 2 should decode to blue");
        Assert.AreEqual(255 * 257, image.GetPixel(3, 1).R, "row order should be preserved");
    }

    [TestMethod]
    public void Read_LargeFrame_SurvivesClearCodesAndSubBlockBoundaries()
    {
        // Big enough to span many sub-blocks and force repeated dictionary resets.
        const int W = 64;
        const int H = 40;
        var indices = new byte[W * H];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = (byte)(i % 4);

        byte[] gif = TestGifs.Build(W, H, Palette,
        [
            new GifTestFrame { Indices = indices, Width = W, Height = H },
        ]);

        RgbaImage image = GifReader.Read(gif).Frames[0].Image;

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                Rgba32 expected = Palette[((y * W) + x) % 4];
                Rgba64 actual = image.GetPixel(x, y);
                Assert.AreEqual(expected.R * 257, actual.R, $"pixel ({x},{y}) decoded wrongly");
                Assert.AreEqual(expected.G * 257, actual.G, $"pixel ({x},{y}) decoded wrongly");
            }
        }
    }

    [TestMethod]
    public void Read_TransparentIndex_BecomesZeroAlpha()
    {
        byte[] gif = TestGifs.Build(2, 1, Palette,
        [
            new GifTestFrame { Indices = [0, 1], Width = 2, Height = 1, TransparentIndex = 0 },
        ]);

        RgbaImage image = GifReader.Read(gif).Frames[0].Image;

        Assert.AreEqual(0, image.GetPixel(0, 0).A);
        Assert.AreEqual(65535, image.GetPixel(1, 0).A);
    }

    [TestMethod]
    public void Read_DisposalMethods_MapOntoApngEquivalents()
    {
        byte[] gif = TestGifs.Build(8, 8, Palette,
        [
            new GifTestFrame { Indices = TestGifs.Solid(8, 8, 1), Width = 8, Height = 8, Disposal = 1 },
            new GifTestFrame { Indices = TestGifs.Solid(4, 4, 2), Width = 4, Height = 4, Disposal = 2 },
            new GifTestFrame { Indices = TestGifs.Solid(2, 2, 3), Width = 2, Height = 2, Disposal = 3 },
            new GifTestFrame { Indices = TestGifs.Solid(2, 2, 0), Width = 2, Height = 2, Disposal = 0 },
        ]);

        AnimationDocument document = GifReader.Read(gif);

        Assert.AreEqual(DisposeOp.None, document.Frames[0].DisposeOp);
        Assert.AreEqual(DisposeOp.Background, document.Frames[1].DisposeOp);
        Assert.AreEqual(DisposeOp.Previous, document.Frames[2].DisposeOp);
        Assert.AreEqual(DisposeOp.None, document.Frames[3].DisposeOp, "unspecified disposal should leave the canvas alone");
    }

    [TestMethod]
    public void Read_FramePosition_IsPreserved()
    {
        byte[] gif = TestGifs.Build(16, 16, Palette,
        [
            new GifTestFrame { Indices = TestGifs.Solid(16, 16, 0), Width = 16, Height = 16 },
            new GifTestFrame { Indices = TestGifs.Solid(4, 4, 1), Width = 4, Height = 4, Left = 5, Top = 7 },
        ]);

        AnimationFrame frame = GifReader.Read(gif).Frames[1];

        Assert.AreEqual(new PixelRect(5, 7, 4, 4), frame.Bounds);
        Assert.AreEqual(BlendOp.Over, frame.BlendOp, "GIF layering is source-over with binary alpha");
    }

    [TestMethod]
    public void Read_Interlaced_RestoresRowOrder()
    {
        const int W = 4;
        const int H = 8;

        // Rows in GIF interlace order: passes start at 0, 4, 2, 1 with steps 8, 8, 4, 2.
        int[] storedRows = [0, 4, 2, 6, 1, 3, 5, 7];
        var indices = new byte[W * H];
        for (int stored = 0; stored < H; stored++)
        {
            byte value = (byte)(storedRows[stored] % 4);
            for (int x = 0; x < W; x++)
                indices[(stored * W) + x] = value;
        }

        byte[] gif = TestGifs.Build(W, H, Palette,
        [
            new GifTestFrame { Indices = indices, Width = W, Height = H, Interlaced = true },
        ]);

        RgbaImage image = GifReader.Read(gif).Frames[0].Image;

        for (int y = 0; y < H; y++)
        {
            Rgba32 expected = Palette[y % 4];
            Assert.AreEqual(expected.R * 257, image.GetPixel(0, y).R, $"row {y} landed in the wrong place");
            Assert.AreEqual(expected.G * 257, image.GetPixel(0, y).G, $"row {y} landed in the wrong place");
        }
    }

    [TestMethod]
    public void Read_LoopCount_ComesFromTheNetscapeExtension()
    {
        byte[] gif = TestGifs.Build(2, 2, Palette,
            [new GifTestFrame { Indices = TestGifs.Solid(2, 2, 1), Width = 2, Height = 2 }],
            loopCount: 5);

        Assert.AreEqual(5, GifReader.Read(gif).LoopCount);
    }

    [TestMethod]
    public void Read_Delays_ConvertExactly()
    {
        byte[] gif = TestGifs.Build(2, 2, Palette,
        [
            new GifTestFrame { Indices = TestGifs.Solid(2, 2, 1), Width = 2, Height = 2, DelayCentiseconds = 7 },
            new GifTestFrame { Indices = TestGifs.Solid(2, 2, 2), Width = 2, Height = 2, DelayCentiseconds = 25 },
        ]);

        AnimationDocument document = GifReader.Read(gif);

        Assert.AreEqual(70, document.Frames[0].DelayMilliseconds);
        Assert.AreEqual(250, document.Frames[1].DelayMilliseconds);
    }

    [TestMethod]
    public void Read_ShortDelays_AreClampedLikeBrowsersByDefault()
    {
        byte[] gif = TestGifs.Build(2, 2, Palette,
        [
            new GifTestFrame { Indices = TestGifs.Solid(2, 2, 1), Width = 2, Height = 2, DelayCentiseconds = 0 },
            new GifTestFrame { Indices = TestGifs.Solid(2, 2, 2), Width = 2, Height = 2, DelayCentiseconds = 1 },
            new GifTestFrame { Indices = TestGifs.Solid(2, 2, 3), Width = 2, Height = 2, DelayCentiseconds = 2 },
        ]);

        AnimationDocument clamped = GifReader.Read(gif);

        Assert.AreEqual(100, clamped.Frames[0].DelayMilliseconds, "a stated delay of 0 plays at 100 ms in browsers");
        Assert.AreEqual(100, clamped.Frames[1].DelayMilliseconds, "a stated delay of 1 plays at 100 ms in browsers");
        Assert.AreEqual(20, clamped.Frames[2].DelayMilliseconds, "2 hundredths is above the clamp and passes through");

        AnimationDocument literal = GifReader.Read(gif, new GifReaderOptions { ClampShortDelays = false });

        Assert.AreEqual(0, literal.Frames[0].DelayMilliseconds);
        Assert.AreEqual(10, literal.Frames[1].DelayMilliseconds);
    }

    [TestMethod]
    public void ColorKey_ErasesTheChosenColour()
    {
        byte[] gif = TestGifs.Build(4, 1, Palette,
        [
            new GifTestFrame { Indices = [0, 1, 0, 2], Width = 4, Height = 1 },
        ]);

        AnimationDocument document = GifReader.Read(gif,
            new GifReaderOptions { TransparentColor = new Rgba32(255, 255, 255) });

        RgbaImage image = document.Frames[0].Image;

        Assert.AreEqual(0, image.GetPixel(0, 0).A, "the keyed colour should be erased");
        Assert.AreEqual(0, image.GetPixel(2, 0).A);
        Assert.AreEqual(65535, image.GetPixel(1, 0).A, "other colours must be untouched");
        Assert.AreEqual(65535, image.GetPixel(3, 0).A);
    }

    [TestMethod]
    public void ColorKey_FromBackground_UsesTheDeclaredIndex()
    {
        byte[] gif = TestGifs.Build(2, 1, Palette,
            [new GifTestFrame { Indices = [2, 1], Width = 2, Height = 1 }],
            backgroundIndex: 2);

        AnimationDocument document = GifReader.Read(gif,
            new GifReaderOptions { TreatBackgroundAsTransparent = true });

        Assert.AreEqual(0, document.Frames[0].Image.GetPixel(0, 0).A);
        Assert.AreEqual(65535, document.Frames[0].Image.GetPixel(1, 0).A);
    }

    [TestMethod]
    public void ColorKey_Tolerance_MatchesNearbyColours()
    {
        Rgba32[] palette = [new(250, 250, 250), new(0, 0, 0)];
        byte[] gif = TestGifs.Build(2, 1, palette,
        [
            new GifTestFrame { Indices = [0, 1], Width = 2, Height = 1 },
        ]);

        var exact = new GifReaderOptions { TransparentColor = new Rgba32(255, 255, 255) };
        Assert.AreEqual(65535, GifReader.Read(gif, exact).Frames[0].Image.GetPixel(0, 0).A,
            "an exact match should not erase a near colour");

        var loose = new GifReaderOptions { TransparentColor = new Rgba32(255, 255, 255), ColorTolerance = 8 };
        Assert.AreEqual(0, GifReader.Read(gif, loose).Frames[0].Image.GetPixel(0, 0).A);
    }

    [TestMethod]
    public void ColorKey_FlattensFramesSoErasingStillWorks()
    {
        // Frame 1 paints red across the canvas, frame 2 covers part of it in white. Keying out
        // white must reveal transparency, not leave the white showing. That only holds because
        // the reader composites first and re-emits whole frames: BlendOp.Over could not lower
        // the alpha of what frame 1 already put on the canvas.
        byte[] gif = TestGifs.Build(4, 1, Palette,
        [
            new GifTestFrame { Indices = TestGifs.Solid(4, 1, 1), Width = 4, Height = 1 },
            new GifTestFrame { Indices = [0, 0], Width = 2, Height = 1, Left = 1 },
        ]);

        AnimationDocument document = GifReader.Read(gif,
            new GifReaderOptions { TransparentColor = new Rgba32(255, 255, 255) });

        RgbaImage second = document.Frames[1].Image;

        Assert.AreEqual(new PixelRect(0, 0, 4, 1), document.Frames[1].Bounds, "keying should flatten to full frames");
        Assert.AreEqual(BlendOp.Source, document.Frames[1].BlendOp);
        Assert.AreEqual(0, second.GetPixel(1, 0).A, "the white overlay should have become transparent");
        Assert.AreEqual(65535, second.GetPixel(0, 0).A, "red from the first frame should remain");
    }

    [TestMethod]
    public void Read_FrameHangingOffTheCanvas_IsClipped()
    {
        byte[] gif = TestGifs.Build(8, 8, Palette,
        [
            new GifTestFrame { Indices = TestGifs.Solid(8, 8, 0), Width = 8, Height = 8 },
            new GifTestFrame { Indices = TestGifs.Solid(6, 6, 1), Width = 6, Height = 6, Left = 5, Top = 5 },
        ]);

        AnimationFrame frame = GifReader.Read(gif).Frames[1];

        Assert.AreEqual(new PixelRect(5, 5, 3, 3), frame.Bounds, "the frame should be clipped to the canvas");
    }

    [TestMethod]
    public void TransparentPixels_HaveTheirColourChannelsZeroed()
    {
        // A pixel with zero alpha is invisible, so the palette colour underneath carries no
        // meaning. Zeroing it costs nothing visually and compresses far better than leaving
        // scattered colours behind an empty alpha channel. Some decoders keep that colour, which
        // is why comparing raw RGBA bytes across implementations is misleading.
        byte[] gif = TestGifs.Build(2, 1, Palette,
        [
            new GifTestFrame { Indices = [0, 1], Width = 2, Height = 1, TransparentIndex = 0 },
        ]);

        Rgba64 erased = GifReader.Read(gif).Frames[0].Image.GetPixel(0, 0);

        Assert.AreEqual(0, erased.A);
        Assert.AreEqual(0, erased.R, "a fully transparent pixel should carry no colour");
        Assert.AreEqual(0, erased.G);
        Assert.AreEqual(0, erased.B);
    }

    [TestMethod]
    public void Read_RejectsNonGifData()
    {
        Assert.ThrowsExactly<GifFormatException>(() => GifReader.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [TestMethod]
    public void IsGif_RecognisesBothSignatures()
    {
        Assert.IsTrue(GifReader.IsGif("GIF87a"u8));
        Assert.IsTrue(GifReader.IsGif("GIF89a"u8));
        Assert.IsFalse(GifReader.IsGif("GIF88a"u8));
        Assert.IsFalse(GifReader.IsGif(PngCodec.EncodeToArray(TestImages.Gradient(2, 2))));
    }

    [TestMethod]
    public void AnimationLoader_ConvertsAGifStraightToApng()
    {
        byte[] gif = TestGifs.Build(8, 8, Palette,
        [
            new GifTestFrame { Indices = TestGifs.Solid(8, 8, 1), Width = 8, Height = 8, DelayCentiseconds = 10 },
            new GifTestFrame { Indices = TestGifs.Solid(8, 8, 2), Width = 8, Height = 8, DelayCentiseconds = 10 },
        ], loopCount: 3);

        AnimationDocument source = AnimationLoader.Load(gif);

        using var stream = new MemoryStream();
        ApngWriter.Write(source, stream);
        AnimationDocument reloaded = ApngReader.Read(stream.ToArray());

        Assert.AreEqual(2, reloaded.Frames.Count);
        Assert.AreEqual(3, reloaded.LoopCount);
        Assert.AreEqual(100, reloaded.Frames[0].DelayMilliseconds);

        RgbaImage[] before = [.. source.RenderFrames()];
        RgbaImage[] after = [.. reloaded.RenderFrames()];
        for (int i = 0; i < before.Length; i++)
            Assert.IsTrue(RgbaImageOps.ContentEquals(before[i], after[i]), $"frame {i} changed during conversion");
    }
}
