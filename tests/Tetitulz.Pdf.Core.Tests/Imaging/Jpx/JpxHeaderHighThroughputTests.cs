// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxHeader.TryReadHighThroughput"/>: HT code-blocks (ITU-T T.814) declared by CAP's
/// Pcap15, or used by code-block style bit 6 in a COD or COC — the main header's or a tile-part's —
/// read from the markers alone. What PDF/A's A29 asks.
/// </summary>
public sealed class JpxHeaderHighThroughputTests
{
    private static readonly byte[] Data5 = { 1, 2, 3, 4, 5 };
    private static readonly byte[] CapPcap15 = J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00);

    private static bool? Read(byte[] codestream) =>
        JpxHeader.TryReadHighThroughput(codestream, out bool ht) ? ht : null;

    [Fact]
    public void APart1CodestreamIsNotHighThroughput()
        => Assert.False(Read(J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc)));

    [Fact]
    public void Pcap15AloneDeclaresIt()
        => Assert.True(Read(J.Concat(J.Soc, J.Siz64(), CapPcap15, J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc)));

    [Fact]
    public void StyleBit6InTheMainCodAloneUsesIt()
        => Assert.True(Read(J.Concat(J.Soc, J.Siz64(), J.Cod(style: 0x40), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc)));

    /// <summary>A COC's style byte sits after its one-byte component index (Csiz &lt; 257): bit 6 there counts; bits 0–5 (the control) do not.</summary>
    [Fact]
    public void StyleBit6InACocUsesIt()
    {
        Assert.True(Read(J.Concat(J.Soc, J.Siz64(3), J.Cod(), J.Coc(1, style: 0x40), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc)));
        Assert.False(Read(J.Concat(J.Soc, J.Siz64(3), J.Cod(), J.Coc(1, style: 0x3F), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc)));
    }

    /// <summary>
    /// A tile-part header's COD is read too — the reader skips each tile-part's data by its Psot to
    /// reach the next header. The second tile-part of the tile carries the HT style; the first, whose
    /// data holds bytes that would read as a COD marker were they not skipped, carries none.
    /// </summary>
    [Fact]
    public void AStyleBit6InALaterTilePartHeaderUsesIt()
    {
        byte[] lookalike = J.Cod(style: 0x40);   // a COD's bytes as tile data: not a marker, must not count
        byte[] withHtInSecond = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5, tnsot: 2), J.TilePart(0, 1, J.Cod(style: 0x40), Data5, tnsot: 2), J.Eoc);
        byte[] lookalikeOnly = J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), lookalike), J.Eoc);
        Assert.True(Read(withHtInSecond));
        Assert.False(Read(lookalikeOnly));
    }

    /// <summary>A tile-part whose Psot runs past the codestream is malformed: nothing is concluded.</summary>
    [Fact]
    public void ATilePartLongerThanTheCodestreamIsMalformed()
        => Assert.Null(Read(J.Concat(J.Soc, J.Siz64(), J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5, psot: 1000), J.Eoc)));
}
