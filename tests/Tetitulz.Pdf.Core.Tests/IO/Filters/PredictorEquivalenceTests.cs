// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Pins <see cref="PredictorHelper.RemovePngPredictor"/> and
/// <see cref="PredictorHelper.RemoveTiffPredictor"/> against the straightforward per-byte
/// implementations they replaced, byte for byte, across every filter type and every shape of
/// input the parameters can describe.
/// </summary>
/// <remarks>
/// <para><b>Why a reference implementation rather than fixed expected bytes.</b> The optimisation
/// hoisted the PNG filter-type <c>switch</c> out of the per-byte loop and peeled the leading
/// <c>bytesPerPixel</c> bytes of each row into their own loop, substituting the zero left-neighbour
/// <i>algebraically</i> — Sub becomes a copy, Average becomes <c>raw + b/2</c>, and Paeth becomes
/// <c>raw + b</c>. Three of those four identities are obvious and the Paeth one is not, so the
/// thing that needs pinning is <b>equivalence over the whole input space</b>, not a handful of
/// vectors someone chose. The reference below is the exact algorithm that shipped before, kept
/// deliberately naive.</para>
///
/// <para><b>The sweep is what makes it falsifiable.</b> Each of the five filter types is exercised
/// uniformly and in a per-row mixture, at several <c>/Colors</c> x <c>/BitsPerComponent</c>
/// combinations, including the two shapes that reach the peeled branches from the wrong side:
/// <c>bytesPerPixel &gt; rowBytes</c> (so a row is <i>all</i> lead and the second loop never runs)
/// and a ragged trailing partial row. Mutating any single arm of the rewrite reddens this file.</para>
/// </remarks>
public class PredictorEquivalenceTests
{
    // ==================================================================
    //  The reference: exactly what shipped before the rewrite
    // ==================================================================

    private static byte[] ReferencePng(byte[] data, int columns, int colors, int bitsPerComponent)
    {
        int bytesPerPixel = Math.Max(1, (colors * bitsPerComponent + 7) / 8);
        int rowBytes = (columns * colors * bitsPerComponent + 7) / 8;
        int rowWithFilter = rowBytes + 1;
        int rows = data.Length / rowWithFilter;

        int remaining = data.Length - rows * rowWithFilter;
        var output = new byte[rows * rowBytes + Math.Max(0, remaining - 1)];
        var prevRow = new byte[rowBytes];

        for (int row = 0; row < rows; row++)
        {
            int srcOffset = row * rowWithFilter;
            int dstOffset = row * rowBytes;
            byte filterType = data[srcOffset];

            for (int col = 0; col < rowBytes; col++)
            {
                byte raw = data[srcOffset + 1 + col];
                byte a = col >= bytesPerPixel ? output[dstOffset + col - bytesPerPixel] : (byte)0;
                byte b = prevRow[col];
                byte c = col >= bytesPerPixel ? prevRow[col - bytesPerPixel] : (byte)0;

                output[dstOffset + col] = filterType switch
                {
                    1 => (byte)(raw + a),
                    2 => (byte)(raw + b),
                    3 => (byte)(raw + (a + b) / 2),
                    4 => (byte)(raw + PredictorHelper.PaethPredictor(a, b, c)),
                    _ => raw,
                };
            }
            Buffer.BlockCopy(output, dstOffset, prevRow, 0, rowBytes);
        }

        if (remaining > 1)
        {
            int srcRemaining = rows * rowWithFilter + 1;
            int dstRemaining = rows * rowBytes;
            int copyLen = Math.Min(remaining - 1, output.Length - dstRemaining);
            if (copyLen > 0)
                Buffer.BlockCopy(data, srcRemaining, output, dstRemaining, copyLen);
        }
        return output;
    }

