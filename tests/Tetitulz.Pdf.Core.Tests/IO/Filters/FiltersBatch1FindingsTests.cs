// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The filters sweep, batch 1: what reading <see cref="LzwDecodeFilter"/> and
/// <see cref="PredictorHelper"/> found.
/// </summary>
public class FiltersBatch1FindingsTests
{
    // ---------------------------------------------------------------- LZW

    /// <summary>
    /// Corrupt input: a code of 258 or more straight after a ClearTable names nothing. It was
    /// skipped and the decode went on to produce the 'A' after it, with no reason recorded; it
    /// stops there now, and says so, as it does for every other code the table does not hold.
    /// </summary>
    [Fact]
    public void Lzw_Corrupt_ACodePastTheTableAfterAClear_StopsAndSaysWhy()
    {
        byte[] stream = Pack9(256, 300, 'A', 257);

        byte[] decoded = new LzwDecodeFilter().Decode(stream, null);

        Assert.Empty(decoded);
        Assert.Contains("follows a ClearTable", LzwDecodeFilter.LastTruncationReason);
    }

    /// <summary>The control: ClearTable, 'A', EndOfData decodes to "A" with no reason.</summary>
    [Fact]
    public void Lzw_AByteAfterAClear_Decodes()
    {
        byte[] decoded = new LzwDecodeFilter().Decode(Pack9(256, 'A', 257), null);

        Assert.Equal(new[] { (byte)'A' }, decoded);
        Assert.Null(LzwDecodeFilter.LastTruncationReason);
    }

    /// <summary>A second ClearTable straight after the first is legal, and decoding goes on.</summary>
    [Fact]
    public void Lzw_TwoClearsInARow_AreNotCorruption()
    {
        byte[] decoded = new LzwDecodeFilter().Decode(Pack9(256, 256, 'A', 257), null);

        Assert.Equal(new[] { (byte)'A' }, decoded);
        Assert.Null(LzwDecodeFilter.LastTruncationReason);
    }

    /// <summary>Codes packed MSB first at 9 bits, the size before the table grows.</summary>
    private static byte[] Pack9(params int[] codes)
    {
        var bytes = new List<byte>();
        int acc = 0, bits = 0;
        foreach (int code in codes)
        {
            acc = (acc << 9) | code;
            bits += 9;
            while (bits >= 8) { bytes.Add((byte)(acc >> (bits - 8))); bits -= 8; acc &= (1 << bits) - 1; }
        }
        if (bits > 0) bytes.Add((byte)(acc << (8 - bits)));
        return bytes.ToArray();
    }

    // ---------------------------------------------------------------- predictors

