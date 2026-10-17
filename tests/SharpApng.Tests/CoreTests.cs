namespace SharpApng.Tests;

[TestClass]
public sealed class PngFilterTests
{
    [TestMethod]
    public void EveryFilter_IsExactlyReversible()
    {
        byte[] current = [10, 200, 30, 255, 90, 12, 240, 7, 1, 2, 3, 4];
        byte[] previous = [5, 190, 60, 250, 88, 20, 111, 9, 4, 3, 2, 1];
        const int Bpp = 4;

        for (int type = PngFilter.TypeNone; type <= PngFilter.TypePaeth; type++)
        {
            var filtered = new byte[current.Length];
            PngFilter.Apply(type, current, previous, filtered, Bpp);
            PngFilter.Unfilter(type, filtered, previous, Bpp);

            CollectionAssert.AreEqual(current, filtered, $"filter type {type} did not round trip");
        }
    }

    [TestMethod]
    public void ChooseAndApply_PicksAFilterThatReverses()
    {
        byte[] current = [1, 2, 3, 4, 9, 8, 7, 6, 100, 101, 102, 103];
        byte[] previous = new byte[current.Length];
        var filtered = new byte[current.Length];
        var scratch = new byte[current.Length];

        int chosen = PngFilter.ChooseAndApply(current, previous, filtered, scratch, 4);
        PngFilter.Unfilter(chosen, filtered, previous, 4);

        CollectionAssert.AreEqual(current, filtered);
    }

    [TestMethod]
    public void Unfilter_RejectsAnUnknownFilterType()
    {
        Assert.ThrowsExactly<PngFormatException>(
            () => PngFilter.Unfilter(9, new byte[4], new byte[4], 4));
    }
}

[TestClass]
public sealed class Crc32Tests
{
    [TestMethod]
    public void MatchesTheKnownCheckValue()
    {
        // The standard CRC-32 check value for "123456789".
        byte[] input = System.Text.Encoding.ASCII.GetBytes("123456789");

        Assert.AreEqual(0xCBF43926u, Crc32.Compute(input));
    }

    [TestMethod]
    public void SplitInput_MatchesTheContiguousResult()
    {
        byte[] all = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.AreEqual(Crc32.Compute(all), Crc32.Compute(all.AsSpan(0, 3), all.AsSpan(3)));
    }
}

[TestClass]
public sealed class RgbaImageOpsTests
{
    [TestMethod]
    public void FindDifference_ReturnsEmptyForIdenticalImages()
    {
        RgbaImage image = TestImages.Gradient(16, 16);

        Assert.IsTrue(RgbaImageOps.FindDifference(image, image.Clone()).IsEmpty);
    }

    [TestMethod]
    public void FindDifference_BoundsASingleChangedPixel()
    {
        RgbaImage before = TestImages.Solid(20, 20, new Rgba32(0, 0, 0));
        RgbaImage after = before.Clone();
        after.SetPixel(7, 13, new Rgba32(1, 2, 3));

        Assert.AreEqual(new PixelRect(7, 13, 1, 1), RgbaImageOps.FindDifference(before, after));
    }

    [TestMethod]
    public void FindDifference_BoundsAScatteredRegion()
    {
        RgbaImage before = TestImages.Solid(20, 20, new Rgba32(0, 0, 0));
        RgbaImage after = before.Clone();
        after.SetPixel(3, 5, new Rgba32(1, 1, 1));
        after.SetPixel(11, 9, new Rgba32(2, 2, 2));

        Assert.AreEqual(PixelRect.FromBounds(3, 5, 12, 10), RgbaImageOps.FindDifference(before, after));
    }

    [TestMethod]
    public void Over_WithAnOpaqueSource_ReplacesTheDestination()
    {
        var source = new Rgba32(10, 20, 30);
        var destination = new Rgba32(200, 100, 50);

        Assert.AreEqual(source, RgbaImageOps.Over(source, destination));
    }

    [TestMethod]
    public void Over_WithATransparentSource_KeepsTheDestination()
    {
        var destination = new Rgba32(200, 100, 50);

        Assert.AreEqual(destination, RgbaImageOps.Over(new Rgba32(0, 0, 0, 0), destination));
    }

    [TestMethod]
    public void Over_CannotReduceAlpha()
    {
        // The structural reason the writer defaults to BlendOp.Source: compositing a fully
        // transparent pixel over an opaque one leaves it opaque, so Over can never erase.
        var opaque = new Rgba32(255, 0, 0);

        Rgba32 result = RgbaImageOps.Over(new Rgba32(0, 0, 0, 0), opaque);

        Assert.AreEqual(255, result.A);
    }

    [TestMethod]
    public void HasBinaryAlpha_DetectsPartialTransparency()
    {
        RgbaImage image = TestImages.Solid(4, 4, new Rgba32(1, 2, 3));
        Assert.IsTrue(RgbaImageOps.HasBinaryAlpha(image, image.Bounds));

        image.SetPixel(2, 2, new Rgba32(1, 2, 3, 128));
        Assert.IsFalse(RgbaImageOps.HasBinaryAlpha(image, image.Bounds));
    }

