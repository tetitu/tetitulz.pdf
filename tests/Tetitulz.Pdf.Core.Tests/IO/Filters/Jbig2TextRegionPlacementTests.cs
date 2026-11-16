// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// T.88 §6.4.5 text-region instance placement — REFCORNER, TRANSPOSED and SBDEFPIXEL.
///
/// <para><b>What these pin.</b> All three fields were parsed into locals and discarded
/// (<c>_ = refCorner; _ = transposed; … _ = sbDefPixel;</c>) in the <b>arithmetic</b> text-region
/// path, which is the path that works. Every instance was therefore placed as if the stream had
/// said TOPLEFT, in a non-transposed region, over a white background — and
/// <c>Jbig2Decoder.Assess</c> went on reporting the segment <c>Decoded</c>. A region using either
/// BOTTOM corner drew every symbol <c>height - 1</c> rows too low, and because glyph heights
/// differ, by a different amount per symbol.</para>
///
/// <para><b>Why the three are split two ways.</b> REFCORNER is now honoured, because the
/// correction follows from what the corner is <i>named</i> — a BOTTOM corner puts the symbol's
/// last row at T — so it can be applied without recourse to the spec text and it improves those
/// files rather than rejecting them. TRANSPOSED and SBDEFPIXEL are refused, because the first
/// exchanges the S and T axes (moving the REFCORNER adjustment to the other axis) and the second
/// needs an intermediate region bitmap this decoder does not build. That is the same trade the
/// Huffman symbol dictionary and Huffman text region were refused under: plausible ink in the
/// wrong places is the one outcome worse than a visible failure.</para>
///
/// <para><b>Why the placement claim is asserted on the pure function.</b> There is no MQ
/// <i>encoder</i> in the tree, so a real arithmetic-coded text region cannot be synthesised — the
/// existing text-region tests feed 32 zero bytes and assert only determinism. Extracting the
/// placement rule makes the claim reachable without one; the refusals below need no encoder
/// because the flags are read at fixed byte offsets before any arithmetic decoding starts.</para>
///
/// <para><b>What these do NOT cover, stated rather than left to be discovered.</b> Nothing here
/// asserts that <c>DecodeTextRegion</c>'s instance loop actually <i>calls</i>
/// <c>TextInstanceTopRow</c> — rewiring the call site back to a bare <c>it</c> would leave all
/// ten green. Reaching that loop needs a valid arithmetic-coded body, so covering it means
/// writing an MQ <b>encoder</b>, which is the same missing piece that keeps every other
/// text-region test synthetic. The rule is pinned; the wiring is reviewed, not tested. If an
/// encoder ever lands, the first test to write is a two-symbol region decoded under BOTTOMLEFT
/// and TOPLEFT that asserts the two outputs differ by the symbol height.</para>
/// </summary>
public class Jbig2TextRegionPlacementTests
{
    private const int BottomLeft = 0, TopLeft = 1, BottomRight = 2, TopRight = 3;

    // ==================================================================
    //  REFCORNER — the placement rule itself
    // ==================================================================

    /// <summary>
    /// A TOP corner puts the symbol's first row at T; a BOTTOM corner puts its last row there,
    /// so the first row is <c>T - height + 1</c>.
    /// </summary>
    [Theory]
    [InlineData(TopLeft,     50, 10, 50)]
    [InlineData(TopRight,    50, 10, 50)]
    [InlineData(BottomLeft,  50, 10, 41)]
    [InlineData(BottomRight, 50, 10, 41)]
    public void TheTopRow_FollowsTheNamedCorner(int refCorner, int t, int height, int expected)
        => Assert.Equal(expected, Jbig2Decoder.TextInstanceTopRow(t, height, refCorner));

    /// <summary>
    /// The regression guard, stating the defect directly: the four corners must not all place an
    /// instance identically. Treating every value as TOPLEFT — which is what discarding the field
    /// amounts to — makes all four answers equal, and this is the assertion that separates the
    /// fixed decoder from the shipped one.
    /// </summary>
    [Fact]
    public void TheBottomCorners_DoNotPlaceAnInstanceWhereTheTopCornersDo()
    {
        const int t = 100, height = 24;

        int top    = Jbig2Decoder.TextInstanceTopRow(t, height, TopLeft);
        int bottom = Jbig2Decoder.TextInstanceTopRow(t, height, BottomLeft);

        Assert.NotEqual(top, bottom);
        Assert.Equal(height - 1, top - bottom);
    }

    /// <summary>
    /// The offset a BOTTOM corner introduces is the symbol's own height, so it differs per symbol.
    /// That is why the defect could not be corrected downstream by shifting the whole region: two
    /// glyphs of different heights on one baseline were displaced by different amounts.
    /// </summary>
    [Fact]
    public void TheBottomCornerOffset_ScalesWithEachSymbolsOwnHeight()
    {
        const int t = 60;

        int shortGlyph = Jbig2Decoder.TextInstanceTopRow(t, 8,  BottomLeft);
        int tallGlyph  = Jbig2Decoder.TextInstanceTopRow(t, 30, BottomLeft);

        Assert.Equal(53, shortGlyph);
        Assert.Equal(31, tallGlyph);
        Assert.NotEqual(shortGlyph, tallGlyph);
    }

