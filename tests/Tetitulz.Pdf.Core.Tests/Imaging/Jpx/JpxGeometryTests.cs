// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxGeometry"/> against T.800 Annex B. The tiling tests use the standard's own worked
/// example (§B.4, Figures B.5 to B.7): every expected value below is printed in the standard, not
/// computed here. That is the independent oracle a geometry needs — the same hand writing the
/// equations and the expected values would share any misreading.
/// </summary>
public sealed class JpxGeometryTests
{
    /// <summary>
    /// §B.4: reference grid 1432x954, image offset (152, 234), tiles 396x297 at offset (0, 0), and
    /// two components, the second sub-sampled by 2 both ways.
    /// </summary>
    private static JpxImageSize ExampleB4()
    {
        byte[] cs = JpxCodestreams.Siz(xsiz: 1432, ysiz: 954, xosiz: 152, yosiz: 234,
            xtsiz: 396, ytsiz: 297, xtosiz: 0, ytosiz: 0,
            components: new[] { (0x07, 1, 1), (0x07, 2, 2) });
        Assert.True(JpxImageSize.TryRead(cs, 2, cs.Length, out JpxImageSize siz, out string reason), reason);
        Assert.True(siz.TryValidateGrid(out reason), reason);
        return siz;
    }

    [Fact]
    public void ComponentExtents_MatchTheWorkedExample()
    {
        JpxImageSize siz = ExampleB4();

        // "The upper left corner of component 0 is found at (152, 234) ... lower right (1431, 953)";
        // component 1 at (76, 117) to (715, 476): 1280x720 and 640x360.
        JpxRect c0 = JpxGeometry.Component(siz, 0);
        JpxRect c1 = JpxGeometry.Component(siz, 1);
        Assert.Equal((152L, 234L, 1432L, 954L), (c0.X0, c0.Y0, c0.X1, c0.Y1));
        Assert.Equal((1280L, 720L), (c0.Width, c0.Height));
        Assert.Equal((76L, 117L, 716L, 477L), (c1.X0, c1.Y0, c1.X1, c1.Y1));
        Assert.Equal((640L, 360L), (c1.Width, c1.Height));
    }

    /// <summary>
    /// The worked example's grid edges are even, so at sub-sampling 2 its ⌈·⌉ and ⌊·⌋ agree and it
    /// cannot see equation B-1's ceiling — a floor there survived mutation until the corpus caught
    /// it. Odd edges and offsets, worked by hand: a partial last sample counts, a partial first one
    /// is skipped.
    /// </summary>
    [Fact]
    public void ComponentExtents_RoundPartialSamplesAsEquationB1Does()
    {
        byte[] cs = JpxCodestreams.Siz(xsiz: 1433, ysiz: 955, xosiz: 153, yosiz: 235,
            xtsiz: 1433, ytsiz: 955, xtosiz: 0, ytosiz: 0,
            components: new[] { (0x07, 1, 1), (0x07, 2, 2) });
        Assert.True(JpxImageSize.TryRead(cs, 2, cs.Length, out JpxImageSize siz, out string reason), reason);
        Assert.True(siz.TryValidateGrid(out reason), reason);

        // ⌈153/2⌉ = 77, ⌈235/2⌉ = 118, ⌈1433/2⌉ = 717, ⌈955/2⌉ = 478 — a floor gives 76, 117, 716, 477.
        JpxRect c1 = JpxGeometry.Component(siz, 1);
        Assert.Equal((77L, 118L, 717L, 478L), (c1.X0, c1.Y0, c1.X1, c1.Y1));
        Assert.Equal((640L, 360L), (c1.Width, c1.Height));
    }

    [Fact]
    public void TileGrid_MatchesTheWorkedExample()
    {
        JpxImageSize siz = ExampleB4();

        // numXtiles = ceil(1432/396) = 4, numYtiles = ceil(954/297) = 4 (equation B-5).
        Assert.Equal(4, siz.NumXTiles);
        Assert.Equal(4, siz.NumYTiles);

        // "tx0(0:3,*) = {152, 396, 792, 1188} ... tx1(0:3,*) = {396, 792, 1188, 1432}
        //  ty0(*,0:3) = {234, 297, 594, 891} ... ty1(*,0:3) = {297, 594, 891, 954}".
        long[] tx0 = { 152, 396, 792, 1188 }, tx1 = { 396, 792, 1188, 1432 };
        long[] ty0 = { 234, 297, 594, 891 }, ty1 = { 297, 594, 891, 954 };
        for (int q = 0; q < 4; q++)
            for (int p = 0; p < 4; p++)
            {
                JpxRect t = JpxGeometry.Tile(siz, p + 4 * q);
                Assert.Equal((tx0[p], ty0[q], tx1[p], ty1[q]), (t.X0, t.Y0, t.X1, t.Y1));
            }
    }

