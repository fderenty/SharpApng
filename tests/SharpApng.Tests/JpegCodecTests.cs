namespace SharpApng.Tests;

[TestClass]
public sealed class JpegCodecTests
{
    // The specification allows implementations to differ on the IDCT, and they do: libjpeg,
    // libjpeg-turbo and Pillow disagree byte for byte with each other. Comparisons therefore
    // allow a tolerance. Demanding equality here would fail for no good reason.
    private const double MaxMeanError = 1.0;
    private const int MaxChannelError = 3;

    [DataTestMethod]
    [DataRow("baseline444", DisplayName = "baseline 4:4:4")]
    [DataRow("baseline422", DisplayName = "baseline 4:2:2")]
    [DataRow("baseline420", DisplayName = "baseline 4:2:0")]
    [DataRow("greyscale", DisplayName = "greyscale, one component")]
    [DataRow("progressive", DisplayName = "progressive 4:2:0")]
    [DataRow("progressive444", DisplayName = "progressive 4:4:4")]
    [DataRow("restart", DisplayName = "restart markers")]
    [DataRow("restart_dense", DisplayName = "restart after every MCU, over noise")]
    [DataRow("restart_noisy444", DisplayName = "restart markers 4:4:4, over noise")]
    [DataRow("odd_size", DisplayName = "37x23, not a multiple of the MCU")]
    [DataRow("tiny1x1", DisplayName = "1x1")]
    [DataRow("tiny3x5", DisplayName = "3x5")]
    [DataRow("lowquality", DisplayName = "quality 15")]
    [DataRow("highquality", DisplayName = "quality 100")]
    [DataRow("large", DisplayName = "200x150")]
    public void Decode_MatchesPillow(string fixture)
    {
        byte[] jpeg = JpegFixtures.Jpeg(fixture);
        RgbaImage expected = JpegFixtures.Reference(fixture);

        RgbaImage actual = JpegCodec.Decode(jpeg);

        AssertSimilar(expected, actual, fixture);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    public void Decode_AppliesExifOrientationByDefault(int orientation)
    {
        string fixture = $"orient{orientation}";
        byte[] jpeg = JpegFixtures.Jpeg(fixture);

        // Pillow's plain decode does not apply the tag, so the reference is the stored
        // orientation. Rotating it the same way must reproduce what the decoder returns.
        RgbaImage stored = JpegFixtures.Reference(fixture);
        RgbaImage expected = RgbaImageOps.ApplyOrientation(stored, (ExifOrientation)orientation);

        RgbaImage actual = JpegCodec.Decode(jpeg);

        Assert.AreEqual(expected.Width, actual.Width, $"orientation {orientation} produced the wrong width");
        Assert.AreEqual(expected.Height, actual.Height, $"orientation {orientation} produced the wrong height");
        AssertSimilar(expected, actual, fixture);
    }

    [TestMethod]
    public void Decode_CanLeaveOrientationAlone()
    {
        byte[] jpeg = JpegFixtures.Jpeg("orient6");

        RgbaImage stored = JpegCodec.Decode(jpeg, new JpegDecoderOptions { ApplyExifOrientation = false });
        RgbaImage upright = JpegCodec.Decode(jpeg);

        Assert.AreEqual(stored.Height, upright.Width, "orientation 6 swaps the dimensions");
        Assert.AreEqual(stored.Width, upright.Height);
    }

    [TestMethod]
    public void ReadInfo_ReportsHeadersWithoutDecoding()
    {
        JpegInfo baseline = JpegCodec.ReadInfo(JpegFixtures.Jpeg("baseline420"));
        JpegInfo progressive = JpegCodec.ReadInfo(JpegFixtures.Jpeg("progressive"));
        JpegInfo grey = JpegCodec.ReadInfo(JpegFixtures.Jpeg("greyscale"));
        JpegInfo rotated = JpegCodec.ReadInfo(JpegFixtures.Jpeg("orient6"));
        JpegInfo plain = JpegCodec.ReadInfo(JpegFixtures.Jpeg("noexif"));

        Assert.AreEqual(64, baseline.Width);
        Assert.AreEqual(48, baseline.Height);
        Assert.AreEqual(3, baseline.ComponentCount);
        Assert.IsFalse(baseline.IsProgressive);

        Assert.IsTrue(progressive.IsProgressive);
        Assert.AreEqual(1, grey.ComponentCount);

        Assert.AreEqual(ExifOrientation.Rotate90, rotated.Orientation);
        Assert.AreEqual(ExifOrientation.Normal, plain.Orientation, "a file without EXIF should report Normal");
    }

    [TestMethod]
    public void IsJpeg_RecognisesTheSignature()
    {
        Assert.IsTrue(JpegCodec.IsJpeg(JpegFixtures.Jpeg("baseline420")));
        Assert.IsFalse(JpegCodec.IsJpeg(PngCodec.EncodeToArray(TestImages.Gradient(4, 4))));
        Assert.IsFalse(JpegCodec.IsJpeg([0xFF, 0xD8]));
    }

    [TestMethod]
    public void Decode_ProducesOpaquePixels()
    {
        RgbaImage image = JpegCodec.Decode(JpegFixtures.Jpeg("baseline444"));

        Assert.AreEqual(PixelFormat.Rgba32, image.Format);
        Assert.IsTrue(image.IsOpaque(), "JPEG has no alpha channel, so every pixel must be opaque");
    }

    [TestMethod]
    public void ImageLoader_AcceptsJpeg()
    {
        RgbaImage image = ImageLoader.Load(JpegFixtures.Jpeg("baseline420"));

        Assert.AreEqual(64, image.Width);
        Assert.AreEqual(48, image.Height);
    }

    [TestMethod]
    public void Decode_RejectsNonJpegData()
    {
        Assert.ThrowsExactly<JpegFormatException>(() => JpegCodec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [TestMethod]
    public void Decode_RejectsArithmeticCoding()
    {
        // SOF9 is extended sequential with arithmetic coding.
        byte[] jpeg = BuildMinimalJpegWithFrameMarker(0xC9);

        JpegFormatException error = Assert.ThrowsExactly<JpegFormatException>(() => JpegCodec.Decode(jpeg));
        StringAssert.Contains(error.Message, "SOF9");
        StringAssert.Contains(error.Message, "rithmetic");
    }

    [TestMethod]
    public void Decode_RejectsLosslessCoding()
    {
        byte[] jpeg = BuildMinimalJpegWithFrameMarker(0xC3);

        JpegFormatException error = Assert.ThrowsExactly<JpegFormatException>(() => JpegCodec.Decode(jpeg));
        StringAssert.Contains(error.Message, "SOF3");
        StringAssert.Contains(error.Message, "ossless");
    }

    [TestMethod]
    public void Decode_RejectsTwelveBitPrecision()
    {
        byte[] jpeg = BuildMinimalSof(precision: 12, componentCount: 3);

        JpegFormatException error = Assert.ThrowsExactly<JpegFormatException>(() => JpegCodec.Decode(jpeg));
        StringAssert.Contains(error.Message, "12-bit");
    }

    [TestMethod]
    public void Decode_RejectsCmykWithAnExplanation()
    {
        byte[] jpeg = BuildMinimalSof(precision: 8, componentCount: 4);

        JpegFormatException error = Assert.ThrowsExactly<JpegFormatException>(() => JpegCodec.Decode(jpeg));
        StringAssert.Contains(error.Message, "CMYK");
        StringAssert.Contains(error.Message, "invert", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Decode_RejectsTruncatedData()
    {
        byte[] jpeg = JpegFixtures.Jpeg("baseline420");

        // Cut inside the headers, before the scan: the failure must be reported, not guessed at.
        Assert.ThrowsExactly<JpegFormatException>(() => JpegCodec.Decode(jpeg.AsSpan(0, 20)));
    }

    [TestMethod]
    public void Decode_SurvivesTruncatedEntropyData()
    {
        // Cutting inside the scan is different: the headers are valid and most of the image is
        // there, so the decoder should return what it has rather than throw.
        byte[] jpeg = JpegFixtures.Jpeg("large");
        RgbaImage image = JpegCodec.Decode(jpeg.AsSpan(0, jpeg.Length - 200));

        Assert.AreEqual(200, image.Width);
        Assert.AreEqual(150, image.Height);
    }

    [TestMethod]
    public void Decode_RejectsAScanReferencingAMissingHuffmanTable()
    {
        byte[] jpeg = JpegFixtures.Jpeg("baseline420");

        // Turn the DHT marker into a comment so its tables never get defined.
        byte[] broken = (byte[])jpeg.Clone();
        for (int i = 0; i + 1 < broken.Length; i++)
        {
            if (broken[i] == 0xFF && broken[i + 1] == 0xC4)
            {
                broken[i + 1] = 0xFE; // COM
                break;
            }
        }

        Assert.ThrowsExactly<JpegFormatException>(() => JpegCodec.Decode(broken));
    }

    private static void AssertSimilar(RgbaImage expected, RgbaImage actual, string fixture)
    {
        Assert.AreEqual(expected.Width, actual.Width, $"{fixture}: width");
        Assert.AreEqual(expected.Height, actual.Height, $"{fixture}: height");

        ReadOnlySpan<Rgba32> a = expected.AsRgba32();
        ReadOnlySpan<Rgba32> b = actual.AsRgba32();

        long total = 0;
        int worst = 0;
        int worstIndex = -1;

        for (int i = 0; i < a.Length; i++)
        {
            int dr = Math.Abs(a[i].R - b[i].R);
            int dg = Math.Abs(a[i].G - b[i].G);
            int db = Math.Abs(a[i].B - b[i].B);

            total += dr + dg + db;
            int local = Math.Max(dr, Math.Max(dg, db));
            if (local > worst)
            {
                worst = local;
                worstIndex = i;
            }
        }

        double mean = (double)total / (a.Length * 3);

        Assert.IsTrue(
            mean < MaxMeanError,
            $"{fixture}: mean channel error {mean:F3} exceeds {MaxMeanError}");

        Assert.IsTrue(
            worst <= MaxChannelError,
            $"{fixture}: worst channel error {worst} exceeds {MaxChannelError} at pixel " +
            $"({worstIndex % expected.Width}, {worstIndex / expected.Width}): " +
            $"expected {a[worstIndex]}, got {b[worstIndex]}");
    }

    /// <summary>Builds a JPEG whose SOF marker is the given code, to exercise the refusals.</summary>
    private static byte[] BuildMinimalJpegWithFrameMarker(byte sofMarker)
    {
        return
        [
            0xFF, 0xD8,                                     // SOI
            0xFF, sofMarker, 0x00, 0x11,                    // SOF, length 17
            0x08, 0x00, 0x10, 0x00, 0x10, 0x03,             // 8-bit, 16x16, 3 components
            0x01, 0x22, 0x00,
            0x02, 0x11, 0x01,
            0x03, 0x11, 0x01,
            0xFF, 0xD9,                                     // EOI
        ];
    }

    private static byte[] BuildMinimalSof(int precision, int componentCount)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0 };
        int length = 8 + (componentCount * 3);
        jpeg.Add((byte)(length >> 8));
        jpeg.Add((byte)length);
        jpeg.Add((byte)precision);
        jpeg.AddRange([0x00, 0x10, 0x00, 0x10]);
        jpeg.Add((byte)componentCount);

        for (int i = 0; i < componentCount; i++)
            jpeg.AddRange([(byte)(i + 1), 0x11, 0x00]);

        jpeg.AddRange([0xFF, 0xD9]);
        return [.. jpeg];
    }
}