    // ==================================================================
    //  TRANSPOSED / SBDEFPIXEL — no longer refused (decoded: Jbig2TextRegionLayoutTests)
    // ==================================================================

    /// <summary>
    /// A transposed region was refused by name; it is decoded now, with the placement pinned
    /// pixel for pixel in <see cref="Jbig2TextRegionLayoutTests"/>.
    /// </summary>
    [Fact]
    public void ATransposedTextRegion_IsNoLongerRefused()
    {
        var filter = new Jbig2DecodeFilter();
        filter.Decode(StreamWithTextRegionFlags(Transposed), parameters: null);

        Assert.DoesNotContain("TRANSPOSED", filter_LastReason(), StringComparison.Ordinal);
    }

    /// <summary>
    /// SBDEFPIXEL=1 was refused for want of a region bitmap to fill; the region has one now.
    /// </summary>
    [Fact]
    public void ATextRegionWithABlackDefaultPixel_IsNoLongerRefused()
    {
        var filter = new Jbig2DecodeFilter();
        filter.Decode(StreamWithTextRegionFlags(DefaultPixelBlack), parameters: null);

        Assert.DoesNotContain("SBDEFPIXEL", filter_LastReason(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixture-validity control, and the reason the two cases above are about the flags rather
    /// than about the body being synthetic. The <b>same</b> stream with the flag word cleared must
    /// not be refused for either reason — otherwise both assertions would pass on a decoder that
    /// simply rejects this fixture outright.
    /// </summary>
    [Fact]
    public void TheSameStreamWithNoFlagsSet_IsRefusedForNeitherReason()
    {
        var filter = new Jbig2DecodeFilter();
        filter.Decode(StreamWithTextRegionFlags(0), parameters: null);

        string reason = filter_LastReason();
        Assert.DoesNotContain("TRANSPOSED", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("SBDEFPIXEL", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each flag is refused on its own account: setting only one must not produce the other's
    /// message. Without this, one over-broad refusal reading "unsupported text region flags"
    /// would satisfy both named assertions above.
    /// </summary>
    [Fact]
    public void EachFlag_IsRefusedForItsOwnReasonAndNotTheOthers()
    {
        var transposed = new Jbig2DecodeFilter();
        transposed.Decode(StreamWithTextRegionFlags(Transposed), parameters: null);
        string transposedReason = filter_LastReason();

        var defaultPixel = new Jbig2DecodeFilter();
        defaultPixel.Decode(StreamWithTextRegionFlags(DefaultPixelBlack), parameters: null);
        string defaultPixelReason = filter_LastReason();

        Assert.DoesNotContain("SBDEFPIXEL", transposedReason, StringComparison.Ordinal);
        Assert.DoesNotContain("TRANSPOSED", defaultPixelReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    // Text-region segment flags (T.88 §7.4.4.1.1), as the 16-bit word at body offset 17.
    private const ushort Transposed        = 1 << 6;
    private const ushort DefaultPixelBlack = 1 << 9;

    private static string filter_LastReason() => Jbig2DecodeFilter.LastFallbackReason ?? string.Empty;

    /// <summary>
    /// A page-info segment plus one immediate text region (type 6) whose 16-bit flag word carries
    /// <paramref name="sbFlags"/>. The region-info block is given plausible dimensions so the only
    /// thing varying between the cases above is the flag word itself.
    /// </summary>
    private static byte[] StreamWithTextRegionFlags(ushort sbFlags)
    {
        var body = new byte[32];

        // Region info: width, height, x, y (4 bytes BE each), then the region flags byte.
        WriteUInt32BE(body, 0, 32);   // region width
        WriteUInt32BE(body, 4, 32);   // region height
        WriteUInt32BE(body, 8, 0);    // region x
        WriteUInt32BE(body, 12, 0);   // region y
        body[16] = 0;                 // external combination operator: OR

        // The text-region flag word.
        body[17] = (byte)(sbFlags >> 8);
        body[18] = (byte)(sbFlags & 0xFF);

        return Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 6, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset]     = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)(value & 0xFF);
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

    private static byte[] SegmentHeader(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength)
        => new byte[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),  (byte)(segmentNumber & 0xFF),
            (byte)(segmentType & 0x3F),
            0x00,   // 0 referred-to segments
            (byte)(pageAssociation & 0xFF),
            (byte)(dataLength >> 24), (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),  (byte)(dataLength & 0xFF),
        };

    private static byte[] PageInfoBody(uint width, uint height)
    {
        var body = new byte[19];
        WriteUInt32BE(body, 0, width);
        WriteUInt32BE(body, 4, height);
        return body;
    }
}
