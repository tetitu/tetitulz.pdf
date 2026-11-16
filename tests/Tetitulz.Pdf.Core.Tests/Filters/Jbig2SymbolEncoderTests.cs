// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Filters;

/// <summary>
/// <see cref="Jbig2EncodeFilter.EncodeWithSymbols"/> — the symbol dictionary, the text region and the
/// residual generic region, checked by decoding back through the filter a PDF reader uses and by
/// reading the segments the stream declares.
/// </summary>
/// <remarks>
/// <para><b>Exactness is the claim, and it is what separates this from the encoders JBIG2 is
/// notorious for.</b> Two components become one symbol only when their bitmaps are identical, so every
/// round trip here is bit for bit — a similarity threshold would be the defect, not a tolerance.</para>
/// <para><b>The structure is asserted, not only the pixels.</b> A round trip passes with the whole
/// symbol path deleted, since <c>EncodeWithSymbols</c> falls back to the generic region; so each test
/// that claims symbols were used reads the segment headers and the dictionary's and region's counts
/// from the stream. The external check — an independent renderer reading the same stream — is in Base.Tests
/// (<c>Jbig2PdfiumOracleTests</c>), where a PDF can be built around it.</para>
/// </remarks>
public class Jbig2SymbolEncoderTests
{
    // ================================================================ a page of repeated glyphs

    /// <summary>
    /// A typed page in miniature: six distinct glyphs, each a single component, placed three hundred
    /// times, and a rule too long to be a symbol. The symbol form decodes to the same pixels and is
    /// shorter than the generic region; the stream holds one dictionary of six symbols, one region of
    /// three hundred instances and one generic region for the rule, at the rule's own position.
    /// </summary>
    [Fact]
    public void ARepeatedGlyphPage_RoundTripsExactly_IsSmallerThanTheGenericRegion_AndDeclaresItsSymbols()
    {
        const int width = 700, height = 240, stride = 88;
        byte[] bits = GlyphPage(width, height, stride, out int distinctGlyphs, out int placed);

        byte[] symbolic = Jbig2EncodeFilter.EncodeWithSymbols(bits, width, height, stride);
        byte[] generic = Jbig2EncodeFilter.Encode(bits, width, height, stride);
        Assert.NotNull(symbolic);
        Assert.NotNull(generic);

        AssertRoundTripped(bits, Decode(symbolic, width, height), width, height, stride);
        Assert.True(symbolic.Length < generic.Length,
            $"the symbol form is {symbolic.Length} bytes against the generic region's {generic.Length}");

        var segments = Segments(symbolic);
        Assert.Equal(new[] { 48, 0, 6, 39 }, segments.Select(s => s.Type).ToArray());
        Assert.Equal(distinctGlyphs, SymbolCount(segments[1].Body));
        Assert.Equal(placed, InstanceCount(segments[2].Body));
        Assert.Equal(new[] { 2u }, segments[2].ReferredTo);

        // The residual is the rule, cropped to itself: region info width, height, x, y.
        byte[] rule = segments[3].Body;
        Assert.Equal(RuleWidth, ReadU32(rule, 0));
        Assert.Equal(RuleHeight, ReadU32(rule, 4));
        Assert.Equal(RuleX, ReadU32(rule, 8));
        Assert.Equal(RuleY, ReadU32(rule, 12));
    }

    /// <summary>
    /// The dictionary's order is the standard's: heights ascending, widths ascending within a height,
    /// so every IADH and IADW is non-negative. Asserted through the decoder's own symbol list — a
    /// dictionary written in another order would still round-trip, since the IDs would follow it.
    /// </summary>
    [Fact]
    public void TheDictionaryIsWrittenInHeightClassesOfAscendingWidth()
    {
        const int width = 64, height = 40, stride = 8;
        var bits = new byte[stride * height];
        // Four solid rectangles: 3x5, 7x5, 4x9 and 2x2 — out of order on the page.
        FillRect(bits, stride, 2, 20, 4, 9);
        FillRect(bits, stride, 10, 2, 7, 5);
        FillRect(bits, stride, 30, 2, 3, 5);
        FillRect(bits, stride, 50, 30, 2, 2);

        byte[] symbolic = Jbig2EncodeFilter.EncodeWithSymbols(bits, width, height, stride);
        var segments = Segments(symbolic);
        Assert.Equal(4, SymbolCount(segments[1].Body));

        var symbols = Jbig2Decoder.DecodeSymbolDictionary(segments[1].Body, new List<Jbig2Decoder.Jbig2Bitmap>(),
            new Core.Util.WorkBudget(1_000_000, "test"));
        Assert.Equal(new[] { (2, 2), (3, 5), (7, 5), (4, 9) },
            symbols.Select(s => (s.Width, s.Height)).ToArray());

        AssertRoundTripped(bits, Decode(symbolic, width, height), width, height, stride);
    }

