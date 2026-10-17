namespace SharpApng;

/// <summary>
/// One colour component of a frame, with its sampling factors and its decoded coefficients.
/// </summary>
internal sealed class JpegComponent
{
    /// <summary>Component identifier as declared in <c>SOF</c>.</summary>
    public required int Id { get; init; }

    /// <summary>Horizontal sampling factor.</summary>
    public required int HorizontalSampling { get; init; }

    /// <summary>Vertical sampling factor.</summary>
    public required int VerticalSampling { get; init; }

    /// <summary>Index of the quantisation table this component uses.</summary>
    public required int QuantisationTableIndex { get; init; }

    /// <summary>Blocks per row, rounded up to whole MCUs.</summary>
    public int BlocksPerLine { get; set; }

    /// <summary>Block rows, rounded up to whole MCUs.</summary>
    public int BlocksPerColumn { get; set; }

    /// <summary>Blocks per row covering only the declared image width.</summary>
    public int ActiveBlocksPerLine { get; set; }

    /// <summary>Block rows covering only the declared image height.</summary>
    public int ActiveBlocksPerColumn { get; set; }

    /// <summary>
    /// All coefficients, 64 per block in natural order, row-major by block.
    /// </summary>
    /// <remarks>
    /// Coefficients are kept for the whole image rather than transformed block by block. A
    /// progressive file refines the same coefficients across several scans, so they cannot be
    /// consumed until every scan has been read; using one representation for both coding
    /// processes keeps a single path through the decoder.
    /// </remarks>
    public short[] Coefficients { get; set; } = [];

    /// <summary>Huffman table index for DC coefficients in the current scan.</summary>
    public int DcTableIndex { get; set; }

    /// <summary>Huffman table index for AC coefficients in the current scan.</summary>
    public int AcTableIndex { get; set; }

    /// <summary>Running DC predictor, reset at every restart interval.</summary>
    public int DcPrediction { get; set; }

    /// <summary>Offset of a block's first coefficient.</summary>
    public int BlockOffset(int row, int column) => ((row * BlocksPerLine) + column) * 64;
}

/// <summary>
/// The state a <c>SOF</c> marker establishes: dimensions, components and MCU geometry.
/// </summary>
internal sealed class JpegFrame
{
    /// <summary>Declared image width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Declared image height in pixels. Zero until <c>DNL</c> when the frame defers it.</summary>
    public int Height { get; set; }

    /// <summary>Sample precision in bits.</summary>
    public int Precision { get; set; }

    /// <summary>True for <c>SOF2</c>.</summary>
    public bool IsProgressive { get; set; }

    /// <summary>The components, in declaration order.</summary>
    public JpegComponent[] Components { get; set; } = [];

    /// <summary>Largest horizontal sampling factor across components.</summary>
    public int MaxHorizontalSampling { get; private set; } = 1;

    /// <summary>Largest vertical sampling factor across components.</summary>
    public int MaxVerticalSampling { get; private set; } = 1;

    /// <summary>MCUs per row.</summary>
    public int McusPerLine { get; private set; }

    /// <summary>MCU rows.</summary>
    public int McusPerColumn { get; private set; }