    /// <summary>
    /// Figure B.7 and its text: in component 1, "tiles (1, 1) and (2, 1) are both of size 198 by 148,
    /// while tiles (1, 2) and (2, 2) are both of size 198 by 149" — the case where sub-sampling makes
    /// equal reference-grid tiles unequal.
    /// </summary>
    [Fact]
    public void SubsampledTileComponents_MatchFigureB7()
    {
        JpxImageSize siz = ExampleB4();

        JpxRect TileComponent(int p, int q) => JpxGeometry.TileComponent(siz, JpxGeometry.Tile(siz, p + 4 * q), 1);

        Assert.Equal((198L, 148L), (TileComponent(1, 1).Width, TileComponent(1, 1).Height));
        Assert.Equal((198L, 148L), (TileComponent(2, 1).Width, TileComponent(2, 1).Height));
        Assert.Equal((198L, 149L), (TileComponent(1, 2).Width, TileComponent(1, 2).Height));
        Assert.Equal((198L, 149L), (TileComponent(2, 2).Width, TileComponent(2, 2).Height));

        // The corners Figure B.7 prints: tile (0, 0) starts at (76, 117) and is 122x32; tile (0, 3)
        // starts at (76, 446) and is 122x31; tile (3, 3) ends at (716, 477).
        JpxRect first = TileComponent(0, 0);
        Assert.Equal((76L, 117L, 122L, 32L), (first.X0, first.Y0, first.Width, first.Height));
        JpxRect bottomLeft = TileComponent(0, 3);
        Assert.Equal((76L, 446L, 122L, 31L), (bottomLeft.X0, bottomLeft.Y0, bottomLeft.Width, bottomLeft.Height));
        JpxRect last = TileComponent(3, 3);
        Assert.Equal((716L, 477L), (last.X1, last.Y1));
    }

    /// <summary>
    /// Resolution levels and sub-bands (equations B-14, B-15) on a tile-component (3, 5)-(20, 17) with
    /// two decomposition levels, worked by hand from the equations.
    /// </summary>
    [Fact]
    public void ResolutionsAndSubbands_FollowEquationsB14AndB15()
    {
        var tc = new JpxRect(3, 5, 20, 17);

        // r = 0: ceil(3/4), ceil(5/4), ceil(20/4), ceil(17/4).
        Assert.Equal("(1, 2)-(5, 5)", JpxGeometry.Resolution(tc, 2, 0).ToString());
        Assert.Equal("(2, 3)-(10, 9)", JpxGeometry.Resolution(tc, 2, 1).ToString());
        Assert.Equal("(3, 5)-(20, 17)", JpxGeometry.Resolution(tc, 2, 2).ToString());

        // nb = 1, HL (xob 1, yob 0): ceil((3-1)/2), ceil(5/2), ceil((20-1)/2), ceil(17/2).
        Assert.Equal("(1, 3)-(10, 9)", JpxGeometry.Subband(tc, 1, 1, 0).ToString());
        // nb = 1, HH: the offset applies in both directions.
        Assert.Equal("(1, 2)-(10, 8)", JpxGeometry.Subband(tc, 1, 1, 1).ToString());
        // nb = 2, LL is the r = 0 resolution level.
        Assert.Equal(JpxGeometry.Resolution(tc, 2, 0).ToString(), JpxGeometry.Subband(tc, 2, 0, 0).ToString());
    }

    /// <summary>
    /// A high-pass band of a tile-component that starts at 0 has a negative numerator in B-15;
    /// ⌈-1/2⌉ is 0, not -1 — the ceiling a truncating division gets wrong for negatives.
    /// </summary>
    [Fact]
    public void Subband_CeilingOfANegativeNumerator_RoundsTowardsPositive()
    {
        var tc = new JpxRect(0, 0, 7, 7);
        Assert.Equal("(0, 0)-(3, 4)", JpxGeometry.Subband(tc, 1, 1, 0).ToString());   // ceil(-1/2)=0, ceil(6/2)=3, ceil(7/2)=4
        Assert.Equal(0, JpxGeometry.CeilDiv(-1, 2));
        Assert.Equal(-1, JpxGeometry.FloorDiv(-1, 2));
        Assert.Equal(2, JpxGeometry.CeilDiv(3, 2));
    }

    /// <summary>
    /// Equation B-16: precincts anchored at (0, 0), counted by the ceiling of the far edge less the
    /// floor of the near edge — and 0 for an empty level, where the subtraction would give 1.
    /// </summary>
    [Fact]
    public void PrecinctCount_FollowsEquationB16()
    {
        // Resolution (2, 3)-(10, 9), PPx 2 (width 4): ceil(10/4) - floor(2/4) = 3.
        Assert.Equal(3, JpxGeometry.PrecinctCount(2, 10, 2));
        // PPy 1 (height 2): ceil(9/2) - floor(3/2) = 4.
        Assert.Equal(4, JpxGeometry.PrecinctCount(3, 9, 1));
        // An empty level has no precincts, although ceil(5/4) - floor(5/4) is 1.
        Assert.Equal(0, JpxGeometry.PrecinctCount(5, 5, 2));
    }

    /// <summary>Equations B-17 and B-18: above level 0 a code-block is at most half a precinct.</summary>
    [Fact]
    public void CodeBlockExponent_IsBoundedByThePrecinct()
    {
        Assert.Equal(5, JpxGeometry.CodeBlockExponent(6, 5, 0));   // min(6, 5)
        Assert.Equal(4, JpxGeometry.CodeBlockExponent(6, 5, 1));   // min(6, 5 - 1)
        Assert.Equal(6, JpxGeometry.CodeBlockExponent(6, 15, 3));  // the default 2^15 precinct never binds
    }
}
