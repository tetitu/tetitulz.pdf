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
/// <see cref="SvgToPdfConverter"/> paints a shape's <c>fill</c> or <c>stroke</c> that names a
/// <c>linearGradient</c> or <c>radialGradient</c> (SVG 1.1 §13.2) with a shading pattern (ISO 32000-1
/// §8.7.3): an axial or radial shading whose function steps through the gradient's stops, placed by a
/// pattern matrix that takes the gradient's space — the element's bounding box, or its user space —
/// to the page.
/// </summary>
/// <remarks>
/// A paint server was reported as not a colour and painted black, so every gradient — the commonest
/// fill after a flat colour in exported artwork — came out as a black shape.
/// </remarks>
public class SvgGradientTests
{
    private const string RedToBlue =
        @"<linearGradient id=""g""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>";

    [Fact]
    public void ALinearGradientIsAnAxialShadingPattern()
    {
        using var doc = Open(RedToBlue + @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int scn = Array.IndexOf(page, "/P0 scn");
        Assert.True(scn > 0 && page[scn - 1] == "/Pattern cs", "the fill was not set to the pattern");
        Assert.True(scn < Array.FindIndex(page, l => l.EndsWith(" re", StringComparison.Ordinal)), "the paint must come before the path");
        var pattern = Pattern(doc, "P0");
        Assert.Equal(2, ((PdfNumber)pattern[new PdfName("PatternType")]).DoubleValue);
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(pattern[new PdfName("Shading")]));
        Assert.Equal(2, ((PdfNumber)shading[new PdfName("ShadingType")]).DoubleValue);
        Assert.Equal(new[] { 0.0, 0, 1, 0 }, Numbers(doc, shading[new PdfName("Coords")]));
        var function = Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]));
        Assert.Equal(new[] { 1.0, 0, 0 }, Numbers(doc, function[new PdfName("C0")]));
        Assert.Equal(new[] { 0.0, 0, 1 }, Numbers(doc, function[new PdfName("C1")]));
        // The bounding box (0, 0, 200, 100) onto the flipped page: x → x·200, y → 200 − y·100.
        Assert.Equal(new[] { 200.0, 0, 0, -100, 0, 200 }, Numbers(doc, pattern[PdfName.Matrix]));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Rendered: red at the left, blue at the right, the two mixed half way. It was black.</summary>
    [Fact]
    public void ALinearGradientRunsAcrossTheShape()
    {
        var at = Render(RedToBlue + @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((255, 0, 0), at(1, 50));
        AssertNear((0, 0, 255), at(198, 50));
        AssertNear((128, 0, 128), at(100, 50));
    }

    /// <summary>
    /// The gradient's units: in the bounding box (the default) it runs across the shape wherever the
    /// shape is — red at its left edge, x = 101 — and in user space from 0 to 200 in the drawing's
    /// coordinates, so x = 101 is half way.
    /// </summary>
    [Theory]
    [InlineData(@"", 255, 0)]
    [InlineData(@"gradientUnits=""userSpaceOnUse"" x1=""0"" x2=""200""", 128, 128)]
    public void TheGradientsUnitsPlaceIt(string units, int red, int blue)
    {
        var at = Render($@"<linearGradient id=""g"" {units}><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>" +
                        @"<rect x=""100"" width=""100"" height=""100"" fill=""url(#g)""/>");

        AssertNear((red, 0, blue), at(101, 50));
    }

    /// <summary>A radial gradient is a radial shading: white at the centre, black at the edge.</summary>
    [Fact]
    public void ARadialGradientIsARadialShading()
    {
        string body = @"<radialGradient id=""r""><stop offset=""0"" stop-color=""#ffffff""/><stop offset=""1"" stop-color=""#000000""/></radialGradient>" +
                      @"<circle cx=""100"" cy=""100"" r=""100"" fill=""url(#r)""/>";
        using var doc = Open(body);
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));
        var at = Render(body);

        Assert.Equal(3, ((PdfNumber)shading[new PdfName("ShadingType")]).DoubleValue);
        Assert.Equal(new[] { 0.5, 0.5, 0, 0.5, 0.5, 0.5 }, Numbers(doc, shading[new PdfName("Coords")]));
        AssertNear((255, 255, 255), at(100, 100));
        AssertNear((13, 13, 13), at(100, 5), tolerance: 20);
    }

    /// <summary>
    /// Stops away from the ends: before the first the first's colour holds, after the last the
    /// last's. With red at 0.25 and blue at 0.75, the first quarter is solid red, the last solid
    /// blue, and the middle mixes.
    /// </summary>
    [Fact]
    public void BeyondTheEndStopsTheirColoursHold()
    {
        var at = Render(@"<linearGradient id=""g""><stop offset=""25%"" stop-color=""#ff0000""/><stop offset=""0.75"" stop-color=""#0000ff""/></linearGradient>" +
                        @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((255, 0, 0), at(25, 50));
        AssertNear((128, 0, 128), at(100, 50));
        AssertNear((0, 0, 255), at(175, 50));
    }

    /// <summary>
    /// Two stops at one offset are a hard edge: red to the left of the middle, blue to the right. The
    /// stitching function has two pieces and one bound, 0.5, not a zero-width piece between two equal
    /// bounds: ISO 32000-1 §7.10.4 asks for bounds in increasing order.
    /// </summary>
    [Fact]
    public void TwoStopsAtOneOffsetAreAnEdge()
    {
        const string Body = @"<linearGradient id=""g""><stop offset=""0.5"" stop-color=""#ff0000""/><stop offset=""0.5"" stop-color=""#0000ff""/></linearGradient>" +
                            @"<rect width=""200"" height=""100"" fill=""url(#g)""/>";
        var at = Render(Body);
        using var doc = Open(Body);
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));
        var function = Assert.IsType<PdfDictionary>(doc.ResolveObject(shading[new PdfName("Function")]));

        AssertNear((255, 0, 0), at(90, 50));
        AssertNear((0, 0, 255), at(110, 50));
        Assert.Equal(2, Assert.IsType<PdfArray>(doc.ResolveObject(function[new PdfName("Functions")])).Count);
        Assert.Equal(new[] { 0.5 }, Numbers(doc, function[new PdfName("Bounds")]));
    }

    /// <summary>Past the gradient's end its last colour carries on: a user-space gradient over 0–50, blue at 150.</summary>
    [Fact]
    public void TheColoursExtendPastTheEnds()
    {
        var at = Render(@"<linearGradient id=""g"" gradientUnits=""userSpaceOnUse"" x1=""0"" x2=""50""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>" +
                        @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((0, 0, 255), at(150, 50));
    }

    /// <summary>
    /// The box is in the shape's own user space, which its <c>transform</c> places: a rect moved 100
    /// right is red at its left edge, x = 101, as the unmoved one is at 1.
    /// </summary>
    [Fact]
    public void TheGradientFollowsTheShapesTransform()
    {
        var at = Render(RedToBlue + @"<rect width=""100"" height=""100"" transform=""translate(100 0)"" fill=""url(#g)""/>");

        AssertNear((255, 0, 0), at(101, 50));
        AssertNear((0, 0, 255), at(198, 50));
    }

    /// <summary>
    /// Markers are not the shape's geometry. Each clips to its viewport, 150 × 150 here, and that clip
    /// is mapped while the shape is still being drawn; counted in, it stretches the square path's box
    /// from 0–100 to 0–150, and at x = 98 the fill is two thirds of the way to blue, not all of it.
    /// The marker's content is one unit at each corner, clear of the pixels read.
    /// </summary>
    [Fact]
    public void MarkersDoNotWidenTheBox()
    {
        var at = Render(RedToBlue +
            @"<marker id=""m"" markerWidth=""150"" markerHeight=""150"" markerUnits=""userSpaceOnUse""><rect width=""1"" height=""1""/></marker>" +
            @"<path d=""M 0 0 H 100 V 100 H 0 Z"" fill=""url(#g)"" marker=""url(#m)""/>");

        AssertNear((255, 0, 0), at(1, 50));
        AssertNear((5, 0, 250), at(98, 50));
    }

    /// <summary>
    /// A radial gradient's focal point: <c>fx</c> and <c>fy</c> default to the centre, and one
    /// outside the circle is moved onto it (§13.2.3).
    /// </summary>
    [Theory]
    [InlineData(@"cx=""0.3"" cy=""0.4"" r=""0.5""", 0.3, 0.4)]
    [InlineData(@"cx=""0.5"" cy=""0.5"" r=""0.5"" fx=""2"" fy=""0.5""", 1.0, 0.5)]
    public void TheFocalPointIsTheCentreOrOnTheCircle(string attributes, double fx, double fy)
    {
        using var doc = Open($@"<radialGradient id=""r"" {attributes}><stop offset=""0"" stop-color=""white""/><stop offset=""1"" stop-color=""black""/></radialGradient>" +
                             @"<rect width=""100"" height=""100"" fill=""url(#r)""/>");
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));

        double[] coords = Numbers(doc, shading[new PdfName("Coords")]);
        Assert.Equal(fx, coords[0], 3);
        Assert.Equal(fy, coords[1], 3);
    }

    /// <summary>
    /// In user space a radius percentage is of the viewport's normalised diagonal,
    /// √((w² + h²) / 2): 50% of a 300 × 100 viewport is 111.8, not 50% of the width.
    /// </summary>
    [Fact]
    public void AUserSpaceRadiusIsOfTheDiagonal()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""300"" height=""100"">" +
            @"<radialGradient id=""r"" gradientUnits=""userSpaceOnUse"" r=""50%""><stop offset=""0"" stop-color=""white""/><stop offset=""1"" stop-color=""black""/></radialGradient>" +
            @"<rect width=""100"" height=""100"" fill=""url(#r)""/></svg>",
            new SvgConversionOptions { PageWidth = 300, PageHeight = 100 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var shading = Assert.IsType<PdfDictionary>(doc.ResolveObject(Pattern(doc, "P0")[new PdfName("Shading")]));

        Assert.Equal(111.8034, Numbers(doc, shading[new PdfName("Coords")])[5], 3);
    }

    /// <summary>
    /// A gradient that names another by <c>href</c> takes its stops, and any attribute it does not
    /// set: here the stops of <c>base</c>, turned vertical by the referring gradient's own y2.
    /// </summary>
    [Fact]
    public void AGradientTakesItsTemplatesStops()
    {
        var at = Render(@"<linearGradient id=""base""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>" +
                        @"<linearGradient id=""g"" href=""#base"" x2=""0"" y2=""1""/>" +
                        @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((255, 0, 0), at(100, 1));
        AssertNear((0, 0, 255), at(100, 98));
    }

    /// <summary><c>gradientTransform</c> turns the gradient: rotated 90° about the box's origin it runs down.</summary>
    [Fact]
    public void AGradientTransformTurnsIt()
    {
        var at = Render(@"<linearGradient id=""g"" gradientTransform=""rotate(90)""><stop offset=""0"" stop-color=""#ff0000""/><stop offset=""1"" stop-color=""#0000ff""/></linearGradient>" +
                        @"<rect width=""200"" height=""100"" fill=""url(#g)""/>");

        AssertNear((255, 0, 0), at(100, 1));
        AssertNear((0, 0, 255), at(100, 98));
    }

    /// <summary>A stroke takes a gradient too, through the stroking pattern operators.</summary>
    [Fact]
    public void AStrokeTakesAGradient()
    {
        using var doc = Open(RedToBlue + @"<rect width=""100"" height=""100"" fill=""none"" stroke=""url(#g)"" stroke-width=""4""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int scn = Array.IndexOf(page, "/P0 SCN");
        Assert.True(scn > 0 && page[scn - 1] == "/Pattern CS", "the stroke was not set to the pattern");
        Assert.Contains("S", page);
    }

    /// <summary>One stop is a flat colour: no pattern.</summary>
    [Fact]
    public void OneStopIsAFlatColour()
    {
        using var doc = Open(@"<linearGradient id=""g""><stop offset=""0"" stop-color=""#00ff00""/></linearGradient><rect width=""10"" height=""10"" fill=""url(#g)""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.Contains("0.0000 1.0000 0.0000 rg", page);
        Assert.DoesNotContain(page, l => l.EndsWith(" scn", StringComparison.Ordinal));
    }

    /// <summary>
    /// A reference that names no gradient falls back to the colour after it, as SVG's paint syntax
    /// has it (<c>url(#none) green</c>); without one it is reported.
    /// </summary>
    [Fact]
    public void AMissingGradientFallsBack()
    {
        using var withFallback = Open(@"<rect width=""10"" height=""10"" fill=""url(#none) #00ff00""/>");
        Assert.Contains("0.0000 1.0000 0.0000 rg", Lines(withFallback.Pages[0].GetContentStreamData()));
        Assert.Null(SvgToPdfConverter.LastError);

        using var without = Open(@"<rect width=""10"" height=""10"" fill=""url(#none)""/>");
        Assert.Contains("names no gradient", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Text takes a gradient too, over the text element's box: the runs' advance across, and the
    /// font's ascent and descent (Helvetica's 718 and 207 per 1000) about the baseline. "HH" at 50
    /// from x = 10 on the baseline y = 100 is 72.2 wide, so its box is (10, 64.1) to (82.2, 110.35).
    /// </summary>
    [Fact]
    public void TextTakesAGradientOverItsBox()
    {
        using var doc = Open(RedToBlue + @"<text x=""10"" y=""100"" font-size=""50"" fill=""url(#g)"">HH</text>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int scn = Array.IndexOf(page, "/P0 scn");
        Assert.True(scn > 0 && scn < Array.IndexOf(page, "BT"), "the text's fill was not set to the pattern");
        // The box onto the flipped page: x → 10 + 72.2·x, y → 200 − (64.1 + 46.25·y).
        Assert.Equal(new[] { 72.2, 0, 0, -46.25, 10, 135.9 }, Numbers(doc, Pattern(doc, "P0")[PdfName.Matrix]));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A tspan's gradient is laid over the whole text element's box, as the text's is, and after a
    /// shape that took one the shape's box is not the text's: two patterns, the text's its own.
    /// </summary>
    [Fact]
    public void ATspansGradientIsOverTheTextsBox()
    {
        using var doc = Open(RedToBlue + @"<rect width=""10"" height=""10"" fill=""url(#g)""/>" +
                             @"<text x=""10"" y=""100"" font-size=""50"">H<tspan fill=""url(#g)"">H</tspan></text>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.Equal(2, page.Count(l => l.EndsWith(" scn", StringComparison.Ordinal)));
        Assert.Equal(new[] { 72.2, 0, 0, -46.25, 10, 135.9 }, Numbers(doc, Pattern(doc, "P1")[PdfName.Matrix]));
    }

    /// <summary>
    /// The box is the text's, however it is drawn: in a link, where every run's point a full size
    /// above its baseline is mapped for the link's rectangle, and moved by the text's transform, which
    /// moves the box with it (50 down: y → 85.9).
    /// </summary>
    [Theory]
    [InlineData(@"<a href=""https://example.org/""><text x=""10"" y=""100"" font-size=""50"" fill=""url(#g)"">HH</text></a>", 135.9)]
    [InlineData(@"<text x=""10"" y=""100"" font-size=""50"" fill=""url(#g)"" transform=""translate(0 50)"">HH</text>", 85.9)]
    public void TheTextsBoxIsItsOwn(string text, double f)
    {
        using var doc = Open(RedToBlue + text);

        Assert.Equal(new[] { 72.2, 0, 0, -46.25, 10, f }, Numbers(doc, Pattern(doc, "P0")[PdfName.Matrix]));
    }

    /// <summary>
    /// Each face's own ascent and descent: "HH" in Courier at 50 is 60 wide, 629 up and 157 down per
    /// 1000, so its box is (10, 68.55) to (70, 107.85).
    /// </summary>
    [Fact]
    public void TheBoxTakesTheFacesMetrics()
    {
        using var doc = Open(RedToBlue + @"<text x=""10"" y=""100"" font-size=""50"" font-family=""monospace"" fill=""url(#g)"">HH</text>");

        Assert.Equal(new[] { 60.0, 0, 0, -39.3, 10, 131.45 }, Numbers(doc, Pattern(doc, "P0")[PdfName.Matrix]));
    }

    /// <summary>Rendered: a red-to-blue gradient across a solid block of text, red at its left, blue at its right.</summary>
    [Fact]
    public void AGradientRunsAcrossTheText()
    {
        var at = Render(RedToBlue + @"<text x=""0"" y=""150"" font-size=""150"" font-family=""monospace"" font-weight=""bold"" fill=""url(#g)"">II</text>");
        var left = Enumerable.Range(0, 60).Select(x => at(x, 110)).First(p => p.Item1 + p.Item2 + p.Item3 < 700);
        var right = Enumerable.Range(0, 60).Select(x => at(179 - x, 110)).First(p => p.Item1 + p.Item2 + p.Item3 < 700);

        Assert.True(left.Item1 > left.Item3, $"the text's left was {left}, not red");
        Assert.True(right.Item3 > right.Item1, $"the text's right was {right}, not blue");
    }

    /// <summary>
    /// Hostile input: two gradients naming each other by <c>href</c> are read once each, not forever.
    /// With no stops between them the gradient paints nothing: the path is ended, not filled.
    /// </summary>
    [Fact]
    public void AnHrefCycleIsBounded()
    {
        using var doc = Open(@"<linearGradient id=""a"" href=""#b""/><linearGradient id=""b"" href=""#a""/><rect width=""10"" height=""10"" fill=""url(#a)""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.Contains("no stops", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("n", page);
        Assert.DoesNotContain("f", page);
    }

    /// <summary>
    /// Hostile input: a template chain longer than <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTreeDepth"/>
    /// is cut there, so stops past the cut are not reached. The control, the same chain one short of
    /// the limit, reaches them.
    /// </summary>
    [Theory]
    [InlineData(50, false)]
    [InlineData(150, true)]
    public void ALongTemplateChainIsCut(int links, bool cut)
    {
        var svg = new StringBuilder();
        for (int i = 0; i < links; i++) svg.Append($@"<linearGradient id=""g{i}"" href=""#g{i + 1}""/>");
        svg.Append($@"<linearGradient id=""g{links}""><stop offset=""0"" stop-color=""red""/><stop offset=""1"" stop-color=""blue""/></linearGradient>");
        using var doc = Open(svg + @"<rect width=""10"" height=""10"" fill=""url(#g0)""/>");

        Assert.Equal(cut, (SvgToPdfConverter.LastError ?? "").Contains("no stops", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertNear((int R, int G, int B) expected, (int R, int G, int B) actual, int tolerance = 12)
    {
        Assert.InRange(actual.R, expected.R - tolerance, expected.R + tolerance);
        Assert.InRange(actual.G, expected.G - tolerance, expected.G + tolerance);
        Assert.InRange(actual.B, expected.B - tolerance, expected.B + tolerance);
    }

    private static PdfDictionary Pattern(PdfDocument doc, string name)
    {
        var patterns = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[new PdfName("Pattern")]));
        return Assert.IsType<PdfDictionary>(doc.ResolveObject(patterns[new PdfName(name)]));
    }

    private static double[] Numbers(PdfDocument doc, PdfObject array)
        => Assert.IsType<PdfArray>(doc.ResolveObject(array)).Select(o => Math.Round(((PdfNumber)doc.ResolveObject(o)).DoubleValue, 4)).ToArray();

    private static PdfDocument Open(string body) => PdfDocument.Open(new MemoryStream(Convert(body)));

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int, int, int)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    private static string[] Lines(byte[] content) => Encoding.Latin1.GetString(content).Split('\n').Select(l => l.Trim()).ToArray();
}