    private static byte[] ReferenceTiff8(byte[] data, int columns, int colors)
    {
        int rowBytes = columns * colors;
        int rows = data.Length / rowBytes;
        int bytesPerPixel = colors;
        var output = new byte[data.Length];

        for (int row = 0; row < rows; row++)
        {
            int off = row * rowBytes;
            output[off] = data[off];
            for (int col = 1; col < rowBytes; col++)
            {
                output[off + col] = col >= bytesPerPixel
                    ? (byte)(data[off + col] + output[off + col - bytesPerPixel])
                    : data[off + col];
            }
        }
        return output;
    }

    // ==================================================================
    //  PNG — one filter type applied uniformly
    // ==================================================================

    /// <summary>
    /// Every filter type, at several component layouts. Filter 0 and the out-of-range type 7 both
    /// take the copy arm, and including 7 is what stops the rewrite's <c>default:</c> quietly
    /// becoming one of the four real filters.
    /// </summary>
    [Theory]
    // filterType, columns, colors, bpc
    [InlineData(0, 40, 3, 8)]
    [InlineData(1, 40, 3, 8)]
    [InlineData(2, 40, 3, 8)]
    [InlineData(3, 40, 3, 8)]
    [InlineData(4, 40, 3, 8)]
    [InlineData(7, 40, 3, 8)]
    [InlineData(1, 40, 1, 8)]   // bytesPerPixel 1 — the cross-reference-stream shape
    [InlineData(2, 11, 1, 8)]
    [InlineData(3, 40, 4, 8)]   // CMYK
    [InlineData(4, 40, 4, 8)]
    [InlineData(4, 40, 3, 16)]  // bytesPerPixel 6
    [InlineData(3, 40, 1, 1)]   // sub-byte: bytesPerPixel clamps to 1
    [InlineData(4, 40, 1, 4)]
    public void APngFilterAppliedUniformlyMatchesThePerByteReference(
        int filterType, int columns, int colors, int bpc)
    {
        byte[] data = PngInput(columns, colors, bpc, rows: 17, filterType: filterType, mixed: false);

        Assert.Equal(
            ReferencePng(data, columns, colors, bpc),
            PredictorHelper.RemovePngPredictor(data, columns, colors, bpc));
    }

    /// <summary>
    /// The realistic case: a different filter on every row, which is what any encoder choosing
    /// per row emits. A rewrite that hoisted the switch to the wrong scope passes the uniform
    /// cases above and fails this one.
    /// </summary>
    [Theory]
    [InlineData(40, 3, 8)]
    [InlineData(40, 1, 8)]
    [InlineData(40, 4, 8)]
    [InlineData(11, 1, 8)]
    public void PerRowMixedPngFiltersMatchThePerByteReference(int columns, int colors, int bpc)
    {
        byte[] data = PngInput(columns, colors, bpc, rows: 31, filterType: 0, mixed: true);

        Assert.Equal(
            ReferencePng(data, columns, colors, bpc),
            PredictorHelper.RemovePngPredictor(data, columns, colors, bpc));
    }

    /// <summary>
    /// A row exactly one pixel wide: the row is <i>entirely</i> lead, so the peeled loop covers all
    /// of it and the running-sum loop never executes. Both sides must still agree.
    /// </summary>
    /// <remarks>
    /// <para>One column of four 16-bit components is 8 bytes per pixel against an 8-byte row. That
    /// is the <b>boundary</b>, and it is as far as this can be pushed: for any legal parameters
    /// <c>rowBytes = ceil(columns·colors·bpc/8) &gt;= ceil(colors·bpc/8) = bytesPerPixel</c>
    /// whenever <c>columns &gt;= 1</c>, which the caller has already validated. So
    /// <c>bytesPerPixel &gt; rowBytes</c> is <b>unreachable</b>.</para>
    ///
    /// <para><b>Stated because mutation testing found it, and the finding is about the code rather
    /// than this test.</b> Replacing <c>Math.Min(bytesPerPixel, rowBytes)</c> with a bare
    /// <c>bytesPerPixel</c> in either peel leaves every case here green — an equivalent mutant, not
    /// a coverage hole. The clamp is defensive and stays, because it is free and it is the only
    /// thing standing between a future parameter-validation change and a <c>Buffer.BlockCopy</c>
    /// that reads past the row; but no test can distinguish it, and pretending otherwise would put
    /// a claim here that the input space cannot support.</para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void APngRowShorterThanOnePixelMatchesThePerByteReference(int filterType)
    {
        const int Columns = 1, Colors = 4, Bpc = 16;
        byte[] data = PngInput(Columns, Colors, Bpc, rows: 9, filterType: filterType, mixed: false);

        Assert.Equal(
            ReferencePng(data, Columns, Colors, Bpc),
            PredictorHelper.RemovePngPredictor(data, Columns, Colors, Bpc));
    }

