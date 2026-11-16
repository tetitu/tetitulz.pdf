// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Data Matrix symbols turned on the page, square and rectangular, rasterised by the test's own rotation
/// (cos and sin, never the locator's transform) and read through <see cref="PdfBarcodeReader"/>. Until
/// 2026-10-18 only square symbols were read (<see cref="DataMatrixLocator"/>); turned ones are
/// <see cref="DataMatrixTurnedLocator"/>'s.
/// </summary>
public class DataMatrixGeometryTests
{
    private const string Payload = "Data Matrix 2026 / lot 4711";

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    [InlineData(250)]
    public void ASquareSymbol_IsRead_TurnedByAnyAngle(double degrees)
    {
        bool[,] grid = DataMatrixEncoder.Encode(Payload);
        Assert.Contains(Read(Turned(grid, degrees, modulePixels: 6)), r => r.Type == BarcodeType.DataMatrix && r.Text == Payload);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(200)]
    public void ARectangularSymbol_IsRead_Turned(double degrees)
    {
        bool[,] grid = DataMatrixEncoder.Encode("RECT 12345", rectangular: true);
        Assert.NotEqual(grid.GetLength(0), grid.GetLength(1));
        Assert.Contains(Read(Turned(grid, degrees, modulePixels: 6)), r => r.Type == BarcodeType.DataMatrix && r.Text == "RECT 12345");
    }

    /// <summary>The square locator alone does not read a turned symbol: the turned one is what does.</summary>
    [Fact]
    public void ATurnedSymbol_IsNotTheSquareLocatorsToRead()
    {
        var (pixels, width, height) = Turned(DataMatrixEncoder.Encode(Payload), 30, modulePixels: 6);
        int stride = width * 4;
        int threshold = BarcodeScanline.Threshold(pixels, width, height, stride);
        Assert.DoesNotContain(DataMatrixLocator.Locate(pixels, width, height, stride, threshold),
            c => DataMatrixGridReader.Decode(c.Grid, out _) == Payload);
        byte[] map = BarcodeScanline.DarkMap(pixels, width, height, stride, threshold);
        Assert.Contains(DataMatrixTurnedLocator.Locate(map, width, height), c => DataMatrixGridReader.Decode(c.Grid, out _) == Payload);
    }

    /// <summary>
    /// Of the symbol's corners only the L's has two solid arms: one L is sampled, and it reads. The other
    /// corners each have a clock track for an arm, which the decoder would refuse anyway — this pins the
    /// arm checks themselves.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(160)]
    public void OnlyTheLsCorner_PassesTheArmChecks(double degrees)
    {
        var (pixels, width, height) = Turned(DataMatrixEncoder.Encode(Payload), degrees, modulePixels: 6);
        int stride = width * 4;
        byte[] map = BarcodeScanline.DarkMap(pixels, width, height, stride, BarcodeScanline.Threshold(pixels, width, height, stride));
        var found = DataMatrixTurnedLocator.Locate(map, width, height, out _, out int sampled);
        Assert.Equal(1, sampled);
        Assert.Contains(found, c => DataMatrixGridReader.Decode(c.Grid, out _) == Payload);
    }

    /// <summary>
    /// Two thin solid strokes meeting at a right angle are an L worth sampling; the same strokes meeting at
    /// sixty degrees are not, though each is as solid and as thin.
    /// </summary>
    [Theory]
    [InlineData(90, 1)]
    [InlineData(60, 0)]
    public void OnlyARightAngledCorner_IsSampled(double angle, int expected)
    {
        const int side = 400, module = 6, arm = 120;
        var map = new byte[side * side];
        double a = angle * Math.PI / 180;
        // One stroke along +x from (100, 300), the other turned by `angle` from it, upward on the page.
        void Stroke(double ux, double uy, double nx, double ny)
        {
            for (double t = 0; t < arm; t += 0.25)
                for (double w = 0; w < module; w += 0.25)
                {
                    int x = (int)(100 + (ux * t) + (nx * w)), y = (int)(300 + (uy * t) + (ny * w));
                    map[(y * side) + x] = 1;
                }
        }
        Stroke(1, 0, 0, -1);
        Stroke(Math.Cos(a), -Math.Sin(a), Math.Sin(a), Math.Cos(a));
        DataMatrixTurnedLocator.Locate(map, side, side, out _, out int sampled);
        Assert.Equal(expected, sampled);
    }

    /// <summary>
    /// A dark row of data beside the bottom arm makes it measure two modules thick; the left arm, light beside
    /// it, measures one. The smaller is the module, and the 16 × 16 size is sampled.
    /// </summary>
    [Fact]
    public void AnArmThickenedByItsNeighbours_DoesNotSetTheModule()
    {
        const int n = 16;
        var grid = new bool[n, n];
        for (int i = 0; i < n; i++)
        {
            grid[i, 0] = true;                 // the left arm
            grid[n - 1, i] = true;             // the bottom arm
            grid[0, i] = i % 2 == 0;           // the top clock track
            grid[i, n - 1] = i % 2 == 1;       // the right clock track
            if (i > 0 && i < n - 1) grid[n - 2, i] = true;   // a dark row beside the bottom arm
        }
        var (pixels, width, height) = Turned(grid, 30, modulePixels: 6);
        int stride = width * 4;
        byte[] map = BarcodeScanline.DarkMap(pixels, width, height, stride, BarcodeScanline.Threshold(pixels, width, height, stride));
        Assert.Contains(DataMatrixTurnedLocator.Locate(map, width, height), c => c.Grid.GetLength(0) == n && c.Grid.GetLength(1) == n);
    }

