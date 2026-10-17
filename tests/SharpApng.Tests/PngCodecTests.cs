namespace SharpApng.Tests;

[TestClass]
public sealed class PngCodecTests
{
    [TestMethod]
    public void RoundTrip_EightBit_IsLossless()
    {
        RgbaImage original = TestImages.Gradient(37, 23);

        RgbaImage decoded = PngCodec.Decode(PngCodec.EncodeToArray(original));

        Assert.IsTrue(RgbaImageOps.ContentEquals(original, decoded), "8-bit round trip changed pixel data.");
    }

    [TestMethod]
    public void RoundTrip_SixteenBit_IsLossless()
    {
        RgbaImage original = TestImages.Gradient(19, 31, seed: 3, PixelFormat.Rgba64);

        RgbaImage decoded = PngCodec.Decode(PngCodec.EncodeToArray(original));

        Assert.AreEqual(PixelFormat.Rgba64, decoded.Format);
        Assert.IsTrue(RgbaImageOps.ContentEquals(original, decoded), "16-bit round trip changed pixel data.");
    }

    [TestMethod]
    public void Encode_AlwaysWritesTruecolorAlpha()
    {
        // Every frame sharing one colour type is what keeps a multi-frame file coherent,
        // so an opaque image must still be written as RGBA rather than dropping to RGB.
        RgbaImage opaque = TestImages.Solid(4, 4, new Rgba32(10, 20, 30));

        byte[] encoded = PngCodec.EncodeToArray(opaque);

        Assert.AreEqual(8, encoded[24], "bit depth should be 8");
        Assert.AreEqual(6, encoded[25], "colour type should be 6 (truecolour with alpha)");
    }

    [TestMethod]
    public void Decode_RejectsCorruptedCrc()
    {
        byte[] encoded = PngCodec.EncodeToArray(TestImages.Gradient(8, 8));
        encoded[^5] ^= 0xFF; // corrupt the IEND payload region's checksum input

        Assert.ThrowsExactly<PngFormatException>(() => PngCodec.Decode(encoded));
    }

    [TestMethod]
    public void Decode_RejectsNonPngData()
    {
        Assert.ThrowsExactly<PngFormatException>(() => PngCodec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
    }

    [TestMethod]
    public void Decode_Grayscale8_ExpandsToRgba()
    {
        byte[] png = TestImages.BuildPng(
            width: 3,
            height: 2,
            bitDepth: 8,
            colorType: 0,
            scanlines: [[0, 128, 255], [64, 32, 16]]);

        RgbaImage image = PngCodec.Decode(png);

        Assert.AreEqual(new Rgba64(0, 0, 0, 65535), image.GetPixel(0, 0));
        Assert.AreEqual(255 * 257, image.GetPixel(2, 0).R);
        Assert.AreEqual(image.GetPixel(1, 1).R, image.GetPixel(1, 1).B, "grey must expand to equal channels");
    }

    [TestMethod]
    public void Decode_Grayscale4_ScalesSamplesToFullRange()
    {
        // Two 4-bit samples per byte: 0x0F is (0, 15), which must scale to (0, 255).
        byte[] png = TestImages.BuildPng(
            width: 2,
            height: 1,
            bitDepth: 4,
            colorType: 0,
            scanlines: [[0x0F]]);

        RgbaImage image = PngCodec.Decode(png);

        Assert.AreEqual(0, image.GetPixel(0, 0).R);
        Assert.AreEqual(255 * 257, image.GetPixel(1, 0).R);
    }

    [TestMethod]
    public void Decode_PaletteWithTransparency_AppliesAlpha()
    {
        byte[] png = TestImages.BuildPng(
            width: 3,
            height: 1,
            bitDepth: 8,
            colorType: 3,
            scanlines: [[0, 1, 2]],
            palette: [255, 0, 0, 0, 255, 0, 0, 0, 255],
            transparency: [0, 128]); // index 0 clear, index 1 half, index 2 defaults to opaque

        RgbaImage image = PngCodec.Decode(png);

        Assert.AreEqual(0, image.GetPixel(0, 0).A);
        Assert.AreEqual(128 * 257, image.GetPixel(1, 0).A);
        Assert.AreEqual(65535, image.GetPixel(2, 0).A);
        Assert.AreEqual(255 * 257, image.GetPixel(2, 0).B);
    }

    [TestMethod]
    public void Decode_TruecolorWithColorKey_MakesMatchingPixelsTransparent()
    {
        byte[] png = TestImages.BuildPng(
            width: 2,
            height: 1,
            bitDepth: 8,
            colorType: 2,
            scanlines: [[255, 0, 255, 10, 20, 30]],
            transparency: [0, 255, 0, 0, 0, 255]); // colour key = magenta

        RgbaImage image = PngCodec.Decode(png);

        Assert.AreEqual(0, image.GetPixel(0, 0).A, "the keyed colour should decode as transparent");
        Assert.AreEqual(65535, image.GetPixel(1, 0).A);
    }

    [TestMethod]
    public void Decode_GrayscaleAlpha16_KeepsFullPrecision()
    {
        // 0x1234 and 0xABCD survive only if 16-bit samples are read big-endian.
        byte[] png = TestImages.BuildPng(
            width: 1,
            height: 1,
            bitDepth: 16,
            colorType: 4,
            scanlines: [[0x12, 0x34, 0xAB, 0xCD]]);

        RgbaImage image = PngCodec.Decode(png);

        Assert.AreEqual(PixelFormat.Rgba64, image.Format);
        Assert.AreEqual(0x1234, image.GetPixel(0, 0).R);
        Assert.AreEqual(0xABCD, image.GetPixel(0, 0).A);
    }

    [TestMethod]
    public void ConvertTo_EightToSixteenAndBack_IsStable()
    {
        RgbaImage original = TestImages.Gradient(16, 16);

        RgbaImage widened = original.ConvertTo(PixelFormat.Rgba64);
        RgbaImage narrowed = widened.ConvertTo(PixelFormat.Rgba32);

        Assert.IsTrue(RgbaImageOps.ContentEquals(original, narrowed), "depth conversion should be reversible");
    }
}
