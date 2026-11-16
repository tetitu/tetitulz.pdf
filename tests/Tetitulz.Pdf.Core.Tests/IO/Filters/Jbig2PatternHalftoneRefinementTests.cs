// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Structural tests for pattern dictionaries, halftone regions, and
/// immediate refinement regions.  Like earlier JBIG2 test files, these are
/// structural-only — byte-exact output requires corpus validation against
/// a reference decoder.
/// </summary>
public class Jbig2PatternHalftoneRefinementTests
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

    private static byte[] PlainSegmentHeader(uint segmentNumber, int segmentType,
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

    // ==================================================================
    //  Pattern dictionary diagnostic
    // ==================================================================

    [Fact]
    public void Diagnose_PatternDictionary_ReportsBenign()
    {
        // Pattern dict body: flags(1) + HDPW(1) + HDPH(1) + GRAYMAX(4) + arith data.
        var body = new byte[1 + 1 + 1 + 4 + 8];
        body[0] = 0;       // flags: HDMMR=0, template 0
        body[1] = 8;       // HDPW
        body[2] = 8;       // HDPH
        body[3] = 0; body[4] = 0; body[5] = 0; body[6] = 1; // GRAYMAX = 1 (2 patterns)

        var stream = Concat(
            PlainSegmentHeader(0, 16, 1, (uint)body.Length), body,
            PlainSegmentHeader(1, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal("PatternDictionary", diag[0].TypeName);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[0].Status);
    }

    // ==================================================================
    //  Halftone region now classifies as Decoded (experimental)
    // ==================================================================

    [Fact]
    public void Diagnose_HalftoneRegion_ReportsDecodedExperimental()
    {
        var body = new byte[17 + 1 + 4 + 4 + 4 + 4 + 2 + 2 + 16];
        var stream = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(32, 32),
            PlainSegmentHeader(1, 22, 1, (uint)body.Length), body,
            PlainSegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("halftone", diag[1].Notes);
        Assert.Contains("experimental", diag[1].Notes);
    }

    // ==================================================================
    //  Refinement region now classifies as Decoded (experimental)
    // ==================================================================

    [Fact]
    public void Diagnose_RefinementRegion_ReportsDecodedExperimental()
    {
        var body = new byte[17 + 1 + 4 + 16];
        var stream = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(32, 32),
            PlainSegmentHeader(1, 42, 1, (uint)body.Length), body,
            PlainSegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
        Assert.Contains("refinement", diag[1].Notes);
        Assert.Contains("experimental", diag[1].Notes);
    }

    // ==================================================================
    //  Filter-level: missing pattern dict → refinement ref → falls back
    // ==================================================================

    [Fact]
    public void Decode_HalftoneWithoutPatternDict_FallsBack()
    {
        // Halftone region without a referred pattern dictionary: decoder throws,
        // filter returns raw bytes.
        var body = new byte[17 + 1 + 4 + 4 + 4 + 4 + 2 + 2 + 8];
        var stream = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(32, 32),
            PlainSegmentHeader(1, 22, 1, (uint)body.Length), body,
            PlainSegmentHeader(2, 51, 0, 0));

        var filter = new Jbig2DecodeFilter();
        byte[] decoded = filter.Decode(stream, parameters: null);
        Assert.Equal(stream, decoded);
    }

    // ==================================================================
    //  Assessment: text region + halftone + refinement all decoded now
    // ==================================================================

    [Fact]
    public void Assess_FullStackExperimental_ReportsAllDecodable()
    {
        // Page info + symbol dict + pattern dict + text region + halftone +
        // refinement region + end markers.  Nothing should be "Unsupported"
        // at the diagnostic level; some are SkippedBenign (symbol/pattern
        // dicts are dict-scoped) and the rest are Decoded experimental.
        var stream = Concat(
            PlainSegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            PlainSegmentHeader(1,  0, 1, 20), new byte[20],        // symbol dict
            PlainSegmentHeader(2, 16, 1, 10), new byte[10],        // pattern dict
            PlainSegmentHeader(3,  6, 1, 40), new byte[40],        // text region
            PlainSegmentHeader(4, 22, 1, 40), new byte[40],        // halftone region
            PlainSegmentHeader(5, 42, 1, 30), new byte[30],        // refinement region
            PlainSegmentHeader(6, 51, 0, 0));

        var asm = Jbig2Decoder.Assess(stream);
        Assert.Equal(0, asm.UnsupportedSegmentCount);
        Assert.True(asm.IsFullyDecodable);
    }
}
