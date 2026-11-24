// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Structural tests for the extended JBIG2 decoder surface:
///  • Arithmetic generic regions templates 1, 2, 3
///  • Symbol dictionary decoder (arithmetic coding only)
///  • Text region decoder (arithmetic coding only)
///  • Diagnose classification of text regions + symbol dicts by coding mode
/// </summary>
public class Jbig2ExtendedSupportTests
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

    // Generic region body with variable AT byte count depending on template.
    private static byte[] GenericRegionBody(int width, int height,
        int gbtemplate, bool tpgdon, byte[] arithData)
    {
        int atCount = gbtemplate == 0 ? 8 : 2;
        var body = new byte[17 + 1 + atCount + arithData.Length];
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        byte flags = (byte)((gbtemplate & 0x03) << 1);
        if (tpgdon) flags |= 0x08;
        body[17] = flags;
        // AT bytes left at 0 (signed zero is a valid override).
        Buffer.BlockCopy(arithData, 0, body, 17 + 1 + atCount, arithData.Length);
        return body;
    }

    // ==================================================================
    //  Arithmetic generic region — templates 1, 2, 3
    // ==================================================================

    [Fact]
    public void Decode_ArithmeticTemplate1_ProducesBitmap()
    {
        var body = GenericRegionBody(16, 4, gbtemplate: 1, tpgdon: false,
            arithData: new byte[] { 0x00, 0x11, 0x22, 0x33 });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 4),
            SegmentHeader(1, 38, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);
        // 16 × 4 at 1bpp = 2 × 4 = 8 bytes.
        Assert.Equal(8, decoded.Length);
    }

    [Fact]
    public void Decode_ArithmeticTemplate2_ProducesBitmap()
    {
        var body = GenericRegionBody(8, 4, gbtemplate: 2, tpgdon: false,
            arithData: new byte[] { 0x00, 0x11 });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(8, 4),
            SegmentHeader(1, 38, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);
        Assert.Equal(4, decoded.Length);
    }

    [Fact]
    public void Decode_ArithmeticTemplate3_ProducesBitmap()
    {
        var body = GenericRegionBody(8, 4, gbtemplate: 3, tpgdon: false,
            arithData: new byte[] { 0x00, 0x11 });
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(8, 4),
            SegmentHeader(1, 38, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);
        Assert.Equal(4, decoded.Length);
    }

    // ==================================================================
    //  Symbol dictionary — arithmetic decoded when referenced
    // ==================================================================

    [Fact]
    public void Diagnose_SymbolDict_Arithmetic_IsBenignSkipNotePointsToDecoding()
    {
        // Body: 2-byte flags (SDHUFF=0, SDTEMPLATE=0) + 8 AT bytes + 8 count bytes.
        var body = new byte[2 + 8 + 8];
        // flags = 0 → arithmetic, template 0, no refagg
        var stream = Concat(
            SegmentHeader(0, 0, 1, (uint)body.Length), body,
            SegmentHeader(1, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[0].Status);
        Assert.Contains("arithmetic", diag[0].Notes);
    }

    [Fact]
    public void Diagnose_SymbolDict_Huffman_NonAggregated_IsSkippedBenign()
    {
        // SDHUFF=1, SDREFAGG=0. The non-aggregated case decodes: the collective bitmap is parsed
        // (MMR or uncompressed) and split by accumulated symbol widths, using the externally
        // verified Annex B tables. A symbol dictionary produces no page content by itself regardless
        // of coding mode, so — matching the arithmetic symbol dictionary's own classification —
        // this is SkippedBenign, not Decoded, and (this row's whole point) not Unsupported either.
        var body = new byte[2 + 8];
        body[0] = 0x00; body[1] = 0x01; // SDHUFF=1, SDREFAGG=0
        var stream = Concat(
            SegmentHeader(0, 0, 1, (uint)body.Length), body,
            SegmentHeader(1, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[0].Status);
    }

    /// <summary>
    /// A Huffman symbol dictionary with SDREFAGG=1 is decoded as it is read, each aggregate symbol
    /// placed by its own Huffman-coded instances (GAPS.md §2a), so it classifies as a dictionary
    /// decoded when referenced, not as a refusal.
    /// </summary>
    [Fact]
    public void Diagnose_SymbolDict_Huffman_RefinementAggregation_IsSkippedBenign()
    {
        var body = new byte[2 + 8];
        body[0] = 0x00; body[1] = 0x03; // SDHUFF=1, SDREFAGG=1
        var stream = Concat(
            SegmentHeader(0, 0, 1, (uint)body.Length), body,
            SegmentHeader(1, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[0].Status);
        Assert.Contains("symbol dictionary (Huffman)", diag[0].Notes);
    }

    // ==================================================================
    //  Text region — arithmetic decoded as experimental
    // ==================================================================

    [Fact]
    public void Diagnose_TextRegion_Arithmetic_IsDecoded()
    {
        // 17-byte region info + 2-byte flags (SBHUFF=0).
        var body = new byte[17 + 2 + 32];
        // region width/height/x/y = 0 (bytes 0-15) ; region-info flags byte (16) = 0.
        // SBFLAGS at bytes 17-18 = 0, 0 → SBHUFF=0.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 6, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("experimental", diag[1].Notes);
    }

    [Fact]
    public void Diagnose_TextRegion_Huffman_NonRefined_IsDecoded()
    {
        // SBHUFF=1, SBREFINE=0. SBFLAGS is a 16-bit big-endian field at body offset 17 and
        // SBHUFF is its bit 0, so the flag sits in the SECOND of those two bytes — offset 18.
        // A non-refined Huffman text region now decodes for real: the runcode-coded symbol-ID
        // table (T.88 §7.4.3.1.7) is built and used instead of approximated with raw log2(N)
        // bits (GAPS.md §2a).
        var body = new byte[17 + 2 + 32];
        body[18] = 0x01;
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 6, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("text region", diag[1].Notes);
    }

    /// <summary>
    /// A Huffman text region with SBREFINE=1 decodes per instance (T.88 §6.4.11), so it classifies as
    /// decoded, not as a refusal (GAPS.md §2a).
    /// </summary>
    [Fact]
    public void Diagnose_TextRegion_Huffman_Refined_IsDecoded()
    {
        var body = new byte[17 + 2 + 32];
        body[18] = 0x03; // SBHUFF=1, SBREFINE=1
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 6, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("text region", diag[1].Notes);
    }

    // ==================================================================
    //  Halftone / refinement regions still unsupported at top level
    // ==================================================================

    [Fact]
    public void Decode_HalftoneRegion_StillFallsBack()
    {
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 22, 1, 32), new byte[32],
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);
        // Halftone region → throws → filter falls back to raw bytes.
        Assert.Equal(stream, decoded);
    }

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
    public void Decode_RefinementRegion_ProducesAUsableDeterministicResult()
    {
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 42, 1, 32), new byte[32],
            SegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);

        AssertUsableAndDeterministic(decoded, stream, 64, 64);
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