    /// <summary>
    /// Works out the MCU grid and allocates coefficient storage. Called once the dimensions are
    /// known, which for a frame using <c>DNL</c> is after the first scan.
    /// </summary>
    public void PrepareComponents()
    {
        MaxHorizontalSampling = 1;
        MaxVerticalSampling = 1;

        foreach (JpegComponent component in Components)
        {
            MaxHorizontalSampling = Math.Max(MaxHorizontalSampling, component.HorizontalSampling);
            MaxVerticalSampling = Math.Max(MaxVerticalSampling, component.VerticalSampling);
        }

        McusPerLine = CeilDiv(Width, 8 * MaxHorizontalSampling);
        McusPerColumn = CeilDiv(Height, 8 * MaxVerticalSampling);

        foreach (JpegComponent component in Components)
        {
            // Active counts cover the real image; the padded counts cover whole MCUs, which is
            // what the entropy-coded data actually contains.
            component.ActiveBlocksPerLine =
                CeilDiv(Width * component.HorizontalSampling, MaxHorizontalSampling * 8);
            component.ActiveBlocksPerColumn =
                CeilDiv(Height * component.VerticalSampling, MaxVerticalSampling * 8);

            component.BlocksPerLine = McusPerLine * component.HorizontalSampling;
            component.BlocksPerColumn = McusPerColumn * component.VerticalSampling;

            long size = (long)component.BlocksPerLine * component.BlocksPerColumn * 64;
            if (size > Array.MaxLength)
                throw new JpegFormatException($"Image is too large to decode: component {component.Id} needs {size} coefficients.");

            component.Coefficients = new short[size];
        }
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;
}

/// <summary>
/// Decodes the entropy-coded segment of one scan into coefficients.
/// </summary>
internal static class JpegScanDecoder
{
    /// <summary>Maps a zigzag index to its position in a natural-order block.</summary>
    public static ReadOnlySpan<byte> ZigZag =>
    [
        0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>
    /// Reads one scan and returns the offset just past its entropy-coded data.
    /// </summary>
    public static int Decode(
        ReadOnlySpan<byte> data,
        int position,
        JpegFrame frame,
        JpegComponent[] scanComponents,
        JpegHuffmanTable?[] dcTables,
        JpegHuffmanTable?[] acTables,
        int restartInterval,
        int spectralStart,
        int spectralEnd,
        int approximationHigh,
        int approximationLow)
    {
        var reader = new JpegBitReader(data, position);
        int eobRun = 0;

        foreach (JpegComponent component in scanComponents)
            component.DcPrediction = 0;

        // A scan carrying one component is not interleaved: its blocks run in raster order over
        // that component's own grid, ignoring MCU boundaries.
        bool interleaved = scanComponents.Length > 1;

        int unitsPerLine, unitsPerColumn;
        if (interleaved)
        {
            unitsPerLine = frame.McusPerLine;
            unitsPerColumn = frame.McusPerColumn;
        }
        else
        {
            unitsPerLine = scanComponents[0].ActiveBlocksPerLine;
            unitsPerColumn = scanComponents[0].ActiveBlocksPerColumn;
        }

        int totalUnits = unitsPerLine * unitsPerColumn;
        int unitsUntilRestart = restartInterval > 0 ? restartInterval : int.MaxValue;
        int decoded = 0;

        while (decoded < totalUnits)
        {
            int batch = Math.Min(unitsUntilRestart, totalUnits - decoded);

            for (int i = 0; i < batch; i++)
            {
                int unit = decoded + i;

                if (interleaved)
                {
                    int mcuRow = unit / unitsPerLine;
                    int mcuColumn = unit % unitsPerLine;

                    foreach (JpegComponent component in scanComponents)
                    {
                        for (int v = 0; v < component.VerticalSampling; v++)
                        {
                            for (int h = 0; h < component.HorizontalSampling; h++)
                            {
                                int row = (mcuRow * component.VerticalSampling) + v;
                                int column = (mcuColumn * component.HorizontalSampling) + h;
                                DecodeBlock(ref reader, frame, component, dcTables, acTables,
                                    component.BlockOffset(row, column),
                                    spectralStart, spectralEnd, approximationHigh, approximationLow, ref eobRun);
                            }
                        }
                    }
                }
                else
                {
                    JpegComponent component = scanComponents[0];
                    int row = unit / unitsPerLine;
                    int column = unit % unitsPerLine;
                    DecodeBlock(ref reader, frame, component, dcTables, acTables,
                        component.BlockOffset(row, column),
                        spectralStart, spectralEnd, approximationHigh, approximationLow, ref eobRun);
                }
            }

            decoded += batch;

            if (decoded >= totalUnits)
                break;

            if (reader.TryConsumeRestartMarker())
            {
                // A restart resynchronises the stream: the DC predictors and any end-of-band run
                // start again from nothing.
                eobRun = 0;
                foreach (JpegComponent component in scanComponents)
                    component.DcPrediction = 0;
            }
            else if (reader.AtMarker)
            {
                // Genuinely out of entropy data. Whatever has been decoded stands.
                break;
            }

            // Otherwise the file declared a restart interval in DRI but wrote no marker here.
            // That happens in the wild — some phone cameras do it on panoramas — and the entropy
            // stream simply continues. Carrying on is what other decoders do; stopping would
            // leave the rest of the image blank.
        }

        return FindNextMarker(data, reader.Position);
    }

    private static void DecodeBlock(
        ref JpegBitReader reader,
        JpegFrame frame,
        JpegComponent component,
        JpegHuffmanTable?[] dcTables,
        JpegHuffmanTable?[] acTables,
        int offset,
        int spectralStart,
        int spectralEnd,
        int approximationHigh,
        int approximationLow,
        ref int eobRun)
    {
        Span<short> block = component.Coefficients.AsSpan(offset, 64);

        if (!frame.IsProgressive)
        {
            DecodeBaselineBlock(ref reader, component, dcTables, acTables, block);
            return;
        }

        if (spectralStart == 0)
        {
            if (approximationHigh == 0)
                DecodeDcFirst(ref reader, component, dcTables, block, approximationLow);
            else
                DecodeDcRefinement(ref reader, block, approximationLow);

            return;
        }

        if (approximationHigh == 0)
            DecodeAcFirst(ref reader, component, acTables, block, spectralStart, spectralEnd, approximationLow, ref eobRun);
        else
            DecodeAcRefinement(ref reader, component, acTables, block, spectralStart, spectralEnd, approximationLow, ref eobRun);
    }

    private static void DecodeBaselineBlock(
        ref JpegBitReader reader,
        JpegComponent component,
        JpegHuffmanTable?[] dcTables,
        JpegHuffmanTable?[] acTables,
        Span<short> block)
    {
        JpegHuffmanTable dc = Require(dcTables, component.DcTableIndex, "DC");
        JpegHuffmanTable ac = Require(acTables, component.AcTableIndex, "AC");

        int magnitude = reader.DecodeSymbol(dc);
        int diff = reader.ReceiveAndExtend(magnitude);
        component.DcPrediction += diff;
        block[0] = (short)component.DcPrediction;

        int k = 1;
        while (k < 64)
        {
            int symbol = reader.DecodeSymbol(ac);
            int run = symbol >> 4;
            int size = symbol & 0x0F;

            if (size == 0)
            {
                if (run != 15)
                    break;      // end of block

                k += 16;        // ZRL: sixteen zeroes
                continue;
            }

            k += run;
            if (k >= 64)
                break;

            block[ZigZag[k]] = (short)reader.ReceiveAndExtend(size);
            k++;
        }
    }

    private static void DecodeDcFirst(
        ref JpegBitReader reader,
        JpegComponent component,
        JpegHuffmanTable?[] dcTables,
        Span<short> block,
        int approximationLow)
    {
        JpegHuffmanTable dc = Require(dcTables, component.DcTableIndex, "DC");

        int magnitude = reader.DecodeSymbol(dc);
        component.DcPrediction += reader.ReceiveAndExtend(magnitude);
        block[0] = (short)(component.DcPrediction << approximationLow);
    }

    private static void DecodeDcRefinement(ref JpegBitReader reader, Span<short> block, int approximationLow)
    {
        if (reader.ReadBit() != 0)
            block[0] |= (short)(1 << approximationLow);
    }

    private static void DecodeAcFirst(
        ref JpegBitReader reader,
        JpegComponent component,
        JpegHuffmanTable?[] acTables,
        Span<short> block,
        int spectralStart,
        int spectralEnd,
        int approximationLow,
        ref int eobRun)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }

