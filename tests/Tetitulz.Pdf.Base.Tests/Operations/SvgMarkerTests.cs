// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> draws markers (SVG 1.1 §11.6): a <c>&lt;marker&gt;</c>'s content
/// at a path's vertices — first, middle, last — placed by <c>refX</c>/<c>refY</c>, turned by
/// <c>orient</c>, scaled by the stroke width in <c>strokeWidth</c> units.
/// </summary>
/// <remarks>
/// Markers were reported and not drawn, so every arrowhead of a diagram, a flow chart or a plotted
/// line was missing. The marker below is a 4 × 4 square with its reference point at its centre, so
/// at a vertex (x, y), unturned and in user units, it draws the rect (x − 2, y − 2, 4, 4).
/// Unflipped, so user space is page space.
/// </remarks>
public class SvgMarkerTests
{
    private const string Square =
        @"<defs><marker id=""m"" markerWidth=""4"" markerHeight=""4"" refX=""2"" refY=""2"" markerUnits=""userSpaceOnUse"" {0}><rect width=""4"" height=""4""/></marker></defs>";

    private static string Defs(string markerAttributes = "") => string.Format(Square, markerAttributes);

    [Fact]
    public void AnEndMarkerIsDrawnAtTheLastVertex()
    {
        string c = Content(Defs() + @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>");

        Assert.Equal(new[] { 8f, -2, 4, 4 }, Assert.Single(DrawnRects(c)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A percentage <c>refX</c>/<c>refY</c> is of the marker's own viewport (batch 79): 50% and 25% of
    /// a 4 × 8 marker are (2, 2), so the 4 × 8 rect sits as with refX="2" refY="2"; with a viewBox the
    /// percentage is of its side — 50% of 8 is 4 viewBox units, 2 on the page at the 0.5 scale, so the
    /// 4 × 4 rect, 2 × 2 on the page, sits at (8, −2).
    /// </summary>
    [Theory]
    [InlineData(@"markerWidth=""4"" markerHeight=""8"" refX=""50%"" refY=""25%""", @"<rect width=""4"" height=""8""/>", 8f, -2f, 4f, 8f)]
    [InlineData(@"markerWidth=""4"" markerHeight=""4"" viewBox=""0 0 8 8"" refX=""50%"" refY=""50%""", @"<rect width=""4"" height=""4""/>", 8f, -2f, 2f, 2f)]
    public void APercentageReferencePointIsOfTheMarkersViewport(string attributes, string content, float x, float y, float w, float h)
    {
        string c = Content($@"<defs><marker id=""m"" {attributes} markerUnits=""userSpaceOnUse"">{content}</marker></defs>" +
                           @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>");

        Assert.Equal(new[] { x, y, w, h }, Assert.Single(DrawnRects(c)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The <c>marker</c> shorthand sets all three: a polyline of three points has three markers.</summary>
    [Fact]
    public void EveryVertexOfAPolylineIsMarked()
    {
        string c = Content(Defs() + @"<polyline points=""0,0 10,0 10,10"" fill=""none"" stroke=""black"" marker=""url(#m)""/>");

        Assert.Equal(new[] { new[] { -2f, -2, 4, 4 }, new[] { 8f, -2, 4, 4 }, new[] { 8f, 8, 4, 4 } }, DrawnRects(c));
    }

    /// <summary>A path's vertices are its commands' end points: marker-mid marks (10, 0) alone.</summary>
    [Fact]
    public void APathsMiddleVerticesAreMarked()
    {
        string c = Content(Defs() + @"<path d=""M 0 0 L 10 0 L 10 10"" fill=""none"" stroke=""black"" marker-mid=""url(#m)""/>");

        Assert.Equal(new[] { new[] { 8f, -2, 4, 4 } }, DrawnRects(c));
    }

    /// <summary>
    /// An arc drawn as several cubics is still one segment: it has no middle vertex, and its ends
    /// are (0, 0) and (10, 0).
    /// </summary>
    [Fact]
    public void AnArcIsOneSegment()
    {
        string mid = Content(Defs() + @"<path d=""M 0 0 A 5 5 0 0 1 10 0"" fill=""none"" stroke=""black"" marker-mid=""url(#m)""/>");
        string all = Content(Defs() + @"<path d=""M 0 0 A 5 5 0 0 1 10 0"" fill=""none"" stroke=""black"" marker=""url(#m)""/>");

        Assert.Empty(DrawnRects(mid));
        Assert.Equal(new[] { new[] { -2f, -2, 4, 4 }, new[] { 8f, -2, 4, 4 } }, DrawnRects(all));
    }

    /// <summary>
    /// An arc's end direction comes from its last cubic: a semicircle from (0, 0) to (10, 0) with
    /// sweep 1 ends heading along +y (90°), so an auto marker there turns, its first corner at (12, −2).
    /// </summary>
    [Fact]
    public void AnArcsEndDirectionTurnsTheMarker()
    {
        string[] lines = Lines(Content(Defs(@"orient=""auto"" overflow=""visible""") +
            @"<path d=""M 0 0 A 5 5 0 0 1 10 0"" fill=""none"" stroke=""black"" marker-end=""url(#m)""/>"));

        int fill = Array.IndexOf(lines, "f");
        float[] first = Numbers(lines[fill - 5], "m");
        Assert.Equal(12f, first[0], 3);
        Assert.Equal(-2f, first[1], 3);
    }

    /// <summary>A quadratic, written as one cubic, is one segment too: no middle vertex.</summary>
    [Fact]
    public void AQuadraticIsOneSegment()
    {
        string c = Content(Defs() + @"<path d=""M 0 0 Q 5 5 10 0 Q 15 -5 20 0"" fill=""none"" stroke=""black"" marker-mid=""url(#m)""/>");

        Assert.Equal(new[] { new[] { 8f, -2, 4, 4 } }, DrawnRects(c));   // one mid vertex, between the two
    }

    /// <summary>
    /// A polygon closes: its last vertex is back at its first, so the end marker is drawn there too —
    /// four markers for three points.
    /// </summary>
    [Fact]
    public void APolygonsClosingVertexIsMarked()
    {
        string c = Content(Defs() + @"<polygon points=""0,0 10,0 10,10"" fill=""none"" stroke=""black"" marker=""url(#m)""/>");

        Assert.Equal(new[] { new[] { -2f, -2, 4, 4 }, new[] { 8f, -2, 4, 4 }, new[] { 8f, 8, 4, 4 }, new[] { -2f, -2, 4, 4 } }, DrawnRects(c));
    }

    /// <summary>The marker properties are inherited: a group's marker-end marks its line.</summary>
    [Fact]
    public void AMarkerIsInheritedFromAGroup()
    {
        string c = Content(Defs() + @"<g marker-end=""url(#m)""><line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black""/></g>");

        Assert.Equal(new[] { 8f, -2, 4, 4 }, Assert.Single(DrawnRects(c)));
    }

    /// <summary>In strokeWidth units a marker scales by the stroke width: 2 makes it 8 × 8 around (10, 0).</summary>
    [Fact]
    public void AMarkerScalesByTheStrokeWidth()
    {
        string c = Content(
            @"<defs><marker id=""m"" markerWidth=""4"" markerHeight=""4"" refX=""2"" refY=""2""><rect width=""4"" height=""4""/></marker></defs>" +
            @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" stroke-width=""2"" marker-end=""url(#m)""/>");

        Assert.Equal(new[] { 6f, -4, 8, 8 }, Assert.Single(DrawnRects(c)));
    }

    /// <summary>
    /// orient="auto" turns the marker with the path: along a line pointing down (90°) the square's
    /// corners are (2, 8), (2, 12), (−2, 12), (−2, 8).
    /// </summary>
    [Fact]
    public void AnAutoMarkerTurnsWithThePath()
    {
        string[] lines = Lines(Content(Defs(@"orient=""auto"" overflow=""visible""") +
            @"<line x1=""0"" y1=""0"" x2=""0"" y2=""10"" stroke=""black"" marker-end=""url(#m)""/>"));

        int fill = Array.IndexOf(lines, "f");
        float[][] corners = { Numbers(lines[fill - 5], "m"), Numbers(lines[fill - 4], "l"), Numbers(lines[fill - 3], "l"), Numbers(lines[fill - 2], "l") };
        float[][] expected = { new[] { 2f, 8 }, new[] { 2f, 12 }, new[] { -2f, 12 }, new[] { -2f, 8 } };
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i][0], corners[i][0], 3);
            Assert.Equal(expected[i][1], corners[i][1], 3);
        }
    }

    /// <summary>
    /// At a middle vertex auto takes the bisector: in along +x (0°), out along +y (90°), so 45°. The
    /// square's first corner, (−2, −2) turned 45° about (10, 0), is (10, −2√2).
    /// </summary>
    [Fact]
    public void AMiddleVertexTakesTheBisector()
    {
        string[] lines = Lines(Content(Defs(@"orient=""auto"" overflow=""visible""") +
            @"<polyline points=""0,0 10,0 10,10"" fill=""none"" stroke=""black"" marker-mid=""url(#m)""/>"));

        int fill = Array.IndexOf(lines, "f");
        float[] first = Numbers(lines[fill - 5], "m");
        Assert.Equal(10f, first[0], 3);
        Assert.Equal(-2 * (float)Math.Sqrt(2), first[1], 3);
    }

    /// <summary>A marker is drawn only where a path names it; on its own it draws nothing and is not reported.</summary>
    [Fact]
    public void AMarkerOnItsOwnIsNotDrawn()
    {
        string c = Content(@"<marker id=""m""><rect width=""4"" height=""4""/></marker><rect x=""50"" width=""1"" height=""1""/>");

        Assert.Single(DrawnRects(c));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// SVG 2's orient="auto-start-reverse" is auto, except that the start marker points back along
    /// the path: on a line pointing down, the start turns 270° (first corner (−2, 2)) and the end 90°
    /// (first corner (2, 8)). Under auto the start turns 90° too (first corner (2, −2)). It was read
    /// as an angle of 0.
    /// </summary>
    [Theory]
    [InlineData("auto-start-reverse", -2f, 2f)]
    [InlineData("auto", 2f, -2f)]
    public void AutoStartReverseTurnsTheStartMarkerBack(string orient, float startX, float startY)
    {
        string[] lines = Lines(Content(Defs($@"orient=""{orient}"" overflow=""visible""") +
            @"<line x1=""0"" y1=""0"" x2=""0"" y2=""10"" stroke=""black"" marker-start=""url(#m)"" marker-end=""url(#m)""/>"));

        int[] fills = Enumerable.Range(0, lines.Length).Where(i => lines[i] == "f").ToArray();
        Assert.Equal(2, fills.Length);
        Assert.Equal(new[] { startX, startY }, Numbers(lines[fills[0] - 5], "m").Select(v => (float)Math.Round(v, 3)));
        Assert.Equal(new[] { 2f, 8f }, Numbers(lines[fills[1] - 5], "m").Select(v => (float)Math.Round(v, 3)));
    }

    /// <summary>A fixed orient angle turns the marker whatever the path's direction: 90° on a line pointing right.</summary>
    [Fact]
    public void AFixedOrientTurnsTheMarker()
    {
        string[] lines = Lines(Content(Defs(@"orient=""90"" overflow=""visible""") +
            @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>"));

        int fill = Array.IndexOf(lines, "f");
        Assert.Equal(new[] { 12f, -2 }, Numbers(lines[fill - 5], "m").Select(v => (float)Math.Round(v, 3)));
    }

    /// <summary>The marker's viewport clips its content, 4 × 4 around the vertex; overflow="visible" does not.</summary>
    [Fact]
    public void AMarkersViewportClips()
    {
        string[] clipped = Lines(Content(Defs() + @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>"));
        string[] visible = Lines(Content(Defs(@"overflow=""visible""") + @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>"));

        int w = Array.IndexOf(clipped, "W");
        Assert.Equal(new[] { 8f, -2, 4, 4 }, Numbers(clipped[w - 1], "re"));
        Assert.DoesNotContain("W", visible);
    }

    /// <summary>A marker's content takes its paint from the marker, not from the path it marks.</summary>
    [Fact]
    public void AMarkerIsPaintedFromItsOwnAncestors()
    {
        string c = Content(
            @"<defs fill=""#0000ff""><marker id=""m"" markerUnits=""userSpaceOnUse""><rect width=""1"" height=""1""/></marker></defs>" +
            @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" fill=""#ff0000"" marker-end=""url(#m)""/>");

        Assert.Contains("0.0000 0.0000 1.0000 rg", Lines(c));
        Assert.DoesNotContain("1.0000 0.0000 0.0000 rg", Lines(c));
    }

    [Fact]
    public void AMissingMarkerIsReported()
    {
        string c = Content(@"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#nothing)""/>");

        Assert.Contains("'marker-end'", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains(Lines(c), l => l == "S");   // the line is still drawn
    }

    /// <summary>A marker's own content marked with the marker itself would recurse; there it is skipped and reported, and the marker is drawn once.</summary>
    [Fact]
    public void AMarkerInsideItsOwnContentIsDrawnOnce()
    {
        string c = Content(
            @"<defs><marker id=""m"" markerUnits=""userSpaceOnUse"" overflow=""visible""><line x1=""0"" y1=""0"" x2=""1"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/></marker></defs>" +
            @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>");

        Assert.Equal(2, Lines(c).Count(l => l == "S"));   // the line, and the marker's line once
        Assert.Contains("the marker itself", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A marker's content may have markers of its own: the inner marker's rect is drawn at the inner line's end, and nothing is said.</summary>
    [Fact]
    public void AMarkerInsideAnotherMarkerIsDrawn()
    {
        string c = Content(
            @"<defs><marker id=""n"" markerUnits=""userSpaceOnUse"" overflow=""visible""><rect width=""2"" height=""2"" fill=""red""/></marker>" +
            @"<marker id=""m"" markerUnits=""userSpaceOnUse"" overflow=""visible""><line x1=""0"" y1=""0"" x2=""1"" y2=""0"" stroke=""black"" marker-end=""url(#n)""/></marker></defs>" +
            @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>");

        Assert.Single(DrawnRects(c));
        Assert.Equal(2, Lines(c).Count(l => l == "S"));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A marker on several vertices is drawn on each: being drawn is per marker while it draws, not for good.</summary>
    [Fact]
    public void AMarkerOnEveryVertexIsDrawnOnEach()
    {
        string c = Content(
            @"<defs><marker id=""n"" markerUnits=""userSpaceOnUse"" overflow=""visible""><rect width=""2"" height=""2"" fill=""red""/></marker></defs>" +
            @"<polyline points=""0,0 10,0 20,0 30,0"" fill=""none"" stroke=""black"" marker-start=""url(#n)"" marker-mid=""url(#n)"" marker-end=""url(#n)""/>");

        Assert.Equal(4, DrawnRects(c).Length);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Hostile input: two markers whose contents mark with each other are drawn once each — the line,
    /// m's line, n's line, and m again refused — reported, in the time two markers take.
    /// </summary>
    [Fact]
    public void AHostileMarkerCycleIsBounded()
    {
        string c = Content(
            @"<defs><marker id=""m"" markerUnits=""userSpaceOnUse"" overflow=""visible""><line x1=""0"" y1=""0"" x2=""1"" y2=""0"" stroke=""black"" marker-end=""url(#n)""/></marker>" +
            @"<marker id=""n"" markerUnits=""userSpaceOnUse"" overflow=""visible""><line x1=""0"" y1=""0"" x2=""1"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/></marker></defs>" +
            @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m)""/>");

        Assert.Equal(3, Lines(c).Count(l => l == "S"));
        Assert.Contains("the marker itself", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>The nesting depth is released with each marker: a hundred and fifty vertices marked one after another are all drawn, past the depth guard's hundred.</summary>
    [Fact]
    public void ManyMarkersOneAfterAnotherAreAllDrawn()
    {
        string points = string.Join(" ", Enumerable.Range(0, 150).Select(i => $"{i},{i % 2 * 5}"));
        string c = Content(
            @"<defs><marker id=""n"" markerUnits=""userSpaceOnUse"" overflow=""visible""><rect width=""1"" height=""1"" fill=""red""/></marker></defs>" +
            $@"<polyline points=""{points}"" fill=""none"" stroke=""black"" marker-start=""url(#n)"" marker-mid=""url(#n)"" marker-end=""url(#n)""/>");

        Assert.Equal(150, DrawnRects(c).Length);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Hostile input: a chain of two thousand distinct markers, each marking with the next, is no
    /// cycle — each is on the stack once — so the cycle guard cannot end it; the depth counter does,
    /// at <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTreeDepth"/>, reported, with the stack intact.
    /// </summary>
    [Fact]
    public void AHostileChainOfDistinctMarkersIsBounded()
    {
        var defs = new StringBuilder("<defs>");
        for (int i = 0; i < 2000; i++)
            defs.Append($@"<marker id=""m{i}"" markerUnits=""userSpaceOnUse"" overflow=""visible""><line x1=""0"" y1=""0"" x2=""1"" y2=""0"" stroke=""black"" marker-end=""url(#m{i + 1})""/></marker>");
        defs.Append(@"<marker id=""m2000"" markerUnits=""userSpaceOnUse"" overflow=""visible""><rect width=""1"" height=""1""/></marker></defs>");
        string c = Content(defs + @"<line x1=""0"" y1=""0"" x2=""10"" y2=""0"" stroke=""black"" marker-end=""url(#m0)""/>");

        int drawn = Lines(c).Count(l => l == "S");
        Assert.InRange(drawn, 2, Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTreeDepth + 1);
        Assert.Contains("nested more than", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker's content is no container's geometry, after a nested marker as before it: a group
    /// clipped to its left half in box units keeps that half whatever the marker's content drew far
    /// away after its own marker finished.
    /// </summary>
    [Fact]
    public void AMarkersContentIsNotItsContainersBoxAfterANestedMarker()
    {
        var at = Render(
            @"<defs><clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath>" +
            @"<marker id=""n"" markerUnits=""userSpaceOnUse"" overflow=""visible""><rect width=""1"" height=""1"" fill=""black""/></marker>" +
            @"<marker id=""m"" markerUnits=""userSpaceOnUse"" overflow=""visible""><line x1=""0"" y1=""0"" x2=""1"" y2=""0"" stroke=""black"" marker-end=""url(#n)""/><rect x=""1000"" y=""1000"" width=""10"" height=""10"" fill=""black""/></marker></defs>" +
            @"<g clip-path=""url(#c)""><path d=""M0 0 H200 V100 H0 Z"" fill=""#ff0000"" marker-end=""url(#m)""/></g>");

        Assert.True(at(50, 50).R > 250 && at(50, 50).G < 5, $"the left half was {at(50, 50)}, not red");
        Assert.True(at(150, 50).G > 250, $"the right half was {at(150, 50)}, not clipped away");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The rects drawn, not a viewport's clip: a clip's <c>re</c> is followed by <c>W</c>.</summary>
    private static float[][] DrawnRects(string content)
    {
        string[] lines = Lines(content);
        return lines.Where((l, i) => l.EndsWith(" re", StringComparison.Ordinal) && (i + 1 >= lines.Length || lines[i + 1] != "W"))
            .Select(l => Numbers(l, "re")).ToArray();
    }

    private static float[] Numbers(string line, string op)
    {
        Assert.EndsWith(" " + op, line, StringComparison.Ordinal);
        return line.Substring(0, line.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
    }

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();

    /// <summary>The page rendered by this library's renderer, read at a pixel, under the default (flipped) page mapping.</summary>
    private static Func<int, int, (int R, int G, int B)> Render(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