    /// <summary>
    /// Connectivity is eight-way, as a glyph's diagonal joins are: two squares touching at a corner
    /// are one component and one symbol; the same two squares a white column apart are two instances
    /// of one symbol. Both decode exactly.
    /// </summary>
    [Fact]
    public void ComponentsAreEightConnected_AndIdenticalOnesShareASymbol()
    {
        const int width = 32, height = 16, stride = 4;

        // Both diagonals: the lower square to the right of the upper one, and to its left. The run
        // join tests the two ends of a span separately, so one diagonal would leave the other untested.
        foreach (int lowerX in new[] { 6, 2 })
        {
            var touching = new byte[stride * height];
            FillRect(touching, stride, lowerX == 6 ? 2 : 6, 2, 4, 4);
            FillRect(touching, stride, lowerX, 6, 4, 4);           // corner to corner with the first
            byte[] one = Jbig2EncodeFilter.EncodeWithSymbols(touching, width, height, stride);
            Assert.Equal(1, SymbolCount(Segments(one)[1].Body));
            Assert.Equal(1, InstanceCount(Segments(one)[2].Body));
            AssertRoundTripped(touching, Decode(one, width, height), width, height, stride);
        }

        var apart = new byte[stride * height];
        FillRect(apart, stride, 2, 2, 4, 4);
        FillRect(apart, stride, 7, 6, 4, 4);               // a white column between
        byte[] two = Jbig2EncodeFilter.EncodeWithSymbols(apart, width, height, stride);
        Assert.Equal(1, SymbolCount(Segments(two)[1].Body));
        Assert.Equal(2, InstanceCount(Segments(two)[2].Body));
        AssertRoundTripped(apart, Decode(two, width, height), width, height, stride);
    }

    /// <summary>
    /// Instances on one row whose boxes overlap — a kerned pair — place the second by a negative S
    /// delta; instances in later rows open new strips. Exact either way.
    /// </summary>
    [Fact]
    public void OverlappingBoxesAndSeveralRows_PlaceEveryInstance()
    {
        const int width = 40, height = 32, stride = 5;
        var bits = new byte[stride * height];
        // Row 1: an L shape and a square whose boxes overlap in x.
        FillRect(bits, stride, 2, 2, 2, 10);
        FillRect(bits, stride, 2, 10, 8, 2);
        FillRect(bits, stride, 6, 2, 3, 3);                // inside the L's box, not touching it
        // Row 2: three identical dots.
        FillRect(bits, stride, 2, 20, 2, 2);
        FillRect(bits, stride, 12, 20, 2, 2);
        FillRect(bits, stride, 30, 20, 2, 2);

        byte[] symbolic = Jbig2EncodeFilter.EncodeWithSymbols(bits, width, height, stride);
        Assert.Equal(3, SymbolCount(Segments(symbolic)[1].Body));     // the L, the square, the dot
        Assert.Equal(5, InstanceCount(Segments(symbolic)[2].Body));
        AssertRoundTripped(bits, Decode(symbolic, width, height), width, height, stride);
    }

    /// <summary>
    /// With nothing symbol-sized — a blank page, or a block larger than a symbol may be — the
    /// symbol form is the generic region, byte for byte: a dictionary of no symbols is not written.
    /// </summary>
    [Fact]
    public void AnImageWithNoSymbol_IsTheGenericRegionStream()
    {
        const int width = 600, height = 600, stride = 75;
        var blank = new byte[stride * height];
        Assert.Equal(Jbig2EncodeFilter.Encode(blank, width, height, stride),
            Jbig2EncodeFilter.EncodeWithSymbols(blank, width, height, stride));

        var block = new byte[stride * height];
        FillRect(block, stride, 10, 10, 580, 580);          // past both the side and the area limit
        byte[] symbolic = Jbig2EncodeFilter.EncodeWithSymbols(block, width, height, stride);
        Assert.Equal(Jbig2EncodeFilter.Encode(block, width, height, stride), symbolic);
        Assert.Equal(new[] { 48, 39 }, Segments(symbolic).Select(s => s.Type).ToArray());
    }