        JpegHuffmanTable ac = Require(acTables, component.AcTableIndex, "AC");
        int k = spectralStart;

        while (k <= spectralEnd)
        {
            int symbol = reader.DecodeSymbol(ac);
            int run = symbol >> 4;
            int size = symbol & 0x0F;

            if (size == 0)
            {
                if (run < 15)
                {
                    // End-of-band run: this block and the next (2^run - 1) + extra are finished.
                    eobRun = (1 << run) - 1;
                    if (run > 0)
                        eobRun += reader.ReadBits(run);

                    break;
                }

                k += 16;
                continue;
            }

            k += run;
            if (k > spectralEnd)
                break;

            block[ZigZag[k]] = (short)(reader.ReceiveAndExtend(size) << approximationLow);
            k++;
        }
    }

    private static void DecodeAcRefinement(
        ref JpegBitReader reader,
        JpegComponent component,
        JpegHuffmanTable?[] acTables,
        Span<short> block,
        int spectralStart,
        int spectralEnd,
        int approximationLow,
        ref int eobRun)
    {
        JpegHuffmanTable ac = Require(acTables, component.AcTableIndex, "AC");

        int positive = 1 << approximationLow;
        int negative = -1 << approximationLow;
        int k = spectralStart;

        if (eobRun == 0)
        {
            while (k <= spectralEnd)
            {
                int symbol = reader.DecodeSymbol(ac);
                int run = symbol >> 4;
                int size = symbol & 0x0F;
                int value = 0;

                if (size == 0)
                {
                    if (run < 15)
                    {
                        // The band ends here. The count covers this block as well as the ones
                        // that follow, which is why it is not decremented until the refinement
                        // pass below has run over the rest of this block.
                        eobRun = 1 << run;
                        if (run > 0)
                            eobRun += reader.ReadBits(run);

                        break;
                    }

                    // run == 15: step over sixteen zero-valued positions, still correcting any
                    // non-zero coefficients encountered on the way.
                }
                else
                {
                    value = reader.ReadBit() != 0 ? positive : negative;
                }

                // Walk forward. Coefficients already non-zero take a correction bit and do not
                // count against the run; only the zero-valued ones do.
                while (k <= spectralEnd)
                {
                    int index = ZigZag[k];
                    if (block[index] != 0)
                    {
                        RefineNonZero(ref reader, block, index, positive, negative);
                    }
                    else
                    {
                        if (run == 0)
                        {
                            if (value != 0)
                                block[index] = (short)value;

                            k++;
                            break;
                        }

                        run--;
                    }

                    k++;
                }
            }
        }

        if (eobRun > 0)
        {
            // Within an end-of-band run, the remaining coefficients of this block still receive
            // correction bits if they are already non-zero. Skipping this loop desynchronises
            // the bit stream for every block that follows.
            while (k <= spectralEnd)
            {
                int index = ZigZag[k];
                if (block[index] != 0)
                    RefineNonZero(ref reader, block, index, positive, negative);

                k++;
            }

            eobRun--;
        }
    }

    private static void RefineNonZero(ref JpegBitReader reader, Span<short> block, int index, int positive, int negative)
    {
        if (reader.ReadBit() == 0)
            return;

        // Only add the correction if this bit position is not already set.
        if ((block[index] & positive) == 0)
            block[index] += (short)(block[index] >= 0 ? positive : negative);
    }

    private static JpegHuffmanTable Require(JpegHuffmanTable?[] tables, int index, string kind)
    {
        if (index < 0 || index >= tables.Length || tables[index] is null)
            throw new JpegFormatException($"The scan references {kind} Huffman table {index}, which no DHT marker defined.");

        return tables[index]!;
    }

    /// <summary>
    /// Advances to the next real marker, stepping over stuffed bytes and fill bytes. Restart
    /// markers left over from a short scan are skipped too.
    /// </summary>
    private static int FindNextMarker(ReadOnlySpan<byte> data, int position)
    {
        while (position + 1 < data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            byte next = data[position + 1];

            if (next == 0x00 || next == 0xFF)
            {
                position++;
                continue;
            }

            if (next >= JpegMarker.Rst0 && next <= JpegMarker.Rst7)
            {
                position += 2;
                continue;
            }

            return position;
        }

        return data.Length;
    }
}
