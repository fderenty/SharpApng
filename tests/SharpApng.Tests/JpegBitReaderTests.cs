namespace SharpApng.Tests;

[TestClass]
public sealed class JpegBitReaderTests
{
    [TestMethod]
    public void ReadBits_UnstuffsFF00()
    {
        // FF 00 encodes a literal FF byte inside entropy-coded data.
        byte[] data = [0xFF, 0x00, 0x5A];
        var reader = new JpegBitReader(data, 0);

        Assert.AreEqual(0xFF, reader.ReadBits(8));
        Assert.AreEqual(0x5A, reader.ReadBits(8));
    }

    [TestMethod]
    public void ReadBits_StopsAtAMarker()
    {
        // FF followed by anything other than 00 ends the segment; further reads yield zeroes
        // rather than running into the marker.
        byte[] data = [0x81, 0xFF, 0xD9, 0x42];
        var reader = new JpegBitReader(data, 0);

        Assert.AreEqual(0x81, reader.ReadBits(8));
        Assert.AreEqual(0, reader.ReadBits(8));
        Assert.IsTrue(reader.AtMarker);
    }

    [TestMethod]
    public void ReadBits_SkipsFillBytesBeforeAMarker()
    {
        byte[] data = [0x33, 0xFF, 0xFF, 0xFF, 0xD9];
        var reader = new JpegBitReader(data, 0);

        Assert.AreEqual(0x33, reader.ReadBits(8));
        Assert.AreEqual(0, reader.ReadBits(4));
        Assert.IsTrue(reader.AtMarker);
    }

    [TestMethod]
    public void TryConsumeRestartMarker_FindsAMarkerSittingAheadOfTheReader()
    {
        // The case that broke real photographs. The reader pulls bytes into its bit buffer
        // ahead of what has been consumed, so when an interval ends the marker can lie a few
        // bytes past the reader's byte position — especially when the interval's last data byte
        // is 0xFF and therefore occupies two bytes as FF 00.
        //
        // Searching only the byte under the reader, or one past it, misses the marker; decoding
        // then runs on into the padding bits and desynchronises for the rest of the image.
        byte[] data = [0xAA, 0xBB, 0xFF, 0x00, 0xFF, 0xD0, 0xCC, 0xDD];
        var reader = new JpegBitReader(data, 0);

        reader.ReadBits(4);   // consume part of the first byte only

        Assert.IsTrue(reader.TryConsumeRestartMarker(), "the restart marker two bytes ahead was not found");
        Assert.AreEqual(6, reader.Position, "the reader should resume just past the marker");
        Assert.AreEqual(0xCC, reader.ReadBits(8), "decoding should continue from after the marker");
    }

    [TestMethod]
    public void TryConsumeRestartMarker_FindsAMarkerDirectlyUnderTheReader()
    {
        byte[] data = [0xFF, 0xD3, 0x77];
        var reader = new JpegBitReader(data, 0);

        Assert.IsTrue(reader.TryConsumeRestartMarker());
        Assert.AreEqual(2, reader.Position);
        Assert.AreEqual(0x77, reader.ReadBits(8));
    }

    [TestMethod]
    public void TryConsumeRestartMarker_LeavesEverythingAloneWhenThereIsNoMarker()
    {
        // Some encoders declare a restart interval in DRI and then never write the markers. The
        // entropy stream simply continues, so a failed probe must not disturb the bit position:
        // clearing the buffer would lose the alignment and corrupt the rest of the scan.
        byte[] data = [0x12, 0x34, 0x56, 0x78, 0x9A];
        var reader = new JpegBitReader(data, 0);

        Assert.AreEqual(0x1, reader.ReadBits(4));

        Assert.IsFalse(reader.TryConsumeRestartMarker());
        Assert.AreEqual(0x2, reader.ReadBits(4), "the bit position must survive a failed probe");
        Assert.AreEqual(0x34, reader.ReadBits(8));
    }

    [TestMethod]
    public void TryConsumeRestartMarker_DoesNotMistakeStuffedDataForAMarker()
    {
        // FF 00 is data, not a marker, and must not be consumed as one.
        byte[] data = [0xFF, 0x00, 0xFF, 0x00, 0x11];
        var reader = new JpegBitReader(data, 0);

        Assert.IsFalse(reader.TryConsumeRestartMarker());
        Assert.AreEqual(0xFF, reader.ReadBits(8));
        Assert.AreEqual(0xFF, reader.ReadBits(8));
        Assert.AreEqual(0x11, reader.ReadBits(8));
    }

    [TestMethod]
    public void Extend_MatchesTheSpecification()
    {
        // Magnitude-coded values: the leading bit decides the sign.
        Assert.AreEqual(0, JpegBitReader.Extend(0, 0));
        Assert.AreEqual(1, JpegBitReader.Extend(1, 1));
        Assert.AreEqual(-1, JpegBitReader.Extend(0, 1));
        Assert.AreEqual(3, JpegBitReader.Extend(3, 2));
        Assert.AreEqual(-3, JpegBitReader.Extend(0, 2));
        Assert.AreEqual(-2, JpegBitReader.Extend(1, 2));
        Assert.AreEqual(255, JpegBitReader.Extend(255, 8));
        Assert.AreEqual(-255, JpegBitReader.Extend(0, 8));
    }
}
