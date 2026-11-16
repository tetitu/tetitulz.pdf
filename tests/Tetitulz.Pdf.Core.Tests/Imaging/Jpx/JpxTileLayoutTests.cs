// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary><see cref="JpxTileLayout"/> against T.800 §B.6 and §B.7, worked by hand.</summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class JpxTileLayoutTests
{
    private static readonly byte[] Data5 = { 1, 2, 3, 4, 5 };

    internal static JpxTileLayout Layout(params byte[][] mainSegments)
    {
        byte[] cs = J.Concat(J.Soc, J.Concat(mainSegments), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        Assert.True(JpxTileLayout.TryBuild(parsed, 0, out JpxTileLayout layout, out reason), reason);
        return layout;
    }

    /// <summary>
    /// 64x64, one decomposition level, 64x64 code-blocks, default precincts: resolution 0 is the LL
    /// band (0, 0)-(32, 32) in one precinct with one code-block; resolution 1 holds HL, LH and HH,
    /// each (0, 0)-(32, 32), one code-block each. Eight structures.
    /// </summary>
    [Fact]
    public void DefaultPrecincts_OnePrecinctPerResolution()
    {
        JpxTileLayout layout = Layout(J.Siz64(), J.Cod(), J.QcdNone());
        JpxTileComponent c = Assert.Single(layout.Components);
        Assert.Equal(2, c.Resolutions.Length);

        JpxResolution r0 = c.Resolutions[0];
        Assert.Equal("(0, 0)-(32, 32)", r0.Bounds.ToString());
        Assert.Equal((1L, 1L), (r0.PrecinctsWide, r0.PrecinctsHigh));
        Assert.Equal((15, 15), (r0.PrecinctWidthExponent, r0.PrecinctHeightExponent));
        Assert.Equal(6, r0.CodeBlockWidthExponent);
        JpxPrecinctBand ll = Assert.Single(Assert.Single(r0.Precincts).Bands);
        Assert.Equal(0, ll.Band.Orientation);
        Assert.Equal("(0, 0)-(32, 32)", Assert.Single(ll.Blocks).Bounds.ToString());

        JpxResolution r1 = c.Resolutions[1];
        Assert.Equal("(0, 0)-(64, 64)", r1.Bounds.ToString());
        Assert.Equal(new[] { 1, 2, 3 }, Array.ConvertAll(r1.Bands, b => b.Orientation));
        foreach (JpxPrecinctBand pb in Assert.Single(r1.Precincts).Bands)
        {
            Assert.Equal("(0, 0)-(32, 32)", pb.Band.Bounds.ToString());
            Assert.Equal(1, pb.Band.Level);
            Assert.Equal("(0, 0)-(32, 32)", Assert.Single(pb.Blocks).Bounds.ToString());
            Assert.NotNull(pb.Inclusion);
        }
        Assert.Equal(8L, layout.Structures);
    }

    /// <summary>
    /// Signalled precincts (Table A.21): 16x16 at resolution 0 (2x2 precincts of a 32x32 level), 32x32
    /// at resolution 1 (2x2 of 64x64). Above level 0 a precinct maps into a band at half its size
    /// (B-17), so precinct 3 of resolution 1 is (16, 16)-(32, 32) of each 32x32 band, and the
    /// code-block, 64x64 by the COD, is bounded to 16x16.
    /// </summary>
    [Fact]
    public void SignalledPrecincts_PartitionEachResolution_AndBoundTheCodeBlock()
    {
        JpxTileLayout layout = Layout(J.Siz64(), J.Cod(precincts: new byte[] { 0x44, 0x55 }), J.QcdNone());
        JpxTileComponent c = Assert.Single(layout.Components);

        JpxResolution r0 = c.Resolutions[0];
        Assert.Equal((2L, 2L), (r0.PrecinctsWide, r0.PrecinctsHigh));
        Assert.Equal(4, r0.CodeBlockWidthExponent);   // min(6, PPx 4)
        Assert.Equal("(16, 16)-(32, 32)", Assert.Single(r0.Precincts[3].Bands[0].Blocks).Bounds.ToString());

        JpxResolution r1 = c.Resolutions[1];
        Assert.Equal((2L, 2L), (r1.PrecinctsWide, r1.PrecinctsHigh));
        Assert.Equal(4, r1.CodeBlockWidthExponent);   // min(6, PPx 5 − 1)
        JpxPrecinctBand hl = r1.Precincts[3].Bands[0];
        Assert.Equal("(16, 16)-(32, 32)", hl.Bounds.ToString());
        Assert.Equal("(16, 16)-(32, 32)", Assert.Single(hl.Blocks).Bounds.ToString());
        Assert.Equal("(0, 16)-(16, 32)", r1.Precincts[2].Bands[0].Bounds.ToString());
        Assert.Equal(4L + 4 + 12 + 12, layout.Structures);
    }

    /// <summary>
    /// A tile-component off the origin, (3, 5)-(16, 14), one level, 4x4 code-blocks: the LL band is
    /// (2, 3)-(8, 7), and the code-block partition anchored at (0, 0) (§B.7) cuts it into four
    /// unequal blocks — (2, 3)-(4, 4), (4, 3)-(8, 4), (2, 4)-(4, 7), (4, 4)-(8, 7) — in raster order.
    /// </summary>
    [Fact]
    public void CodeBlocks_AreAnchoredAtTheOrigin_AndClippedToTheBand()
    {
        byte[] siz = J.Siz(16, 14, 3, 5, 16, 14, 0, 0, new[] { (0x07, 1, 1) });
        JpxTileLayout layout = Layout(siz, J.Cod(xcbValue: 0, ycbValue: 0), J.QcdNone());
        JpxPrecinctBand ll = Assert.Single(layout.Components[0].Resolutions[0].Precincts)!.Bands[0];
        Assert.Equal("(2, 3)-(8, 7)", ll.Bounds.ToString());
        Assert.Equal((2, 2), (ll.BlocksWide, ll.BlocksHigh));
        Assert.Equal(
            new[] { "(2, 3)-(4, 4)", "(4, 3)-(8, 4)", "(2, 4)-(4, 7)", "(4, 4)-(8, 7)" },
            Array.ConvertAll(ll.Blocks, b => b.Bounds.ToString()));
    }

    /// <summary>
    /// Sub-sampling can leave a precinct with no samples of a band (§B.6): with the second component
    /// at (2, 2) on a 64x64 grid and 32x32 precincts at resolution 1, its 32x32 level has one
    /// precinct, its 16x16 bands hold one block each, and the first component's level has four.
    /// </summary>
    [Fact]
    public void ASubsampledComponent_HasFewerPrecincts()
    {
        byte[] siz = J.Siz(64, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1), (0x07, 2, 2) });
        JpxTileLayout layout = Layout(siz, J.Cod(precincts: new byte[] { 0x55, 0x55 }), J.QcdNone());
        Assert.Equal(4L, layout.Components[0].Resolutions[1].PrecinctCount);
        JpxResolution r1 = layout.Components[1].Resolutions[1];
        Assert.Equal("(0, 0)-(32, 32)", r1.Bounds.ToString());
        Assert.Equal(1L, r1.PrecinctCount);
        Assert.Equal("(0, 0)-(16, 16)", Assert.Single(r1.Precincts[0].Bands[2].Blocks).Bounds.ToString());
    }

    /// <summary>
    /// The refusal precedes the allocation it bounds, at the default ceiling: a 4096x4096 tile with
    /// one level, 2x2 precincts everywhere and 4x4 code-blocks declares a million precinct-bands at
    /// resolution 0 and twelve million at resolution 1 — past the four million of
    /// <see cref="PdfLimits.DefaultMaxJpxCodeBlocks"/> — from a 120-byte header, and is refused by
    /// name with nothing built. The same header over a 64x64 tile is accepted.
    /// </summary>
    [Fact]
    public void MillionsOfStructuresFromAShortHeader_AreRefusedBeforeAllocation()
    {
        static byte[] Header(int side) => J.Concat(J.Soc, J.Siz(side, side, 0, 0, side, side, 0, 0, new[] { (0x07, 1, 1) }),
            J.Cod(levels: 1, xcbValue: 0, ycbValue: 0, precincts: new byte[] { 0x11, 0x11 }), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);

        Assert.True(JpxCodestreamParser.TryParse(Header(4096), out JpxCodestream hostile, out string reason), reason);
        Assert.False(JpxTileLayout.TryBuild(hostile, 0, out JpxTileLayout none, out reason));
        Assert.Null(none);
        Assert.Contains($"PdfLimits.MaxJpxCodeBlocks is {PdfLimits.DefaultMaxJpxCodeBlocks}", reason, StringComparison.Ordinal);

        Assert.True(JpxCodestreamParser.TryParse(Header(64), out JpxCodestream small, out reason), reason);
        Assert.True(JpxTileLayout.TryBuild(small, 0, out JpxTileLayout layout, out reason), reason);
        Assert.Equal(256L + 256 + 3 * 1024 + 3 * 1024, layout.Structures);   // 16x16 precincts at r0 (one block each); 32x32 at r1 over three bands
    }

    /// <summary>
    /// The largest tile a SIZ can declare, 2^32 − 1 a side: its area wraps a <c>long</c> negative and
    /// slipped under the pixel-area ceiling, to be refused later by the structure count for the
    /// wrong reason. The area is compared by division, and the refusal names the pixel area.
    /// </summary>
    [Fact]
    public void ATileWhoseAreaOverflowsALong_IsRefusedByThePixelArea()
    {
        byte[] cs = J.Concat(J.Soc, J.Siz(0xFFFFFFFF, 0xFFFFFFFF, 0, 0, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0, new[] { (0x07, 1, 1) }),
            J.Cod(), J.QcdNone(), J.TilePart(0, 0, Array.Empty<byte>(), Data5), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        Assert.False(JpxTileLayout.TryBuild(parsed, 0, out _, out reason));
        Assert.Contains("4294967295x4294967295 samples of the reference grid; PdfLimits.MaxImagePixelArea is", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="PdfLimits.MaxJpxCodeBlocks"/> counts code-blocks and precinct-bands, and is read at
    /// build time: the eight-structure layout is refused at 7 and accepted at 8.
    /// </summary>
    [Fact]
    public void TheStructureCeiling_IsReadFromPdfLimits()
    {
        byte[] cs = J.Minimal();
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        try
        {
            PdfLimits.MaxJpxCodeBlocks = 7;
            Assert.False(JpxTileLayout.TryBuild(parsed, 0, out _, out reason));
            Assert.Contains("PdfLimits.MaxJpxCodeBlocks is 7", reason, StringComparison.Ordinal);
            PdfLimits.MaxJpxCodeBlocks = 8;
            Assert.True(JpxTileLayout.TryBuild(parsed, 0, out JpxTileLayout layout, out reason), reason);
            Assert.Equal(8L, layout.Structures);

            PdfLimits.MaxImagePixelArea = 64 * 64 - 1;
            Assert.False(JpxTileLayout.TryBuild(parsed, 0, out _, out reason));
            Assert.Contains("PdfLimits.MaxImagePixelArea is 4095", reason, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
