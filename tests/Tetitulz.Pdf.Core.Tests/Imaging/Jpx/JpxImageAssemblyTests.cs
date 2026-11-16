// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxDecodedImage"/>: the image area (T.800 §B.2), the placement of tile-components
/// with upsampling by replication, the ceilings it reads at run time, and the narrowing to 8 bits
/// by the one narrowing rule (once <c>Jpeg2000DecodePlugin.To8Bit</c>'s).
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class JpxImageAssemblyTests
{
    private static JpxImageSize Grid(long xsiz, long ysiz, long xosiz, long yosiz, params (int Ssiz, int XR, int YR)[] components)
    {
        byte[] siz = J.Siz(xsiz, ysiz, xosiz, yosiz, xtsiz: xsiz, ytsiz: ysiz, xtosiz: 0, ytosiz: 0, components);
        Assert.True(JpxImageSize.TryRead(siz, 2, siz.Length, out JpxImageSize size, out string reason), reason);
        Assert.True(size.TryValidateGrid(out reason), reason);
        return size;
    }

    private static JpxDecodedImage Create(JpxImageSize grid)
    {
        Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out JpxDecodedImage image, out string reason), reason);
        return image;
    }

    /// <summary>The image is Xsiz − XOsiz by Ysiz − YOsiz, with one plane of that size per component, all zero until placed.</summary>
    [Fact]
    public void TryCreate_SizesTheImageAreaOfTheReferenceGrid()
    {
        JpxDecodedImage image = Create(Grid(1432, 954, 152, 234, (0x07, 1, 1), (0x87, 2, 2)));
        Assert.Equal((1280, 720), (image.Width, image.Height));
        Assert.Equal(2, image.Samples.Length);
        Assert.Equal(1280 * 720, image.Plane(1).Length);
        Assert.Equal((8, true, 2, 2), (image.Components[1].Precision, image.Components[1].IsSigned, image.Components[1].XRsiz, image.Components[1].YRsiz));
        Assert.All(image.Plane(0), v => Assert.Equal(0, v));
        Assert.Same(image.Plane(0), image.Samples[0]);
    }

    /// <summary>
    /// A single tile at the grid's resolution covering the image area is adopted: the plane is the
    /// caller's array, nothing copied — with or without an image offset, since the tile-component
    /// then spans exactly the area. A tile that is not the whole plane (a part of the area, a
    /// second tile, a sub-sampled component) is placed by copy, and the control shows the copy:
    /// the caller's array changed afterwards leaves the plane as it was.
    /// </summary>
    [Fact]
    public void Adopt_TakesTheWholePlaneWithoutACopy_AndPlacesAnythingElse()
    {
        JpxDecodedImage whole = Create(Grid(3, 2, 0, 0, (0x07, 1, 1), (0x07, 2, 1)));
        int[] samples = { 1, 2, 3, 4, 5, 6 };
        Assert.True(whole.Adopt(0, new JpxRect(0, 0, 3, 2), samples));
        Assert.Same(samples, whole.Samples[0]);
        Assert.False(whole.Adopt(1, new JpxRect(0, 0, 2, 2), new[] { 7, 8, 9, 10 }));   // sub-sampled: replicated, so copied
        Assert.Equal(new[] { 7, 7, 8, 9, 9, 10 }, whole.Samples[1]);

        JpxDecodedImage offset = Create(Grid(4, 2, 1, 0, (0x07, 1, 1)));
        int[] shifted = { 1, 2, 3, 4, 5, 6 };
        Assert.True(offset.Adopt(0, new JpxRect(1, 0, 4, 2), shifted));
        Assert.Same(shifted, offset.Samples[0]);

        JpxDecodedImage partly = Create(Grid(4, 2, 0, 0, (0x07, 1, 1)));
        int[] part = { 1, 2, 3, 4 };
        Assert.False(partly.Adopt(0, new JpxRect(1, 0, 3, 2), part));
        part[0] = 99;
        Assert.Equal(new[] { 0, 1, 2, 0, 0, 3, 4, 0 }, partly.Samples[0]);

        byte[] siz = J.Siz(4, 2, 0, 0, xtsiz: 2, ytsiz: 2, xtosiz: 0, ytosiz: 0, new[] { (0x07, 1, 1) });
        Assert.True(JpxImageSize.TryRead(siz, 2, siz.Length, out JpxImageSize grid, out string reason), reason);
        Assert.True(grid.TryValidateGrid(out reason), reason);
        JpxDecodedImage tiled = Create(grid);
        Assert.False(tiled.Adopt(0, JpxGeometry.TileComponent(grid, JpxGeometry.Tile(grid, 0), 0), new[] { 1, 2, 3, 4 }));
        Assert.False(tiled.Adopt(0, JpxGeometry.TileComponent(grid, JpxGeometry.Tile(grid, 1), 0), new[] { 5, 6, 7, 8 }));
        Assert.Equal(new[] { 1, 2, 5, 6, 3, 4, 7, 8 }, tiled.Samples[0]);
    }

    /// <summary>A component past 31 bits: its long array is adopted whole, without a copy, and placed by replication when sub-sampled.</summary>
    [Fact]
    public void Adopt_WidePlane_TakesTheArrayWithoutACopy_AndPlacesASubSampledOne()
    {
        JpxDecodedImage image = Create(Grid(3, 2, 0, 0, (0x1F, 1, 1), (0x1F, 2, 1)));   // 32-bit
        long[] samples = { 1L << 31, 2, 3, 4, 5, 1L << 31 | 6 };
        Assert.True(image.Adopt(0, new JpxRect(0, 0, 3, 2), samples));
        Assert.Same(samples, image.WideSamples[0]);
        Assert.Null(image.Samples[0]);
        Assert.False(image.Adopt(1, new JpxRect(0, 0, 2, 2), new long[] { 7, 1L << 33, 9, 10 }));
        Assert.Equal(new long[] { 7, 7, 1L << 33, 9, 9, 10 }, image.WideSamples[1]);
        // Narrowed and interleaved: a 32-bit sample's top byte per component, 2^33 clipped to 255.
        Assert.Equal(new byte[] { 128, 0, 0, 0, 0, 255, 0, 0, 0, 0, 128, 0 }, image.ToInterleaved8Bit());
    }

    /// <summary>
    /// The image over the area at a reduction: each side ⌈· / 2^r⌉ of the grid value, so a 7x5
    /// area at (3, 2) — (3, 2)-(10, 7) — is (2, 1)-(5, 4) one level short and (1, 1)-(3, 2) two
    /// short; placement by the reduced bounds fills the reduced plane. The ceilings are the full
    /// area's whatever the reduction; a reduction outside 0 to 32 is refused, and so is one that
    /// leaves an offset area empty.
    /// </summary>
    [Fact]
    public void TryCreate_AtAReduction_SizesTheImageAreaByItsCeilings()
    {
        JpxImageSize grid = Grid(10, 7, 3, 2, (0x07, 1, 1));
        Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 1, out JpxDecodedImage one, out string reason), reason);
        Assert.Equal((1, 3, 3), (one.Reduction, one.Width, one.Height));
        Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 2, out JpxDecodedImage two, out reason), reason);
        Assert.Equal((2, 2, 1), (two.Reduction, two.Width, two.Height));

        // A tile-component at the reduced bounds fills the plane; one at the full bounds does not fit and throws for the count.
        one.Place(0, new JpxRect(2, 1, 5, 4), new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, one.Samples[0]);
        Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 1, out JpxDecodedImage adopted, out reason), reason);
        Assert.True(adopted.Adopt(0, new JpxRect(2, 1, 5, 4), new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 }));
        Assert.False(adopted.Adopt(0, new JpxRect(3, 2, 10, 7), new int[35]));   // the full bounds are not the reduced plane
        // Nine samples at the plane's origin but as one row of nine: the same count is not the same
        // shape, so they are placed — the row's overlap, three samples — rather than adopted whole.
        Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 1, out JpxDecodedImage row, out reason), reason);
        Assert.False(row.Adopt(0, new JpxRect(2, 1, 11, 2), new[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 }));
        Assert.Equal(new[] { 9, 8, 7, 0, 0, 0, 0, 0, 0 }, row.Samples[0]);

        Assert.False(JpxDecodedImage.TryCreate(grid, grid.Components, 33, out _, out reason));
        Assert.Contains("a reduction of 33 levels", reason, StringComparison.Ordinal);
        Assert.False(JpxDecodedImage.TryCreate(grid, grid.Components, -1, out _, out reason));
        Assert.Contains("a reduction of -1 levels", reason, StringComparison.Ordinal);
        // Thirty-two levels short, the offset area's two corners fall in one 2^32 cell and the area is
        // empty — refused with the reason; from the origin the same reduction leaves one sample.
        Assert.False(JpxDecodedImage.TryCreate(grid, grid.Components, 32, out _, out reason));
        Assert.Contains("empty at reduction 32", reason, StringComparison.Ordinal);
        JpxImageSize origin = Grid(10, 7, 0, 0, (0x07, 1, 1));
        Assert.True(JpxDecodedImage.TryCreate(origin, origin.Components, 32, out JpxDecodedImage deepest, out reason), reason);
        Assert.Equal((1, 1), (deepest.Width, deepest.Height));
    }

    /// <summary>
    /// The ceilings, read at run time: the pixel area, the dimension, and the sample count against
    /// <see cref="PdfLimits.MaxDecompressedBytes"/> (one byte a sample); a component deeper than the
    /// planes hold. Each refusal names its limit; the control just under it is accepted.
    /// </summary>
    [Fact]
    public void TryCreate_RefusesWhatTheCeilingsExclude()
    {
        try
        {
            JpxImageSize grid = Grid(4, 4, 0, 0, (0x07, 1, 1), (0x07, 1, 1));

            PdfLimits.MaxImagePixelArea = 15;
            Assert.False(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out _, out string reason));
            Assert.Contains("PdfLimits.MaxImagePixelArea is 15", reason, StringComparison.Ordinal);
            PdfLimits.MaxImagePixelArea = 16;
            Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out _, out reason), reason);

            PdfLimits.MaxDecompressedBytes = 31;
            Assert.False(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out _, out reason));
            Assert.Contains("32 samples; PdfLimits.MaxDecompressedBytes is 31", reason, StringComparison.Ordinal);
            PdfLimits.MaxDecompressedBytes = 32;
            Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out _, out reason), reason);

            // 32 samples at the measured price; the reduced area is what is charged (2x2 x 2 at reduction 1).
            // The price is a measurement (4 086 MB for 243 million samples on 2026-10-24), pinned: the
            // assertions below are written from the constant and would follow it anywhere.
            Assert.Equal(17, JpxDecodedImage.WorkingBytesPerSample);
            PdfLimits.MaxJpxWorkingSetBytes = 32 * JpxDecodedImage.WorkingBytesPerSample - 1;
            Assert.False(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out _, out reason));
            Assert.Contains($"{32 * JpxDecodedImage.WorkingBytesPerSample} bytes at {JpxDecodedImage.WorkingBytesPerSample} a sample; PdfLimits.MaxJpxWorkingSetBytes is {32 * JpxDecodedImage.WorkingBytesPerSample - 1}", reason, StringComparison.Ordinal);
            Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 1, out _, out reason), reason);
            PdfLimits.MaxJpxWorkingSetBytes = 32 * JpxDecodedImage.WorkingBytesPerSample;
            Assert.True(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out _, out reason), reason);

            PdfLimits.MaxImageDimension = 3;
            Assert.False(JpxDecodedImage.TryCreate(grid, grid.Components, 0, out _, out reason));
            Assert.Contains("PdfLimits.MaxImageDimension is 3", reason, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }

        // 32 bits takes a long plane; 38 is T.800's deepest; a CBD could declare 39, which no plane holds.
        JpxImageSize deep = Grid(4, 4, 0, 0, (0x1F, 1, 1));   // 32-bit
        Assert.True(JpxDecodedImage.TryCreate(deep, deep.Components, 0, out JpxDecodedImage wideImage, out string why), why);
        Assert.True(wideImage.IsWide(0));
        Assert.Equal(16, wideImage.WidePlane(0).Length);
        Assert.Null(wideImage.Samples[0]);
        JpxImageSize deepest = Grid(4, 4, 0, 0, (0x25, 1, 1));   // 38-bit
        Assert.True(JpxDecodedImage.TryCreate(deepest, deepest.Components, 0, out _, out why), why);
        JpxImageSize narrow = Grid(4, 4, 0, 0, (0x1E, 1, 1));   // 31-bit: an int plane
        Assert.True(JpxDecodedImage.TryCreate(narrow, narrow.Components, 0, out JpxDecodedImage narrowImage, out why), why);
        Assert.False(narrowImage.IsWide(0));
        var past = new[] { new JpxComponentSize(39, false, 1, 1) };
        Assert.False(JpxDecodedImage.TryCreate(narrow, past, 0, out _, out why));
        Assert.Contains("39-bit samples", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 3x2 component sub-sampled 2x2 on a 5x4 grid: sample (cx, cy) fills grid columns 2cx and
    /// 2cx + 1 and rows 2cy and 2cy + 1, the last column just once because the grid is odd.
    /// </summary>
    [Fact]
    public void Place_UpsamplesASubsampledComponentByReplication()
    {
        JpxDecodedImage image = Create(Grid(5, 4, 0, 0, (0x07, 2, 2)));
        image.Place(0, new JpxRect(0, 0, 3, 2), new[] { 1, 2, 3, 4, 5, 6 });

        Assert.Equal(new[]
        {
            1, 1, 2, 2, 3,
            1, 1, 2, 2, 3,
            4, 4, 5, 5, 6,
            4, 4, 5, 5, 6,
        }, image.Samples[0]);
    }

    /// <summary>
    /// An image offset that is not a multiple of the separation: XOsiz 1, Xsiz 6, XRsiz 2 makes the
    /// component start at ⌈1/2⌉ = 1 (grid column 2), so grid column 1 — the image's first — takes the
    /// component's first sample too, and columns 2-3 and 4-5 the first and second.
    /// </summary>
    [Fact]
    public void Place_ExtendsTheFirstSampleBackToAnOddImageOffset()
    {
        JpxDecodedImage image = Create(Grid(6, 1, 1, 0, (0x07, 2, 1)));
        Assert.Equal((5, 1), (image.Width, image.Height));
        image.Place(0, new JpxRect(1, 0, 3, 1), new[] { 7, 9 });

        Assert.Equal(new[] { 7, 7, 7, 9, 9 }, image.Samples[0]);
    }

    /// <summary>
    /// Two tiles side by side on a 5x2 grid, 3 wide: each tile-component's samples land at its
    /// offset, and the second row of the second tile is not written where the first tile's is.
    /// </summary>
    [Fact]
    public void Place_PutsTwoTilesSideBySide()
    {
        byte[] siz = J.Siz(5, 2, 0, 0, xtsiz: 3, ytsiz: 2, xtosiz: 0, ytosiz: 0, new[] { (0x07, 1, 1) });
        Assert.True(JpxImageSize.TryRead(siz, 2, siz.Length, out JpxImageSize grid, out string reason), reason);
        Assert.True(grid.TryValidateGrid(out reason), reason);
        Assert.Equal(2, grid.NumXTiles);
        JpxDecodedImage image = Create(grid);

        JpxRect tile0 = JpxGeometry.TileComponent(grid, JpxGeometry.Tile(grid, 0), 0);
        JpxRect tile1 = JpxGeometry.TileComponent(grid, JpxGeometry.Tile(grid, 1), 0);
        Assert.Equal("(0, 0)-(3, 2)", tile0.ToString());
        Assert.Equal("(3, 0)-(5, 2)", tile1.ToString());
        image.Place(0, tile1, new[] { 4, 5, 9, 10 });
        image.Place(0, tile0, new[] { 1, 2, 3, 6, 7, 8 });

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, image.Samples[0]);
    }

    /// <summary>
    /// The same two tiles with the component sub-sampled 2x1: tile 0's component is columns 0-1 (grid
    /// 0-3) and tile 1's column 2 (grid 4), so grid column 3 takes tile 0's second sample although it
    /// lies in tile 1's part of the grid — B-12's ceiling puts the sample at grid 2 in tile 0.
    /// </summary>
    [Fact]
    public void Place_ReplicatesAcrossATileBoundaryOnTheGrid()
    {
        byte[] siz = J.Siz(5, 1, 0, 0, xtsiz: 3, ytsiz: 1, xtosiz: 0, ytosiz: 0, new[] { (0x07, 2, 1) });
        Assert.True(JpxImageSize.TryRead(siz, 2, siz.Length, out JpxImageSize grid, out string reason), reason);
        Assert.True(grid.TryValidateGrid(out reason), reason);
        JpxDecodedImage image = Create(grid);

        JpxRect tile0 = JpxGeometry.TileComponent(grid, JpxGeometry.Tile(grid, 0), 0);
        JpxRect tile1 = JpxGeometry.TileComponent(grid, JpxGeometry.Tile(grid, 1), 0);
        Assert.Equal("(0, 0)-(2, 1)", tile0.ToString());
        Assert.Equal("(2, 0)-(3, 1)", tile1.ToString());
        image.Place(0, tile0, new[] { 1, 2 });
        image.Place(0, tile1, new[] { 3 });

        Assert.Equal(new[] { 1, 1, 2, 2, 3 }, image.Samples[0]);
    }

    /// <summary>
    /// The class's rule, position by position: every grid position of the image area takes the component sample at
    /// (⌊x / XRsiz⌋, ⌊y / YRsiz⌋), clamped into the component's extent — over tiles of 8x7, separations up to 3,
    /// and offsets in both directions that are not multiples of them, so the first row and column extend back and
    /// a component row's replicated span is copied down rows that cross tiles.
    /// </summary>
    [Theory]
    [InlineData(2, 2, 1, 1)]
    [InlineData(3, 2, 5, 4)]
    [InlineData(1, 3, 2, 6)]
    [InlineData(3, 3, 4, 4)]
    [InlineData(2, 1, 0, 3)]
    public void Place_GivesEveryGridPositionTheSampleItsSeparationNames_AcrossTiles(int xr, int yr, int xo, int yo)
    {
        byte[] siz = J.Siz(xo + 29, yo + 19, xo, yo, xtsiz: 8, ytsiz: 7, xtosiz: 0, ytosiz: 0, new[] { (0x07, xr, yr) });
        Assert.True(JpxImageSize.TryRead(siz, 2, siz.Length, out JpxImageSize grid, out string reason), reason);
        Assert.True(grid.TryValidateGrid(out reason), reason);
        JpxDecodedImage image = Create(grid);
        JpxRect extent = JpxGeometry.Component(grid, 0);
        static int Value(long cx, long cy) => (int)(cy * 1000 + cx);

        for (int t = 0; t < grid.NumTiles; t++)
        {
            JpxRect tc = JpxGeometry.TileComponent(grid, JpxGeometry.Tile(grid, t), 0);
            if (tc.Width <= 0 || tc.Height <= 0) continue;
            var samples = new int[tc.Width * tc.Height];
            for (long cy = tc.Y0; cy < tc.Y1; cy++)
                for (long cx = tc.X0; cx < tc.X1; cx++) samples[(cy - tc.Y0) * tc.Width + (cx - tc.X0)] = Value(cx, cy);
            image.Place(0, tc, samples);
        }

        var expected = new int[image.Width * image.Height];
        for (long y = yo; y < yo + 19; y++)
            for (long x = xo; x < xo + 29; x++)
            {
                long cx = Math.Clamp(x / xr, extent.X0, extent.X1 - 1), cy = Math.Clamp(y / yr, extent.Y0, extent.Y1 - 1);
                expected[(y - yo) * image.Width + (x - xo)] = Value(cx, cy);
            }
        Assert.Equal(expected, image.Samples[0]);
    }

    /// <summary>Samples outside the component's extent are ignored; a short sample array is refused.</summary>
    [Fact]
    public void Place_ClipsToTheComponentAndChecksTheSampleCount()
    {
        JpxDecodedImage image = Create(Grid(3, 1, 0, 0, (0x07, 1, 1)));
        image.Place(0, new JpxRect(1, 0, 5, 1), new[] { 1, 2, 3, 4 });
        Assert.Equal(new[] { 0, 1, 2 }, image.Samples[0]);
        Assert.Throws<ArgumentException>(() => image.Place(0, new JpxRect(0, 0, 3, 1), new[] { 1, 2 }));
    }

    /// <summary>
    /// The plugin's rule: an 8-bit sample is clipped and taken as is; a 12-bit one is shifted right by
    /// 4 (4095 → 255, 2048 → 128); a 4-bit one is scaled by 255/15 (15 → 255, 7 → 119); a signed one
    /// is offset first (−128 → 0, 0 → 128, 127 → 255 at 8 bits; −2048 → 0 at 12).
    /// </summary>
    [Fact]
    public void NarrowTo8Bit_FollowsTheOneRule()
    {
        Assert.Equal(255, JpxDecodedImage.NarrowTo8Bit(255, 8, false));
        Assert.Equal(255, JpxDecodedImage.NarrowTo8Bit(300, 8, false));
        Assert.Equal(0, JpxDecodedImage.NarrowTo8Bit(-5, 8, false));
        Assert.Equal(200, JpxDecodedImage.NarrowTo8Bit(200, 8, false));
        Assert.Equal(255, JpxDecodedImage.NarrowTo8Bit(4095, 12, false));
        Assert.Equal(128, JpxDecodedImage.NarrowTo8Bit(2048, 12, false));
        Assert.Equal(255, JpxDecodedImage.NarrowTo8Bit(15, 4, false));
        Assert.Equal(119, JpxDecodedImage.NarrowTo8Bit(7, 4, false));
        Assert.Equal(255, JpxDecodedImage.NarrowTo8Bit(99, 4, false));
        Assert.Equal(0, JpxDecodedImage.NarrowTo8Bit(-128, 8, true));
        Assert.Equal(128, JpxDecodedImage.NarrowTo8Bit(0, 8, true));
        Assert.Equal(255, JpxDecodedImage.NarrowTo8Bit(127, 8, true));
        Assert.Equal(0, JpxDecodedImage.NarrowTo8Bit(-2048, 12, true));
        Assert.Equal(255, JpxDecodedImage.NarrowTo8Bit(1, 1, false));
    }

    /// <summary>
    /// Two components, 8-bit and 12-bit, on a 2x1 grid interleave pixel by pixel in component order,
    /// each narrowed by its own depth. A component's depth is fixed when the image is created; a
    /// tile that reconstructs another depth is refused by the decoder, not recorded here.
    /// </summary>
    [Fact]
    public void ToInterleaved8Bit_InterleavesEachComponentNarrowedByItsDepth()
    {
        JpxDecodedImage image = Create(Grid(2, 1, 0, 0, (0x07, 1, 1), (0x0B, 1, 1)));
        image.Place(0, new JpxRect(0, 0, 2, 1), new[] { 10, 300 });
        image.Place(1, new JpxRect(0, 0, 2, 1), new[] { 4095, 2048 });
        Assert.Equal(new byte[] { 10, 255, 255, 128 }, image.ToInterleaved8Bit());
        Assert.Equal((12, false, 1, 1), (image.Components[1].Precision, image.Components[1].IsSigned, image.Components[1].XRsiz, image.Components[1].YRsiz));
    }
}