    /// <summary>
    /// A last row shorter than the rest, 8 bits per component: decoded as far as it goes. Its
    /// bytes came back as zeros, in a buffer the input's length.
    /// </summary>
    [Fact]
    public void Tiff8_APartialLastRow_IsDecoded()
    {
        byte[] data = { 1, 1, 1, 1, 2, 2 };

        byte[] decoded = PredictorHelper.RemoveTiffPredictor(data, columns: 4, colors: 1, bitsPerComponent: 8);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 2, 4 }, decoded);
    }

    /// <summary>The same for 16 bits: the whole sample of the partial row summed, an odd byte kept.</summary>
    [Fact]
    public void Tiff16_APartialLastRow_IsDecoded()
    {
        byte[] data = { 0, 1, 0, 1, 0, 5, 7 };

        byte[] decoded = PredictorHelper.RemoveTiffPredictor(data, columns: 2, colors: 1, bitsPerComponent: 16);

        Assert.Equal(new byte[] { 0, 1, 0, 2, 0, 5, 7 }, decoded);
    }

    /// <summary>
    /// The skip reason belongs to the call: a stream with no predictor, or no parameters at all,
    /// clears what the previous one left. Both early returns skipped the reset.
    /// </summary>
    [Fact]
    public void Apply_WithNoPredictor_ClearsThePreviousReason()
    {
        PredictorHelper.RemoveTiffPredictor(new byte[4], columns: 0, colors: 1, bitsPerComponent: 8);
        Assert.NotNull(PredictorHelper.LastPredictorSkipReason);

        PredictorHelper.Apply(new byte[4], null);
        Assert.Null(PredictorHelper.LastPredictorSkipReason);

        PredictorHelper.RemoveTiffPredictor(new byte[4], columns: 0, colors: 1, bitsPerComponent: 8);
        var parameters = new PdfDictionary();
        parameters.Set(PdfName.Predictor, new PdfNumber(1));
        PredictorHelper.Apply(new byte[4], parameters);
        Assert.Null(PredictorHelper.LastPredictorSkipReason);
    }

    // ---------------------------------------------------------------- CCITT

    /// <summary>
    /// A line whose black run cannot be resolved is padded white, as the doc comment says. The
    /// existing recovery test fails on the first run, which is white either way; this one fails
    /// on the second, where padding in the current colour paints the rest of the row black.
    /// </summary>
    [Fact]
    public void Ccitt_ARecoveryAfterAWhiteRun_PadsWhiteNotBlack()
    {
        // W4 ("1011"), then zeros: no black code resolves.
        byte[] packed = CcittFaxDecoder.DecodeGroup3_1D(new byte[] { 0xB0, 0, 0, 0 }, columns: 8, rows: 1,
            endOfBlock: false, encodedByteAlign: false, requireEol: false, out int rows);

        Assert.Equal(1, rows);
        Assert.NotNull(CcittFaxDecoder.LastRowRecovered);
        Assert.Equal(new byte[] { 0x00 }, packed);   // 1 is black: nothing painted
    }

    /// <summary>
    /// Mixed T.4 with /K 2 and three lines: one 1D, then two 2D. /K 2 permits one 2D line in a
    /// row, and the decoder read the second as 1D despite its tag, which garbled it. Producers
    /// write /K loosely; the tag bit is what the encoder used, and it decides now.
    /// </summary>
    [Fact]
    public void Ccitt_G3Mixed_TheTagBitDecidesPastK()
    {
        string bits =
            "000000000001" + "1" + "1011" + "011"     // 1D: W4 B4
            + "000000000001" + "0" + "1" + "1"       // 2D: V0 V0
            + "000000000001" + "0" + "1" + "1"       // 2D again, past /K 2's one
            + string.Concat(System.Linq.Enumerable.Repeat("000000000001", 6));

        var parms = new PdfDictionary();
        parms.Set(PdfName.K, new PdfNumber(2));
        parms.Set(PdfName.Columns, new PdfNumber(8));
        parms.Set(PdfName.BlackIs1, PdfBoolean.True);
        parms.Set(PdfName.EndOfBlock, PdfBoolean.True);

        byte[] decoded = new CcittFaxDecodeFilter().Decode(Bits(bits), parms);

        Assert.Equal(new byte[] { 0x0F, 0x0F, 0x0F }, decoded);
    }

    private static byte[] Bits(string bits)
    {
        var result = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i] == '1') result[i / 8] |= (byte)(0x80 >> (i % 8));
        return result;
    }
}

/// <summary>The CCITT pixel budget bounds the row count, settable through <see cref="PdfLimits"/>.</summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public class CcittPixelBudgetTests
{
    /// <summary>
    /// Hostile /Rows: 5 rows of 8 columns asked for under a 24-pixel budget. The decode stops at 3
    /// rows, the budget's worth; the control under the default budget decodes all 5.
    /// </summary>
    [Theory]
    [InlineData(24L, 3)]
    [InlineData(-1L, 5)]
    public void Ccitt_TheRowCountIsBoundedByThePixelBudget(long pixels, int expectedRows)
    {
        try
        {
            if (pixels > 0) PdfLimits.MaxCcittPixels = pixels;

            // All-white G4: V0 against the white reference, one bit a row.
            CcittFaxDecoder.DecodeGroup4(new byte[] { 0xFF }, columns: 8, rows: 5, endOfBlock: false, out int rows);

            Assert.Equal(expectedRows, rows);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
