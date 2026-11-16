// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Two JBIG2 findings that both surface as "the image is wrong but nothing said so".
/// </summary>
public class Jbig2PageDefaultsAndGlobalsTests
{
    // ==================================================================
    //  Page information segment — default pixel value (T.88 §7.4.8.5 bit 2)
    // ==================================================================

    [Fact]
    public void Decode_PageWithDefaultPixelValueSet_StartsBlackNotWhite()
    {
        // The page-info flags byte was read and discarded, and a new bitmap is always all-zero, so
        // a page declared black started white. A region composited onto it with AND to punch white
        // text out of a black field then computes `s & d` against d = 0 everywhere, the whole page
        // decodes to 0, the filter inverts it, and the reader draws SOLID BLACK.
        byte[] black = Decode(defaultPixelValue: true, width: 16, height: 4);
        byte[] white = Decode(defaultPixelValue: false, width: 16, height: 4);

        // The filter inverts T.88 polarity at the PDF boundary, so a T.88 "1 = black" page comes
        // back as 0x00 samples and a white page as 0xFF.
        Assert.All(black, b => Assert.Equal(0x00, b));
        Assert.All(white, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void Decode_PageWithDefaultPixelValueSet_MasksThePaddingBitsPastTheWidth()
    {
        // 12 columns = 2 bytes per row with 4 padding bits. Those bits are unreachable through
        // GetPixel, but DrawAt and the filter's inversion read whole bytes, so a set padding bit
        // would surface as ink past the page edge.
        byte[] page = Decode(defaultPixelValue: true, width: 12, height: 2);

        Assert.Equal(4, page.Length);            // 2 bytes per row, 2 rows
        // Inverted: the 12 used bits are 0, the 4 padding bits are 1.
        Assert.Equal(0x00, page[0]);
        Assert.Equal(0x0F, page[1]);
    }

    private static byte[] Decode(bool defaultPixelValue, uint width, uint height)
    {
        var body = new byte[19];
        WriteUInt32(body, 0, width);
        WriteUInt32(body, 4, height);
        // body[16] is the page segment flags byte; bit 2 is the default pixel value.
        body[16] = (byte)(defaultPixelValue ? 0x04 : 0x00);

        byte[] stream = Concat(
            SegmentHeader(0, 48, 1, 19), body,
            SegmentHeader(1, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] output = filter.Decode(stream, parameters: null);
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        return output;
    }

    // ==================================================================
    //  /JBIG2Globals failure must not be reported as a failed decode
    // ==================================================================

    [Fact]
    public void Decode_UnusableGlobalsButSelfContainedPage_ReturnsPixelsWithNoFallbackReason()
    {
        // The globals failure used to be written straight into LastFallbackReason and then fall
        // through to a decode that succeeds — leaving the slot non-null beside a real bitmap and
        // inverting the documented contract ("a non-null value means the returned array is not a
        // bitmap"). A caller following that contract discarded a valid image and drew nothing.
        var parms = new PdfDictionary();
        parms[PdfName.JBIG2Globals] = new PdfNumber(8);   // present, not a stream

        var body = new byte[19];
        WriteUInt32(body, 0, 16);
        WriteUInt32(body, 4, 4);
        byte[] stream = Concat(SegmentHeader(0, 48, 1, 19), body, SegmentHeader(1, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] output = filter.Decode(stream, parms);

        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);   // these bytes ARE pixels
        Assert.NotEqual(stream, output);
        Assert.Equal(2 * 4, output.Length);                  // 16 px wide, 4 rows

        // …and the globals problem is still reported, on its own slot.
        Assert.NotNull(Jbig2DecodeFilter.LastGlobalsWarning);
        Assert.Contains("JBIG2Globals", Jbig2DecodeFilter.LastGlobalsWarning!);
    }

    [Fact]
    public void Decode_UnusableGlobalsAndAFailedDecode_StillLeadsWithTheGlobalsRootCause()
    {
        // When the decode DOES fail, the globals problem is very often the root cause, so it must
        // still lead the fallback reason rather than being replaced by the symptom.
        var parms = new PdfDictionary();
        parms[PdfName.JBIG2Globals] = new PdfNumber(8);

        // A text region with no page-info segment: the decoder throws.
        byte[] stream = Concat(SegmentHeader(0, 6, 1, 4), new byte[4]);

        var filter = new Jbig2DecodeFilter();
        byte[] output = filter.Decode(stream, parms);

        Assert.Equal(stream, output);                        // raw passthrough
        Assert.NotNull(Jbig2DecodeFilter.LastFallbackReason);
        Assert.StartsWith("/JBIG2Globals", Jbig2DecodeFilter.LastFallbackReason!);
        Assert.Contains("Decoding then failed", Jbig2DecodeFilter.LastFallbackReason!);
    }

    [Fact]
    public void Decode_NoGlobalsAtAll_LeavesTheGlobalsWarningNull()
    {
        var body = new byte[19];
        WriteUInt32(body, 0, 16);
        WriteUInt32(body, 4, 4);
        byte[] stream = Concat(SegmentHeader(0, 48, 1, 19), body, SegmentHeader(1, 51, 0, 0));

        new Jbig2DecodeFilter().Decode(stream, parameters: null);

        Assert.Null(Jbig2DecodeFilter.LastGlobalsWarning);
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
    }

    // ------------------------------------------------------------------ helpers

    private static void WriteUInt32(byte[] b, int offset, uint v)
    {
        b[offset] = (byte)(v >> 24); b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8); b[offset + 3] = (byte)v;
    }

    private static byte[] SegmentHeader(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength) => new byte[]
    {
        (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
        (byte)(segmentNumber >> 8),  (byte)segmentNumber,
        (byte)(segmentType & 0x3F),
        0x00,
        (byte)pageAssociation,
        (byte)(dataLength >> 24), (byte)(dataLength >> 16),
        (byte)(dataLength >> 8),  (byte)dataLength,
    };

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
