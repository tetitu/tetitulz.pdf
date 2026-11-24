// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for multi-instance aggregation in symbol dictionaries and
/// user-defined Huffman tables (segment type 53).
/// </summary>
public class Jbig2AggregationAndTablesTests
{
    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p?.Length ?? 0;
        var r = new byte[total];
        int off = 0;
        foreach (var p in parts)
        {
            if (p == null || p.Length == 0) continue;
            Buffer.BlockCopy(p, 0, r, off, p.Length);
            off += p.Length;
        }
        return r;
    }

    private static byte[] SegmentHeader(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength)
    {
        return new byte[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),  (byte)(segmentNumber & 0xFF),
            (byte)(segmentType & 0x3F),
            0x00,
            (byte)(pageAssociation & 0xFF),
            (byte)(dataLength >> 24), (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),  (byte)(dataLength & 0xFF),
        };
    }

    // ==================================================================
    //  User-defined Huffman tables
    // ==================================================================

    [Fact]
    public void Tables_Segment_IsClassifiedAsBenignWithDescriptiveNote()
    {
        // Minimal Tables segment body: flags(1) + HTLOW(4) + HTHIGH(4).
        // Use HTLOW == HTHIGH so the row-reading loop terminates immediately.
        var body = new byte[1 + 4 + 4];
        // flags = 0 (no tails, PS=1, RS=1)
        // HTLOW = 0 (all zeros)
        // HTHIGH = 0
        var stream = Concat(
            SegmentHeader(0, 53, 0, (uint)body.Length), body,
            SegmentHeader(1, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[0].Status);
        Assert.Contains("user-defined", diag[0].Notes);
        Assert.Equal("HuffmanTables", diag[0].TypeName);
    }

    /// <summary>
    /// Packs (value, bitWidth) pairs MSB-first into bytes, zero-padding the final byte — the same
    /// convention <c>MsbBitStream</c> reads, so a hand-derived bit layout can be built directly
    /// from field widths instead of pre-computed hex.
    /// </summary>
    private static byte[] PackBits(params (int value, int width)[] fields)
    {
        var bits = new System.Collections.Generic.List<bool>();
        foreach (var (value, width) in fields)
            for (int i = width - 1; i >= 0; i--)
                bits.Add(((value >> i) & 1) != 0);

        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    /// <summary>
    /// Verifies the corrected flags-byte layout and mandatory-row structure (B.2 6-10) end to end,
    /// against hand-derived canonical codes — not just that parsing succeeds. Four rows: one
    /// regular row (PrefixLen 1), the mandatory lower-range line (PrefixLen 2), the mandatory
    /// upper-range line (PrefixLen 3) and, because HTOOB is set, an out-of-band line (PrefixLen 3,
    /// the second code of that length). <c>AssignCanonicalPrefixes</c> assigns codes "0", "10",
    /// "110", "111" respectively — the same canonical procedure already pinned for the standard
    /// tables, applied here to prove the user-defined path reaches it with the right PREFLENs.
    /// </summary>
    [Fact]
    public void ParseUserDefinedHuffmanTable_DecodesAllFourRowKinds_WithTheCorrectedFlagsLayout()
    {
        // flags: bit0=HTOOB(1), bits1-3=HTPS-1(1 -> HTPS=2), bits4-6=HTRS-1(0 -> HTRS=1).
        byte flags = 0x03;
        int htLow = 0, htHigh = 2;
        byte[] header = Concat(new[] { flags },
            Be32(htLow), Be32(htHigh));

        // Row bits, HTPS=2 / HTRS=1 throughout:
        //   regular row:      PREFLEN=1 (01), RANGELEN=1 (1)  -> currentRangeLow 0 -> 2, loop ends
        //   lower-range line: PREFLEN=2 (10)
        //   upper-range line: PREFLEN=3 (11)
        //   OOB line:         PREFLEN=3 (11)
        byte[] rows = PackBits((1, 2), (1, 1), (2, 2), (3, 2), (3, 2));

        var body = Concat(header, rows);
        var table = Jbig2Decoder.ParseUserDefinedHuffmanTable(body);

        Assert.NotNull(table);
        Assert.Equal(4, table.Entries.Length);

        // "0" (1 bit) -> the regular row: RangeLow 0, 1 extra bit. Bit stream "0 1" decodes 0+1=1.
        Assert.Equal(1, table.Decode(new MsbBitStream(PackBits((0, 1), (1, 1)))));

        // "10" -> the lower-range line: RangeLow (htLow-1)=-1, counts down, 32 extra bits (all 0
        // past EOF here) -> -1 - 0 = -1.
        Assert.Equal(-1, table.Decode(new MsbBitStream(PackBits((0b10, 2)))));

        // "110" -> the upper-range line: RangeLow htHigh=2, ascending, 32 extra bits (0) -> 2.
        Assert.Equal(2, table.Decode(new MsbBitStream(PackBits((0b110, 3)))));

        // "111" -> the OOB line.
        Assert.Equal(Jbig2Decoder.Jbig2HuffmanTable.OOB,
            table.Decode(new MsbBitStream(PackBits((0b111, 3)))));
    }

    private static byte[] Be32(int v) => new[]
    {
        (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
    };

    [Fact]
    public void ParseUserDefinedHuffmanTable_MalformedInput_ReturnsNull()
    {
        // Truncated body — too short for even the fixed-size header fields.
        var body = new byte[4];
        var result = Jbig2Decoder.ParseUserDefinedHuffmanTable(body);
        Assert.Null(result);
    }

    /// <summary>
    /// flags=0, HTLOW=HTHIGH=0 → the regular-row loop (B.2 5) never runs, but the lower-range
    /// (B.2 6-7) and upper-range (B.2 8-9) lines are <b>mandatory</b>, not gated by any flag — a
    /// real Tables segment always carries at least these two — not an <i>empty</i> table, which
    /// would treat flag bit 0 as an optional "low tail" and a non-existent bit 1 as an optional
    /// "high tail".
    /// </summary>
    [Fact]
    public void ParseUserDefinedHuffmanTable_EmptyRange_StillHasTheMandatoryLowerAndUpperRangeLines()
    {
        var body = new byte[] { 0x00, 0, 0, 0, 0, 0, 0, 0, 0 };
        var result = Jbig2Decoder.ParseUserDefinedHuffmanTable(body);
        Assert.NotNull(result);
        Assert.Equal(2, result.Entries.Length);
    }

    // ==================================================================
    //  Multi-instance aggregation — doesn't crash when triggered
    // ==================================================================

    private static byte[] PageInfoBody(uint width, uint height)
    {
        var body = new byte[19];
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        return body;
    }

    /// <summary>
    /// The SDREFAGG=1 path must reach the aggregation decoder and, when the arithmetic body does
    /// not describe a usable symbol, hand back the <b>input unchanged</b> and say why — never a
    /// half-filled buffer a caller would mistake for pixels.
    ///
    /// <para>Both facts are asserted against a control: the same stream with the symbol dictionary
    /// removed decodes to a 3-byte × 8-row page bitmap with
    /// <see cref="Jbig2DecodeFilter.LastFallbackReason"/> null. That contrast is what makes the
    /// passthrough claim mean something — it pins the returned length to the compressed input's
    /// rather than the bitmap's, and pins the reason slot to being set by <i>this</i> call.</para>
    ///
    /// <para><b>Note on the fixture, which the predecessor's <c>Assert.NotNull</c> concealed.</b>
    /// Its bytes cannot decode: the arithmetic integer decoder reads absurd symbol dimensions out
    /// of the 16 filler bytes and the decode is abandoned. Asserting a decoded bitmap here would
    /// need a genuinely MQ-encoded symbol dictionary, which this synthetic builder cannot make.
    /// What is testable — and what a host actually depends on — is that the failure is reported
    /// instead of hidden.</para>
    /// </summary>
    [Fact]
    public void SymbolDict_WithAggregation_ReportsFallbackInsteadOfReturningAPartialBitmap()
    {
        // Symbol dictionary with SDREFAGG=1 + arithmetic coding.  Flags bytes:
        //   SDFLAGS word = 0x0002 (SDREFAGG=1, everything else default).
        // Then 8 AT bytes + 4 refinement AT bytes + 4 SDNUMEXSYMS + 4 SDNUMNEWSYMS + arith body.
        var body = new byte[2 + 8 + 4 + 4 + 4 + 16];
        body[0] = 0x00; body[1] = 0x02;   // SDFLAGS: SDREFAGG=1
        // SDNUMEXSYMS = 1, SDNUMNEWSYMS = 1
        int nexOff = 2 + 8 + 4;
        body[nexOff]     = 0; body[nexOff + 1] = 0;
        body[nexOff + 2] = 0; body[nexOff + 3] = 1;
        int nnewOff = nexOff + 4;
        body[nnewOff]     = 0; body[nnewOff + 1] = 0;
        body[nnewOff + 2] = 0; body[nnewOff + 3] = 1;

        const uint pageW = 24, pageH = 8;
        int bitmapBytes = (int)((pageW + 7) / 8 * pageH);

        // Control: page information alone decodes to the declared page, reason slot clear.
        var pageOnly = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(pageW, pageH),
            SegmentHeader(1, 51, 0, 0));
        byte[] control = new Jbig2DecodeFilter().Decode(pageOnly, parameters: null);
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Equal(bitmapBytes, control.Length);

        // Same stream plus the aggregating symbol dictionary.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(pageW, pageH),
            SegmentHeader(1,  0, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));
        byte[] decoded = new Jbig2DecodeFilter().Decode(stream, parameters: null);

        string reason = Jbig2DecodeFilter.LastFallbackReason;
        Assert.False(string.IsNullOrEmpty(reason),
            "an abandoned decode must announce itself, or the caller paints the compressed bytes");
        Assert.Contains("Exception", reason, StringComparison.Ordinal);

        // The whole input comes back, byte for byte — not a truncated or partly-written page.
        Assert.Equal(stream, decoded);
        Assert.NotEqual(bitmapBytes, decoded.Length);
    }

    // ==================================================================
    //  Comprehensive assessment — all JBIG2 segment types present
    // ==================================================================

    [Fact]
    public void Assess_EverySegmentType_IsFullyDecodable()
    {
        // Build a stream with one of each segment type we can now classify as
        // Decoded or SkippedBenign.  Aim: no Unsupported classifications.
        var stream = Concat(
            SegmentHeader(0,  48, 1, 19), new byte[19],   // page info
            SegmentHeader(1,   0, 1, 20), new byte[20],   // symbol dictionary
            SegmentHeader(2,  16, 1, 10), new byte[10],   // pattern dictionary
            SegmentHeader(3,  53, 0,  9), new byte[9],    // user-defined tables
            SegmentHeader(4,   6, 1, 40), new byte[40],   // text region (immediate)
            SegmentHeader(5,  22, 1, 40), new byte[40],   // halftone region
            SegmentHeader(6,  42, 1, 30), new byte[30],   // refinement region
            SegmentHeader(7,  38, 1, 30), new byte[30],   // generic region
            SegmentHeader(8,  49, 1,  0),                 // end of page
            SegmentHeader(9,  51, 0,  0));                // end of file

        var asm = Jbig2Decoder.Assess(stream);
        Assert.Equal(0, asm.UnsupportedSegmentCount);
        Assert.True(asm.IsFullyDecodable);
    }
}
