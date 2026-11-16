// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// Tier 1 against the reference decoder's encoder: the lossless fixtures without decomposition levels put each
/// sample, less the DC shift, straight into a code-block coefficient, so the decoded block must
/// equal the source pixels exactly — through the parser, tier 2 and tier 1 alone.
/// </summary>
public sealed class JpxLosslessFixtureTests
{
    /// <summary>Decodes a fixture's tile 0 and returns every component's samples in raster order.</summary>
    internal static int[][] DecodeLevelZero(JpxLosslessFixtures.Fixture fixture, out List<string> problems)
    {
        problems = new List<string>();
        Assert.True(JpxCodestreamParser.TryParse(fixture.Codestream, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);

        var samples = new int[fixture.Components][];
        for (int c = 0; c < fixture.Components; c++)
        {
            Assert.Equal(0, tile.Layout.Components[c].Levels);
            int shift = 1 << (cs.Size.Components[c].Precision - 1);
            JpxBandCoefficients ll = Assert.Single(JpxTier1Decoder.Decode(cs, tile, c, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), problems));
            Assert.Equal(0, ll.Band.Orientation);
            Assert.Equal((fixture.Width, fixture.Height), ((int)ll.Band.Bounds.Width, (int)ll.Band.Bounds.Height));
            var plane = new int[ll.Values.Length];
            // Reversible and fully decoded: the coefficient is exact (E-7); the half is dropped.
            for (int i = 0; i < plane.Length; i++) plane[i] = JpxBlockDecoder.Truncate(ll.Values[i]) + shift;
            samples[c] = plane;
        }
        return samples;
    }

    public static IEnumerable<object[]> LevelZeroFixtures()
    {
        foreach (JpxLosslessFixtures.Fixture f in JpxLosslessFixtures.All)
            if (f.Levels == 0 && f.Exact) yield return new object[] { f.Name };
    }

    /// <summary>
    /// One 40x24 block; 16x16 blocks in 32x32 precincts over three layers (each block's data in
    /// three chunks); three components in 8x8 blocks. Every sample equal, every problem list empty.
    /// </summary>
    [Theory]
    [MemberData(nameof(LevelZeroFixtures))]
    public void EveryLevelZeroFixture_DecodesToItsPixels(string name)
    {
        JpxLosslessFixtures.Fixture fixture = System.Array.Find(JpxLosslessFixtures.All, f => f.Name == name)!;
        int[][] samples = DecodeLevelZero(fixture, out List<string> problems);
        Assert.Empty(problems);
        for (int c = 0; c < fixture.Components; c++)
        {
            var expected = new int[fixture.Planes[c].Length];
            for (int i = 0; i < expected.Length; i++) expected[i] = fixture.Planes[c][i];
            Assert.Equal(expected, samples[c]);
        }
        // Potency: the pattern is not flat — a decoder answering the DC shift everywhere would not pass.
        Assert.True(new HashSet<byte>(fixture.Planes[0]).Count > 16);
    }

    /// <summary>The three-layer fixture's blocks arrive over several packets, so some block's data is in more than one chunk.</summary>
    [Fact]
    public void TheLayeredFixture_HasMultiChunkBlocks()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_Blocks16_Precincts32_ThreeLayers;
        Assert.True(JpxCodestreamParser.TryParse(fixture.Codestream, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(3, cs.CodingFor(0).Layers);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        int multiChunkBlocks = 0, blocks = 0;
        foreach (JpxPrecinct precinct in tile.Layout.Components[0].Resolutions[0].Precincts)
            foreach (JpxCodeBlock block in precinct.Bands[0].Blocks)
            {
                blocks++;
                if (block.Chunks.Count >= 2) multiChunkBlocks++;
            }
        Assert.Equal(6, blocks);   // 40x24 in 16x16 blocks: 3 by 2
        Assert.True(multiChunkBlocks >= 1, $"{multiChunkBlocks} blocks with more than one chunk");
    }
}
