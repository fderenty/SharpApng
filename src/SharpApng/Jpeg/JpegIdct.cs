namespace SharpApng;

/// <summary>
/// Integer inverse discrete cosine transform for one 8×8 block, including dequantisation and the
/// level shift back to unsigned samples.
/// </summary>
/// <remarks>
/// <para>
/// A separable two-pass transform in fixed point, following the well-known Loeffler, Ligtenberg
/// and Moschytz factorisation: rows first at higher precision, then columns with the descaling
/// folded into the final shift.
/// </para>
/// <para>
/// The specification permits implementations to differ slightly here, and they do — libjpeg,
/// libjpeg-turbo and Pillow do not agree byte for byte with one another. Comparisons against
/// another decoder must therefore allow a tolerance rather than demand equality.
/// </para>
/// </remarks>
internal static class JpegIdct
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;

    // Cosine terms scaled by 2^13 and rounded.
    private const int Fix0_298631336 = 2446;
    private const int Fix0_390180644 = 3196;
    private const int Fix0_541196100 = 4433;
    private const int Fix0_765366865 = 6270;
    private const int Fix0_899976223 = 7373;
    private const int Fix1_175875602 = 9633;
    private const int Fix1_501321110 = 12299;
    private const int Fix1_847759065 = 15137;
    private const int Fix1_961570560 = 16069;
    private const int Fix2_053119869 = 16819;
    private const int Fix2_562915447 = 20995;
    private const int Fix3_072711026 = 25172;

    /// <summary>
    /// Dequantises a block of coefficients and transforms it into 8×8 samples.
    /// </summary>
    /// <param name="coefficients">64 coefficients in natural (de-zigzagged) order.</param>
    /// <param name="quantisation">64 quantisation factors in the same order.</param>
    /// <param name="output">Destination for the 8×8 samples.</param>
    /// <param name="outputOffset">Index of the block's top-left sample in <paramref name="output"/>.</param>
    /// <param name="outputStride">Distance in samples between consecutive output rows.</param>
    public static void Transform(
        ReadOnlySpan<short> coefficients,
        ReadOnlySpan<ushort> quantisation,
        Span<byte> output,
        int outputOffset,
        int outputStride)
    {
        Span<int> workspace = stackalloc int[64];

        // Pass one: columns.
        for (int column = 0; column < 8; column++)
        {
            // A column whose only non-zero coefficient is the DC term is very common in smooth
            // areas; the result is a constant, so skip the butterflies entirely.
            if (coefficients[column + 8] == 0 && coefficients[column + 16] == 0
                && coefficients[column + 24] == 0 && coefficients[column + 32] == 0
                && coefficients[column + 40] == 0 && coefficients[column + 48] == 0
                && coefficients[column + 56] == 0)
            {
                int flat = (coefficients[column] * quantisation[column]) << Pass1Bits;
                for (int row = 0; row < 8; row++)
                    workspace[column + (row * 8)] = flat;

                continue;
            }

            int z2 = coefficients[column + 16] * quantisation[column + 16];
            int z3 = coefficients[column + 48] * quantisation[column + 48];

            int z1 = (z2 + z3) * Fix0_541196100;
            int tmp2 = z1 + (z3 * -Fix1_847759065);
            int tmp3 = z1 + (z2 * Fix0_765366865);

            z2 = coefficients[column] * quantisation[column];
            z3 = coefficients[column + 32] * quantisation[column + 32];

            int tmp0 = (z2 + z3) << ConstBits;
            int tmp1 = (z2 - z3) << ConstBits;

            int t10 = tmp0 + tmp3;
            int t13 = tmp0 - tmp3;
            int t11 = tmp1 + tmp2;
            int t12 = tmp1 - tmp2;

            tmp0 = coefficients[column + 56] * quantisation[column + 56];
            tmp1 = coefficients[column + 40] * quantisation[column + 40];
            tmp2 = coefficients[column + 24] * quantisation[column + 24];
            tmp3 = coefficients[column + 8] * quantisation[column + 8];

            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            int z4 = tmp1 + tmp3;
            int z5 = (z3 + z4) * Fix1_175875602;

            tmp0 *= Fix0_298631336;
            tmp1 *= Fix2_053119869;
            tmp2 *= Fix3_072711026;
            tmp3 *= Fix1_501321110;
            z1 *= -Fix0_899976223;
            z2 *= -Fix2_562915447;
            z3 = (z3 * -Fix1_961570560) + z5;
            z4 = (z4 * -Fix0_390180644) + z5;

            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;

            workspace[column] = Descale(t10 + tmp3, ConstBits - Pass1Bits);
            workspace[column + 56] = Descale(t10 - tmp3, ConstBits - Pass1Bits);
            workspace[column + 8] = Descale(t11 + tmp2, ConstBits - Pass1Bits);
            workspace[column + 48] = Descale(t11 - tmp2, ConstBits - Pass1Bits);
            workspace[column + 16] = Descale(t12 + tmp1, ConstBits - Pass1Bits);
            workspace[column + 40] = Descale(t12 - tmp1, ConstBits - Pass1Bits);
            workspace[column + 24] = Descale(t13 + tmp0, ConstBits - Pass1Bits);
            workspace[column + 32] = Descale(t13 - tmp0, ConstBits - Pass1Bits);
        }

        // Pass two: rows, writing out clamped samples.
        for (int row = 0; row < 8; row++)
        {
            int offset = row * 8;
            int destination = outputOffset + (row * outputStride);

            int z2 = workspace[offset + 2];
            int z3 = workspace[offset + 6];

            int z1 = (z2 + z3) * Fix0_541196100;
            int tmp2 = z1 + (z3 * -Fix1_847759065);
            int tmp3 = z1 + (z2 * Fix0_765366865);

            int tmp0 = (workspace[offset] + workspace[offset + 4]) << ConstBits;
            int tmp1 = (workspace[offset] - workspace[offset + 4]) << ConstBits;

            int t10 = tmp0 + tmp3;
            int t13 = tmp0 - tmp3;
            int t11 = tmp1 + tmp2;
            int t12 = tmp1 - tmp2;

            tmp0 = workspace[offset + 7];
            tmp1 = workspace[offset + 5];
            tmp2 = workspace[offset + 3];
            tmp3 = workspace[offset + 1];

            z1 = tmp0 + tmp3;
            z2 = tmp1 + tmp2;
            z3 = tmp0 + tmp2;
            int z4 = tmp1 + tmp3;
            int z5 = (z3 + z4) * Fix1_175875602;

            tmp0 *= Fix0_298631336;
            tmp1 *= Fix2_053119869;
            tmp2 *= Fix3_072711026;
            tmp3 *= Fix1_501321110;
            z1 *= -Fix0_899976223;
            z2 *= -Fix2_562915447;
            z3 = (z3 * -Fix1_961570560) + z5;
            z4 = (z4 * -Fix0_390180644) + z5;

            tmp0 += z1 + z3;
            tmp1 += z2 + z4;
            tmp2 += z2 + z3;
            tmp3 += z1 + z4;

            output[destination] = Sample(t10 + tmp3);
            output[destination + 7] = Sample(t10 - tmp3);
            output[destination + 1] = Sample(t11 + tmp2);
            output[destination + 6] = Sample(t11 - tmp2);
            output[destination + 2] = Sample(t12 + tmp1);
            output[destination + 5] = Sample(t12 - tmp1);
            output[destination + 3] = Sample(t13 + tmp0);
            output[destination + 4] = Sample(t13 - tmp0);
        }
    }

    private static int Descale(int value, int bits) => (value + (1 << (bits - 1))) >> bits;

    /// <summary>
    /// Descales a second-pass result, undoes the level shift, and saturates. Clamping matters:
    /// truncating with a cast would wrap an overshoot to the opposite end of the range and
    /// speckle sharp edges.
    /// </summary>
    private static byte Sample(int value)
    {
        int result = Descale(value, ConstBits + Pass1Bits + 3) + 128;
        return result <= 0 ? (byte)0 : result >= 255 ? (byte)255 : (byte)result;
    }
}
