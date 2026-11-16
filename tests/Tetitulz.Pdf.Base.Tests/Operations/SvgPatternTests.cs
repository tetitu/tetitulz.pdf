// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> paints a <c>fill</c> or <c>stroke</c> that names a <c>pattern</c>
/// (SVG 1.1 §13.3) with a tiling pattern (ISO 32000-1 §8.7.3): the tile is the cell, the pattern's
/// children its content, the pattern matrix takes it to the page.
/// </summary>
/// <remarks>A pattern was reported as naming no gradient and painted black, or its fallback colour.</remarks>
public class SvgPatternTests
{
    /// <summary>A 20-unit tile in user space with a red 10-unit square in its corner.</summary>
    private const string Checks =
        @"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""20"" height=""20""><rect width=""10"" height=""10"" fill=""#ff0000""/></pattern>";

    private const string Page = @"<rect width=""200"" height=""200"" fill=""url(#p)""/>";

    /// <summary>
    /// The fill is set to a tiling pattern: PatternType 1, coloured, its cell the tile in page units
    /// — the flipped page puts the 20-unit tile at y 180 to 200 — stepping 20 each way, its matrix the
    /// identity for a tile in the root's user space, its content the red square.
    /// </summary>
    [Fact]
    public void APatternIsATilingPattern()
    {
        using var doc = Open(Checks + Page);
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int scn = Array.IndexOf(page, "/P0 scn");
        Assert.True(scn > 0 && page[scn - 1] == "/Pattern cs", "the fill was not set to the pattern");
        var (dictionary, content) = Pattern(doc, "P0");
        Assert.Equal(1, ((PdfNumber)dictionary[new PdfName("PatternType")]).DoubleValue);
        Assert.Equal(1, ((PdfNumber)dictionary[new PdfName("PaintType")]).DoubleValue);
        Assert.Equal(new[] { 0.0, 180, 20, 200 }, Numbers(doc, dictionary[new PdfName("BBox")]));
        Assert.Equal(20, ((PdfNumber)dictionary[new PdfName("XStep")]).DoubleValue);
        Assert.Equal(20, ((PdfNumber)dictionary[new PdfName("YStep")]).DoubleValue);
        Assert.Equal(new[] { 1.0, 0, 0, 1, 0, 0 }, Numbers(doc, dictionary[PdfName.Matrix]));
        Assert.NotNull(doc.ResolveObject(dictionary[new PdfName("Resources")]));
        string[] cell = Lines(content);
        Assert.Contains("1.0000 0.0000 0.0000 rg", cell);
        Assert.Contains("0.0000 190.0000 10.0000 10.0000 re", cell);   // the 10-unit square, at the top of the flipped cell
        Assert.Contains("f", cell);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Rendered: red in each tile's corner square, white in the rest of the tile, tile after tile.</summary>
    [Fact]
    public void APatternTilesAcrossTheShape()
    {
        var at = Render(Checks + Page);

        AssertRed(at(5, 5));
        AssertWhite(at(15, 5));
        AssertWhite(at(15, 15));
        AssertRed(at(25, 5));
        AssertRed(at(5, 25));
        AssertRed(at(185, 185));
        AssertWhite(at(195, 195));
    }

    /// <summary>
    /// The tile in the default units is a fraction of the shape's box: a half-and-half tile on a
    /// 200 × 100 shape is 100 × 50, and its 50-unit red square (in user units, the default content
    /// units) fills its left half.
    /// </summary>
    [Fact]
    public void ATileInBoxUnitsIsAFractionOfTheShape()
    {
        var at = Render(
            @"<pattern id=""p"" width=""0.5"" height=""0.5""><rect width=""50"" height=""50"" fill=""#ff0000""/></pattern>" +
            @"<rect width=""200"" height=""100"" fill=""url(#p)""/>");

        AssertRed(at(25, 25));
        AssertWhite(at(75, 25));
        AssertRed(at(125, 25));
        AssertRed(at(25, 75));
        AssertWhite(at(75, 75));
        AssertWhite(at(25, 125));   // below the shape
    }

    /// <summary>Content in box units scales with the shape: a quarter-and-quarter square on 200 × 100 is 50 × 25.</summary>
    [Fact]
    public void ContentInBoxUnitsScalesWithTheShape()
    {
        var at = Render(
            @"<pattern id=""p"" width=""0.5"" height=""0.5"" patternContentUnits=""objectBoundingBox""><rect width=""0.25"" height=""0.25"" fill=""#ff0000""/></pattern>" +
            @"<rect width=""200"" height=""100"" fill=""url(#p)""/>");

        AssertRed(at(25, 12));
        AssertWhite(at(25, 40));
        AssertWhite(at(75, 12));
        AssertRed(at(125, 12));
    }

    /// <summary>A viewBox fits the content into the tile: a 10-unit viewBox in a 20-unit tile draws the 5-unit square 10 wide.</summary>
    [Fact]
    public void AViewBoxFitsTheContentIntoTheTile()
    {
        var at = Render(
            @"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""20"" height=""20"" viewBox=""0 0 10 10""><rect width=""5"" height=""5"" fill=""#ff0000""/></pattern>" + Page);

        AssertRed(at(5, 5));
        AssertRed(at(8, 8));
        AssertWhite(at(15, 5));
        AssertWhite(at(15, 15));
        AssertRed(at(25, 5));
    }

    /// <summary>patternTransform moves the tiles: translated 10 along x, the red square is at 10 to 20 of each tile.</summary>
    [Fact]
    public void APatternTransformMovesTheTiles()
    {
        var at = Render(
            @"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""20"" height=""20"" patternTransform=""translate(10 0)""><rect width=""10"" height=""10"" fill=""#ff0000""/></pattern>" + Page);

        AssertWhite(at(5, 5));
        AssertRed(at(15, 5));
        AssertWhite(at(25, 5));
        AssertRed(at(35, 5));
    }

    /// <summary>The shape's own transform carries the pattern: under scale(2) the 20-unit tile is 40 on the page.</summary>
    [Fact]
    public void TheShapesTransformCarriesThePattern()
    {
        var at = Render(Checks + @"<rect width=""100"" height=""100"" fill=""url(#p)"" transform=""scale(2)""/>");

        AssertRed(at(15, 15));
        AssertWhite(at(25, 25));
        AssertRed(at(55, 15));
    }

    /// <summary>
    /// An href template supplies the tile and, when the pattern has no children of its own, the
    /// content; a pattern with children of its own draws them in the template's tile.
    /// </summary>
    [Fact]
    public void AnHrefTemplateSuppliesTheTileAndTheContent()
    {
        string @base = Checks.Replace(@"id=""p""", @"id=""base""");
        var inherited = Render(@base + @"<pattern id=""p"" href=""#base""/>" + Page);
        var own = Render(@base + @"<pattern id=""p"" href=""#base""><rect width=""10"" height=""10"" fill=""#0000ff""/></pattern>" + Page);

        AssertRed(inherited(5, 5));
        AssertWhite(inherited(15, 15));
        AssertRed(inherited(25, 5));
        Assert.InRange(own(5, 5).B, 250, 255);
        Assert.InRange(own(5, 5).R, 0, 5);
        AssertWhite(own(15, 15));
    }

    /// <summary>A stroke takes a pattern too, through the stroking pattern colour space.</summary>
    [Fact]
    public void AStrokeTakesAPattern()
    {
        string body = Checks + @"<rect x=""50"" y=""50"" width=""100"" height=""100"" fill=""none"" stroke=""url(#p)"" stroke-width=""20""/>";
        using var doc = Open(body);
        string[] page = Lines(doc.Pages[0].GetContentStreamData());
        var at = Render(body);

        Assert.Contains("/Pattern CS", page);
        Assert.Contains("/P0 SCN", page);
        AssertRed(at(45, 105));    // on the left edge's stroke, in a tile's red corner
        AssertWhite(at(55, 115));  // on the stroke, in the tile's white part
        AssertWhite(at(100, 100)); // inside, unfilled
    }

    /// <summary>
    /// A pattern in a pattern: the cell's content paints with another pattern, whose resources the
    /// cell shares — red 5-unit squares every 10 inside the outer tile's 20-unit square, every 40.
    /// </summary>
    [Fact]
    public void APatternInsideAPatternsContentPaints()
    {
        var at = Render(
            @"<pattern id=""inner"" patternUnits=""userSpaceOnUse"" width=""10"" height=""10""><rect width=""5"" height=""5"" fill=""#ff0000""/></pattern>" +
            @"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""40"" height=""40""><rect width=""20"" height=""20"" fill=""url(#inner)""/></pattern>" + Page);

        AssertRed(at(2, 2));
        AssertWhite(at(7, 7));
        AssertRed(at(12, 2));
        AssertWhite(at(22, 2));
        AssertRed(at(42, 2));
    }

    /// <summary>A tile with no width, or a pattern with no content, paints nothing: the path is ended, not filled, and nothing is said.</summary>
    [Theory]
    [InlineData(@"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""0"" height=""20""><rect width=""10"" height=""10"" fill=""#ff0000""/></pattern>")]
    [InlineData(@"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""20"" height=""20""/>")]
    public void AnEmptyTileOrPatternPaintsNothing(string pattern)
    {
        using var doc = Open(pattern + Page);
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.Contains("n", page);
        Assert.DoesNotContain("f", page);
        Assert.DoesNotContain("/P0 scn", page);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A pattern in box units on a shape with no box is an empty cell, reported; the page still converts.</summary>
    [Fact]
    public void APatternOnAShapeWithNoBoxIsReported()
    {
        var at = Render(@"<pattern id=""p"" width=""0.5"" height=""0.5""><rect width=""10"" height=""10"" fill=""#ff0000""/></pattern>" +
                        @"<rect width=""0"" height=""0"" fill=""url(#p)""/><rect x=""100"" y=""100"" width=""50"" height=""50"" fill=""#ff0000""/>");

        Assert.Contains("no width or height", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        AssertRed(at(125, 125));
    }

    /// <summary>
    /// Hostile input: a pattern whose content paints with the pattern itself is drawn once — the inner
    /// use is an empty cell, reported — and converts in the time one cell takes.
    /// </summary>
    [Fact]
    public void AHostileSelfPaintingPatternIsDrawnOnce()
    {
        var at = Render(
            @"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""20"" height=""20""><rect width=""10"" height=""10"" fill=""url(#p)""/></pattern>" + Page);

        Assert.Contains("paints with the pattern itself", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        AssertWhite(at(5, 5));
    }

    /// <summary>
    /// A pattern's children are not its container's box: a group clipped to its left half in box
    /// units keeps that half whatever the pattern's content draws far away — a box widened to 1010
    /// would leave the whole shape visible.
    /// </summary>
    [Fact]
    public void APatternsContentIsNotItsContainersBox()
    {
        var at = Render(
            @"<pattern id=""p"" patternUnits=""userSpaceOnUse"" width=""20"" height=""20""><rect width=""20"" height=""20"" fill=""#ff0000""/><rect x=""1000"" y=""1000"" width=""10"" height=""10"" fill=""#0000ff""/></pattern>" +
            @"<clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath>" +
            @"<g clip-path=""url(#c)""><rect width=""200"" height=""100"" fill=""url(#p)""/></g>");

        AssertRed(at(50, 50));
        AssertWhite(at(150, 50));
    }

    /// <summary>
    /// A pattern's href names a pattern: one to a gradient ends the chain there, so a pattern with no
    /// content of its own paints nothing — and does not try to draw the gradient's stops.
    /// </summary>
    [Fact]
    public void AnHrefToAnotherKindOfElementEndsTheChain()
    {
        using var doc = Open(
            @"<linearGradient id=""g""><stop offset=""0"" stop-color=""#ff0000""/></linearGradient>" +
            @"<pattern id=""p"" href=""#g"" patternUnits=""userSpaceOnUse"" width=""20"" height=""20""/>" + Page);
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.Contains("n", page);
        Assert.DoesNotContain("/P0 scn", page);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertRed((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 0, 5);
    }

    private static void AssertWhite((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 250, 255);
        Assert.InRange(p.B, 250, 255);
    }

    private static (PdfDictionary Dictionary, byte[] Content) Pattern(PdfDocument doc, string name)
    {
        var patterns = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[new PdfName("Pattern")]));
        var stream = Assert.IsType<PdfStream>(doc.ResolveObject(patterns[new PdfName(name)]));
        return (stream, stream.GetDecodedData());
    }

    private static double[] Numbers(PdfDocument doc, PdfObject array)
        => Assert.IsType<PdfArray>(doc.ResolveObject(array)).Select(o => Math.Round(((PdfNumber)doc.ResolveObject(o)).DoubleValue, 4)).ToArray();

    private static PdfDocument Open(string body) => PdfDocument.Open(new MemoryStream(Convert(body)));

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int R, int G, int B)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    private static string[] Lines(byte[] content) => Encoding.Latin1.GetString(content).Split('\n').Select(l => l.Trim()).ToArray();
}