    [TestMethod]
    public void RenderFrames_HonoursDisposeBackground()
    {
        var document = new AnimationDocument(8, 8);
        document.Add(new AnimationFrame(
            TestImages.Solid(4, 4, new Rgba32(255, 0, 0)), 100, 0, 0, DisposeOp.Background, BlendOp.Source));
        document.Add(new AnimationFrame(
            TestImages.Solid(2, 2, new Rgba32(0, 255, 0)), 100, 6, 6, DisposeOp.None, BlendOp.Source));

        RgbaImage[] rendered = [.. document.RenderFrames()];

        Assert.AreEqual(255, rendered[0].GetPixel(1, 1).R / 257, "the first frame should be painted");
        Assert.AreEqual(0, rendered[1].GetPixel(1, 1).A, "disposal should have cleared the first frame's region");
        Assert.AreEqual(255, rendered[1].GetPixel(6, 6).G / 257);
    }

    [TestMethod]
    public void RenderFrames_HonoursDisposePrevious()
    {
        var document = new AnimationDocument(8, 8);
        document.Add(new AnimationFrame(TestImages.Solid(8, 8, new Rgba32(10, 10, 10)), 100));
        document.Add(new AnimationFrame(
            TestImages.Solid(4, 4, new Rgba32(200, 0, 0)), 100, 0, 0, DisposeOp.Previous, BlendOp.Source));
        document.Add(new AnimationFrame(
            TestImages.Solid(1, 1, new Rgba32(0, 0, 255)), 100, 7, 7, DisposeOp.None, BlendOp.Source));

        RgbaImage[] rendered = [.. document.RenderFrames()];

        Assert.AreEqual(200, rendered[1].GetPixel(0, 0).R / 257);
        Assert.AreEqual(10, rendered[2].GetPixel(0, 0).R / 257, "the canvas should have been restored");
    }
}

[TestClass]
public sealed class BmpCodecTests
{
    [TestMethod]
    public void Decode_BottomUp32Bit_RestoresRowOrder()
    {
        Rgba32[] pixels =
        [
            new(255, 0, 0), new(0, 255, 0),
            new(0, 0, 255), new(255, 255, 255),
        ];

        RgbaImage image = BmpCodec.Decode(TestImages.BuildBmp32(2, 2, pixels));

        Assert.AreEqual(255, image.GetPixel(0, 0).R / 257, "the top-left pixel should be red");
        Assert.AreEqual(255, image.GetPixel(1, 1).B / 257);
    }

    [TestMethod]
    public void Decode_TopDown32Bit_RestoresRowOrder()
    {
        Rgba32[] pixels =
        [
            new(255, 0, 0), new(0, 255, 0),
            new(0, 0, 255), new(255, 255, 255),
        ];

        RgbaImage image = BmpCodec.Decode(TestImages.BuildBmp32(2, 2, pixels, topDown: true));

        Assert.AreEqual(255, image.GetPixel(0, 0).R / 257);
        Assert.AreEqual(255, image.GetPixel(0, 1).B / 257);
    }

    [TestMethod]
    public void Decode_PreservesAlpha()
    {
        // Row-major from the top left: (0,0) (1,0) (0,1) (1,1).
        Rgba32[] pixels = [new(10, 20, 30, 64), new(40, 50, 60, 255), new(0, 0, 0, 0), new(1, 1, 1, 128)];

        RgbaImage image = BmpCodec.Decode(TestImages.BuildBmp32(2, 2, pixels));

        Assert.AreEqual(64, image.GetPixel(0, 0).A / 257);
        Assert.AreEqual(255, image.GetPixel(1, 0).A / 257);
        Assert.AreEqual(0, image.GetPixel(0, 1).A, "a genuinely transparent pixel must stay transparent");
        Assert.AreEqual(128, image.GetPixel(1, 1).A / 257);
    }

    [TestMethod]
    public void Decode_TreatsAnAllZeroAlphaChannelAsOpaque()
    {
        // Plenty of writers leave the fourth byte at zero without meaning "invisible". Taking
        // that literally would decode the whole image to nothing.
        Rgba32[] pixels = [new(10, 20, 30, 0), new(40, 50, 60, 0), new(70, 80, 90, 0), new(1, 2, 3, 0)];

        RgbaImage image = BmpCodec.Decode(TestImages.BuildBmp32(2, 2, pixels));

        Assert.AreEqual(255, image.GetPixel(0, 0).A / 257);
        Assert.AreEqual(10 * 257, image.GetPixel(0, 0).R, "colour data must survive the opaque fallback");
    }

    [TestMethod]
    public void Decode_RejectsNonBmpData()
    {
        Assert.ThrowsExactly<BmpFormatException>(() => BmpCodec.Decode(new byte[64]));
    }

    [TestMethod]
    public void ImageLoader_DispatchesOnContentNotExtension()
    {
        byte[] png = PngCodec.EncodeToArray(TestImages.Gradient(4, 4));
        byte[] bmp = TestImages.BuildBmp32(1, 1, [new Rgba32(1, 2, 3)]);

        Assert.AreEqual(4, ImageLoader.Load(png).Width);
        Assert.AreEqual(1, ImageLoader.Load(bmp).Width);
        Assert.ThrowsExactly<NotSupportedException>(() => ImageLoader.Load(new byte[] { 9, 9, 9, 9 }));
    }
}