    /// <summary>A component at the symbol limit is a symbol; one pixel past it is residual.</summary>
    [Fact]
    public void TheSymbolSizeLimitIsAtTheLimit()
    {
        int side = Jbig2SymbolEncoder.MaxSymbolSide;
        int width = side + 8, height = 2 * side + 16, stride = (width + 7) / 8;
        var bits = new byte[stride * height];
        FillRect(bits, stride, 0, 0, 1, side);               // a vertical hairline of exactly the limit
        FillRect(bits, stride, 4, side + 8, 1, side + 1);    // one longer

        var segments = Segments(Jbig2EncodeFilter.EncodeWithSymbols(bits, width, height, stride));
        Assert.Equal(new[] { 48, 0, 6, 39 }, segments.Select(s => s.Type).ToArray());
        Assert.Equal(1, SymbolCount(segments[1].Body));
        Assert.Equal(side + 1, ReadU32(segments[3].Body, 4));   // the residual's height
    }

    /// <summary>Unusable input is declined, as the generic form declines it.</summary>
    [Theory]
    [InlineData(0, 8, 1)]
    [InlineData(8, 0, 1)]
    [InlineData(8, 8, 0)]
    public void MalformedInputIsDeclined(int width, int height, int stride)
    {
        Assert.Null(Jbig2EncodeFilter.EncodeWithSymbols(new byte[64], width, height, stride));
        Assert.Null(Jbig2EncodeFilter.EncodeWithSymbols(null, 8, 8, 1));
    }

    /// <summary>
    /// The exact bytes two 3×3 squares encode to: a characterisation pin, so an unintended change to
    /// the segment layout, the context layout or the coding order is loud. Every round trip here is
    /// blind to a change made on both sides at once; this is not, and an independent renderer read these bytes to the
    /// input pixels (<c>Jbig2PdfiumOracleTests</c>) before they were pinned. If a deliberate change
    /// moves them, re-derive them, re-run the oracle, and say why in the commit.
    /// </summary>
    [Fact]
    public void TheSymbolStreamBytesArePinnedSoAnUnintendedChangeIsLoud()
    {
        const int width = 16, height = 8, stride = 2;
        var bits = new byte[stride * height];
        FillRect(bits, stride, 1, 1, 3, 3);
        FillRect(bits, stride, 12, 1, 3, 3);

        byte[] expected =
        {
            // Page information: header, 19 bytes of a 16x8 page.
            0x00, 0x00, 0x00, 0x01, 0x30, 0x00, 0x01, 0x00, 0x00, 0x00, 0x13,
            0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            // Symbol dictionary (type 0): header; flags; the four AT pixels; one exported, one new;
            // the coded symbol, ending in FLUSH's FF AC.
            0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x18,
            0x00, 0x00,
            0x03, 0xFF, 0xFD, 0xFF, 0x02, 0xFE, 0xFE, 0xFE,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x66, 0x21, 0x5A, 0xBF, 0xFF, 0xAC,
            // Text region (type 6) referring to segment 2: header; region info; flags (TOPLEFT);
            // two instances; the coded placements.
            0x00, 0x00, 0x00, 0x03, 0x06, 0x20, 0x02, 0x01, 0x00, 0x00, 0x00, 0x1C,
            0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x10,
            0x00, 0x00, 0x00, 0x02,
            0xA5, 0xC2, 0x9F, 0xFF, 0xAC,
        };

        Assert.Equal(expected, Jbig2EncodeFilter.EncodeWithSymbols(bits, width, height, stride));
    }

    // ================================================================ the two mirrors

    /// <summary>
    /// The integer encoder and the decoder agree on every range of Annex A, both signs, and OOB.
    /// Values at each range's two ends, since the prefix changes there; a sequence in one context
    /// block, since the contexts adapt across values.
    /// </summary>
    [Fact]
    public void TheIntegerEncoderMirrorsTheDecoder()
    {
        int[] values =
        {
            0, 1, 2, 3, 4, 5, 19, 20, 21, 83, 84, 85, 339, 340, 341, 4435, 4436, 4437,
            100_000, 1 << 30, int.MaxValue, -1, -3, -4, -19, -20, -84, -340, -4436, -70_000, int.MinValue + 1,
        };
        var mq = new MqEncoder(512);
        var enc = new Jbig2IntegerEncoder(mq, 0);
        foreach (int v in values) enc.Encode(v);
        enc.EncodeOob();
        enc.Encode(7);                                    // something after the OOB, decodable
        byte[] data = mq.Finish();

        var dec = new Jbig2Decoder.Jbig2IntegerDecoder(new MqDecoder(data, 0, 512), 0);
        foreach (int v in values) Assert.Equal(v, dec.Decode());
        Assert.Equal(Jbig2Decoder.Jbig2IntegerDecoder.OOB, dec.Decode());
        Assert.Equal(7, dec.Decode());
    }

