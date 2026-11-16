// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The four walks over a JBIG2 segment stream agree about where an unknown-length segment's body
/// ends (ITU-T T.88 §7.2.7).
/// </summary>
/// <remarks>
/// <para><b>They gave three different answers, and two of them were reachable from one API
/// result.</b> <c>Decode</c> scanned for the <c>FF AC</c> terminator and, finding none, decoded the
/// remainder of the stream as that segment's body. <c>EnumerateSegments</c> and <c>Diagnose</c>
/// scanned and, finding none, stopped — dropping every later segment. <c>Assess</c>'s own second
/// walk did not scan at all: <c>if (hdr.UnknownLength) break;</c>. So a stream whose unknown-length
/// segment preceded its page-info segment made <c>Assess</c> report <b>page 0×0</b> while the
/// <c>Diagnose</c> call at the top of that very method listed the page-info segment it had refused
/// to reach.</para>
///
/// <para><b>The decoder's answer is the one that wins.</b> <c>Diagnose</c> documents itself as
/// describing "what the decoder would do with every segment"; a walk that stops where the decoder
/// continues is describing a different program.</para>
///
/// <para><b>These assert the walks against each other</b>, which is the only shape that can catch
/// this class of defect — each walk was internally self-consistent, so no test of one alone would
/// ever have gone red.</para>
/// </remarks>
public sealed class Jbig2SegmentWalkAgreementTests
{
    private const int SegGenericRegionImmediate = 38;
    private const int SegPageInfo = 48;
    private const uint UnknownLength = 0xFFFFFFFF;

    /// <summary>
    /// <b>Separating input A.</b> An unknown-length segment, properly terminated, sitting before
    /// the page-info segment. Every walk must reach the page info.
    /// </summary>
    [Fact]
    public void APageInfoAfterATerminatedUnknownLengthSegmentIsStillFound()
    {
        byte[] stream = Concat(
            Header(segNum: 1, type: SegGenericRegionImmediate, dataLength: UnknownLength),
            new byte[] { 0x01, 0x02, 0x03, 0x04 },      // body
            new byte[] { 0xFF, 0xAC },                  // T.88 §7.2.7 terminator
            Header(segNum: 2, type: SegPageInfo, dataLength: 19),
            PageInfoBody(width: 640, height: 480));

        var assessment = Jbig2Decoder.Assess(stream);

        // The defect: this reported 0 × 0 while listing the very segment that carries the size.
        Assert.Equal(640, assessment.PageWidth);
        Assert.Equal(480, assessment.PageHeight);

        // …and the two walks that DID reach it must still agree that it is there.
        Assert.Contains(assessment.Segments, s => s.SegmentType == SegPageInfo);
        Assert.Equal(2, Jbig2Decoder.EnumerateSegments(stream).Count);
    }

    /// <summary>
    /// <b>The cross-walk invariant.</b> <c>Diagnose</c> and <c>EnumerateSegments</c> must reach the
    /// same segments, whatever the stream contains — they are two spellings of one walk.
    /// </summary>
    /// <remarks>
    /// <b>An earlier draft of this test could not fail.</b> It asserted a single-segment malformed
    /// stream produced one entry from each — true whether the walk stopped at the unterminated
    /// segment or ran past it, because with no terminator the body runs to the end of the stream
    /// either way, so both answers end the walk in the same place. The markerless case is genuinely
    /// not separable through these two APIs; what IS separable is whether the two walks advance
    /// identically, which is what the shared rule buys and what this asserts. The terminated
    /// stream is the one that exercises the <c>+ 2</c>.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTwoWalksReachTheSameSegments(bool terminated)
    {
        var parts = new List<byte[]>
        {
            Header(segNum: 1, type: SegGenericRegionImmediate, dataLength: UnknownLength),
            new byte[] { 0x01, 0x02, 0x03, 0x04 },
        };
        if (terminated)
        {
            parts.Add(new byte[] { 0xFF, 0xAC });
            parts.Add(Header(segNum: 2, type: SegPageInfo, dataLength: 19));
            parts.Add(PageInfoBody(width: 640, height: 480));
        }

        byte[] stream = Concat(parts.ToArray());

        int[] fromDiagnose = Jbig2Decoder.Diagnose(stream)
            .Where(d => d.SegmentType >= 0)
            .Select(d => d.SegmentType)
            .ToArray();
        int[] fromEnumerate = Jbig2Decoder.EnumerateSegments(stream)
            .Select(h => h.SegmentType)
            .ToArray();

        Assert.Equal(fromEnumerate, fromDiagnose);
        Assert.Equal(terminated ? 2 : 1, fromEnumerate.Length);
    }

    /// <summary>
    /// The premise. Without this, both cases above could pass on a stream the walks never parsed
    /// at all — an empty result is the same shape as "stopped early".
    /// </summary>
    [Fact]
    public void TheFixtureReallyParsesAsTwoSegmentsWithoutTheUnknownLengthOne()
    {
        byte[] control = Concat(
            Header(segNum: 1, type: SegGenericRegionImmediate, dataLength: 4),
            new byte[] { 0x01, 0x02, 0x03, 0x04 },
            Header(segNum: 2, type: SegPageInfo, dataLength: 19),
            PageInfoBody(width: 640, height: 480));

        var assessment = Jbig2Decoder.Assess(control);

        Assert.Equal(640, assessment.PageWidth);
        Assert.Equal(480, assessment.PageHeight);
        Assert.Equal(2, Jbig2Decoder.EnumerateSegments(control).Count);
    }

    /// <summary>
    /// The globals prologue is shared too — three copies existed and only two null-guarded their
    /// arrays. A null page stream with real globals must not throw.
    /// </summary>
    [Fact]
    public void GlobalsAreConcatenatedEvenWhenThePageStreamIsEmpty()
    {
        byte[] globals = Concat(
            Header(segNum: 1, type: SegPageInfo, dataLength: 19),
            PageInfoBody(width: 200, height: 100));

        var assessment = Jbig2Decoder.Assess(Array.Empty<byte>(), globals);

        Assert.Equal(200, assessment.PageWidth);
        Assert.Equal(100, assessment.PageHeight);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A minimal T.88 §7.2 segment header: 4-byte number, flags, a zero referred-to count, a
    /// 1-byte page association and the 4-byte data length. 11 bytes.
    /// </summary>
    private static byte[] Header(uint segNum, int type, uint dataLength)
    {
        var h = new List<byte>();
        h.AddRange(BE32(segNum));
        h.Add((byte)(type & 0x3F));   // bit 6 clear → 1-byte page association
        h.Add(0x00);                  // referred-to count 0, no retention flags
        h.Add(0x01);                  // page association
        h.AddRange(BE32(dataLength));
        return h.ToArray();
    }

    /// <summary>T.88 §7.4.8 page information: width, height, then 11 further bytes.</summary>
    private static byte[] PageInfoBody(uint width, uint height)
    {
        var b = new List<byte>();
        b.AddRange(BE32(width));
        b.AddRange(BE32(height));
        b.AddRange(BE32(0));          // X resolution
        b.AddRange(BE32(0));          // Y resolution
        b.Add(0x00);                  // flags
        b.AddRange(new byte[] { 0x00, 0x00 });   // striping information
        return b.ToArray();
    }

    private static byte[] BE32(uint v) =>
        new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p);
        return all.ToArray();
    }
}
