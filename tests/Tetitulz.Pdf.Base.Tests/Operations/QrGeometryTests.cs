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
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// QR symbols that are not square to the page: turned by any angle, sheared, foreshortened. Each is
/// rasterised here by the test's own geometry — a rotation by cos and sin, a homography inverted by its
/// adjugate — never through the <c>PerspectiveTransform</c> the locator samples with, so the two cannot
/// share a mistake. The symbols are this library's encoder's (version 3, with an alignment pattern) and
/// an independent encoder's (version 15, from <see cref="QrThirdPartySymbolTests"/>). Until 2026-10-18 the locator read
/// axis-aligned symbols only.
/// </summary>
public class QrGeometryTests
{
    private const string Payload = "https://example.com/invoice/4711-2026";   // version 3 at level M
    private static readonly string LongPayload = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 4));

    // ------------------------------------------------------------------ rotation

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(137)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(-20)]
    public void ATurnedSymbol_IsRead_AtAnyAngle(double degrees)
    {
        bool[,] grid = QrCodeEncoder.Encode(Payload);
        Assert.True(QrLayout.VersionOf(grid.GetLength(0)) >= 2);   // it has an alignment pattern to find
        Assert.Contains(Payload, Read(Rotated(grid, degrees, modulePixels: 6)));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(41)]
    [InlineData(200)]
    public void AnotherEncodersLargeSymbol_IsRead_Turned(double degrees)
        => Assert.Contains(LongPayload, Read(Rotated(QrThirdPartySymbolTests.Grid(QrThirdPartySymbolTests.V15), degrees, modulePixels: 5)));

    /// <summary>The control the rotations need: without the alignment pattern's corner a version 1 symbol still reads.</summary>
    [Fact]
    public void AVersionOneSymbol_IsRead_Turned()
    {
        bool[,] grid = QrCodeEncoder.Encode("HI");
        Assert.Equal(1, QrLayout.VersionOf(grid.GetLength(0)));
        Assert.Contains("HI", Read(Rotated(grid, 33, modulePixels: 7)));
    }

    // ------------------------------------------------------------------ shear and perspective

    [Fact]
    public void AShearedSymbol_IsRead()
    {
        bool[,] grid = QrCodeEncoder.Encode(Payload);
        // x' = 6u + 1.2v, y' = 6v: a parallelogram, as a skewed scan makes.
        Assert.Contains(Payload, Read(Projected(grid, new[] { 6.0, 1.2, 60, 0, 6.0, 60, 0, 0, 1 })));
    }

    [Theory]
    [InlineData(0.004, 0)]      // the top edge longer than the bottom
    [InlineData(0, 0.004)]      // the left edge longer than the right
    [InlineData(0.003, -0.002)]
    public void AForeshortenedSymbol_IsRead(double g, double h)
    {
        bool[,] grid = QrCodeEncoder.Encode(Payload);
        // A homography: an affine part and a perspective row; the denominator g·u + h·v + 1.
        Assert.Contains(Payload, Read(Projected(grid, new[] { 6.0, 0.6, 80, -0.4, 6.0, 80, g, h, 1 })));
    }

    /// <summary>
    /// The alignment pattern is what makes a foreshortened symbol read: the parallelogram's fourth
    /// corner is wrong under perspective, and only the found pattern corrects it. Without the search a
    /// strongly foreshortened version 15 is not read.
    /// </summary>
    [Fact]
    public void AStronglyForeshortenedLargeSymbol_IsRead()
    {
        bool[,] grid = QrThirdPartySymbolTests.Grid(QrThirdPartySymbolTests.V15);
        Assert.Contains(LongPayload, Read(Projected(grid, new[] { 6.0, 0, 60, 0, 6.0, 60, 0.0022, 0.0022, 1 })));
    }

    // ------------------------------------------------------------------ the bound

    /// <summary>
    /// Hostile input: a page tiled with finder patterns, eight by eight, makes right-angled triples by the
    /// thousand. Each finder is tried only with pairs of its <see cref="QrLocator.NearestFinders"/> nearest, so
    /// the combinations tested are linear in the finders, and the candidates offered stop at the modules the
    /// <see cref="CandidateBudget"/> allows those not read.
    /// </summary>
    [Fact]
    public void AHostilePageOfFinderPatterns_IsTestedNearEachFinderOnly()
    {
        var (pixels, width, height) = FinderGrid(8, 8, modulePixels: 3);
        int stride = width * 4;
        int threshold = BarcodeScanline.Threshold(pixels, width, height, stride);

        Assert.Equal(64, QrLocator.CountFinderCandidates(pixels, width, height, stride, threshold));   // potency: every tile is a finder
        Assert.True(QrLocator.CountTriples(pixels, width, height, stride, threshold) > 64);           // potency: squares everywhere
        Assert.InRange(QrLocator.CountTripleTests(pixels, width, height, stride, threshold), 1, 64 * PairsPerFinder);
        Assert.NotEmpty(QrLocator.Locate(pixels, width, height, stride, threshold));
    }

    /// <summary>
    /// A symbol ringed, outside its quiet zone, by finder patterns a third its module: more of them are nearer
    /// each of its corners than its own corners are to each other. A finder is paired only with its nearest of a
    /// module it could share (within a factor of two, as a triple's must be), so the ring does not take the
    /// symbol's corners' places among them, and the symbol is read; the ring itself, the potency, is found.
    /// </summary>
    [Fact]
    public void ASymbolRingedBySmallerFinders_IsRead()
    {
        bool[,] grid = QrCodeEncoder.Encode("HI");
        int size = grid.GetLength(0);
        const int module = 6, tiny = 2, cell = 8 * tiny, margin = 120;
        int symbol = size * module, side = symbol + (2 * margin);
        var pixels = new byte[side * side * 4];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
        void Ink(int x, int y)
        {
            int o = ((y * side) + x) * 4;
            pixels[o] = 0; pixels[o + 1] = 0; pixels[o + 2] = 0;
        }

        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                if (grid[r, c])
                    for (int dy = 0; dy < module; dy++)
                        for (int dx = 0; dx < module; dx++)
                            Ink(margin + (c * module) + dx, margin + (r * module) + dy);

        // Small finders on a grid of cells, each kept wholly outside the symbol and its four-module quiet zone.
        int keepOutLow = margin - (4 * module), keepOutHigh = margin + symbol + (4 * module);
        int ring = 0;
        for (int cy = 0; cy + cell <= side; cy += cell)
            for (int cx = 0; cx + cell <= side; cx += cell)
            {
                bool clear = cx + cell <= keepOutLow || cx >= keepOutHigh || cy + cell <= keepOutLow || cy >= keepOutHigh;
                if (!clear) continue;
                ring++;
                for (int r = 0; r < 7; r++)
                    for (int c = 0; c < 7; c++)
                    {
                        bool dark = r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                        if (!dark) continue;
                        for (int dy = 0; dy < tiny; dy++)
                            for (int dx = 0; dx < tiny; dx++)
                                Ink(cx + (c * tiny) + dx, cy + (r * tiny) + dy);
                    }
            }

        int threshold = BarcodeScanline.Threshold(pixels, side, side, side * 4);
        Assert.Equal(ring + 3, QrLocator.CountFinderCandidates(pixels, side, side, side * 4, threshold));   // potency: the ring and the corners
        Assert.Contains("HI", Read((pixels, side, side)));
    }

    /// <summary>
    /// Hostile input: a page of finder patterns whose squares, none of them a symbol, outspend the
    /// <see cref="CandidateBudget"/>: the locator stops once the candidates not read have sampled its modules,
    /// the last one charged at most one symbol over.
    /// </summary>
    [Fact]
    public void AHostilePageOfFinderSquares_StopsAtTheModuleBudget()
    {
        var (pixels, width, height) = FinderGrid(24, 24, modulePixels: 2);
        int stride = width * 4;
        int threshold = BarcodeScanline.Threshold(pixels, width, height, stride);
        var budget = new CandidateBudget();
        QrLocator.Locate(pixels, width, height, stride, threshold, _ => false, budget);
        Assert.True(budget.Spent, "potency: the squares outspend the budget");
        Assert.True(budget.FailedModules < CandidateBudget.MaxFailedModules + (177 * 177), $"{budget.FailedModules} modules charged");
    }

    private const int PairsPerFinder = QrLocator.NearestFinders * (QrLocator.NearestFinders - 1) / 2;

    /// <summary>
    /// Hostile input: a row of finder patterns, every three collinear, makes no triple at all, and every
    /// combination of them was tested — a thousand finders took 41 s. Each finder is tried with its nearest only;
    /// three finders, the control, are tested once.
    /// </summary>
    [Fact]
    public void AHostileRowOfFinderPatterns_IsTestedNearEachFinderOnly()
    {
        var (pixels, width, height) = FinderRow(300, modulePixels: 2);
        int stride = width * 4;
        int threshold = BarcodeScanline.Threshold(pixels, width, height, stride);
        Assert.Equal(300, QrLocator.CountFinderCandidates(pixels, width, height, stride, threshold));   // potency: every one a finder
        Assert.InRange(QrLocator.CountTripleTests(pixels, width, height, stride, threshold), 300, 300 * PairsPerFinder);
        Assert.Equal(0, QrLocator.CountTriples(pixels, width, height, stride, threshold));

        var (few, fewWidth, fewHeight) = FinderRow(3, modulePixels: 2);
        int fewThreshold = BarcodeScanline.Threshold(few, fewWidth, fewHeight, fewWidth * 4);
        Assert.Equal(1, QrLocator.CountTripleTests(few, fewWidth, fewHeight, fewWidth * 4, fewThreshold));
    }

    /// <summary>
    /// Hostile input: a page tiled with more finder patterns than <see cref="QrLocator.MaxFinders"/> stops
    /// recording them there — forty thousand, each recorded against those before it, took 76 s. The eight by
    /// eight page above, under the bound, is the control.
    /// </summary>
    [Fact]
    public void AHostilePageOfManyFinderPatterns_StopsAtTheFinderBound()
    {
        var (pixels, width, height) = FinderGrid(40, 40, modulePixels: 2);
        int stride = width * 4;
        int threshold = BarcodeScanline.Threshold(pixels, width, height, stride);
        Assert.Equal(QrLocator.MaxFinders, QrLocator.CountFinderCandidates(pixels, width, height, stride, threshold));
    }
    /// <summary>
    /// Three finders that cannot be a symbol's corners make no triple: modules of very different sizes,
    /// two sides of very different lengths, a corner far from square. The square arrangement is the control.
    /// </summary>
    [Theory]
    [InlineData("square", 1)]
    [InlineData("modules", 0)]
    [InlineData("sides", 0)]
    [InlineData("angle", 0)]
    public void ThreeFindersThatCannotBeASymbol_MakeNoTriple(string arrangement, int expected)
    {
        // Finder centres in module units on a 3-pixel module; the 'modules' case draws the third finder at
        // three times the scale.
        var (pixels, width, height) = Finders(arrangement switch
        {
            "square" => new[] { (10.0, 10.0, 1), (60.0, 10.0, 1), (10.0, 60.0, 1) },
            "modules" => new[] { (10.0, 10.0, 1), (60.0, 10.0, 1), (12.0, 60.0, 3) },
            "sides" => new[] { (10.0, 10.0, 1), (100.0, 10.0, 1), (10.0, 35.0, 1) },
            _ => new[] { (10.0, 10.0, 1), (60.0, 10.0, 1), (35.0, 17.0, 1) },   // a 150-degree corner
        });
        int stride = width * 4;
        int threshold = BarcodeScanline.Threshold(pixels, width, height, stride);
        Assert.Equal(3, QrLocator.CountFinderCandidates(pixels, width, height, stride, threshold));   // potency
        Assert.Equal(expected, QrLocator.CountTriples(pixels, width, height, stride, threshold));
    }

    /// <summary>Finder patterns centred at (x, y) module units, each drawn at its own scale, 3 pixels a module.</summary>
    private static (byte[], int, int) Finders((double X, double Y, int Scale)[] centres)
    {
        const int px = 3, side = 130 * px;
        return Rasterise(new bool[1, 1], side, side, (x, y) => (-1, -1)) is var blank
            ? Ink(blank, centres, px) : blank;
    }

    private static (byte[], int, int) Ink((byte[] Pixels, int Width, int Height) image, (double X, double Y, int Scale)[] centres, int px)
    {
        foreach (var (cx, cy, scale) in centres)
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    // Module coordinates within this finder, its top-left corner 3.5 modules up and left of the centre.
                    double u = (((x + 0.5) / px) - cx) / scale + 3.5, v = (((y + 0.5) / px) - cy) / scale + 3.5;
                    if (u < 0 || v < 0 || u >= 7 || v >= 7) continue;
                    int r = (int)v, c = (int)u;
                    bool dark = r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                    int o = ((y * image.Width) + x) * 4;
                    byte value = dark ? (byte)0 : (byte)255;
                    image.Pixels[o] = value; image.Pixels[o + 1] = value; image.Pixels[o + 2] = value;
                }
        return image;
    }

    // ------------------------------------------------------------------ through a page

    /// <summary>A symbol drawn under a rotated <c>cm</c> on a PDF page reads back through the renderer.</summary>
    [Fact]
    public void ASymbolDrawnTurnedOnAPage_IsReadThroughTheRenderer()
    {
        bool[,] grid = QrCodeEncoder.Encode(Payload);
        int size = grid.GetLength(0);
        double a = 25 * Math.PI / 180;
        var content = new StringBuilder();
        content.Append(FormattableString.Invariant($"q {Math.Cos(a):0.####} {Math.Sin(a):0.####} {-Math.Sin(a):0.####} {Math.Cos(a):0.####} 200 60 cm 0 g\n"));
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
                if (grid[row, column])
                    content.Append(FormattableString.Invariant($"{column * 5} {(size - 1 - row) * 5} 5 5 re\n"));
        content.Append("f Q");

        using var input = new MemoryStream(OnePage(content.ToString()), writable: false);
        var found = PdfBarcodeReader.Read(input);
        var symbol = Assert.Single(found, r => r.Type == BarcodeType.QrCode && r.Text == Payload);

        // The box covers the turned symbol: its side, 5 points a module, spans (cos + sin) times as much.
        double pixelsPerPoint = PdfBarcodeReader.DefaultDpi / 72.0;
        double span = size * 5 * (Math.Cos(a) + Math.Sin(a)) * pixelsPerPoint;
        Assert.InRange(symbol.Right - symbol.Left, span * 0.95, span * 1.05);
        Assert.InRange(symbol.Bottom - symbol.Top, span * 0.95, span * 1.05);
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> Read((byte[] Pixels, int Width, int Height) image)
    {
        int stride = image.Width * 4;
        int threshold = BarcodeScanline.Threshold(image.Pixels, image.Width, image.Height, stride);
        foreach (var (grid, _, _, _, _) in QrLocator.Locate(image.Pixels, image.Width, image.Height, stride, threshold))
        {
            string text = QrGridReader.Decode(grid, out _);
            if (text != null) yield return text;
        }
    }

    /// <summary>The symbol turned by <paramref name="degrees"/> about its centre, a quiet zone of four modules all round.</summary>
    internal static (byte[], int, int) Rotated(bool[,] grid, double degrees, int modulePixels)
    {
        int size = grid.GetLength(0);
        double half = size / 2.0;
        int side = (int)Math.Ceiling((size + 8) * modulePixels * Math.Sqrt(2));
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a), centre = side / 2.0;
        return Rasterise(grid, side, side, (x, y) =>
        {
            double dx = (x - centre) / modulePixels, dy = (y - centre) / modulePixels;
            return (half + (cos * dx) + (sin * dy), half - (sin * dx) + (cos * dy));   // the inverse rotation
        });
    }

    /// <summary>
    /// The symbol through the homography <paramref name="h"/> (row-major, module coordinates to pixels),
    /// each pixel mapped back through its adjugate.
    /// </summary>
    internal static (byte[], int, int) Projected(bool[,] grid, double[] h)
    {
        double[] inverse =
        {
            (h[4] * h[8]) - (h[5] * h[7]), (h[2] * h[7]) - (h[1] * h[8]), (h[1] * h[5]) - (h[2] * h[4]),
            (h[5] * h[6]) - (h[3] * h[8]), (h[0] * h[8]) - (h[2] * h[6]), (h[2] * h[3]) - (h[0] * h[5]),
            (h[3] * h[7]) - (h[4] * h[6]), (h[1] * h[6]) - (h[0] * h[7]), (h[0] * h[4]) - (h[1] * h[3]),
        };
        int size = grid.GetLength(0);
        double maxX = 0, maxY = 0;
        foreach (var (u, v) in new[] { (0.0, 0.0), (size, 0.0), (0.0, size), ((double)size, (double)size) })
        {
            double w = (h[6] * u) + (h[7] * v) + h[8];
            maxX = Math.Max(maxX, ((h[0] * u) + (h[1] * v) + h[2]) / w);
            maxY = Math.Max(maxY, ((h[3] * u) + (h[4] * v) + h[5]) / w);
        }
        int width = (int)maxX + 80, height = (int)maxY + 80;
        return Rasterise(grid, width, height, (x, y) =>
        {
            double w = (inverse[6] * x) + (inverse[7] * y) + inverse[8];
            return (((inverse[0] * x) + (inverse[1] * y) + inverse[2]) / w, ((inverse[3] * x) + (inverse[4] * y) + inverse[5]) / w);
        });
    }

    /// <summary>
    /// <paramref name="count"/> finder patterns in one row, a module of light between each, a quiet zone of four
    /// modules all round: every three are collinear, so none can be a symbol's corners.
    /// </summary>
    private static (byte[], int, int) FinderRow(int count, int modulePixels) => FinderGrid(count, 1, modulePixels);

    /// <summary><paramref name="columns"/> by <paramref name="rows"/> finder patterns, a module of light between each, a quiet zone of four modules all round.</summary>
    private static (byte[], int, int) FinderGrid(int columns, int rows, int modulePixels)
    {
        int width = ((columns * 8) + 7) * modulePixels, height = ((rows * 8) + 7) * modulePixels;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int u = (x / modulePixels) - 4, v = (y / modulePixels) - 4;
                int c = u % 8, r = v % 8;
                bool dark = u >= 0 && u < columns * 8 && c < 7 && v >= 0 && v < rows * 8 && r < 7
                            && (r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4));
                int o = ((y * width) + x) * 4;
                byte value = dark ? (byte)0 : (byte)255;
                pixels[o] = value; pixels[o + 1] = value; pixels[o + 2] = value; pixels[o + 3] = 255;
            }
        return (pixels, width, height);
    }

    /// <summary>White premultiplied RGBA, each pixel's centre mapped to module coordinates and inked where the module is dark.</summary>
    private static (byte[], int, int) Rasterise(bool[,] grid, int width, int height, Func<double, double, (double U, double V)> toModule)
    {
        int size = grid.GetLength(0);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (u, v) = toModule(x + 0.5, y + 0.5);
                bool dark = u >= 0 && v >= 0 && u < size && v < size && grid[(int)v, (int)u];
                int o = ((y * width) + x) * 4;
                byte c = dark ? (byte)0 : (byte)255;
                pixels[o] = c; pixels[o + 1] = c; pixels[o + 2] = c; pixels[o + 3] = 255;
            }
        return (pixels, width, height);
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
