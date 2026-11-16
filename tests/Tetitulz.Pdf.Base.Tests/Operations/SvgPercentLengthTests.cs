// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> reads a percentage length as a share of the nearest viewport (SVG
/// 1.1 §7.10): its viewBox's size where it has one, else its own width and height — the width for a
/// horizontal length, the height for a vertical one, and the normalised diagonal
/// √((w² + h²) / 2) for any other.
/// </summary>
/// <remarks>
/// A percentage was read as user units and reported, so the commonest background of an exported
/// drawing, <c>&lt;rect width="100%" height="100%"/&gt;</c>, was a 100 × 100 square in a corner. A
/// nested <c>svg</c>'s percentage size was read as its viewBox's, and a missing one too, where SVG
/// takes 100% of the enclosing viewport. Every case draws red on a white 200 × 200 page.
/// </remarks>
public class SvgPercentLengthTests
{
    [Fact]
    public void ARectsPercentagesAreOfTheViewport()
    {
        var at = Render(@"<rect x=""10%"" width=""80%"" height=""50%"" fill=""#ff0000""/>");

        AssertRed(at(170, 90));     // x 20–180, y 0–100
        AssertWhite(at(190, 50));
        AssertWhite(at(10, 50));
        AssertWhite(at(100, 110));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>With a viewBox the percentages are of it: 100% of a 50-wide viewBox is the page's width.</summary>
    [Fact]
    public void WithAViewBoxThePercentagesAreOfIt()
    {
        var at = Render(@"<rect width=""100%"" height=""50%"" fill=""#ff0000""/>", @"viewBox=""0 0 50 50""");

        AssertRed(at(190, 90));
        AssertWhite(at(190, 110));
    }

    /// <summary>
    /// A radius is of the normalised diagonal: 10% of √((200² + 100²) / 2) ≈ 15.8 in a 200 × 100
    /// viewBox, drawn on the 200 × 200 page at 1 × 2.
    /// </summary>
    [Fact]
    public void ARadiusIsOfTheNormalisedDiagonal()
    {
        var at = Render(@"<circle cx=""50%"" cy=""50%"" r=""10%"" fill=""#ff0000""/>", NonSquare);

        AssertRed(at(100 + 14, 100));
        AssertWhite(at(100 + 18, 100));
    }

    /// <summary>
    /// In a viewport that is not square each side is its own: x and width of 200, y and height of
    /// 100, in a 200 × 100 viewBox drawn at 1 × 2. The rect is x 50–150, y 25–75, page y 50–150.
    /// </summary>
    [Fact]
    public void ANonSquareViewportsSidesAreItsOwn()
    {
        var at = Render(@"<rect x=""25%"" y=""25%"" width=""50%"" height=""50%"" fill=""#ff0000""/>", NonSquare);

        AssertRed(at(55, 60));
        AssertRed(at(145, 60));
        AssertRed(at(55, 140));
        AssertRed(at(145, 140));
        AssertWhite(at(100, 45));
        AssertWhite(at(100, 155));
        AssertWhite(at(45, 100));
        AssertWhite(at(155, 100));
    }

    /// <summary>An ellipse's radii are each of their own side: rx 25% of 200 and ry 25% of 100, both 50 on the page.</summary>
    [Fact]
    public void AnEllipsesRadiiAreOfTheirOwnSides()
    {
        var at = Render(@"<ellipse cx=""50%"" cy=""50%"" rx=""25%"" ry=""25%"" fill=""#ff0000""/>", NonSquare);

        AssertRed(at(145, 100));
        AssertRed(at(100, 145));
        AssertWhite(at(155, 100));
        AssertWhite(at(100, 155));
    }

    /// <summary>
    /// A rect's corner radii too: rx 20% of 200 and ry 20% of 100 round a 40-point corner on the page.
    /// (14, 4) is outside it; read as 20 user units, rx made a 20 × 40 corner that covers it.
    /// </summary>
    [Fact]
    public void ARectsCornerRadiiAreOfTheirOwnSides()
    {
        var at = Render(@"<rect width=""100%"" height=""100%"" rx=""20%"" ry=""20%"" fill=""#ff0000""/>", NonSquare);

        AssertWhite(at(14, 4));
        AssertRed(at(45, 2));       // past the corner
        AssertRed(at(2, 45));
    }

    [Fact]
    public void ALinesEndsAreOfTheViewport()
    {
        var at = Render(@"<line x1=""0"" y1=""50%"" x2=""100%"" y2=""50%"" stroke=""#ff0000"" stroke-width=""4""/>", NonSquare);

        AssertRed(at(195, 100));    // y 50 of 100, drawn at 2
        AssertWhite(at(195, 110));
    }

    /// <summary>A marker's content is in the marker's viewport: 50% of a 10 × 10 one, not of the page's.</summary>
    [Fact]
    public void AMarkersContentIsInItsViewport()
    {
        var at = Render(
            @"<defs><marker id=""m"" markerWidth=""10"" markerHeight=""10"" markerUnits=""userSpaceOnUse"" overflow=""visible"">" +
            @"<rect width=""50%"" height=""50%"" fill=""#ff0000""/></marker></defs>" +
            @"<path d=""M100,100 L150,100"" stroke=""none"" marker-start=""url(#m)""/>");

        AssertRed(at(102, 102));
        AssertWhite(at(108, 102));
    }

    /// <summary>After a nested viewport the enclosing one is back: 50% of the root is 100 again.</summary>
    [Fact]
    public void AfterANestedViewportTheEnclosingOneIsBack()
    {
        var at = Render(@"<svg width=""50"" height=""50""/><rect x=""50%"" width=""10"" height=""10"" fill=""#ff0000""/>");

        AssertRed(at(105, 5));
        AssertWhite(at(30, 5));
    }

    /// <summary>An image's place and size are of the viewport: the right half of the top half.</summary>
    [Fact]
    public void AnImagesPercentagesAreOfTheViewport()
    {
        using var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(1, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        bmp.SetPixel(0, 0, new SkiaSharp.SKColor(255, 0, 0));
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        string href = "data:image/png;base64," + System.Convert.ToBase64String(data.ToArray());

        var at = Render($@"<image href=""{href}"" x=""50%"" width=""50%"" height=""50%"" preserveAspectRatio=""none""/>");

        AssertRed(at(150, 50));
        AssertWhite(at(50, 50));
        AssertWhite(at(150, 150));
    }

    /// <summary>A nested viewport's percentage size is of the enclosing one: 50% of 200 is 100.</summary>
    [Fact]
    public void ANestedViewportsPercentageSizeIsOfTheEnclosingOne()
    {
        var at = Render(@"<svg width=""50%"" height=""50%"" viewBox=""0 0 10 10""><rect width=""10"" height=""10"" fill=""#ff0000""/></svg>");

        AssertRed(at(90, 90));
        AssertWhite(at(110, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A nested viewport without a size is 100% of the enclosing one.</summary>
    [Fact]
    public void ANestedViewportWithoutASizeFillsTheEnclosingOne()
    {
        var at = Render(@"<svg viewBox=""0 0 10 10""><rect width=""10"" height=""10"" fill=""#ff0000""/></svg>");

        AssertRed(at(190, 190));
    }

    /// <summary>
    /// Inside a nested viewport a percentage is of it: 50% of an 80-wide viewport at x 100 is 40
    /// (read as user units it was 50, which a 100-wide viewport could not tell apart).
    /// </summary>
    [Fact]
    public void InsideANestedViewportAPercentageIsOfIt()
    {
        var at = Render(@"<svg x=""100"" width=""80"" height=""100""><rect width=""50%"" height=""100%"" fill=""#ff0000""/></svg>");

        AssertRed(at(135, 50));
        AssertWhite(at(145, 50));
        AssertWhite(at(90, 50));
    }

    /// <summary>
    /// Inside a nested viewport with a viewBox a percentage is of the viewBox: 50% of 10 is 5, drawn
    /// at 10 — 50 on the page, not 50% of the viewport's 100.
    /// </summary>
    [Fact]
    public void InsideANestedViewBoxAPercentageIsOfIt()
    {
        var at = Render(@"<svg width=""100"" height=""100"" viewBox=""0 0 10 10""><rect width=""50%"" height=""50%"" fill=""#ff0000""/></svg>");

        AssertRed(at(45, 45));
        AssertWhite(at(55, 45));
    }

    /// <summary>A use's place is of the viewport: x 50% of 200 is 100.</summary>
    [Fact]
    public void AUsesPlaceIsOfTheViewport()
    {
        var at = Render(@"<defs><rect id=""r"" width=""10"" height=""10"" fill=""#ff0000""/></defs><use href=""#r"" x=""50%""/>");

        AssertRed(at(105, 5));
        AssertWhite(at(55, 5));
    }

    /// <summary>A used symbol is a viewport too: its content's percentages are of the use's size, 50% of 80.</summary>
    [Fact]
    public void InsideAUsedSymbolAPercentageIsOfIt()
    {
        var at = Render(@"<defs><symbol id=""s""><rect width=""50%"" height=""100%"" fill=""#ff0000""/></symbol></defs><use href=""#s"" width=""80"" height=""100""/>");

        AssertRed(at(35, 50));
        AssertWhite(at(45, 50));
    }

    /// <summary>A gradient in user space measures its percentages in the viewBox, not the root's width.</summary>
    [Fact]
    public void AUserSpaceGradientsPercentagesAreOfTheViewBox()
    {
        var at = Render(
            @"<defs><linearGradient id=""g"" gradientUnits=""userSpaceOnUse"" x1=""0%"" x2=""100%"">" +
            @"<stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient></defs>" +
            @"<rect width=""100"" height=""100"" fill=""url(#g)""/>", @"viewBox=""0 0 100 100""");

        var right = at(198, 100);
        Assert.InRange(right.B, 240, 255);   // the end of the gradient is at the viewBox's right edge
        Assert.InRange(right.R, 0, 15);
    }

    /// <summary>A stroke width is of the normalised diagonal: 5% of 200 is 10, 5 each side of the line.</summary>
    [Fact]
    public void AStrokeWidthIsOfTheNormalisedDiagonal()
    {
        var at = Render(@"<line x1=""0"" y1=""100"" x2=""200"" y2=""100"" stroke=""#ff0000"" stroke-width=""5%""/>");

        AssertRed(at(100, 104));
        AssertWhite(at(100, 107));
    }

    /// <summary>
    /// A text's position is of the viewport too, its shift as well: x 50% + dx 5% of 200 is 110, and
    /// y 25% + dy 5% of 100 is 30, drawn at 2 and flipped to 140.
    /// </summary>
    [Fact]
    public void ATextsPositionIsOfTheViewport()
    {
        byte[] pdf = Convert(@"<text x=""50%"" dx=""5%"" y=""25%"" dy=""5%"">A</text>", NonSquare);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        string content = System.Text.Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());

        // The run's origin: Td's operands, or Tm's last two where the drawing scales.
        string place = content.Split('\n').Select(l => l.Trim()).First(l => l.EndsWith(" Td", StringComparison.Ordinal) || l.EndsWith(" Tm", StringComparison.Ordinal));
        string[] operands = place.Split(' ');
        Assert.Equal(110, double.Parse(operands[operands.Length - 3], System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal(140, double.Parse(operands[operands.Length - 2], System.Globalization.CultureInfo.InvariantCulture), 3);
    }

    /// <summary>A font size is not a viewport length: its percentage is of the parent's font size (batch 70, <see cref="SvgLengthUnitTests"/>), and nothing is said.</summary>
    [Fact]
    public void AFontSizePercentageIsNotOfTheViewport()
    {
        Convert(@"<text x=""10"" y=""50"" font-size=""50%"">A</text>");

        Assert.Null(SvgToPdfConverter.LastError);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A 200 × 100 viewBox on the 200 × 200 page: x at 1, y at 2.</summary>
    private const string NonSquare = @"viewBox=""0 0 200 100"" preserveAspectRatio=""none""";

    private static void AssertRed((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 0, 5);
    }

    private static void AssertWhite((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 250, 255);
    }

    private static byte[] Convert(string body, string rootAttributes = @"width=""200"" height=""200""")
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" " + rootAttributes + ">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int R, int G, int B)> Render(string body, string rootAttributes = @"width=""200"" height=""200""")
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body, rootAttributes)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