    /// <summary>The symbol-ID coder and its decoder agree at every code length a dictionary can have.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    public void TheSymbolIdEncoderMirrorsTheDecoder(int codeLen)
    {
        int max = (1 << codeLen) - 1;
        int[] ids = { 0, 1, max, max / 2, 1, 0, max };
        var mq = new MqEncoder(1 << codeLen);
        foreach (int id in ids) Jbig2SymbolEncoder.EncodeSymbolId(mq, 0, codeLen, id);
        byte[] data = mq.Finish();

        var dec = new MqDecoder(data, 0, 1 << codeLen);
        foreach (int id in ids) Assert.Equal(id, Jbig2Decoder.DecodeSymbolId(dec, 0, codeLen));
    }

    // ================================================================ helpers

    private const int RuleX = 20, RuleY = 200, RuleWidth = 600, RuleHeight = 3;

    /// <summary>
    /// Six glyphs of 11×15 — a filled box each with a different interior pixel cleared, so each is
    /// one component and no two are equal — placed in ten rows of thirty, and the rule.
    /// </summary>
    private static byte[] GlyphPage(int width, int height, int stride, out int distinctGlyphs, out int placed)
    {
        var bits = new byte[stride * height];
        distinctGlyphs = 6;
        placed = 0;
        for (int row = 0; row < 10; row++)
            for (int col = 0; col < 30; col++)
            {
                int glyph = (row * 7 + col * 3) % distinctGlyphs;
                int x = 8 + col * 22, y = 6 + row * 19;
                FillRect(bits, stride, x, y, 11, 15);
                ClearPixel(bits, stride, x + 2 + glyph, y + 3 + glyph);
                placed++;
            }
        FillRect(bits, stride, RuleX, RuleY, RuleWidth, RuleHeight);
        return bits;
    }

    private static void FillRect(byte[] bits, int stride, int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
    }

    private static void ClearPixel(byte[] bits, int stride, int x, int y)
        => bits[y * stride + (x >> 3)] &= (byte)~(0x80 >> (x & 7));

    private sealed class Segment
    {
        public int Type;
        public uint[] ReferredTo;
        public byte[] Body;
    }

    /// <summary>The segments of an embedded stream, each with its body — through the decoder's own header reader.</summary>
    private static List<Segment> Segments(byte[] stream)
    {
        var headers = Jbig2Decoder.EnumerateSegments(stream);
        var list = new List<Segment>();
        int offset = 0;
        foreach (var h in headers)
        {
            int bodyStart = offset + h.HeaderLength;
            var body = new byte[h.DataLength];
            Array.Copy(stream, bodyStart, body, 0, (int)h.DataLength);
            list.Add(new Segment { Type = h.SegmentType, ReferredTo = h.ReferredToSegments, Body = body });
            offset = bodyStart + (int)h.DataLength;
        }
        return list;
    }

    /// <summary>SDNUMNEWSYMS: after the two flag bytes, eight AT bytes and SDNUMEXSYMS.</summary>
    private static int SymbolCount(byte[] dictionaryBody) => (int)ReadU32(dictionaryBody, 14);

    /// <summary>SBNUMINSTANCES: after the 17-byte region information and the two flag bytes.</summary>
    private static int InstanceCount(byte[] textRegionBody) => (int)ReadU32(textRegionBody, 19);

    private static long ReadU32(byte[] b, int at)
        => ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];

    private static byte[] Decode(byte[] encoded, int width, int height)
    {
        Assert.NotNull(encoded);
        var parms = new PdfDictionary(2);
        parms.Set(PdfName.Width, new PdfNumber(width));
        parms.Set(PdfName.Height, new PdfNumber(height));
        byte[] decoded = new Jbig2DecodeFilter().Decode(encoded, parms);
        Assert.NotNull(decoded);
        return decoded;
    }

    /// <summary>T.88 bits in, PDF samples out: a black pixel is a set bit on one side and a 0 on the other.</summary>
    private static void AssertRoundTripped(byte[] t88, byte[] pdfSamples, int width, int height, int stride)
    {
        int actualStride = (width + 7) / 8;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int black = (t88[(y * stride) + (x >> 3)] >> (7 - (x & 7))) & 1;
                int sample = (pdfSamples[(y * actualStride) + (x >> 3)] >> (7 - (x & 7))) & 1;
                Assert.True(black == 1 - sample, $"pixel ({x},{y}) came back as sample {sample} for a T.88 bit of {black}");
            }
    }
}
