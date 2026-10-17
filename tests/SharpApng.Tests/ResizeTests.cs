namespace SharpApng.Tests;

[TestClass]
public sealed class ResizeTests
{
    [TestMethod]
    public void Resize_SolidColour_KeepsItExactly()
    {
        RgbaImage source = TestImages.Solid(64, 64, new Rgba32(37, 111, 200));

        foreach (ResampleFilter filter in Enum.GetValues<ResampleFilter>())
        {
            RgbaImage result = RgbaImageOps.Resize(source, 16, 16, filter);

            Assert.AreEqual(16, result.Width);
            Assert.AreEqual(16, result.Height);

            foreach (Rgba32 p in result.AsRgba32())
                Assert.AreEqual(new Rgba32(37, 111, 200), p, $"{filter} altered a flat colour");
        }
    }

    [TestMethod]
    public void Resize_DoesNotBleedTransparentColourIntoTheEdge()
    {
        // The test that matters, and the one that catches non-premultiplied filtering.
        //
        // Transparent pixels carry black, opaque ones carry white. The bite is in the pixels
        // that come out *partly* transparent, along the boundary: premultiplied, the black
        // contributes nothing because its alpha is zero, so the colour stays pure white and only
        // the alpha falls. Averaged directly, the black drags the colour towards grey — the dark
        // fringe seen around badly resized cut-outs.
        //
        // Checking only fully opaque pixels would prove nothing: a pixel whose kernel reached a
        // transparent neighbour cannot still have alpha 255, so such a test can never fail.
        var source = new RgbaImage(64, 64);
        Span<Rgba32> pixels = source.AsRgba32();

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                // Left half opaque white, right half transparent black.
                pixels[(y * 64) + x] = x < 32
                    ? new Rgba32(255, 255, 255)
                    : new Rgba32(0, 0, 0, 0);
            }
        }

        foreach (ResampleFilter filter in new[] { ResampleFilter.Triangle, ResampleFilter.Lanczos3 })
        {
            RgbaImage result = RgbaImageOps.Resize(source, 31, 31, filter);
            Span<Rgba32> got = result.AsRgba32();
            int partial = 0;

            foreach (Rgba32 p in got)
            {
                if (p.A == 0)
                    continue;

                partial += p.A < 255 ? 1 : 0;

                Assert.AreEqual(255, p.R, $"{filter} bled darkness into a partly transparent pixel: {p}");
                Assert.AreEqual(255, p.G, $"{filter} bled darkness into a partly transparent pixel: {p}");
                Assert.AreEqual(255, p.B, $"{filter} bled darkness into a partly transparent pixel: {p}");
            }

            Assert.IsTrue(partial > 0, $"{filter} produced no partly transparent pixels, so the test proved nothing");
        }
    }

    [TestMethod]
    public void Resize_FullyTransparentImage_StaysTransparent()
    {
        var source = new RgbaImage(32, 32);

        RgbaImage result = RgbaImageOps.Resize(source, 8, 8, ResampleFilter.Lanczos3);

        foreach (Rgba32 p in result.AsRgba32())
            Assert.AreEqual(0, p.A);
    }

    [TestMethod]
    public void Resize_SixteenBit_BehavesLikeEightBit()
    {
        RgbaImage deep = TestImages.Solid(32, 32, new Rgba32(10, 20, 30)).ConvertTo(PixelFormat.Rgba64);

        RgbaImage result = RgbaImageOps.Resize(deep, 8, 8);

        Assert.AreEqual(PixelFormat.Rgba64, result.Format);
        foreach (Rgba64 p in result.AsRgba64())
        {
            Assert.AreEqual(10 * 257, p.R);
            Assert.AreEqual(65535, p.A);
        }
    }

    [TestMethod]
    public void Resize_SameDimensions_ReturnsAnEqualCopy()
    {
        RgbaImage source = TestImages.Gradient(24, 18);

        RgbaImage result = RgbaImageOps.Resize(source, 24, 18, ResampleFilter.Lanczos3);

        Assert.AreNotSame(source, result);
        Assert.IsTrue(RgbaImageOps.ContentEquals(source, result), "an unchanged size should not run the filter");
    }

    [TestMethod]
    public void Resize_Upscaling_Works()
    {
        RgbaImage source = TestImages.Solid(4, 4, new Rgba32(200, 100, 50));

        RgbaImage result = RgbaImageOps.Resize(source, 16, 16, ResampleFilter.Triangle);

        Assert.AreEqual(16, result.Width);
        Assert.AreEqual(new Rgba32(200, 100, 50), result.AsRgba32()[0]);
    }

    [TestMethod]
    public void Resize_ToOnePixel_Averages()
    {
        var source = new RgbaImage(2, 1);
        source.SetPixel(0, 0, new Rgba32(0, 0, 0));
        source.SetPixel(1, 0, new Rgba32(255, 255, 255));

        RgbaImage result = RgbaImageOps.Resize(source, 1, 1);

        Rgba64 p = result.GetPixel(0, 0);
        Assert.IsTrue(p.R is > 120 * 257 and < 136 * 257, $"expected roughly mid grey, got {p}");
    }

    [TestMethod]
    public void Resize_RejectsNonPositiveDimensions()
    {
        RgbaImage source = TestImages.Gradient(8, 8);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RgbaImageOps.Resize(source, 0, 8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RgbaImageOps.Resize(source, 8, -1));
    }

    [TestMethod]
    public void ResizeToFit_PreservesAspectRatioInBothOrientations()
    {
        RgbaImage landscape = TestImages.Solid(400, 200, new Rgba32(1, 2, 3));
        RgbaImage portrait = TestImages.Solid(200, 400, new Rgba32(1, 2, 3));

        RgbaImage a = RgbaImageOps.ResizeToFit(landscape, 128);
        RgbaImage b = RgbaImageOps.ResizeToFit(portrait, 128);

        Assert.AreEqual(128, a.Width);
        Assert.AreEqual(64, a.Height);
        Assert.AreEqual(64, b.Width);
        Assert.AreEqual(128, b.Height);
    }

    [TestMethod]
    public void ResizeToFit_NeverEnlarges()
    {
        RgbaImage small = TestImages.Gradient(50, 30);

        RgbaImage result = RgbaImageOps.ResizeToFit(small, 128);

        Assert.AreEqual(50, result.Width);
        Assert.AreEqual(30, result.Height);
        Assert.IsTrue(RgbaImageOps.ContentEquals(small, result));
    }

    [TestMethod]
    public void ResizeToFit_NeverProducesAZeroDimension()
    {
        // An extreme aspect ratio would round the short side to zero without a floor.
        RgbaImage wide = TestImages.Solid(4000, 3, new Rgba32(9, 9, 9));

        RgbaImage result = RgbaImageOps.ResizeToFit(wide, 128);

        Assert.AreEqual(128, result.Width);
        Assert.AreEqual(1, result.Height);
    }

    [TestMethod]
    public void ApplyOrientation_TransposingCases_SwapDimensions()
    {
        RgbaImage source = TestImages.Solid(8, 4, new Rgba32(1, 2, 3));

        foreach (ExifOrientation orientation in new[]
        {
            ExifOrientation.Transpose, ExifOrientation.Rotate90,
            ExifOrientation.Transverse, ExifOrientation.Rotate270,
        })
        {
            RgbaImage result = RgbaImageOps.ApplyOrientation(source, orientation);
            Assert.AreEqual(4, result.Width, $"{orientation} should swap the dimensions");
            Assert.AreEqual(8, result.Height, $"{orientation} should swap the dimensions");
        }
    }

    [TestMethod]
    public void ApplyOrientation_Rotate90_MovesCornersCorrectly()
    {
        // Mark the top-left corner and follow it. Orientation 6 means the stored image must be
        // rotated 90 degrees clockwise to display upright, which sends top-left to top-right.
        var source = new RgbaImage(4, 2);
        source.SetPixel(0, 0, new Rgba32(255, 0, 0));

        RgbaImage result = RgbaImageOps.ApplyOrientation(source, ExifOrientation.Rotate90);

        Assert.AreEqual(2, result.Width);
        Assert.AreEqual(4, result.Height);
        Assert.AreEqual(255 * 257, result.GetPixel(1, 0).R, "the marked corner should land top-right");
    }

    [TestMethod]
    public void ApplyOrientation_Rotate180_IsItsOwnInverse()
    {
        RgbaImage source = TestImages.Gradient(9, 5);

        RgbaImage once = RgbaImageOps.ApplyOrientation(source, ExifOrientation.Rotate180);
        RgbaImage twice = RgbaImageOps.ApplyOrientation(once, ExifOrientation.Rotate180);

        Assert.IsTrue(RgbaImageOps.ContentEquals(source, twice));
    }

    [TestMethod]
    public void ApplyOrientation_FlipHorizontal_MirrorsColumns()
    {
        var source = new RgbaImage(3, 1);
        source.SetPixel(0, 0, new Rgba32(10, 0, 0));
        source.SetPixel(2, 0, new Rgba32(20, 0, 0));

        RgbaImage result = RgbaImageOps.ApplyOrientation(source, ExifOrientation.FlipHorizontal);

        Assert.AreEqual(20 * 257, result.GetPixel(0, 0).R);
        Assert.AreEqual(10 * 257, result.GetPixel(2, 0).R);
    }

    [TestMethod]
    public void ApplyOrientation_Normal_ReturnsACopy()
    {
        RgbaImage source = TestImages.Gradient(6, 6);

        RgbaImage result = RgbaImageOps.ApplyOrientation(source, ExifOrientation.Normal);

        Assert.AreNotSame(source, result);
        Assert.IsTrue(RgbaImageOps.ContentEquals(source, result));
    }
}
