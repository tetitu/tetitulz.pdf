// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Code 128 and EAN-13 symbols turned on the page — sideways, skewed, upside down. Each is written by the
/// library's writer, rendered, and the rendered pixels turned by the test (nearest pixel, about the
/// centre); the reader reads rows and lines every fifteen degrees. Until 2026-10-18 it read rows alone.
/// </summary>
public class BarcodeLinearOrientationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(22)]
    [InlineData(37)]
    [InlineData(90)]
    [InlineData(128)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(-61)]
    public void ACode128Symbol_IsRead_TurnedByAnyAngle(double degrees)
    {
        const string payload = "SHIP-4711/B";
        var found = PdfBarcodeReader.ReadPixels(Turned(Render(payload, BarcodeType.Code128), degrees));
        Assert.Contains(found, r => r.Type == BarcodeType.Code128 && r.Text == payload);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(45)]
    [InlineData(200)]
    public void AnEan13Symbol_IsRead_Turned(double degrees)
    {
        const string payload = "5901234123457";
        var found = PdfBarcodeReader.ReadPixels(Turned(Render(payload, BarcodeType.Ean13), degrees));
        Assert.Contains(found, r => r.Type == BarcodeType.Ean13 && r.Text == payload);
    }

    /// <summary>
    /// The vertical symbol's box is tall and narrow: the lines that read it run down the page, and the box
    /// is their endpoints'. The upright symbol's, the control, is wide and short.
    /// </summary>
    [Fact]
    public void ASidewaysSymbolsBox_IsTallAndNarrow()
    {
        RenderedPage upright = Render("SHIP-4711/B", BarcodeType.Code128);
        var flat = Assert.Single(PdfBarcodeReader.ReadPixels(upright), r => r.Type == BarcodeType.Code128);
        var sideways = Assert.Single(PdfBarcodeReader.ReadPixels(Turned(upright, 90)), r => r.Type == BarcodeType.Code128);

        Assert.True(flat.Right - flat.Left > flat.Bottom - flat.Top);
        Assert.True(sideways.Bottom - sideways.Top > sideways.Right - sideways.Left);
        // The lengths swap: the symbol's length read across the page becomes its length read down it.
        Assert.InRange(sideways.Bottom - sideways.Top, (flat.Right - flat.Left) * 0.95, (flat.Right - flat.Left) * 1.05);
    }

    /// <summary>
    /// The box is where the turned symbol is: the upright symbol's box, its corners turned as the pixels
    /// were, bounds the turned one's within a tenth — upside down and at 270°, read only by lines going
    /// back the other way, and at 37°, merged from many lines.
    /// </summary>
    [Theory]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(37)]
    public void ATurnedSymbolsBox_IsWhereTheSymbolIs(double degrees)
    {
        RenderedPage upright = Render("SHIP-4711/B", BarcodeType.Code128);
        var flat = Assert.Single(PdfBarcodeReader.ReadPixels(upright), r => r.Type == BarcodeType.Code128);
        RenderedPage turned = Turned(upright, degrees);
        var read = Assert.Single(PdfBarcodeReader.ReadPixels(turned), r => r.Type == BarcodeType.Code128);

        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double cx = turned.Width / 2.0, cy = turned.Height / 2.0, px = upright.Width / 2.0, py = upright.Height / 2.0;
        var corners = new[] { (flat.Left, flat.Top), (flat.Right, flat.Top), (flat.Left, flat.Bottom), (flat.Right, flat.Bottom) }
            .Select(p => (X: cx + (cos * (p.Item1 - px)) - (sin * (p.Item2 - py)), Y: cy + (sin * (p.Item1 - px)) + (cos * (p.Item2 - py))))
            .ToArray();
        double left = corners.Min(c => c.X), right = corners.Max(c => c.X), top = corners.Min(c => c.Y), bottom = corners.Max(c => c.Y);
        double slack = 0.1 * Math.Max(right - left, bottom - top);
        Assert.InRange(read.Left, left - slack, left + slack);
        Assert.InRange(read.Right, right - slack, right + slack);
        Assert.InRange(read.Top, top - slack, top + slack);
        Assert.InRange(read.Bottom, bottom - slack, bottom + slack);
    }

    /// <summary>
    /// A symbol foreshortened along its length — leaning away, its far end <paramref name="farScale"/> of
    /// its near end's height, its modules narrowing steadily towards it (the test's own homography) — is
    /// read, and turned too.
    /// </summary>
    [Theory]
    [InlineData(BarcodeType.Code128, 0.7, 0)]
    [InlineData(BarcodeType.Code128, 0.45, 0)]
    [InlineData(BarcodeType.Code128, 0.5, 30)]
    [InlineData(BarcodeType.Ean13, 0.7, 0)]
    [InlineData(BarcodeType.Ean13, 0.45, 0)]
    [InlineData(BarcodeType.Ean13, 0.5, 30)]
    public void AForeshortenedSymbol_IsRead(BarcodeType type, double farScale, double degrees)
    {
        string payload = type == BarcodeType.Ean13 ? "5901234123457" : "SHIP-4711/B";
        var found = PdfBarcodeReader.ReadPixels(Turned(Foreshortened(Render(payload, type), farScale), degrees));
        Assert.Contains(found, r => r.Type == type && r.Text == payload);
    }

    /// <summary>
    /// The page mapped by the homography taking its rectangle to a trapezoid whose left edge is the page's and
    /// whose right edge is <paramref name="farScale"/> of its height about the middle: a page leaning away to
    /// the right, nearest pixel, white outside.
    /// </summary>
    private static RenderedPage Foreshortened(RenderedPage page, double farScale)
    {
        double w = page.Width, h = page.Height, half = h * farScale / 2;
        double[] inverse = TestHomography.Invert(TestHomography.Solve(
            new[] { (0.0, 0.0), (w, 0.0), (w, h), (0.0, h) },
            new[] { (0.0, 0.0), (w, (h / 2) - half), (w, (h / 2) + half), (0.0, h) }));
        var pixels = Enumerable.Repeat((byte)255, page.Width * page.Height * 4).ToArray();
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
            {
                var (u, v) = TestHomography.Apply(inverse, x + 0.5, y + 0.5);
                int sx = (int)Math.Floor(u), sy = (int)Math.Floor(v);
                if (sx < 0 || sy < 0 || sx >= page.Width || sy >= page.Height) continue;
                Array.Copy(page.Pixels, (sy * page.Stride) + (sx * 4), pixels, ((y * page.Width) + x) * 4, 4);
            }
        return new RenderedPage(page.Width, page.Height, page.Width * 4, pixels);
    }

    private static RenderedPage Render(string payload, BarcodeType type)
    {
        byte[] pdf = PdfBarcodeWriter.GeneratePdf(payload, type, new BarcodeOptions { Size = 200f, ShowText = false });
        return PdfOperations.RenderPageToPixels(new MemoryStream(pdf, writable: false), 0, 150);
    }

    /// <summary>The page's pixels turned by <paramref name="degrees"/> about its centre onto a square canvas big enough to hold it, white elsewhere.</summary>
    private static RenderedPage Turned(RenderedPage page, double degrees)
    {
        int side = (int)Math.Ceiling(Math.Sqrt((page.Width * (double)page.Width) + (page.Height * (double)page.Height)));
        var pixels = Enumerable.Repeat((byte)255, side * side * 4).ToArray();
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double cx = side / 2.0, cy = side / 2.0, px = page.Width / 2.0, py = page.Height / 2.0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                int sx = (int)Math.Floor(px + (cos * dx) + (sin * dy)), sy = (int)Math.Floor(py - (sin * dx) + (cos * dy));
                if (sx < 0 || sy < 0 || sx >= page.Width || sy >= page.Height) continue;
                Array.Copy(page.Pixels, (sy * page.Stride) + (sx * 4), pixels, ((y * side) + x) * 4, 4);
            }
        return new RenderedPage(side, side, side * 4, pixels);
    }
}
