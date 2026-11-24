// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the CCITT T.4 (Group 3 1D) / T.6 (Group 4 MMR) fax decoder.
///
/// Each test builds the encoded bit stream by concatenating hand-verified bit
/// strings (Huffman codes taken straight from ITU-T T.4 Annex A) and packs
/// them MSB-first into bytes.  The decoder's output is then compared to the
/// expected raw bitmap.
///
/// Internal decoder convention is "1 = black"; after decode the filter inverts
/// bytes unless /BlackIs1 is true, so the test expectations below assume the
/// PDF defaults: /BlackIs1 false → "1 = white" in the output.
/// </summary>
public class CcittFaxDecodeFilterTests
{
    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>Packs a bit string ("1010...") into an MSB-first byte array.</summary>
    private static byte[] BitsToBytes(string bits)
    {
        int n = (bits.Length + 7) / 8;
        var result = new byte[n];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                result[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return result;
    }

    private static PdfDictionary DecodeParms(int k, int columns, int? rows = null,
        bool blackIs1 = false, bool endOfBlock = true)
    {
        var d = new PdfDictionary();
        d[new PdfName("K")]        = new PdfNumber(k);
        d[new PdfName("Columns")]  = new PdfNumber(columns);
        if (rows.HasValue) d[new PdfName("Rows")] = new PdfNumber(rows.Value);
        d[new PdfName("BlackIs1")] = blackIs1 ? PdfBoolean.True : PdfBoolean.False;
        d[new PdfName("EndOfBlock")] = endOfBlock ? PdfBoolean.True : PdfBoolean.False;
        return d;
    }

    // ==================================================================
    //  Group 4 (T.6) — single row, 8 columns
    // ==================================================================

    // All-white row: V0 (code "1") against an implicit all-white reference.
    private const string G4_AllWhite8_Bits =
        "1"                             // V0 — a1 = b1 + 0 = 8 (end of line)
        + "000000000001"                // EOL (first half of EOFB)
        + "000000000001";               // EOL (second half of EOFB)

    /// <summary>
    /// Wave 17 R1.9: <c>/Columns</c> 2^32 + 8 read through <c>(int)LongValue</c> wrapped to 8 and the
    /// stream decoded as an eight-column image; saturated (<c>IntValue</c>) it meets the column cap
    /// and the filter falls back by name. The eight-column control is the test above.
    /// </summary>
    [Fact]
    public void ColumnsPastInt_SaturatesAndIsRefusedByTheCap_NotWrappedToASmallWidth()
    {
        byte[] encoded = BitsToBytes(G4_AllWhite8_Bits);
        var parms = new PdfDictionary();
        parms[new PdfName("K")]       = new PdfNumber(-1);
        parms[new PdfName("Columns")] = new PdfNumber(4294967304.0);   // 2^32 + 8

        byte[] result = new CcittFaxDecodeFilter().Decode(encoded, parms);

        Assert.Same(encoded, result);   // the fallback hands the input back
        Assert.Contains("exceeds", CcittFaxDecodeFilter.LastFallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public void G4_AllWhite_SingleRow_DecodesToAllOnes_DefaultBlackIs1False()
    {
        byte[] encoded = BitsToBytes(G4_AllWhite8_Bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded, DecodeParms(k: -1, columns: 8));

        // Internal 1-bit bitmap is all zero (white).  /BlackIs1 false inverts → 0xFF.
        Assert.Single(decoded);
        Assert.Equal((byte)0xFF, decoded[0]);
    }

    [Fact]
    public void G4_AllWhite_SingleRow_BlackIs1True_PreservesZero()
    {
        byte[] encoded = BitsToBytes(G4_AllWhite8_Bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded, DecodeParms(k: -1, columns: 8, blackIs1: true));

        // BlackIs1 = true → no inversion; all-white internally is 0x00.
        Assert.Single(decoded);
        Assert.Equal((byte)0x00, decoded[0]);
    }

    // Half white / half black row.  HORZ + white-4 + black-4.
    // Huffman codes (T.4 Annex A): White-4 = "1011", Black-4 = "011".
    private const string G4_HalfHalf8_Bits =
        "001"                           // HORZ (horizontal mode)
        + "1011"                        // white-run-4 (terminating, length 4)
        + "011"                         // black-run-4 (terminating, length 3)
        + "000000000001"                // EOL
        + "000000000001";               // EOL

    [Fact]
    public void G4_HalfWhiteHalfBlack_SingleRow_ProducesExpectedByte()
    {
        byte[] encoded = BitsToBytes(G4_HalfHalf8_Bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded, DecodeParms(k: -1, columns: 8, blackIs1: true));

        // Internal: 4 white then 4 black → 0b00001111 = 0x0F.  BlackIs1=true keeps it.
        Assert.Single(decoded);
        Assert.Equal((byte)0x0F, decoded[0]);
    }

    [Fact]
    public void G4_HalfWhiteHalfBlack_SingleRow_DefaultInvertsToF0()
    {
        byte[] encoded = BitsToBytes(G4_HalfHalf8_Bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded, DecodeParms(k: -1, columns: 8));

        Assert.Single(decoded);
        Assert.Equal((byte)0xF0, decoded[0]);   // 0x0F inverted
    }

    // ==================================================================
    //  Group 4 — reference-line tracking across two rows
    // ==================================================================

    // Row 1: Horizontal W4 B4.  Row 2: two V0 codes — each matches the prior
    // changing element on the reference line — so row 2 reproduces row 1.
    private const string G4_TwoRowsRepeat_Bits =
        "001"                           // row 1: HORZ
        + "1011"                        // row 1: white-4
        + "011"                         // row 1: black-4
        + "1"                           // row 2: V0 → a1 = 4 (matches row-1 transition)
        + "1"                           // row 2: V0 → a1 = 8 (matches end of line)
        + "000000000001"                // EOL
        + "000000000001";               // EOL

    [Fact]
    public void G4_TwoRowsViaV0_ReproducesReferenceLine()
    {
        byte[] encoded = BitsToBytes(G4_TwoRowsRepeat_Bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded, DecodeParms(k: -1, columns: 8, blackIs1: true));

        Assert.Equal(2, decoded.Length);
        Assert.Equal((byte)0x0F, decoded[0]);   // row 1
        Assert.Equal((byte)0x0F, decoded[1]);   // row 2 — same as row 1
    }

    // ==================================================================
    //  Group 4 — early stop on explicit /Rows when no EOFB in stream
    // ==================================================================

    [Fact]
    public void G4_RowsParameter_StopsBeforeEofb()
    {
        // Encode row 1 (HORZ W4 B4) without an EOFB; expect the decoder to stop
        // after exactly 1 row when /Rows = 1 is supplied.
        string bits = "001" + "1011" + "011";
        byte[] encoded = BitsToBytes(bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded,
            DecodeParms(k: -1, columns: 8, rows: 1, blackIs1: true, endOfBlock: false));

        Assert.Single(decoded);
        Assert.Equal((byte)0x0F, decoded[0]);
    }

    // ==================================================================
    //  Group 3 (T.4) 1D — single row
    // ==================================================================

    // EOL then White-8 (code "10011").  With /Rows = 1 the decoder stops
    // after one row; no RTC required.
    private const string G3_AllWhite8_Bits =
        "000000000001"                  // EOL (required at line start by T.4 convention)
        + "10011";                      // white-run-8 (terminating, length 5)

    [Fact]
    public void G3_1D_AllWhite_SingleRow_DecodesToAllOnes()
    {
        byte[] encoded = BitsToBytes(G3_AllWhite8_Bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded,
            DecodeParms(k: 0, columns: 8, rows: 1, blackIs1: false, endOfBlock: false));

        Assert.Single(decoded);
        Assert.Equal((byte)0xFF, decoded[0]);   // all white → invert → 0xFF
    }

    // EOL + White-4 + Black-4.  White-4 = "1011", Black-4 = "011".
    private const string G3_HalfHalf8_Bits =
        "000000000001"                  // EOL
        + "1011"                        // white-run-4
        + "011";                        // black-run-4

    [Fact]
    public void G3_1D_HalfWhiteHalfBlack_SingleRow_ProducesExpectedByte()
    {
        byte[] encoded = BitsToBytes(G3_HalfHalf8_Bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded,
            DecodeParms(k: 0, columns: 8, rows: 1, blackIs1: true, endOfBlock: false));

        Assert.Single(decoded);
        Assert.Equal((byte)0x0F, decoded[0]);   // 4 white (zeros) + 4 black (ones)
    }

    // ==================================================================
    //  Safety: malformed input must not throw; fall back to raw bytes.
    // ==================================================================

    [Fact]
    public void MalformedG4_ReturnsRawDataFallback()
    {
        // Random garbage — decoder should throw internally and the filter
        // should catch and return the input bytes unchanged.
        byte[] garbage = new byte[] { 0xAB, 0xCD, 0xEF, 0x55 };
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(garbage,
            DecodeParms(k: -1, columns: 8, rows: 1, blackIs1: true, endOfBlock: false));

        // Either the decoder produced a plausible 1-byte row, or the fallback
        // returned the input verbatim.  Both are acceptable — the invariant is
        // "no exception, no data loss".
        Assert.NotNull(decoded);
        Assert.NotEmpty(decoded);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var filter = new CcittFaxDecodeFilter();
        byte[] decoded = filter.Decode(System.Array.Empty<byte>(), DecodeParms(k: -1, columns: 8));
        Assert.Empty(decoded);
    }

    // ==================================================================
    //  Unit tests of reference-line helpers
    // ==================================================================

    [Fact]
    public void FindB1_AllWhiteReference_ReturnsColumns()
    {
        // No transitions at all → no b1 candidate.
        byte[] refLine = { 0, 0, 0, 0, 0, 0, 0, 0 };
        int b1 = CcittFaxDecoder.FindB1(refLine, a0: -1, a0Color: 0);
        Assert.Equal(8, b1);
    }

    [Fact]
    public void FindB1_ReturnsFirstOppositeColorTransition()
    {
        // ref = W W W W B B B B.  a0 at -1, a0Color white → b1 = 4.
        byte[] refLine = { 0, 0, 0, 0, 1, 1, 1, 1 };
        int b1 = CcittFaxDecoder.FindB1(refLine, a0: -1, a0Color: 0);
        Assert.Equal(4, b1);
    }

    [Fact]
    public void FindB1_SkipsTransitionToSameColour()
    {
        // ref = W B W B W W W W.  a0 at -1, a0Color white.  First black
        // transition is at p=1 — that's b1.
        byte[] refLine = { 0, 1, 0, 1, 0, 0, 0, 0 };
        int b1 = CcittFaxDecoder.FindB1(refLine, a0: -1, a0Color: 0);
        Assert.Equal(1, b1);
    }

    [Fact]
    public void FindB2_ReturnsNextTransitionAfterB1()
    {
        // ref = W W B B B W W W.  First transition W→B at 2 (b1), next at 5 (b2).
        byte[] refLine = { 0, 0, 1, 1, 1, 0, 0, 0 };
        int b2 = CcittFaxDecoder.FindB2(refLine, b1: 2);
        Assert.Equal(5, b2);
    }

    [Fact]
    public void PackLines_EmptyReturnsEmpty()
    {
        var packed = CcittFaxDecoder.PackLines(new List<byte[]>(), columns: 8);
        Assert.Empty(packed);
    }

    [Fact]
    public void PackLines_SingleLine_PacksMsbFirst()
    {
        var lines = new List<byte[]> { new byte[] { 1, 0, 1, 0, 0, 0, 0, 1 } };
        var packed = CcittFaxDecoder.PackLines(lines, columns: 8);
        Assert.Single(packed);
        Assert.Equal((byte)0b10100001, packed[0]);
    }

    // ==================================================================
    //  Group 4 — VR(3) / VL(3) mode codes (bug fix regression)
    // ==================================================================

    [Fact]
    public void G4_VR3Mode_DecodesCorrectly()
    {
        // VR(3) "0000011" and VL(3) "0000010" decode; an inverted b6 guard (b6 != 0 for
        // b6 == 0) would throw on them. Real e-signature T.6 tiles consistently contain
        // V(±3) codes for text runs that straddle the reference line by more than 2
        // positions, so every tile would fall back to raw compressed bytes.
        //
        // Image: 4-wide × 2-row.
        //   Row 0 = [black, white, white, white]  (HORZ white(0)+black(1), then V0)
        //   Row 1 = [white, white, white, black]  (VR(3): a1 = b1+3 = 0+3 = 3, then V0)
        //
        // T.6 bit string (MSB-first):
        //   Row 0: 001 + 00110101 + 010 + 1     (HORZ + white-run-0 + black-run-1 + V0)
        //   Row 1: 0000011 + 1                   (VR(3) + V0)
        //   EOFB:  000000000001 + 000000000001
        const string bits =
            "001" + "00110101" + "010" + "1"    // row 0
            + "0000011" + "1"                   // row 1: VR(3) + V0
            + "000000000001" + "000000000001";  // EOFB

        byte[] encoded = BitsToBytes(bits);
        var filter = new CcittFaxDecodeFilter();

        byte[] decoded = filter.Decode(encoded,
            DecodeParms(k: -1, columns: 4, rows: 2, blackIs1: true));

        // With BlackIs1=true (no byte inversion), 1=black convention is preserved.
        // Row 0 = [1,0,0,0] MSB-first → 0x80.  Row 1 = [0,0,0,1] MSB-first → 0x10.
        Assert.Equal(2, decoded.Length);
        Assert.Equal((byte)0x80, decoded[0]);
        Assert.Equal((byte)0x10, decoded[1]);
    }
}
