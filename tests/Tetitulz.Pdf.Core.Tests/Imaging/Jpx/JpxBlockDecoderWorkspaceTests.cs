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
/// <see cref="JpxBlockDecoder"/> as a workspace: one instance decodes every code-block of a
/// tile-component in turn, so what a block leaves behind must not reach the next; and its two
/// context tables against Tables D.1 to D.3 entry by entry, since the decoder only ever indexes
/// them and a wrong entry is a wrong context on some neighbourhood the fixtures may not hold.
/// </summary>
public sealed class JpxBlockDecoderWorkspaceTests
{
    private static List<(JpxCodeBlock Block, int Orientation, int Mb)> Blocks(JpxLosslessFixtures.Fixture fixture, out byte[] data, out JpxCodeBlockStyle style, out int roiShift)
    {
        Assert.True(JpxCodestreamParser.TryParse(fixture.Codestream, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        JpxTileComponent tc = tile.Layout.Components[0];
        JpxQuantization q = cs.QuantizationFor(0, 0);
        data = tile.Data;
        style = tc.Coding.CodeBlockStyle;
        roiShift = cs.RoiShiftFor(0, 0);
        var blocks = new List<(JpxCodeBlock, int, int)>();
        for (int r = 0; r < tc.Resolutions.Length; r++)
            foreach (JpxPrecinct precinct in tc.Resolutions[r].Precincts)
                foreach (JpxPrecinctBand pb in precinct.Bands)
                    foreach (JpxCodeBlock block in pb.Blocks)
                        if (block.Passes > 0)
                            blocks.Add((block, pb.Band.Orientation, JpxQuantizationSteps.MagnitudeBitPlanes(q, tc.Levels, r, pb.Band.Orientation)));
        return blocks;
    }

    /// <summary>
    /// The 16x16-block fixture has six blocks of three shapes (16x16, 8x16, 16x8 — wait, 40x24 in
    /// 16x16: 16x16, 16x16, 8x16 on the first row and 16x8, 16x8, 8x8 on the second), and the
    /// two-level one has bands of 10x6 and 20x12 in one block each. Decoded one after another
    /// through one workspace, every block equals its decode through a fresh one — the magnitudes,
    /// flags and joined chunks of the block before are cleared, whether the next is smaller,
    /// larger or the same. Potency: the blocks differ from each other, so a stale sample would show.
    /// </summary>
    [Theory]
    [InlineData("Gray40x24_Blocks16_Precincts32_ThreeLayers")]
    [InlineData("Gray40x24_TwoLevels_Rpcl_ThreeLayers")]
    [InlineData("Rgb40x24_NoMct_Blocks8")]
    public void OneWorkspace_DecodesBlocksInARow_AsFreshOnesDo(string name)
    {
        JpxLosslessFixtures.Fixture fixture = System.Array.Find(JpxLosslessFixtures.All, f => f.Name == name)!;
        var blocks = Blocks(fixture, out byte[] data, out JpxCodeBlockStyle style, out int roiShift);
        Assert.True(blocks.Count >= 4, $"only {blocks.Count} blocks");

        var shared = new JpxBlockDecoder();
        var results = new List<int[]>();
        var distinct = new HashSet<string>();
        foreach ((JpxCodeBlock block, int orientation, int mb) in blocks)
        {
            int w = (int)block.Bounds.Width, h = (int)block.Bounds.Height;
            var viaShared = new int[w * h];
            shared.Decode(block, data, orientation, style, mb, roiShift, viaShared, 0, w, out string problem);
            Assert.Null(problem);
            int[] viaFresh = JpxBlockDecoder.Decode(block, data, orientation, style, mb, roiShift, out problem);
            Assert.Null(problem);
            Assert.Equal(viaFresh, viaShared);
            results.Add(viaShared);
            distinct.Add(string.Join(",", viaShared));
        }
        Assert.True(distinct.Count >= 3, "the blocks decode alike; a stale workspace would not show");
        Assert.Contains(results, r => System.Array.Exists(r, v => v != 0));
    }

    /// <summary>
    /// The target's stride and offset place the block inside a larger array — the band — and a
    /// block with no coding passes writes zeros over its rectangle rather than leaving it.
    /// </summary>
    [Fact]
    public void Decode_WritesTheBlockAtItsOffsetInTheTarget_AndZerosAnEmptyOne()
    {
        var blocks = Blocks(JpxLosslessFixtures.Gray40x24_OneBlock, out byte[] data, out JpxCodeBlockStyle style, out int roiShift);
        (JpxCodeBlock block, int orientation, int mb) = blocks[0];
        int w = (int)block.Bounds.Width, h = (int)block.Bounds.Height;
        Assert.Equal((40, 24), (w, h));

        int stride = w + 5, offset = 2 * stride + 3;
        var target = new int[offset + h * stride];
        for (int i = 0; i < target.Length; i++) target[i] = 12345;
        new JpxBlockDecoder().Decode(block, data, orientation, style, mb, roiShift, target, offset, stride, out string problem);
        Assert.Null(problem);
        int[] plain = JpxBlockDecoder.Decode(block, data, orientation, style, mb, roiShift, out _);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(plain[y * w + x], target[offset + y * stride + x]);
        Assert.Equal(12345, target[0]);
        Assert.Equal(12345, target[offset + w]);   // the gap between the rows is not touched

        var empty = new JpxCodeBlock(new JpxRect(0, 0, 3, 2));   // no chunks, no passes
        var region = new int[6];
        for (int i = 0; i < region.Length; i++) region[i] = 7;
        new JpxBlockDecoder().Decode(empty, data, 0, style, mb, 0, region, 0, 3, out problem);
        Assert.Null(problem);
        Assert.Equal(new int[6], region);
    }

    /// <summary>
    /// Table D.1 by neighbourhood bits (NW N NE W · E SW S SE from bit 0), one row per column of
    /// the table: the LL/LH column, then HL with the roles of ΣH and ΣV swapped, then HH by ΣD.
    /// The centre bit is not part of the neighbourhood and changes nothing.
    /// </summary>
    [Theory]
    // LL and LH: ΣH 2 → 8; ΣH 1 with ΣV ≥ 1 → 7, with ΣD ≥ 1 → 6, alone → 5; ΣV 2 → 4; ΣV 1 → 3; ΣD ≥ 2 → 2; ΣD 1 → 1; none → 0.
    [InlineData(0, 0b000_101_000, 8)]
    [InlineData(0, 0b000_001_010, 7)]
    [InlineData(0, 0b000_001_001, 6)]
    [InlineData(0, 0b000_100_000, 5)]
    [InlineData(0, 0b010_000_010, 4)]
    [InlineData(0, 0b010_000_000, 3)]
    [InlineData(0, 0b100_000_001, 2)]
    [InlineData(0, 0b000_000_100, 1)]
    [InlineData(0, 0b000_000_000, 0)]
    [InlineData(2, 0b000_001_010, 7)]
    [InlineData(2, 0b010_000_000, 3)]
    // HL: ΣV plays ΣH's part. Two vertical → 8; one vertical with a horizontal → 7; one horizontal alone → 3.
    [InlineData(1, 0b010_000_010, 8)]
    [InlineData(1, 0b000_001_010, 7)]
    [InlineData(1, 0b000_100_000, 3)]
    [InlineData(1, 0b000_101_000, 4)]
    // HH: ΣD ≥ 3 → 8; ΣD 2 with ΣH + ΣV ≥ 1 → 7, alone → 6; ΣD 1 with ΣH + ΣV ≥ 2 → 5, 1 → 4, 0 → 3; ΣD 0 with ΣH + ΣV ≥ 2 → 2, 1 → 1, 0 → 0.
    [InlineData(3, 0b101_000_101, 8)]
    [InlineData(3, 0b101_000_001, 8)]
    [InlineData(3, 0b000_100_101, 7)]
    [InlineData(3, 0b000_000_101, 6)]
    [InlineData(3, 0b010_100_001, 5)]
    [InlineData(3, 0b000_100_001, 4)]
    [InlineData(3, 0b000_000_001, 3)]
    [InlineData(3, 0b010_101_000, 2)]
    [InlineData(3, 0b000_100_000, 1)]
    [InlineData(3, 0b000_000_000, 0)]
    public void TheZeroCodingTable_IsTableD1(int orientation, int neighbourhood, int context)
    {
        Assert.Equal(context, JpxBlockDecoder.ZeroCodingContextAt(orientation, neighbourhood));
        Assert.Equal(context, JpxBlockDecoder.ZeroCodingContextAt(orientation, neighbourhood | (1 << 4)));
    }

    /// <summary>
    /// Tables D.2 and D.3: each neighbour contributes 1, −1 or 0, the horizontal and vertical sums
    /// are clipped to ±1, and the pair names the context (9 to 13) and the XOR bit. Opposite signs
    /// cancel; a lone negative below gives context 10 with the bit set.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false, false, false, false, false, 9, 0)]    // H 0, V 0
    [InlineData(false, false, false, false, true, false, false, false, 10, 0)]    // V 1 (N positive)
    [InlineData(false, false, false, false, false, false, true, true, 10, 1)]     // V −1 (S negative)
    [InlineData(false, false, false, false, true, false, true, true, 9, 0)]       // V 0 (N positive, S negative)
    [InlineData(true, false, false, false, false, false, false, false, 12, 0)]    // H 1
    [InlineData(true, false, false, false, true, false, false, false, 13, 0)]     // H 1, V 1
    [InlineData(true, false, false, false, false, false, true, true, 11, 0)]      // H 1, V −1
    [InlineData(false, false, true, true, false, false, false, false, 12, 1)]     // H −1 (E negative)
    [InlineData(false, false, true, true, true, false, false, false, 11, 1)]      // H −1, V 1
    [InlineData(true, true, true, true, false, false, true, true, 13, 1)]         // H −2 clipped to −1, V −1
    [InlineData(true, false, true, true, false, false, false, false, 9, 0)]       // H 0 (W positive, E negative)
    public void TheSignTable_IsTablesD2AndD3(bool wSig, bool wNeg, bool eSig, bool eNeg, bool nSig, bool nNeg, bool sSig, bool sNeg, int context, int xorBit)
    {
        int index = JpxBlockDecoder.SignIndex(wSig, wNeg, eSig, eNeg, nSig, nNeg, sSig, sNeg);
        Assert.Equal(context, JpxBlockDecoder.SignContextAt(index, out int xor));
        Assert.Equal(xorBit, xor);
    }
}
