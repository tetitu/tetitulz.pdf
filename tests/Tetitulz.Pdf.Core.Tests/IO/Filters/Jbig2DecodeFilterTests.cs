// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the partial JBIG2 decoder.  Covers the segment-parsing framework
/// and the safe-fallback behaviour.  End-to-end generic-region decoding isn't
/// tested because synthesising a valid JBIG2 bitstream by hand is impractical
/// (each region still needs MMR-T.6 encoded data which requires an encoder).
/// </summary>
public class Jbig2DecodeFilterTests
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

    // Minimal segment header builder (small segment numbers → 1-byte reference
    // width; no referred-to segments, no retention flags).
    private static byte[] SegmentHeader(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength)
    {
        // Segment number (4 bytes BE)
        // Segment flags (1 byte): low 6 bits = type, bit 7 = page-assoc 32-bit (unused)
        // Referred-to bytes (1): count in high 3 bits = 0, low 5 bits reserved
        // Page association (1 byte, since flag bit 7 = 0)
        // Data length (4 bytes BE)
        return new byte[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),  (byte)(segmentNumber & 0xFF),
            (byte)(segmentType & 0x3F),
            0x00,   // 0 referred-to segments
            (byte)(pageAssociation & 0xFF),
            (byte)(dataLength >> 24), (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),  (byte)(dataLength & 0xFF),
        };
    }

    // Page info segment body: 19 bytes.
    private static byte[] PageInfoBody(uint width, uint height)
    {
        var body = new byte[19];
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        // xres, yres, flags, stripingInfo remain zero.
        return body;
    }

    // ==================================================================
    //  Segment parser
    // ==================================================================

    [Fact]
    public void Enumerate_PageInfoThenEof_ReturnsTwoSegments()
    {
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(100, 200),
            SegmentHeader(1, 51, 1, 0));   // end of file, no body

        var segs = Jbig2Decoder.EnumerateSegments(stream);
        Assert.Equal(2, segs.Count);
        Assert.Equal(48, segs[0].SegmentType);
        Assert.Equal((uint)19, segs[0].DataLength);
        Assert.Equal(51, segs[1].SegmentType);
        Assert.Equal((uint)0, segs[1].DataLength);
    }

    [Fact]
    public void Enumerate_StripsFileHeaderBeforeParsing()
    {
        byte[] fileHeader =
        {
            0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A,
            0x03,                                   // flags: number-of-pages-unknown (bit 1 set) + sequential (bit 0 set)
        };
        var stream = Concat(fileHeader,
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            SegmentHeader(1, 51, 1, 0));

        var segs = Jbig2Decoder.EnumerateSegments(stream);
        Assert.Equal(2, segs.Count);
        Assert.Equal(48, segs[0].SegmentType);
    }

    // ==================================================================
    //  Decode end-to-end (no generic regions) — empty page bitmap.
    // ==================================================================

    /// <summary>
    /// A page-information segment on its own describes an empty page, and an empty page is
    /// <b>white</b>. Because the decoded bytes are consumed as 1-bit DeviceGray samples — 0 is
    /// black, the default <c>/Decode</c> is <c>[0 1]</c> (§8.9.5.2) — white must come back as
    /// <c>0xFF</c>, not as the all-zero bitmap T.88 builds internally.
    ///
    /// <para>T.88's polarity (1 = black) reaching the image machinery unconverted would make
    /// <b>this very stream — a blank page — paint solid black</b>, and a real scan render
    /// white-on-black. Nothing downstream can repair it, because nothing downstream can tell a
    /// JBIG2-sourced 1-bpc image from any other.</para>
    /// </summary>
    [Fact]
    public void Decode_PageInfoOnly_ReturnsAWhitePageBitmap_InThePdfSampleConvention()
    {
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(width: 16, height: 4),
            SegmentHeader(1, 49, 1, 0),   // end of page
            SegmentHeader(2, 51, 0, 0));  // end of file

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);

        // 16 × 4 bitmap at 1 bpp → ceil(16/8) * 4 = 2 * 4 = 8 bytes.
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Equal(8, decoded.Length);
        foreach (byte b in decoded) Assert.Equal(0xFF, b);
    }

    // No switch restores the inverted polarity: a compatibility flag for a defect is how the
    // defect comes back. The test above asserts the polarity on the one stream where the two
    // readings are exact complements of each other, a blank page.

    // ==================================================================
    //  Fallback behaviour — unsupported features must not throw.
    // ==================================================================

    /// <summary>
    /// The wrapper filter's contract for a region it decodes only experimentally: it must never
    /// throw on synthetic input, and it must return a <b>usable, deterministic</b> result — either
    /// the experimental bitmap for the declared page size, or the raw bytes it fell back to.
    ///
    /// <para>The predecessor asserted <c>NotNull</c> and said so in its comment ("we just verify
    /// Decode doesn't throw"). That admits garbage of any length, and cannot tell a stable
    /// fallback from one that returns something different on every call.</para>
    /// </summary>
    [Fact]
    public void Decode_TextRegion_ProducesAUsableDeterministicResult()
    {
        var textBody = new byte[32];
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 6,  1, (uint)textBody.Length), textBody,
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);

        AssertUsableAndDeterministic(decoded, stream, 64, 64);
    }

    [Fact]
    public void Decode_CorruptInput_FallsBackWithoutException()
    {
        byte[] garbage = { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02 };
        var filter = new Jbig2DecodeFilter();

        var ex = Record.Exception(() =>
        {
            byte[] decoded = filter.Decode(garbage, parameters: null);
            Assert.NotNull(decoded);
            Assert.NotEmpty(decoded);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Decode_EmptyInput_ReturnsEmpty()
    {
        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(Array.Empty<byte>(), parameters: null);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Decode_MissingPageInfo_FallsBack()
    {
        // Only an end-of-file segment — no page info → throw inside decoder → fallback.
        var stream = Concat(SegmentHeader(0, 51, 0, 0));
        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);
        Assert.Equal(stream, decoded);   // fell back to raw
    }

    // ==================================================================
    //  Bitmap combination helpers
    // ==================================================================

    [Fact]
    public void Jbig2Bitmap_SetAndGetPixel_RoundTrips()
    {
        var bmp = new Jbig2Decoder.Jbig2Bitmap(16, 4);
        bmp.SetPixel(0, 0, 1);
        bmp.SetPixel(15, 3, 1);
        bmp.SetPixel(7, 2, 1);
        Assert.Equal(1, bmp.GetPixel(0, 0));
        Assert.Equal(1, bmp.GetPixel(15, 3));
        Assert.Equal(1, bmp.GetPixel(7, 2));
        Assert.Equal(0, bmp.GetPixel(1, 0));
        Assert.Equal(0, bmp.GetPixel(0, 1));
    }

    [Fact]
    public void Jbig2Bitmap_DrawAt_OrOperator_CombinesTwoBitmaps()
    {
        var page = new Jbig2Decoder.Jbig2Bitmap(16, 4);
        var src  = new Jbig2Decoder.Jbig2Bitmap(8, 2);
        src.SetPixel(0, 0, 1);
        src.SetPixel(7, 1, 1);

        page.DrawAt(src, dstX: 4, dstY: 1, combinationOperator: 0);

        Assert.Equal(1, page.GetPixel(4, 1));
        Assert.Equal(1, page.GetPixel(11, 2));
        Assert.Equal(0, page.GetPixel(0, 0));
    }

    [Fact]
    public void Jbig2Bitmap_DrawAt_ReplaceOperator_OverwritesDestination()
    {
        var page = new Jbig2Decoder.Jbig2Bitmap(8, 2);
        page.SetPixel(0, 0, 1);
        page.SetPixel(1, 0, 1);
        var src = new Jbig2Decoder.Jbig2Bitmap(8, 2);   // all zero
        page.DrawAt(src, 0, 0, combinationOperator: 4);

        Assert.Equal(0, page.GetPixel(0, 0));
        Assert.Equal(0, page.GetPixel(1, 0));
    }

    // ==================================================================
    //  JBIG2Globals concatenation
    // ==================================================================

    [Fact]
    public void Decode_WithJBIG2Globals_PrependsGlobals()
    {
        // Globals: only EOF (doesn't affect decoding, but must be consumable).
        var globals = Concat(SegmentHeader(100, 51, 0, 0));
        var main    = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(8, 1),
            SegmentHeader(1, 51, 0, 0));

        var globalsStream = new PdfStream { RawData = globals };
        var parms = new PdfDictionary();
        parms[new PdfName("JBIG2Globals")] = globalsStream;

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(main, parms);

        // 8 wide, 1 row → 1 byte, all white. White is 0xFF here and not 0x00: the T.88 page
        // bitmap is all-zero (its default pixel value), and the filter inverts it into the PDF
        // sample convention where 0 is black — see PageInfoOnly_DecodesToWhite_NotSolidBlack.
        Assert.Single(decoded);
        Assert.Equal((byte)0xFF, decoded[0]);
    }

    // ==================================================================
    //  Output polarity (§7.4.7 → §8.9.5.2)
    // ==================================================================

    /// <summary>
    /// The passthrough is <b>not</b> inverted: those bytes are the compressed input, handed on so a
    /// host can give the blob to an external JBIG2 library, and flipping them would corrupt it.
    /// </summary>
    [Fact]
    public void FallbackPassthrough_ReturnsTheCompressedBytesUnaltered()
    {
        // A page wider than any bitmap the decoder will allocate is refused by HandlePageInfo, so the
        // filter falls back — the shortest route to the passthrough that does not depend on a region
        // decoder. (An unknown-height page now decodes; see Jbig2UnknownHeightPageTests.)
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(0x7FFFFFFF, 64),
            SegmentHeader(1, 51, 0, 0));

        byte[] decoded = new Jbig2DecodeFilter().Decode(stream, parameters: null);

        Assert.False(string.IsNullOrEmpty(Jbig2DecodeFilter.LastFallbackReason));
        Assert.Equal(stream, decoded);
    }

    /// <summary>
    /// Asserts a JBIG2 decode produced one of its two permitted outcomes — the raw passthrough or
    /// a bitmap of the declared page size — and that it is deterministic across calls.
    /// </summary>
    private static void AssertUsableAndDeterministic(
        byte[] decoded, byte[] rawStream, int pageWidth, int pageHeight)
    {
        Assert.NotNull(decoded);
        Assert.NotEmpty(decoded);

        int bitmapBytes = (pageWidth + 7) / 8 * pageHeight;
        Assert.True(decoded.Length == rawStream.Length || decoded.Length == bitmapBytes,
            $"expected either the raw passthrough ({rawStream.Length} bytes) or a " +
            $"{pageWidth}x{pageHeight} bitmap ({bitmapBytes} bytes), got {decoded.Length}");

        byte[] again = new Jbig2DecodeFilter().Decode(rawStream, parameters: null);
        Assert.Equal(decoded, again);
    }
}
