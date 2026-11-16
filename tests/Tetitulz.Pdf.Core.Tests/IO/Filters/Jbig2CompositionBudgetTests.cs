// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Adversarial-input guard for the JBIG2 aggregate composition budget.
///
/// <para><c>Jbig2Bitmap</c>'s dimension check caps each individual bitmap at 64 Mi pixels, and
/// its comment claimed that "bounds the work". It bounds one <b>allocation</b>. It does not
/// bound the <b>composition</b>: the halftone grid loop runs HGW×HGH cells and calls
/// <c>DrawAt</c> per cell, and <c>DrawAt</c> walked its source unconditionally. A pattern
/// dictionary of 255×255 cells (65 025 px — passes) followed by a halftone region with an
/// 8000×8000 grid (64 Mi cells — exactly at the cap, also passes) composes ~4.2×10<sup>12</sup>
/// pixel operations out of roughly a hundred bytes of segment bodies, and the MQ decoder pads
/// past end-of-data with 0xFF rather than reporting EOF, so short input does not stop it. The
/// text-region path amplifies the same way through instance count × symbol area.</para>
///
/// <para>Every assertion below is the guard's own evidence — an exact budget charge, a typed
/// <see cref="WorkBudgetExceededException"/>, or the filter's reported fallback reason. None is
/// a wall-clock measurement.</para>
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]   // one test lowers the composition budget and restores it
public class Jbig2CompositionBudgetTests
{
    // ==================================================================
    //  DrawAt — clipped charge, and the empty-destination short circuit
    // ==================================================================

    [Fact]
    public void DrawAt_SourceEntirelyOffTheDestination_ChargesOnlyTheCallItself()
    {
        // The amplification the halftone grid exploits: a cell whose pattern lands entirely
        // off-page used to cost its full source area anyway, because the loop bounds-checked
        // each destination pixel instead of clipping the source rectangle first.
        var page = new Jbig2Decoder.Jbig2Bitmap(64, 64);
        var pattern = new Jbig2Decoder.Jbig2Bitmap(1000, 1000);
        var budget = new WorkBudget(1_000_000, "test");

        page.DrawAt(pattern, -5000, -5000, 0, budget);

        // One unit for the call, nothing for the million pixels that could not land.
        Assert.Equal(1, budget.Spent);
    }

    [Fact]
    public void DrawAt_SourcePartlyOffTheDestination_ChargesOnlyTheOverlap()
    {
        // 100×100 source at (60, 60) on a 64×64 destination overlaps by 4×4.
        var page = new Jbig2Decoder.Jbig2Bitmap(64, 64);
        var src = new Jbig2Decoder.Jbig2Bitmap(100, 100);
        var budget = new WorkBudget(1_000_000, "test");

        page.DrawAt(src, 60, 60, 0, budget);

        Assert.Equal(1 + 4 * 4, budget.Spent);
    }

    [Fact]
    public void DrawAt_FullyContainedSource_ChargesItsWholeArea()
    {
        var page = new Jbig2Decoder.Jbig2Bitmap(64, 64);
        var src = new Jbig2Decoder.Jbig2Bitmap(16, 8);
        var budget = new WorkBudget(1_000_000, "test");

        page.DrawAt(src, 4, 4, 0, budget);

        Assert.Equal(1 + 16 * 8, budget.Spent);
    }

    [Fact]
    public void DrawAt_PastTheBudget_ThrowsTheTypedRefusal()
    {
        var page = new Jbig2Decoder.Jbig2Bitmap(256, 256);
        var src = new Jbig2Decoder.Jbig2Bitmap(256, 256);
        var budget = new WorkBudget(1000, "test");

        Assert.Throws<WorkBudgetExceededException>(() => page.DrawAt(src, 0, 0, 0, budget));
    }

