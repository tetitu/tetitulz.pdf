// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxTier1Decoder"/> assembles a tile-component's bands from its code-blocks. The
/// level-zero fixtures prove the values (<see cref="JpxLosslessFixtureTests"/>); here the
/// structure: which bands, at which bounds, under a reduction, and the work counted.
/// </summary>
public sealed class JpxTier1DecoderTests
{
    private static JpxTileDecode Tile(JpxLosslessFixtures.Fixture fixture, out JpxCodestream cs)
    {
        Assert.True(JpxCodestreamParser.TryParse(fixture.Codestream, out cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        return tile;
    }

    /// <summary>
    /// Two decomposition levels over 40x24: seven bands — the 10x6 LL, then HL/LH/HH at level 2
    /// (10x6, 10x6, 10x6) and at level 1 (20x12 each) — in that order; a reduction of 1 stops at
    /// four; a reduction of 2 leaves the LL band alone.
    /// </summary>
    [Fact]
    public void Bands_ComeInResolutionOrder_AndReductionDropsTheHighest()
    {
        JpxTileDecode tile = Tile(JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers, out JpxCodestream cs);
        var problems = new List<string>();
        List<JpxBandCoefficients> bands = JpxTier1Decoder.Decode(cs, tile, 0, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), problems);
        Assert.Empty(problems);
        Assert.Equal(
            new[] { "0:2:10x6", "1:2:10x6", "2:2:10x6", "3:2:10x6", "1:1:20x12", "2:1:20x12", "3:1:20x12" },
            bands.Select(b => $"{b.Band.Orientation}:{b.Band.Level}:{b.Band.Bounds.Width}x{b.Band.Bounds.Height}").ToArray());
        foreach (JpxBandCoefficients band in bands)
        {
            Assert.Equal(band.Band.Bounds.Width * band.Band.Bounds.Height, band.Values.Length);
            Assert.Contains(band.Values, v => v != 0);   // every band of a textured image carries something
        }

        Assert.Equal(4, JpxTier1Decoder.Decode(cs, tile, 0, 1, new WorkBudget(PdfLimits.MaxJpxDecodeWork), problems).Count);
        Assert.Equal(1, JpxTier1Decoder.Decode(cs, tile, 0, 2, new WorkBudget(PdfLimits.MaxJpxDecodeWork), problems).Count);
    }

    /// <summary>One unit of work per coding pass: the budget's spend is the tile-component's pass total, and a budget short of it throws.</summary>
    [Fact]
    public void TheWorkBudget_CountsCodingPasses()
    {
        JpxTileDecode tile = Tile(JpxLosslessFixtures.Gray40x24_Blocks16_Precincts32_ThreeLayers, out JpxCodestream cs);
        long passes = 0;
        foreach (JpxPrecinct precinct in tile.Layout.Components[0].Resolutions[0].Precincts)
            foreach (JpxCodeBlock block in precinct.Bands[0].Blocks) passes += block.Passes;
        Assert.True(passes > 6, $"only {passes} passes");

        var enough = new WorkBudget(passes);
        JpxTier1Decoder.Decode(cs, tile, 0, 0, enough, new List<string>());
        Assert.Equal(passes, enough.Spent);
        Assert.Throws<WorkBudgetExceededException>(() => JpxTier1Decoder.Decode(cs, tile, 0, 0, new WorkBudget(passes - 1), new List<string>()));
    }
}