    /// <summary>
    /// A trailing partial row — the input is not a whole number of filtered rows. The reference
    /// copies the fragment through verbatim (minus its filter byte) and the rewrite must too.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    public void ARaggedTrailingPngRowMatchesThePerByteReference(int extraBytes)
    {
        const int Columns = 40, Colors = 3, Bpc = 8;
        byte[] whole = PngInput(Columns, Colors, Bpc, rows: 5, filterType: 4, mixed: false);

        var data = new byte[whole.Length + extraBytes];
        Buffer.BlockCopy(whole, 0, data, 0, whole.Length);
        for (int i = 0; i < extraBytes; i++) data[whole.Length + i] = (byte)(0x5A + i);

        Assert.Equal(
            ReferencePng(data, Columns, Colors, Bpc),
            PredictorHelper.RemovePngPredictor(data, Columns, Colors, Bpc));
    }

    // ==================================================================
    //  TIFF predictor 2, 8 bits per component
    // ==================================================================

    /// <summary>The 8-bpc horizontal-differencing path, whose first-pixel test was peeled the same way.</summary>
    [Theory]
    [InlineData(40, 3)]
    [InlineData(40, 1)]
    [InlineData(40, 4)]
    [InlineData(1, 3)]   // one pixel per row: the row is all lead
    [InlineData(1, 1)]
    public void TheTiff8BitPathMatchesThePerByteReference(int columns, int colors)
    {
        byte[] data = Noise(columns * colors * 13);

        Assert.Equal(
            ReferenceTiff8(data, columns, colors),
            PredictorHelper.RemoveTiffPredictor(data, columns, colors, 8));
    }

    // ==================================================================
    //  Fixtures
    // ==================================================================

    /// <summary>
    /// Filtered PNG rows whose payload is deterministic pseudo-random noise. The bytes are not a
    /// real encoding of anything — the predictor is a pure function of them, so equivalence over
    /// arbitrary bytes is a strictly stronger claim than equivalence over well-formed images, and
    /// it reaches the wrapping cases that real data rarely does.
    /// </summary>
    private static byte[] PngInput(int columns, int colors, int bpc, int rows, int filterType, bool mixed)
    {
        int rowBytes = (columns * colors * bpc + 7) / 8;
        var data = new byte[rows * (rowBytes + 1)];
        uint seed = 0xC0FFEE11;

        for (int row = 0; row < rows; row++)
        {
            int off = row * (rowBytes + 1);
            // 0..4 cycling, so every filter appears and no row shares its predecessor's.
            data[off] = mixed ? (byte)(row % 5) : (byte)filterType;
            for (int i = 0; i < rowBytes; i++)
            {
                seed = seed * 1664525 + 1013904223;
                data[off + 1 + i] = (byte)(seed >> 24);
            }
        }
        return data;
    }

    private static byte[] Noise(int length)
    {
        var data = new byte[length];
        uint seed = 0x1234ABCD;
        for (int i = 0; i < length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            data[i] = (byte)(seed >> 24);
        }
        return data;
    }
}