    /// <summary>Small modules — three pixels, a 0.25 mm module at 300 dpi — turned: the half pixel between the hull's centres and the symbol's edge counts here.</summary>
    [Fact]
    public void ASymbolOfSmallModules_IsRead_Turned()
    {
        Assert.Contains(Read(Turned(DataMatrixEncoder.Encode(Payload), 30, modulePixels: 3)), r => r.Type == BarcodeType.DataMatrix && r.Text == Payload);
        // A rectangular one: its bottom arm, thirty-two modules, is the one whose end matters most.
        Assert.Contains(Read(Turned(DataMatrixEncoder.Encode("RECT 12345", rectangular: true), 30, modulePixels: 3)),
            r => r.Type == BarcodeType.DataMatrix && r.Text == "RECT 12345");
    }

    /// <summary>
    /// The sizes tried are those the arms fit: for a square symbol only square sizes (the proportions), and
    /// none whose module would be under three fifths of the arms' thickness (the implied module).
    /// </summary>
    [Fact]
    public void TheSizesTried_AreThoseTheArmsFit()
    {
        var (pixels, width, height) = Turned(DataMatrixEncoder.Encode(Payload), 30, modulePixels: 6);
        int stride = width * 4;
        byte[] map = BarcodeScanline.DarkMap(pixels, width, height, stride, BarcodeScanline.Threshold(pixels, width, height, stride));
        var grids = DataMatrixTurnedLocator.Locate(map, width, height).Select(c => c.Grid).ToList();
        Assert.Contains(grids, g => g.GetLength(0) == 22);                     // potency: the symbol's own size
        Assert.All(grids, g => Assert.Equal(g.GetLength(0), g.GetLength(1)));
        Assert.All(grids, g => Assert.InRange(g.GetLength(0), 12, 40));        // 132 px over sizes whose module is 0.6 to 1.6 of a measured ~6 px
    }

    /// <summary>A symbol drawn under a rotated <c>cm</c> on a PDF page reads back through the renderer.</summary>
    [Fact]
    public void ASymbolDrawnTurnedOnAPage_IsReadThroughTheRenderer()
    {
        bool[,] grid = DataMatrixEncoder.Encode(Payload);
        int rows = grid.GetLength(0), columns = grid.GetLength(1);
        double a = -35 * Math.PI / 180;
        var content = new StringBuilder();
        content.Append(FormattableString.Invariant($"q {Math.Cos(a):0.####} {Math.Sin(a):0.####} {-Math.Sin(a):0.####} {Math.Cos(a):0.####} 120 200 cm 0 g\n"));
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                if (grid[row, column])
                    content.Append(FormattableString.Invariant($"{column * 5} {(rows - 1 - row) * 5} 5 5 re\n"));
        content.Append("f Q");
        using var input = new MemoryStream(OnePage(content.ToString()), writable: false);
        Assert.Contains(PdfBarcodeReader.Read(input), r => r.Type == BarcodeType.DataMatrix && r.Text == Payload);
    }

    /// <summary>
    /// Hostile input: a page of ten thousand separate dots is ten thousand pieces of ink. The search stops
    /// at <see cref="DataMatrixTurnedLocator.MaxComponents"/>.
    /// </summary>
    [Fact]
    public void AHostilePageOfDots_StopsAtTheComponentBound()
    {
        const int side = 400;
        var map = new byte[side * side];
        for (int y = 1; y < side; y += 4)
            for (int x = 1; x < side; x += 4)
                map[(y * side) + x] = 1;   // 100 × 100 single-pixel dots, none touching
        DataMatrixTurnedLocator.Locate(map, side, side, out int examined, out _);
        Assert.Equal(DataMatrixTurnedLocator.MaxComponents, examined);

        // Potency: below the bound every dot is examined.
        var few = new byte[side * side];
        for (int i = 0; i < 50; i++) few[((4 * i + 1) * side) + 1] = 1;
        DataMatrixTurnedLocator.Locate(few, side, side, out examined, out _);
        Assert.Equal(50, examined);
    }

    /// <summary>
    /// Hostile input: tall bars, each as wide as the smallest symbol's bottom arm. Every run in every row is a
    /// bottom arm whose left arm the locator walks up, and the walk went to the top of the ink — each run its
    /// row's whole height above it, 48 million pixels for this page. No size's left arm is longer than its
    /// bottom one by more than a tenth, so the walk stops one pixel past that: every run walked, none past it.
    /// </summary>
    [Fact]
    public void AHostilePageOfTallBars_WalksEachArmOnlyAsFarAsAnySizeReaches()
    {
        const int bars = 24, bar = 16, height = 2000, width = bars * (bar + 1);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte c = x % (bar + 1) < bar ? (byte)0 : (byte)255;
                int o = ((y * width) + x) * 4;
                pixels[o] = c; pixels[o + 1] = c; pixels[o + 2] = c; pixels[o + 3] = 255;
            }

