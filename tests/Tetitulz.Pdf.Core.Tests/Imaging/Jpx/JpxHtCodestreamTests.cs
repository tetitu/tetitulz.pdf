// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// A whole HTJ2K codestream, written out here, through <see cref="JpxDecoder"/> to pixels — the one
/// HT decode that needs no corpus, so it runs where the corpora are absent (CI). Every stage is the
/// ordinary decoder's but tier 1: the parser accepts the CAP and the HT code-block style, tier 2
/// cuts the segment, <see cref="JpxHtBlockDecoder"/> decodes it, and the wavelet and DC shift place it.
/// </summary>
public sealed class JpxHtCodestreamTests
{
    /// <summary>
    /// A 2 × 2, 8-bit image, no decomposition level, reversible, one layer; Rsiz bit 14 and a CAP
    /// with Pcap15 and Ccap15 0 (HTONLY, T.814 §A.2, §A.3); COD code-block style 0x40 (HT, Table A.3);
    /// QCD with 1 guard bit and exponent 8, so M<sub>b</sub> = 8 (E-2).
    /// </summary>
    private static byte[] Codestream(int style) =>
        J.Concat(J.Soc, J.Siz(2, 2, 0, 0, 2, 2, 0, 0, new[] { (0x07, 1, 1) }, rsiz: 0x4000),
            J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00),
            J.Cod(levels: 0, style: style), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), Packet()), J.Eoc);

    /// <summary>
    /// The one packet: non-empty (1); included (1); six zero bit-planes (0000001, the tag tree saying
    /// "not below 1" six times, then "below 7"); one coding pass (0); Lblock unchanged (0); 5 bytes in
    /// 3 + ⌊log2 1⌋ = 3 bits (101). The body is <see cref="JpxHtBlockDecoderTests"/>' single-quad
    /// segment whose one significant sample, top-left, has μ = 2.
    /// </summary>
    private static byte[] Packet() =>
        J.Concat(JpxPacketBits.Bytes("1 1 0000001 0 0 101"), new byte[] { 0x00, 0x7F, 0x17, 0x74, 0x00 });

    /// <summary>
    /// S<sub>blk</sub> = 6, so the cleanup's plane is 8 − 1 − 6 = 1 and μ = 2 is the coefficient
    /// 2 · 2 + ½ · 2 = 5 (the half at the last decoded plane, E-8 with r = ½); the DC shift of 128 makes
    /// the pixel 133, and the three insignificant samples 128. The control reads the same bytes as
    /// Part 1 code-blocks (no style bit): the MQ decoder makes something else of them.
    /// </summary>
    [Fact]
    public void AnHtCodestreamDecodesToItsPixels()
    {
        Assert.True(JpxDecoder.TryDecode(Codestream(0x40), 0, out JpxDecodeResult ht, out string reason), reason);
        Assert.Equal(0, ht.Codestream.HtCapabilities);
        Assert.Equal(new[] { 133, 128, 128, 128 }, ht.ComponentPlane(0, out _));

        Assert.True(JpxDecoder.TryDecode(Codestream(0x00), 0, out JpxDecodeResult part1, out reason), reason);
        Assert.NotEqual(new[] { 133, 128, 128, 128 }, part1.ComponentPlane(0, out _));
    }
}
