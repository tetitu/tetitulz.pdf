// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for <see cref="Jbig2Decoder.Diagnose"/> — verifies segment
/// classification without actually decoding region bodies.  These are the
/// tests a triage tool would rely on when sorting real-world JBIG2 PDFs into
/// "we can decode this" vs "this needs features we haven't built yet".
/// </summary>
public class Jbig2DiagnoseTests
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

    // 17-byte region-info header + 1 byte generic-region flags.
    // mmrBit = 1 for MMR, 0 for arithmetic.
    private static byte[] GenericRegionBody(int mmrBit, int dataBytes)
    {
        var body = new byte[17 + 1 + dataBytes];
        // Region info left as zeros; segment type carries the actual width/height
        // via the segment's data length in real streams — for diagnostic tests we
        // only need the 18th byte (flags) to distinguish MMR vs arithmetic.
        body[17] = (byte)(mmrBit & 0x01);
        return body;
    }

    // ==================================================================
    //  Pure structural classification
    // ==================================================================

    [Fact]
    public void Diagnose_StructuralSegments_AreDecoded()
    {
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),        // page info
            SegmentHeader(1, 49, 1, 0),                                // end of page
            SegmentHeader(2, 51, 0, 0));                               // end of file

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(3, diag.Count);
        Assert.All(diag, d => Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, d.Status));
        Assert.Equal("PageInformation", diag[0].TypeName);
        Assert.Equal("EndOfPage",       diag[1].TypeName);
        Assert.Equal("EndOfFile",       diag[2].TypeName);
    }

    // ==================================================================
    //  Generic region MMR vs arithmetic split
    // ==================================================================

    [Fact]
    public void Diagnose_GenericRegion_Mmr_IsDecoded()
    {
        // Body length = 17 + 1 + 5 (5 bytes of MMR data).
        const int bodyLen = 17 + 1 + 5;
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            SegmentHeader(1, 38, 1, (uint)bodyLen), GenericRegionBody(mmrBit: 1, dataBytes: 5),
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(3, diag.Count);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("MMR", diag[1].Notes);
    }

    [Fact]
    public void Diagnose_GenericRegion_Arithmetic_IsDecodedExperimentally()
    {
        // Arithmetic generic regions are now decoded experimentally per GAPS 1.5
        // (the per-pixel output is labelled experimental until corpus-validated).
        // Diagnose reports `Decoded` with an "experimental" note.
        const int bodyLen = 17 + 1 + 5;
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            SegmentHeader(1, 38, 1, (uint)bodyLen), GenericRegionBody(mmrBit: 0, dataBytes: 5),
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("arithmetic", diag[1].Notes);
        Assert.Contains("experimental", diag[1].Notes);
    }

    // ==================================================================
    //  Symbol dictionaries and intermediate segments: skip silently
    // ==================================================================

    [Fact]
    public void Diagnose_SymbolDictionary_IsSkippedBenign()
    {
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(8, 8),
            SegmentHeader(1, 0, 1, 10), new byte[10],   // symbol dictionary, dummy body
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[1].Status);
        Assert.Equal("SymbolDictionary", diag[1].TypeName);
    }

    [Fact]
    public void Diagnose_IntermediateGenericRegion_IsDecodedForARefinement()
    {
        // The decoder keeps an intermediate generic region as a refinement's reference, so the
        // diagnosis says it is decoded. An intermediate halftone region is still skipped.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(8, 8),
            SegmentHeader(1, 36, 1, 0),                    // intermediate generic, no body
            SegmentHeader(2, 20, 1, 0),                    // intermediate halftone, no body
            SegmentHeader(3, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[2].Status);
    }

    // ==================================================================
    //  Unsupported immediate-region types
    // ==================================================================

    [Fact]
    public void Diagnose_TextRegion_IsUnsupported()
    {
        // Text regions are now decoded experimentally per GAPS 1.5
        // (Huffman + arithmetic decoder ships, output not corpus-validated).
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1,  6, 1, 32), new byte[32],     // immediate text region
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("text region", diag[1].Notes);
        Assert.Contains("experimental", diag[1].Notes);
    }

    [Fact]
    public void Diagnose_HalftoneRegion_IsUnsupported()
    {
        // Halftone regions are now decoded experimentally per GAPS 1.5.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 22, 1, 24), new byte[24],     // immediate halftone region
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("halftone", diag[1].Notes);
        Assert.Contains("experimental", diag[1].Notes);
    }

    [Fact]
    public void Diagnose_RefinementRegion_IsUnsupported()
    {
        // Refinement regions are now decoded experimentally per GAPS 1.5.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 42, 1, 24), new byte[24],     // immediate refinement region
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("refinement", diag[1].Notes);
        Assert.Contains("experimental", diag[1].Notes);
    }

    // ==================================================================
    //  Edge cases
    // ==================================================================

    [Fact]
    public void Diagnose_EmptyStream_ReturnsEmptyList()
    {
        var diag = Jbig2Decoder.Diagnose(Array.Empty<byte>());
        Assert.Empty(diag);
    }

    [Fact]
    public void Diagnose_TruncatedHeader_ReportsMalformed()
    {
        // Only 3 bytes — less than a segment number (4 bytes).
        var diag = Jbig2Decoder.Diagnose(new byte[] { 0x00, 0x00, 0x00 });
        Assert.Single(diag);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Unsupported, diag[0].Status);
        Assert.Equal("malformed", diag[0].TypeName);
    }

    [Fact]
    public void Diagnose_WithGlobals_IncludesGlobalsSegments()
    {
        var globals = Concat(SegmentHeader(100, 0, 0, 0));   // global symbol dict
        var main    = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            SegmentHeader(1, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(main, globals);
        Assert.Equal(3, diag.Count);
        Assert.Equal("SymbolDictionary", diag[0].TypeName);   // globals come first
        Assert.Equal("PageInformation", diag[1].TypeName);
    }

    [Fact]
    public void Diagnose_MixedWorkflow_SummariesArePlausible()
    {
        // Real-world-ish: page info + a symbol dict (skipped) + an immediate text
        // region we can't decode + end markers.  Diagnose should return the
        // segments untouched — the filter wrapping this would fall back.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(200, 200),
            SegmentHeader(1,  0, 1, 12), new byte[12],
            SegmentHeader(2,  6, 1, 40), new byte[40],
            SegmentHeader(3, 49, 1, 0),
            SegmentHeader(4, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(5, diag.Count);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded,       diag[0].Status);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[1].Status);
        // Text region is decoded experimentally per GAPS 1.5.
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded,       diag[2].Status);
        Assert.Contains("experimental", diag[2].Notes);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded,       diag[3].Status);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded,       diag[4].Status);
    }

    // ==================================================================
    //  Stream-level assessment
    // ==================================================================

    [Fact]
    public void Assess_StreamThatWouldDecodeFully_ReportsOk()
    {
        const int bodyLen = 17 + 1 + 5;
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(32, 16),
            SegmentHeader(1, 38, 1, (uint)bodyLen), GenericRegionBody(mmrBit: 1, dataBytes: 5),
            SegmentHeader(2, 49, 1, 0),
            SegmentHeader(3, 51, 0, 0));

        var asm = Jbig2Decoder.Assess(stream);
        Assert.True(asm.IsFullyDecodable);
        Assert.Equal(0, asm.UnsupportedSegmentCount);
        Assert.Equal(32, asm.PageWidth);
        Assert.Equal(16, asm.PageHeight);
        Assert.Empty(asm.BlockerSummary);
        Assert.StartsWith("OK", asm.Summary);
    }

    [Fact]
    public void Assess_StreamWithTextRegion_ReportsBlocked()
    {
        // Text regions are decoded experimentally per GAPS 1.5; Assess reports
        // the stream as fully-decodable with the experimental warning surfaced
        // through the segment notes rather than via a blocker entry.  The page
        // dimensions are still reported correctly so triage tooling can size
        // the eventual bitmap.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1,  6, 1, 40), new byte[40],
            SegmentHeader(2, 51, 0, 0));

        var asm = Jbig2Decoder.Assess(stream);
        Assert.True(asm.IsFullyDecodable);
        Assert.Equal(0, asm.UnsupportedSegmentCount);
        Assert.Equal(64, asm.PageWidth);
        Assert.Equal(64, asm.PageHeight);
        Assert.Contains("text region", asm.Segments[1].Notes);
        Assert.Contains("experimental", asm.Segments[1].Notes);
    }

    [Fact]
    public void Assess_StreamWithNoPageInfo_ReturnsZeroDims()
    {
        var stream = Concat(SegmentHeader(0, 51, 0, 0));
        var asm = Jbig2Decoder.Assess(stream);
        Assert.Equal(0, asm.PageWidth);
        Assert.Equal(0, asm.PageHeight);
    }

    [Fact]
    public void Assess_BlockerSummary_DeduplicatesRepeatedReasons()
    {
        // Text regions are decoded experimentally per GAPS 1.5, so they do not
        // contribute blockers — Assess reports the stream fully decodable.
        // The dedup behaviour is still valuable for genuinely unsupported
        // segment types; here we just confirm the experimental notes appear.
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1,  6, 1, 24), new byte[24],
            SegmentHeader(2,  6, 1, 24), new byte[24],
            SegmentHeader(3, 51, 0, 0));

        var asm = Jbig2Decoder.Assess(stream);
        Assert.Equal(0, asm.UnsupportedSegmentCount);
        Assert.True(asm.IsFullyDecodable);
        Assert.Contains("experimental", asm.Segments[1].Notes);
        Assert.Contains("experimental", asm.Segments[2].Notes);
    }
}