    [Fact]
    public void DrawAt_ClippingDoesNotChangeThePixelsItDraws()
    {
        // The clip is an optimisation of the loop bounds, not a change of output: only in-bounds
        // destination pixels were ever written before either.
        var page = new Jbig2Decoder.Jbig2Bitmap(8, 8);
        var src = new Jbig2Decoder.Jbig2Bitmap(4, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                src.SetPixel(x, y, 1);

        page.DrawAt(src, 6, 6, 0);   // only the 2×2 corner lands

        Assert.Equal(1, page.GetPixel(6, 6));
        Assert.Equal(1, page.GetPixel(7, 7));
        Assert.Equal(0, page.GetPixel(5, 5));
        Assert.Equal(0, page.GetPixel(0, 0));
    }

    // ==================================================================
    //  End to end: the halftone grid × pattern product
    // ==================================================================

    [Fact]
    public void Decode_HalftoneGridTimesPatternArea_IsRefusedAndReported()
    {
        byte[] stream = HalftoneAmplificationStream(gridSide: 100, patternSide: 255,
            pageSide: 512);

        var filter = new Jbig2DecodeFilter();
        byte[] output = filter.Decode(stream, parameters: null);

        // The filter's contract: a non-null LastFallbackReason means the returned array is the
        // untouched input, not a bitmap.
        Assert.NotNull(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Contains("budget", Jbig2DecodeFilter.LastFallbackReason!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stream, output);
    }

    [Fact]
    public void Decode_HalftoneWithinTheBudget_StillProducesABitmap()
    {
        // The budget must not become a cap on legitimate halftones: the same fixture with a grid
        // small enough to fit decodes and returns page pixels rather than the raw segments.
        byte[] stream = HalftoneAmplificationStream(gridSide: 4, patternSide: 32, pageSide: 512);

        var filter = new Jbig2DecodeFilter();
        byte[] output = filter.Decode(stream, parameters: null);

        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        // 512×512 at 1 bpp, MSB-packed: 64 bytes per row.
        Assert.Equal(64 * 512, output.Length);
    }

    /// <summary>
    /// A decode that would outgrow the <i>caller's</i> <see cref="StreamFilter.MaxOutputBytes"/> is
    /// refused, and refused as a throw rather than as the raw-bytes passthrough.
    /// </summary>
    /// <remarks>
    /// JBIG2 decodes to a bitmap sized by the segment headers, not by the input, and it consulted
    /// no ceiling at all — so a caller sizing its own from <c>/DL</c> was overrun in silence. The
    /// <c>LastFallbackReason</c> assertion is the load-bearing one: <c>Decode</c> ends in a
    /// catch-all that turns any exception into "decode failed, here are the compressed bytes", so a
    /// ceiling check written <i>inside</i> the try would swallow this throw and report the wrong
    /// cause. It sits outside for that reason, and this is what pins it there.
    /// <para>The fixture is the one
    /// <see cref="Decode_HalftoneWithinTheBudget_StillProducesABitmap"/> proves reaches the decoder
    /// and yields 32 768 bytes, so the refusal cannot be a passthrough wearing a ceiling's clothes.</para>
    /// </remarks>
    [Fact]
    public void Decode_OutputBeyondTheCallersCeiling_IsRefusedRatherThanReturned()
    {
        byte[] stream = HalftoneAmplificationStream(gridSide: 4, patternSide: 32, pageSide: 512);

        var filter = new Jbig2DecodeFilter { MaxOutputBytes = 1024 };

        var ex = Assert.Throws<System.IO.InvalidDataException>(
            () => filter.Decode(stream, parameters: null));

        Assert.Contains("1024", ex.Message, StringComparison.Ordinal);
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
    }

    /// <summary>The floor: the same decode under a ceiling that fits is untouched by the check.</summary>
    [Fact]
    public void Decode_OutputWithinTheCallersCeiling_IsUnaffected()
    {
        byte[] stream = HalftoneAmplificationStream(gridSide: 4, patternSide: 32, pageSide: 512);

        var filter = new Jbig2DecodeFilter { MaxOutputBytes = 64 * 512 };

        byte[] output = filter.Decode(stream, parameters: null);

        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Equal(64 * 512, output.Length);
    }

    // ==================================================================
    //  The MMR generic region — sized by its own header, charged before the decoder is
    // ==================================================================

    /// <summary>
    /// The arithmetic generic region charged its pixels inside the decoder; the MMR one handed the
    /// region header's width and height straight to the T.6 decoder, which allocates two lines of a
    /// byte per column and scans one per code. The coverage-guided fuzzer (GAPS.md §7c) found a
    /// 701-byte file declaring a region 2³⁰ wide: two gigabyte lines, seconds of scanning. A width
    /// past <c>int</c> also wrapped negative in the cast. Both are now refused by the one dimension
    /// check every other region takes, before a byte of the decoder runs; the filter reports the
    /// refusal and hands the bytes on.
    /// </summary>
    [Theory]
    [InlineData(1u << 30, 1u)]
    [InlineData(0x8000_0000u, 1u)]
    [InlineData(9000u, 9000u)]
    public void MmrGenericRegion_WithDimensionsPastTheBitmapCeiling_IsRefusedBeforeTheDecoderIsSized(uint width, uint height)
    {
        byte[] stream = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            PlainSegmentHeader(1, 38, 1, (uint)(18 + 4)), MmrGenericRegionBody(width, height, new byte[] { 0x80, 0, 0, 0 }),
            PlainSegmentHeader(2, 51, 0, 0));

        byte[] output = new Jbig2DecodeFilter().Decode(stream, parameters: null);

        Assert.Same(stream, output);
        Assert.Contains("dimensions out of range", Jbig2DecodeFilter.LastFallbackReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A region inside the bitmap ceiling is charged its area to the decode's budget as the
    /// arithmetic path charges its own: three 8000 × 8000 MMR regions (192 Mi pixels, each a
    /// kilobyte of white rows) on a sixteen-pixel page exceed the 128 Mi budget and are refused by
    /// it, where one decodes. The page is small so the composition's own charge (the clipped
    /// sixteen by sixteen) cannot be what refuses — the charge under test is the region's.
    /// </summary>
    [Fact]
    public void MmrGenericRegions_AreChargedTheirAreaToTheBudget()
    {
        byte[] white = CcittFaxEncoder.Encode(new byte[1000 * 8000], 8000, 8000, blackIs1: true);
        byte[] One(uint n) => Concat(PlainSegmentHeader(n, 38, 1, (uint)(18 + white.Length)), MmrGenericRegionBody(8000, 8000, white));

        byte[] three = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            One(1), One(2), One(3),
            PlainSegmentHeader(4, 51, 0, 0));
        byte[] one = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            One(1),
            PlainSegmentHeader(2, 51, 0, 0));

        byte[] refused = new Jbig2DecodeFilter().Decode(three, parameters: null);
        Assert.Same(three, refused);
        Assert.Contains("Work budget for JBIG2 decode exceeded", Jbig2DecodeFilter.LastFallbackReason, StringComparison.Ordinal);

        byte[] decoded = new Jbig2DecodeFilter().Decode(one, parameters: null);
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Equal(16 * 16 / 8, decoded.Length);
    }

    // ==================================================================
    //  A text-region instance is work before it draws
    // ==================================================================

    /// <summary>
    /// The coverage-guided fuzzer's 121 bytes (GAPS.md §7c): a symbol dictionary whose aggregate
    /// instances name a symbol it does not have, billions of them. An instance that drew nothing was
    /// charged nothing, so the loop ran its five integer decodes a few hundred nanoseconds at a time
    /// for four minutes inside the budget, then declined for a different reason. Every instance is
    /// now charged <see cref="Jbig2Decoder.TextInstanceUnits"/> up front, and the budget refuses.
    /// </summary>
    [Fact]
    public void Hostile_ADictionaryOfBillionsOfInstancesNamingNoSymbol_IsRefusedByTheBudget()
    {
        byte[] stream = Convert.FromBase64String(
            "AAAAATAAAQAAABMAAAAgAAAAEAAAAAAAQBgAAAAAAQAAAAAAAHIAAwAAAgAAAQAAACYUi4uLAf8AAAIAAAB+GRkZGRkgAlXSngAAAAAAAAAAADMBqYNoZBLri4uLiwAAi4uLAAD9wgACAP0BCv4AAAACp+bZZX//rA==");

        byte[] output = new Jbig2DecodeFilter().Decode(stream, parameters: null);

        Assert.Same(stream, output);
        Assert.Contains("Work budget for JBIG2 decode exceeded", Jbig2DecodeFilter.LastFallbackReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The price, pinned through the budget's own evidence: the symbol form of a 32 × 16 bitmap of two
    /// identical 3 × 3 squares is one symbol (its setup and nine pixels — the encoder keeps a bitmap
    /// once), a page-sized text region of two instances (<see cref="Jbig2Decoder.TextInstanceUnits"/>
    /// and a nine-pixel draw each) composed onto the page, and nothing left for a residual region;
    /// the page and the text region are each a bitmap of their own
    /// (<see cref="Jbig2Decoder.RegionBitmapPixelsPerUnit"/>) — and spends exactly what those add up
    /// to. A price that changed, in either direction, changes this sum.
    /// </summary>
    [Fact]
    public void TextInstances_AreChargedTheirPrice_AndTheDecodeSpendsWhatItsPartsAddUpTo()
    {
        var bits = new byte[4 * 16];
        void Set(int x, int y) => bits[y * 4 + x / 8] |= (byte)(0x80 >> (x % 8));
        for (int y = 2; y < 5; y++) for (int x = 2; x < 5; x++) Set(x, y);
        for (int y = 9; y < 12; y++) for (int x = 20; x < 23; x++) Set(x, y);
        byte[] stream = Jbig2EncodeFilter.EncodeWithSymbols(bits, 32, 16, 4);

        new Jbig2DecodeFilter().Decode(stream, parameters: null);

        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Equal(16, Jbig2Decoder.TextInstanceUnits);
        long oneSymbol = Jbig2Decoder.GenericRegionSetupUnits + 9;
        long twoInstances = 2 * (Jbig2Decoder.TextInstanceUnits + 1 + 9);
        long textRegionOntoPage = 1 + 32 * 16;
        long pageAndRegionBitmaps = 2 * (1 + 32 * 16 / Jbig2Decoder.RegionBitmapPixelsPerUnit);
        Assert.Equal(oneSymbol + twoInstances + textRegionOntoPage + pageAndRegionBitmaps, Jbig2Decoder.LastWorkUnits);
    }

    // ==================================================================
    //  The page's budget above the stream's
    // ==================================================================

    /// <summary>
    /// The composition budget is per stream, and a stream of a few dozen bytes spends it whole — a
    /// region declared 8192 pixels square is 64 Mi pixels before a bit is decoded — so a page of such
    /// images cost that many budgets (GAPS.md §7c's residual). Given a parent through
    /// <see cref="StreamFilter.Work"/>, the stream's budget spends it too, clamped to what the stream
    /// could pay: under a stream budget of one million and a page budget of two and a half, two such
    /// streams fall back to their bytes as before and the third is refused by the page — thrown, not
    /// swallowed into the fallback, so the renderer can report the page's ceiling rather than a third
    /// stream failing on its own. Without a parent every one falls back, as it always did.
    /// </summary>
    [Fact]
    public void AStreamThatSpendsItsBudget_SpendsThePagesToo_AndThePagesRefusalIsThrown()
    {
        byte[] stream = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            PlainSegmentHeader(1, 38, 1, (uint)(18 + 8 + 2)), ArithmeticGenericRegionBody(8192, 8192),
            PlainSegmentHeader(2, 51, 0, 0));
        try
        {
            PdfLimits.MaxJbig2CompositionPixels = 1_000_000;
            var page = new Tetitulz.Pdf.Core.Util.WorkBudget(2_500_000, "image decoding on this page");

            for (int i = 0; i < 2; i++)
            {
                byte[] output = new Jbig2DecodeFilter { Work = page }.Decode(stream, parameters: null);
                Assert.Same(stream, output);
                Assert.Contains("JBIG2 decode", Jbig2DecodeFilter.LastFallbackReason, StringComparison.Ordinal);
            }
            Assert.Equal(2_000_000, page.Spent);

            var ex = Assert.Throws<Tetitulz.Pdf.Core.Util.WorkBudgetExceededException>(
                () => new Jbig2DecodeFilter { Work = page }.Decode(stream, parameters: null));
            Assert.Same(page, ex.Budget);
            Assert.Contains("image decoding on this page", ex.Message, StringComparison.Ordinal);

            // No parent: the third stream is the first's twin — refused by its own budget, bytes back.
            for (int i = 0; i < 3; i++)
                Assert.Same(stream, new Jbig2DecodeFilter().Decode(stream, parameters: null));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The floor under both: an MMR generic region of real pixels still decodes to them.</summary>
    [Fact]
    public void MmrGenericRegion_DecodesItsPixels()
    {
        // 32 × 16, one bit a pixel, 1 = black (T.88's polarity): two squares.
        var bits = new byte[4 * 16];
        void Set(int x, int y) => bits[y * 4 + x / 8] |= (byte)(0x80 >> (x % 8));
        for (int y = 2; y < 6; y++) for (int x = 2; x < 6; x++) Set(x, y);
        for (int y = 9; y < 13; y++) for (int x = 20; x < 24; x++) Set(x, y);
        byte[] mmr = CcittFaxEncoder.Encode(bits, 32, 16, blackIs1: true);

        byte[] stream = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(32, 16),
            PlainSegmentHeader(1, 38, 1, (uint)(18 + mmr.Length)), MmrGenericRegionBody(32, 16, mmr),
            PlainSegmentHeader(2, 51, 0, 0));

        byte[] output = new Jbig2DecodeFilter().Decode(stream, parameters: null);

        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        // The filter inverts at the PDF boundary: a 1-bit DeviceGray 0 is black.
        var expected = new byte[bits.Length];
        for (int i = 0; i < bits.Length; i++) expected[i] = (byte)~bits[i];
        Assert.Equal(expected, output);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Region info + the generic region flags (arithmetic, template 0) + the eight nominal AT bytes + two bytes of data.</summary>
    private static byte[] ArithmeticGenericRegionBody(uint width, uint height)
    {
        var body = new byte[17 + 1 + 8 + 2];
        WriteUInt32(body, 0, width);
        WriteUInt32(body, 4, height);
        body[16] = 0;      // external combination operator OR
        body[17] = 0x00;   // MMR = 0, GBTEMPLATE = 0, TPGDON = 0
        sbyte[] at = { 3, -1, -3, -1, 2, -2, -2, -2 };
        for (int i = 0; i < 8; i++) body[18 + i] = (byte)at[i];
        body[26] = 0xFF; body[27] = 0xAC;
        return body;
    }

    /// <summary>Region info (§7.4.1: width, height, x, y, flags) + the generic region flags with MMR set + the T.6 data.</summary>
    private static byte[] MmrGenericRegionBody(uint width, uint height, byte[] mmr)
    {
        var body = new byte[18 + mmr.Length];
        WriteUInt32(body, 0, width);
        WriteUInt32(body, 4, height);
        body[16] = 0;      // external combination operator OR
        body[17] = 0x01;   // MMR = 1
        Buffer.BlockCopy(mmr, 0, body, 18, mmr.Length);
        return body;
    }

    /// <summary>
    /// Page info + a single-pattern dictionary + a halftone region that draws that pattern once
    /// per grid cell at unit spacing, so every cell lands on the page and is charged in full.
    /// </summary>
    private static byte[] HalftoneAmplificationStream(int gridSide, int patternSide, int pageSide)
    {
        // Pattern dictionary body: flags(1) HDPW(1) HDPH(1) GRAYMAX(4) + arithmetic data.
        var patternBody = new byte[1 + 1 + 1 + 4 + 8];
        patternBody[0] = 0;                        // HDMMR = 0, template 0
        patternBody[1] = (byte)patternSide;        // HDPW
        patternBody[2] = (byte)patternSide;        // HDPH
        // GRAYMAX = 0 → exactly one pattern.

        // Halftone region body: region info(17) + flags(1) + HGW(4) HGH(4) HGX(4) HGY(4)
        // + HRX(2) HRY(2). No trailing arithmetic data, so the single index plane is blank and
        // every cell selects pattern 0.
        var halftoneBody = new byte[17 + 1 + 16 + 4];
        WriteUInt32(halftoneBody, 0, (uint)pageSide);   // region width
        WriteUInt32(halftoneBody, 4, (uint)pageSide);   // region height
        WriteUInt32(halftoneBody, 8, 0);                // region X
        WriteUInt32(halftoneBody, 12, 0);               // region Y
        halftoneBody[16] = 0;                           // external combination operator = OR
        halftoneBody[17] = 0;                           // HMMR = 0, template 0, no skip
        WriteUInt32(halftoneBody, 18, (uint)gridSide);  // HGW
        WriteUInt32(halftoneBody, 22, (uint)gridSide);  // HGH
        WriteUInt32(halftoneBody, 26, 0);               // HGX
        WriteUInt32(halftoneBody, 30, 0);               // HGY
        halftoneBody[34] = 0; halftoneBody[35] = 1;     // HRX = 1
        halftoneBody[36] = 0; halftoneBody[37] = 0;     // HRY = 0

        return Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody((uint)pageSide, (uint)pageSide),
            PlainSegmentHeader(1, 16, 1, (uint)patternBody.Length), patternBody,
            ReferringSegmentHeader(2, 22, referredTo: 1, pageAssociation: 1,
                dataLength: (uint)halftoneBody.Length), halftoneBody,
            PlainSegmentHeader(3, 51, 0, 0));
    }

    private static void WriteUInt32(byte[] b, int offset, uint v)
    {
        b[offset] = (byte)(v >> 24); b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8); b[offset + 3] = (byte)v;
    }

    private static byte[] PlainSegmentHeader(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength) => new byte[]
    {
        (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
        (byte)(segmentNumber >> 8),  (byte)segmentNumber,
        (byte)(segmentType & 0x3F),
        0x00,                                    // 0 referred-to segments
        (byte)pageAssociation,
        (byte)(dataLength >> 24), (byte)(dataLength >> 16),
        (byte)(dataLength >> 8),  (byte)dataLength,
    };

    /// <summary>Header naming exactly one referred-to segment (§7.2.3, short form).</summary>
    private static byte[] ReferringSegmentHeader(uint segmentNumber, int segmentType,
        uint referredTo, uint pageAssociation, uint dataLength) => new byte[]
    {
        (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
        (byte)(segmentNumber >> 8),  (byte)segmentNumber,
        (byte)(segmentType & 0x3F),
        0x20,                                    // count = 1 in the high 3 bits
        (byte)referredTo,                        // 1 byte, since this segment number <= 0xFF
        (byte)pageAssociation,
        (byte)(dataLength >> 24), (byte)(dataLength >> 16),
        (byte)(dataLength >> 8),  (byte)dataLength,
    };

    private static byte[] PageInfoBody(uint width, uint height)
    {
        var body = new byte[19];
        WriteUInt32(body, 0, width);
        WriteUInt32(body, 4, height);
        return body;
    }

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
}
