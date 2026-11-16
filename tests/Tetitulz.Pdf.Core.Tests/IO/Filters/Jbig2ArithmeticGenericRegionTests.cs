// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the arithmetic-coded generic region decoder (template 0).
///
/// <para>
/// These tests cover structural behaviour only — dispatch routing, segment
/// parsing, and safe-fallback guarantees.  End-to-end bit-exact output
/// requires validation against a reference decoder (an independent decoder / another reader)
/// with a real JBIG2 corpus; the decoder is labelled <i>experimental</i>
/// in the diagnostic surface until that validation happens.
/// </para>
/// </summary>
public class Jbig2ArithmeticGenericRegionTests
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

    private static byte[] PageInfoBody(uint width, uint height)
    {
        var body = new byte[19];
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        return body;
    }

    // Builds a generic-region segment body with the given width/height + flags.
    // Layout: 17-byte region-info header + 1 flags + 8 AT-pixel bytes (template 0) + arith data.
    private static byte[] GenericRegionArithBody(int width, int height,
        int gbtemplate, bool tpgdon, byte[] arithData)
    {
        var body = new byte[17 + 1 + 8 + arithData.Length];
        // Region info header (17 bytes): width(4) height(4) x(4) y(4) flags(1).
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        // x, y = 0
        // Generic-region flags byte at offset 17: MMR=0, TEMPLATE=gbtemplate, TPGDON.
        byte flags = 0;
        flags |= (byte)((gbtemplate & 0x03) << 1);
        if (tpgdon) flags |= 0x08;
        body[17] = flags;
        // AT pixels at 18..25 — default positions for template 0:
        // AT1=(+3,-1), AT2=(-3,-1), AT3=(+2,-2), AT4=(-2,-2).  (Two's complement.)
        body[18] = 0x03; body[19] = 0xFF;   // AT1
        body[20] = 0xFD; body[21] = 0xFF;   // AT2
        body[22] = 0x02; body[23] = 0xFE;   // AT3
        body[24] = 0xFE; body[25] = 0xFE;   // AT4
        // Arithmetic-coded data
        Buffer.BlockCopy(arithData, 0, body, 26, arithData.Length);
        return body;
    }

    // ==================================================================
    //  Dispatch: arithmetic generic region no longer always-unsupported
    // ==================================================================

    [Fact]
    public void Diagnose_ArithmeticTemplate0_IsDecodedExperimental()
    {
        var genericBody = GenericRegionArithBody(16, 8, gbtemplate: 0, tpgdon: false,
            arithData: new byte[] { 0x00, 0x00, 0x00, 0x00 });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 8),
            SegmentHeader(1, 38, 1, (uint)genericBody.Length), genericBody,
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("template 0", diag[1].Notes);
        Assert.Contains("experimental", diag[1].Notes);
    }

    [Fact]
    public void Diagnose_ArithmeticTemplate1_StillUnsupported()
    {
        // Per GAPS 1.5 the arithmetic template framework now decodes
        // experimentally for all four templates; Diagnose reports Decoded
        // with "experimental" and the template number so triage tooling can
        // still route corpus-validation work appropriately.
        var genericBody = GenericRegionArithBody(16, 8, gbtemplate: 1, tpgdon: false,
            arithData: new byte[] { 0x00, 0x00, 0x00, 0x00 });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 8),
            SegmentHeader(1, 38, 1, (uint)genericBody.Length), genericBody,
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("template 1", diag[1].Notes);
    }

    // ==================================================================
    //  End-to-end: decoder produces a page bitmap of the right size
    //  without throwing.  Pixel values are NOT validated here — only
    //  that the decode completes and shapes match.
    // ==================================================================

    [Fact]
    public void Decode_ArithmeticT0_ProducesBitmapOfExpectedSize()
    {
        // 24x8 page, single arithmetic generic region covering the full page.
        // Arithmetic payload is arbitrary bytes — we don't care what pixels come
        // out, only that decode runs to completion and the output bitmap has
        // the right dimensions.
        var genericBody = GenericRegionArithBody(24, 8, gbtemplate: 0, tpgdon: false,
            arithData: new byte[] { 0x84, 0xC7, 0x3B, 0xFC, 0xE1, 0xA4, 0x73, 0x21 });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(24, 8),
            SegmentHeader(1, 38, 1, (uint)genericBody.Length), genericBody,
            SegmentHeader(2, 49, 1, 0),
            SegmentHeader(3, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);

        // 24 × 8 at 1bpp = 3 bytes per row × 8 rows = 24 bytes.
        Assert.Equal(24, decoded.Length);
    }

    /// <summary>
    /// With TPGDON set, template 0 must still decode to a packed 1-bpp bitmap of the declared
    /// 16×4 page — 2 bytes per row × 4 rows — and the typical-prediction path must actually
    /// produce pixels rather than leaving the page untouched.
    ///
    /// <para>Both halves are the point. The filter's failure mode is a <b>silent passthrough of
    /// the compressed input</b>, whose length is the 84-byte segment stream, not 8; and a decoder
    /// that consumed the data and wrote nothing would still return 8 zero bytes. The predecessor
    /// asserted only non-null and non-empty, which the passthrough satisfies.</para>
    /// </summary>
    [Fact]
    public void Decode_ArithmeticT0_WithTpgdon_ProducesTheDeclaredPageBitmap()
    {
        const int pageW = 16, pageH = 4;
        var genericBody = GenericRegionArithBody(pageW, pageH, gbtemplate: 0, tpgdon: true,
            arithData: new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(pageW, pageH),
            SegmentHeader(1, 38, 1, (uint)genericBody.Length), genericBody,
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);

        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Equal((pageW + 7) / 8 * pageH, decoded.Length);
        Assert.NotEqual(stream.Length, decoded.Length);

        // Not a uniform fill: the MQ decoder wrote real, varying pixels into the page.
        Assert.True(decoded.Distinct().Count() > 1,
            "every byte is 0x" + decoded[0].ToString("X2") +
            " — the region decoded to a uniform page, so no pixels were produced");
    }

    [Fact]
    /// <summary>
    /// The wrapper filter's contract for a region it decodes only experimentally: it must never
    /// throw on synthetic input, and it must return a <b>usable, deterministic</b> result — either
    /// the experimental bitmap for the declared page size, or the raw bytes it fell back to.
    ///
    /// <para>The predecessor asserted <c>NotNull</c> and said so in its comment ("we just verify
    /// Decode doesn't throw"). That admits garbage of any length, and cannot tell a stable
    /// fallback from one that returns something different on every call.</para>
    /// </summary>
    public void Decode_ArithmeticT1_ProducesAUsableDeterministicResult()
    {
        var genericBody = GenericRegionArithBody(16, 4, gbtemplate: 1, tpgdon: false,
            arithData: new byte[] { 0x00, 0x11, 0x22, 0x33 });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 4),
            SegmentHeader(1, 38, 1, (uint)genericBody.Length), genericBody,
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);

        AssertUsableAndDeterministic(decoded, stream, 16, 4);
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
