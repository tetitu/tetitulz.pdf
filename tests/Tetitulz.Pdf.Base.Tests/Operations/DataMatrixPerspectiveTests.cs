// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Data Matrix symbols under perspective: the symbol's square mapped onto a quadrilateral by the test's
/// own homography (solved here from the four corners, never the locator's transform) and read through
/// <see cref="PdfBarcodeReader"/>.
/// </summary>
public class DataMatrixPerspectiveTests
{
    private const string Payload = "Data Matrix 2026 / lot 4711";

    /// <summary>
    /// The corners on the page, in modules of the unwarped symbol from its top-left, of a symbol whose top
    /// edge is <paramref name="topShrink"/> shorter than its bottom (a keystone) and whose right edge is
    /// <paramref name="rightShrink"/> shorter than its left.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.15, 0.0)]
    [InlineData(0.3, 0.0)]
    [InlineData(0.0, 0.3)]
    [InlineData(0.25, 0.25)]
    [InlineData(-0.3, 0.0)]
    public void ASymbolUnderPerspective_IsRead(double topShrink, double rightShrink)
    {
        bool[,] grid = DataMatrixEncoder.Encode(Payload);
        Assert.Contains(Read(Warped(grid, topShrink, rightShrink, modulePixels: 8, degrees: 0)), r => r.Type == BarcodeType.DataMatrix && r.Text == Payload);
    }

    [Theory]
    [InlineData(0.3, 0.0, 35)]
    [InlineData(0.2, 0.2, 200)]
    [InlineData(0.4, 0.0, 120)]
    public void ASymbolUnderPerspective_IsRead_Turned(double topShrink, double rightShrink, double degrees)
    {
        bool[,] grid = DataMatrixEncoder.Encode(Payload);
        Assert.Contains(Read(Warped(grid, topShrink, rightShrink, modulePixels: 8, degrees)), r => r.Type == BarcodeType.DataMatrix && r.Text == Payload);
    }

    /// <summary>A rectangular symbol, and one of four-pixel modules, under perspective.</summary>
    [Fact]
    public void ARectangularOrSmallSymbolUnderPerspective_IsRead()
    {
        bool[,] rect = DataMatrixEncoder.Encode("RECT 12345", rectangular: true);
        Assert.Contains(Read(Warped(rect, 0.25, 0.0, modulePixels: 8, degrees: 15)), r => r.Text == "RECT 12345");
        Assert.Contains(Read(Warped(DataMatrixEncoder.Encode(Payload), 0.2, 0.1, modulePixels: 4, degrees: 25)), r => r.Text == Payload);
    }

    /// <summary>
    /// When both clock tracks are traced, the one size they count is sampled, through the corner they meet
    /// at; the arms' proportions, which perspective breaks, are not consulted.
    /// </summary>
    [Fact]
    public void ATracedSymbol_IsSampledAtTheSizeItsTracksCount()
    {
        var found = Locate(Warped(DataMatrixEncoder.Encode(Payload), 0.3, 0.0, modulePixels: 8, degrees: 35));
        var grid = Assert.Single(found);
        Assert.Equal(22, grid.GetLength(0));
        Assert.Equal(22, grid.GetLength(1));
        Assert.Equal(Payload, DataMatrixGridReader.Decode(grid, out _));
    }

    /// <summary>
    /// A mark in the quiet zone a module past the top-right corner, in line with the top clock track, is
    /// counted as the track's twelfth dark module: 24 columns over 22 rows is no ECC-200 size, so the sizes the
    /// arms fit are sampled instead, as before 2026-10-20, and the symbol still reads. The control, without the
    /// mark, is traced and sampled at its one size.
    /// </summary>
    [Fact]
    public void AnUncountedTrack_FallsBackToTheSizesTheArmsFit()
    {
        bool[,] symbol = DataMatrixEncoder.Encode(Payload);
        int rows = symbol.GetLength(0), columns = symbol.GetLength(1);
        var marked = new bool[rows, columns + 4];
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++) marked[row, column] = symbol[row, column];
        marked[0, columns + 1] = marked[0, columns + 2] = true;   // a module of paper, then two of ink

        var found = Locate(Warped(marked, 0, 0, modulePixels: 8, degrees: 30));
        Assert.True(found.Count > 1, "one size was sampled, so the mark was not counted into the track");
        Assert.Contains(found, g => DataMatrixGridReader.Decode(g, out _) == Payload);
        Assert.Single(Locate(Warped(symbol, 0, 0, modulePixels: 8, degrees: 30)));
    }

    private static List<bool[,]> Locate((byte[] Pixels, int Width, int Height) image)
    {
        var (pixels, width, height) = image;
        int stride = width * 4;
        byte[] map = BarcodeScanline.DarkMap(pixels, width, height, stride, BarcodeScanline.Threshold(pixels, width, height, stride));
        return DataMatrixTurnedLocator.Locate(map, width, height).ConvertAll(c => c.Grid);
    }

    private static IReadOnlyList<BarcodeReadResult> Read((byte[] Pixels, int Width, int Height) image)
        => PdfBarcodeReader.ReadPixels(new RenderedPage(image.Width, image.Height, image.Width * 4, image.Pixels));

    /// <summary>
    /// The symbol drawn on white through the homography taking its corners (0,0), (c,0), (c,r), (0,r) to a
    /// quadrilateral whose top edge is shortened by <paramref name="topShrink"/> of its width (both ends in
    /// by half that) and whose right edge by <paramref name="rightShrink"/> of its height, turned by
    /// <paramref name="degrees"/> about the page's centre, a quiet zone of several modules round it.
    /// </summary>
    private static (byte[], int, int) Warped(bool[,] grid, double topShrink, double rightShrink, int modulePixels, double degrees)
    {
        int rows = grid.GetLength(0), columns = grid.GetLength(1);
        double w = columns * modulePixels, h = rows * modulePixels;
        int side = (int)Math.Ceiling((Math.Max(rows, columns) + 12) * modulePixels * 1.5);
        double centre = side / 2.0;
        // Page corners before the turn, about the centre.
        var tl = (X: -w / 2 + (topShrink * w / 2), Y: -h / 2);
        var tr = (X: w / 2 - (topShrink * w / 2), Y: -h / 2 + (rightShrink * h / 2));
        var br = (X: w / 2, Y: h / 2 - (rightShrink * h / 2));
        var bl = (X: -w / 2, Y: h / 2);
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        (double X, double Y) Turn((double X, double Y) p) => (centre + (cos * p.X) - (sin * p.Y), centre + (sin * p.X) + (cos * p.Y));
        double[] inverse = TestHomography.Invert(TestHomography.Solve(
            new[] { (0.0, 0.0), ((double)columns, 0.0), ((double)columns, (double)rows), (0.0, (double)rows) },
            new[] { Turn(tl), Turn(tr), Turn(br), Turn(bl) }));

        var pixels = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                var (u, v) = TestHomography.Apply(inverse, x + 0.5, y + 0.5);
                bool dark = u >= 0 && v >= 0 && u < columns && v < rows && grid[(int)v, (int)u];
                int o = ((y * side) + x) * 4;
                byte c = dark ? (byte)0 : (byte)255;
                pixels[o] = c; pixels[o + 1] = c; pixels[o + 2] = c; pixels[o + 3] = 255;
            }
        return (pixels, side, side);
    }
}
