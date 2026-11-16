// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// T.88 §6.6 halftone regions: which ones are refused, and why.
///
/// <para><b>History.</b> These tests pinned three refusals: more than two patterns (only plane 0
/// was ever decoded), HENABLESKIP and HDEFPIXEL. Annex C.5's multi-plane decode, HSKIP and a region
/// bitmap with HDEFPIXEL and HCOMBOP are implemented now, verified pixel for pixel in
/// <see cref="Jbig2HalftoneDecodeTests"/> against streams encoded from the standard. What remains
/// refused was an MMR region of more than one plane, until the T.6 decoder reported where each
/// plane's data ended; nothing here is refused for its plane count now.</para>
///
/// <para><b>Why these stay testable without valid data.</b> The pattern count is
/// <c>GRAYMAX + 1</c>, a plain header field, so it decides the plane count before any coded data is
/// read.</para>
/// </summary>
public class Jbig2HalftoneRegionTests
{
    /// <summary>
    /// An arithmetic halftone is not refused for its plane count, however many planes it needs.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(15u)]
    public void AnArithmeticHalftone_IsNotRefusedForItsPlaneCount(uint grayMax)
    {
        Decode(grayMax);
        Assert.DoesNotContain("bit planes", Reason(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An MMR halftone is not refused for its plane count either. It was, for more than one plane,
    /// until the T.6 decoder reported where each plane's data ended; the planes are decoded in
    /// <see cref="Jbig2HalftoneDecodeTests"/>.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(15u)]
    public void AnMmrHalftone_IsNotRefusedForItsPlaneCount(uint grayMax)
    {
        Decode(grayMax, halftoneFlags: Mmr);
        Assert.DoesNotContain("bit planes", Reason(), StringComparison.Ordinal);
    }

    /// <summary>HENABLESKIP and HDEFPIXEL are decoded now, not refused by name.</summary>
    [Theory]
    [InlineData(EnableSkip, "HENABLESKIP")]
    [InlineData(DefaultPixelBlack, "HDEFPIXEL")]
    public void AHalftoneFlag_IsNoLongerRefused(byte flag, string name)
    {
        Decode(grayMax: 1, halftoneFlags: flag);
        Assert.DoesNotContain(name, Reason(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    private const byte Mmr               = 0x01;   // §7.4.5.1.1 bit 0
    private const byte EnableSkip        = 0x08;   // §7.4.5.1.1 bit 3
    private const byte DefaultPixelBlack = 0x80;   // §7.4.5.1.1 bit 7

    private static string Reason() => Jbig2DecodeFilter.LastFallbackReason ?? string.Empty;

    private static void Decode(uint grayMax, byte halftoneFlags = 0)
        => new Jbig2DecodeFilter().Decode(Stream(grayMax, halftoneFlags), parameters: null);

    /// <summary>
    /// Page info, a pattern dictionary declaring <paramref name="grayMax"/>, and an immediate
    /// halftone region referring to it.
    ///
    /// <para>The dictionary's pattern <i>bitmaps</i> are decoded from zero bytes and are therefore
    /// blank, which does not matter: every assertion here is about how many patterns were
    /// declared, and <c>GRAYMAX</c> is read straight out of the header.</para>
    /// </summary>
    private static byte[] Stream(uint grayMax, byte halftoneFlags)
    {
        // Pattern dictionary body: flags, HDPW, HDPH, GRAYMAX, then pattern data.
        var dict = new byte[7 + 16];
        dict[0] = 0x00;   // HDMMR = 0, HDTEMPLATE = 0
        dict[1] = 4;      // HDPW
        dict[2] = 4;      // HDPH
        WriteUInt32BE(dict, 3, grayMax);

        // Halftone region body: 17-byte region info, the flag byte, then the grid geometry.
        var region = new byte[40];
        WriteUInt32BE(region, 0, 32);    // region width
        WriteUInt32BE(region, 4, 32);    // region height
        WriteUInt32BE(region, 8, 0);     // region x
        WriteUInt32BE(region, 12, 0);    // region y
        region[16] = 0;                  // external combination operator: OR
        region[17] = halftoneFlags;
        WriteUInt32BE(region, 18, 2);    // HGW
        WriteUInt32BE(region, 22, 2);    // HGH

        return Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 16, 1, (uint)dict.Length), dict,
            SegmentHeaderReferring(2, 23, 1, (uint)region.Length, referredTo: 1), region,
            SegmentHeader(3, 51, 0, 0));
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

    /// <summary>
    /// A header declaring exactly one referred-to segment (§7.2.4/§7.2.5): the count sits in the
    /// top three bits of the referred-to byte, and the referred segment number is one byte while
    /// this segment's own number fits in one.
    /// </summary>
    private static byte[] SegmentHeaderReferring(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength, byte referredTo)
        => new byte[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),  (byte)(segmentNumber & 0xFF),
            (byte)(segmentType & 0x3F),
            0x20,   // one referred-to segment
            referredTo,
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