        int offered = 0;
        DataMatrixLocator.Locate(pixels, width, height, width * 4, 128, _ => { offered++; return false; }, new CandidateBudget(), out long armSteps);
        Assert.Equal(0, offered);
        int limit = (int)(bar * 1.1) + 1;
        Assert.InRange(armSteps, (long)height * bars, (long)height * bars * limit);
    }

    /// <summary>
    /// Hostile input: a page of empty Data Matrix frames, each a candidate no grid of which reads. Each
    /// locator stops once the grids not read have sampled the <see cref="CandidateBudget"/>'s modules, the
    /// last one charged at most one symbol over. The square locator is fed frames with both clock tracks (its
    /// top-edge test refuses a solid one); the outline locator solid rings, one piece of ink each — a frame
    /// whose clock modules stand apart is a score of pieces, and the outline locator's bound on pieces would
    /// stop it first.
    /// </summary>
    [Fact]
    public void AHostilePageOfEmptyFrames_StopsAtTheModuleBudget()
    {
        long most = CandidateBudget.MaxFailedModules + (144 * 144);

        var (clocked, side) = Frames(clocks: true);
        var square = new CandidateBudget();
        DataMatrixLocator.Locate(Rgba(clocked), side, side, side * 4, 128, _ => false, square, out _);
        Assert.True(square.Spent, $"potency: the frames outspend the square locator's budget ({square.FailedModules} modules)");
        Assert.True(square.FailedModules < most, $"{square.FailedModules} modules charged");

        var (rings, ringSide) = Frames(clocks: false);
        var turned = new CandidateBudget();
        DataMatrixTurnedLocator.Locate(rings, ringSide, ringSide, _ => false, turned);
        Assert.True(turned.Spent, $"potency: the rings outspend the turned locator's budget ({turned.FailedModules} modules)");
        Assert.True(turned.FailedModules < most, $"{turned.FailedModules} modules charged");
    }

    /// <summary>Sixty by sixty frames of twenty modules at two pixels a module, as a dark map: the L and either both clock tracks or a solid top and right.</summary>
    private static (byte[] Dark, int Side) Frames(bool clocks)
    {
        const int frames = 60, modules = 20, px = 2, pitch = (modules * px) + 4, side = frames * pitch;
        var dark = new byte[side * side];
        for (int fy = 0; fy < frames; fy++)
            for (int fx = 0; fx < frames; fx++)
                for (int r = 0; r < modules; r++)
                    for (int c = 0; c < modules; c++)
                    {
                        bool ink = c == 0 || r == modules - 1
                                   || (r == 0 && (!clocks || c % 2 == 0)) || (c == modules - 1 && (!clocks || r % 2 == 1));
                        if (!ink) continue;
                        for (int dy = 0; dy < px; dy++)
                            for (int dx = 0; dx < px; dx++)
                                dark[(((fy * pitch) + (r * px) + dy) * side) + (fx * pitch) + (c * px) + dx] = 1;
                    }
        return (dark, side);
    }

    /// <summary>A dark map as white premultiplied RGBA, ink black.</summary>
    private static byte[] Rgba(byte[] dark)
    {
        var pixels = new byte[dark.Length * 4];
        for (int i = 0; i < dark.Length; i++)
        {
            byte v = dark[i] != 0 ? (byte)0 : (byte)255;
            pixels[(i * 4) + 0] = v; pixels[(i * 4) + 1] = v; pixels[(i * 4) + 2] = v; pixels[(i * 4) + 3] = 255;
        }
        return pixels;
    }
    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<BarcodeReadResult> Read((byte[] Pixels, int Width, int Height) image)
        => PdfBarcodeReader.ReadPixels(new RenderedPage(image.Width, image.Height, image.Width * 4, image.Pixels));

    /// <summary>The symbol turned by <paramref name="degrees"/> about its centre on white, a quiet zone of four modules round it.</summary>
    private static (byte[], int, int) Turned(bool[,] grid, double degrees, int modulePixels)
    {
        int rows = grid.GetLength(0), columns = grid.GetLength(1);
        int side = (int)Math.Ceiling((Math.Max(rows, columns) + 8) * modulePixels * Math.Sqrt(2));
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a), centre = side / 2.0;
        var pixels = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double dx = (x + 0.5 - centre) / modulePixels, dy = (y + 0.5 - centre) / modulePixels;
                double u = (columns / 2.0) + (cos * dx) + (sin * dy), v = (rows / 2.0) - (sin * dx) + (cos * dy);
                bool dark = u >= 0 && v >= 0 && u < columns && v < rows && grid[(int)v, (int)u];
                int o = ((y * side) + x) * 4;
                byte c = dark ? (byte)0 : (byte)255;
                pixels[o] = c; pixels[o + 1] = c; pixels[o + 2] = c; pixels[o + 3] = 255;
            }
        return (pixels, side, side);
    }

    private static byte[] OnePage(string content)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 420] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
