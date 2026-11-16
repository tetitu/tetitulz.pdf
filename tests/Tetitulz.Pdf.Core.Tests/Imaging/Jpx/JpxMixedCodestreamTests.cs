// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The MIXED set (ITU-T T.814 §A.4) without the ISO corpus: a Part 1 codestream relabelled as MIXED —
/// Rsiz bit 14, a CAP whose Ccap15 says MIXED, code-block style bits 6 and 7 — is still a valid MIXED
/// codestream whose every block is a Part 1 one, because a Part 1 encoder writes minimal length fields:
/// a block's first non-zero length begins with 1, or comes with Lblock still 3, and §A.4 gives both to
/// Part 1. The reference decoder's lossless fixtures relabelled so decode as they do unlabelled, every block found
/// Part 1 by tier 2 and decoded by the Part 1 block decoder with the style's Part 1 bits.
/// (<see cref="JpxIsoHtConformanceTests"/> holds the ISO MIXED files, HT and Part 1 blocks together.)
/// </summary>
public sealed class JpxMixedCodestreamTests
{
    /// <summary>The codestream relabelled MIXED: Rsiz bit 14, a CAP (Pcap15, Ccap15 0xC000) after SIZ, bits 6 and 7 in every main-header COD and COC style byte.</summary>
    internal static byte[] RelabelledMixed(byte[] codestream)
    {
        var output = new List<byte>(codestream.Length + 10) { 0xFF, 0x4F };
        int components = 0;
        int p = 2;
        while (true)
        {
            int marker = (codestream[p] << 8) | codestream[p + 1];
            if (marker == 0xFF90) break;
            int length = (codestream[p + 2] << 8) | codestream[p + 3];
            byte[] segment = codestream.Skip(p).Take(2 + length).ToArray();
            if (marker == 0xFF51)
            {
                segment[4] |= 0x40;
                components = (segment[38] << 8) | segment[39];
            }
            else if (marker == 0xFF52) segment[12] |= 0xC0;
            else if (marker == 0xFF53) segment[4 + (components < 257 ? 1 : 2) + 1 + 3] |= 0xC0;
            output.AddRange(segment);
            if (marker == 0xFF51) output.AddRange(new byte[] { 0xFF, 0x50, 0x00, 0x08, 0x00, 0x02, 0x00, 0x00, 0xC0, 0x00 });
            p += 2 + length;
        }
        output.AddRange(codestream.Skip(p));
        return output.ToArray();
    }

    public static IEnumerable<object[]> Fixtures() => JpxLosslessFixtures.All.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void APart1CodestreamRelabelledMixed_DecodesAsItDid(string name)
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.All.Single(f => f.Name == name);
        byte[] mixed = RelabelledMixed(fixture.Codestream);
        Assert.True(JpxCodestreamParser.TryParse(mixed, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(0xC000, cs.HtCapabilities & 0xC000);
        Assert.NotEqual(JpxCodeBlockStyle.None, cs.Main.Coding.Component.CodeBlockStyle & JpxCodeBlockStyle.Mixed);

        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult plain, out reason), reason);
        Assert.True(JpxDecoder.TryDecode(mixed, 0, out JpxDecodeResult result, out reason), reason);
        Assert.Empty(result.Deviations);
        for (int c = 0; c < fixture.Components; c++)
        {
            Assert.Equal(plain.ComponentPlane(c, out _), result.ComponentPlane(c, out _));
            if (fixture.Exact) Assert.Equal(fixture.Planes[c].Select(b => (int)b), result.ComponentPlane(c, out _));
        }

        // Tier 2 found every contributing block a Part 1 one: the decode above went through the Part 1 block decoder.
        int part1 = 0;
        foreach (int tile in cs.Tiles.Keys)
        {
            Assert.True(JpxTier2Decoder.TryDecodeTile(cs, tile, new WorkBudget(1_000_000), out JpxTileDecode decoded, out reason), reason);
            foreach (JpxCodeBlock block in decoded.Layout.Components.SelectMany(tc => tc.Resolutions).SelectMany(r => r.Precincts)
                         .SelectMany(pr => pr.Bands).SelectMany(b => b.Blocks).Where(b => b.Chunks.Any(ch => ch.Data.Length > 0)))
            {
                Assert.Equal(JpxBlockCoder.Part1, block.Coder);
                part1++;
            }
        }
        Assert.True(part1 > 0, "no block had bytes");
    }
}
