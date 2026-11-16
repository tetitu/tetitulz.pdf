// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> applies a <c>clip-path</c> that is a CSS basic shape (CSS
/// Masking 1 §5.1): <c>inset()</c>, <c>circle()</c>, <c>ellipse()</c>, <c>polygon()</c>,
/// <c>path()</c>, measured in the element's box or the view-box, as a path clip. Read back from the
/// rendered pixels of a red 50-unit square at (50, 50).
/// </summary>
/// <remarks>Any <c>clip-path</c> but a <c>url(#id)</c> was reported and left out.</remarks>
public class SvgBasicShapeClipTests
{
    private static string Square(string clip) => $@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" style=""clip-path: {clip}""/>";

    /// <summary>inset: one value all round, two for top-and-bottom then left-and-right, a percentage of the box.</summary>
    [Theory]
    [InlineData("inset(10px)", 75, 75, true, 55, 75, false)]
    [InlineData("inset(10px)", 85, 85, true, 75, 92, false)]
    [InlineData("inset(10px 20px)", 75, 65, true, 65, 75, false)]
    [InlineData("inset(25%)", 65, 75, true, 60, 75, false)]
    [InlineData("inset(0 0 0 30px)", 90, 75, true, 70, 75, false)]
    public void AnInsetShrinksTheBox(string clip, int redX, int redY, bool red, int whiteX, int whiteY, bool white)
    {
        var at = Render(Square(clip));

        Assert.Equal(red, IsRed(at(redX, redY)));
        Assert.Equal(white, IsRed(at(whiteX, whiteY)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// inset round: the corners are cut by the radii — 20 all round, or 20 horizontal by 10 vertical
    /// — and the edges' middles stay. The cut is an arc, not a chamfer: a point inside the arc but
    /// beyond its chord (93, 57) is kept. With the vertical radius 10 the left edge is straight
    /// again 8 below the top (51, 58); with 20 it is still curving there.
    /// </summary>
    [Theory]
    [InlineData("inset(0 round 20px)", false)]
    [InlineData("inset(0 round 20px / 10px)", true)]
    public void AnInsetsCornersAreRounded(string clip, bool straightAgainAt58)
    {
        var at = Render(Square(clip));

        Assert.False(IsRed(at(51, 51)), "the corner was not cut");
        Assert.True(IsRed(at(75, 51)), "the top edge's middle was cut");
        Assert.True(IsRed(at(51, 75)), "the left edge's middle was cut");
        Assert.True(IsRed(at(75, 75)));
        Assert.True(IsRed(at(93, 57)), "the corner was chamfered, not rounded");
        Assert.Equal(straightAgainAt58, IsRed(at(51, 58)));
    }

    /// <summary>
    /// Each axis is measured on its own side of a 100 × 50 box: a 10% top-and-bottom inset is 5 (of
    /// the height), not 10; a circle's 50% is of the diagonal over √2, 39.5, not 50 of the width.
    /// </summary>
    [Fact]
    public void ANonSquareBoxMeasuresEachAxis()
    {
        string Wide(string clip) => $@"<rect x=""50"" y=""50"" width=""100"" height=""50"" fill=""#ff0000"" style=""clip-path: {clip}""/>";
        var inset = Render(Wide("inset(10% 0)"));
        var circle = Render(Wide("circle(50%)"));

        Assert.True(IsRed(inset(75, 57)));
        Assert.False(IsRed(inset(75, 52)));
        Assert.True(IsRed(circle(62, 75)));
        Assert.False(IsRed(circle(58, 75)));
    }

    /// <summary>Radii that would overlap are scaled down together: 100 all round on a 50 box is a circle, not a fold.</summary>
    [Fact]
    public void OverlappingRadiiAreScaledDown()
    {
        var at = Render(Square("inset(0 round 100px)"));

        Assert.True(IsRed(at(75, 75)));
        Assert.True(IsRed(at(52, 75)), "the circle's left extreme was cut");
        Assert.False(IsRed(at(56, 56)), "the corner is inside the circle");
    }

    /// <summary>
    /// circle: a radius, or closest-side by default (25 on the 50 box), or farthest-side; placed by a
    /// position — the centre by default, a percentage pair, or edge keywords in either order.
    /// </summary>
    [Theory]
    [InlineData("circle(20px)", 75, 57, true, 75, 54, false)]
    [InlineData("circle()", 51, 75, true, 52, 52, false)]
    [InlineData("circle(farthest-side at 0 0)", 75, 75, true, 99, 99, false)]
    [InlineData("circle(10px at 25% 75%)", 62, 87, true, 75, 75, false)]
    [InlineData("circle(10px at right bottom)", 97, 97, true, 75, 75, false)]
    [InlineData("circle(10px at bottom right)", 97, 97, true, 75, 75, false)]
    [InlineData("circle(10px at right 5px bottom 5px)", 93, 93, true, 80, 95, false)]
    [InlineData("circle(50%)", 52, 75, true, 52, 52, false)]
    public void ACircleIsPlacedAndSized(string clip, int redX, int redY, bool red, int whiteX, int whiteY, bool white)
    {
        var at = Render(Square(clip));

        Assert.Equal(red, IsRed(at(redX, redY)));
        Assert.Equal(white, IsRed(at(whiteX, whiteY)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>ellipse: two radii, each a length, a percentage of its axis, or closest-side / farthest-side along that axis.</summary>
    [Theory]
    [InlineData("ellipse(25px 10px)", 97, 75, true, 75, 63, false)]
    [InlineData("ellipse(25px 10px)", 75, 67, true, 62, 63, false)]
    [InlineData("ellipse(50% 20%)", 97, 75, true, 75, 63, false)]
    [InlineData("ellipse(closest-side farthest-side at 10px 25px)", 60, 55, true, 75, 75, false)]   // a position's lengths are from the box's origin
    public void AnEllipseHasTwoRadii(string clip, int redX, int redY, bool red, int whiteX, int whiteY, bool white)
    {
        var at = Render(Square(clip));

        Assert.Equal(red, IsRed(at(redX, redY)));
        Assert.Equal(white, IsRed(at(whiteX, whiteY)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>polygon: a triangle from the top's middle to the bottom corners keeps the middle and cuts the top corners.</summary>
    [Fact]
    public void APolygonClipsToItsVertices()
    {
        var at = Render(Square("polygon(50% 0, 100% 100%, 0 100%)"));

        Assert.True(IsRed(at(75, 55)));
        Assert.True(IsRed(at(75, 95)));
        Assert.False(IsRed(at(55, 55)));
        Assert.False(IsRed(at(95, 55)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A polygon's fill rule: a square with an inner square wound the same way is solid under nonzero
    /// and hollow under evenodd, which is written as <c>W*</c>.
    /// </summary>
    [Theory]
    [InlineData("nonzero", true, "W")]
    [InlineData("evenodd", false, "W*")]
    public void APolygonsFillRuleIsKept(string rule, bool centreRed, string op)
    {
        string clip = $"polygon({rule}, 0 0, 100% 0, 100% 100%, 0 100%, 0 0, 25% 25%, 75% 25%, 75% 75%, 25% 75%, 25% 25%)";
        var at = Render(Square(clip));
        string[] lines = Lines(Square(clip));

        Assert.Equal(centreRed, IsRed(at(75, 75)));
        Assert.True(IsRed(at(55, 55)));
        Assert.Contains(op, lines);
    }

    /// <summary>path(): SVG path data from the box's origin — a 25-unit square at the box's corner — under its fill rule.</summary>
    [Theory]
    [InlineData("path('M0 0 H25 V25 H0 Z')", "W")]
    [InlineData("path(evenodd, 'M0 0 H25 V25 H0 Z')", "W*")]
    public void APathIsFromTheBoxsOrigin(string clip, string op)
    {
        var at = Render(Square(clip));

        Assert.True(IsRed(at(60, 60)));
        Assert.False(IsRed(at(85, 85)));
        Assert.False(IsRed(at(60, 85)));
        Assert.Contains(op, Lines(Square(clip)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// The reference box: the element's box by default — a circle at 75, 75 in it is at 125, 125 on
    /// the page and misses the square — or the view-box, where the same circle sits on the square's
    /// centre; the keyword may come first.
    /// </summary>
    [Theory]
    [InlineData("circle(10px at 75px 75px)", false)]
    [InlineData("circle(10px at 75px 75px) view-box", true)]
    [InlineData("view-box circle(10px at 75px 75px)", true)]
    [InlineData("circle(10px at 75px 75px) fill-box", false)]
    public void TheGeometryBoxChoosesTheReferenceBox(string clip, bool centreRed)
    {
        var at = Render(Square(clip));

        Assert.Equal(centreRed, IsRed(at(75, 75)));
        Assert.False(IsRed(at(88, 75)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A group takes a basic-shape clip in its union box: the inset applies to the square inside it as to the square itself.</summary>
    [Fact]
    public void AContainerTakesABasicShapeClipInItsBox()
    {
        var at = Render(@"<g style=""clip-path: inset(10px)""><rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000""/></g>");

        Assert.True(IsRed(at(75, 75)));
        Assert.False(IsRed(at(55, 75)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>An image takes one in its place box: a circle of 25 on a 200 × 100 image keeps its middle and cuts its ends.</summary>
    [Fact]
    public void AnImageTakesABasicShapeClip()
    {
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";   // one red pixel
        var at = Render($@"<image href=""{png}"" width=""200"" height=""100"" style=""clip-path: circle(25px)"" preserveAspectRatio=""none""/>");

        Assert.True(IsRed(at(100, 50)));
        Assert.True(IsRed(at(90, 50)));
        Assert.False(IsRed(at(60, 50)));
        Assert.False(IsRed(at(140, 50)));
    }

    /// <summary>A shape with no area — an inset past the box, a circle of 0 — clips everything away: the square is not drawn.</summary>
    [Theory]
    [InlineData("inset(30px)")]
    [InlineData("circle(0)")]
    [InlineData("ellipse(0 10px)")]
    public void AShapeWithNoAreaClipsEverything(string clip)
    {
        var at = Render(Square(clip));

        Assert.False(IsRed(at(75, 75)));
        // The empty clip is a zero-size path, not a W with no path — which a reader may take as no clip at all.
        Assert.Contains("0 0 0 0 re", Lines(Square(clip)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>What cannot be read is reported and the square drawn unclipped: an unknown shape, a bad length, a bad geometry box, too few vertices, an unquoted path.</summary>
    [Theory]
    [InlineData("star(5px)", "not a basic shape")]
    [InlineData("inset(5em)", "cannot read")]
    [InlineData("circle(-5px)", "cannot read")]
    [InlineData("circle(10px) shape-box", "not a geometry box")]
    [InlineData("polygon(0 0, 100% 100%)", "three or more")]
    [InlineData("path(M0 0)", "quoted path")]
    [InlineData("inset(1px 2px 3px 4px 5px)", "one to four")]
    [InlineData("nonsense", "neither a url(#id) nor a basic shape")]
    public void AShapeThatCannotBeReadIsReported(string clip, string reason)
    {
        var at = Render(Square(clip));

        Assert.True(IsRed(at(51, 51)), "the square was clipped");
        Assert.Contains(reason, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A stroke-box is the fill box grown by half the stroke width: a 10-wide stroke on the 50-square
    /// makes it 45 to 105, so a 10 inset clips at 55 and 95 — (57, 75) and (93, 75) are kept, (53, 75)
    /// cut — where the fill-box clips at 60 and 90 and cuts both. An element that strokes nothing has
    /// the fill box.
    /// </summary>
    [Theory]
    [InlineData(@"stroke=""#ff0000"" stroke-width=""10""", "inset(10px) stroke-box", true, false)]
    [InlineData(@"stroke=""#ff0000"" stroke-width=""10""", "inset(10px)", false, false)]
    [InlineData(@"stroke=""none"" stroke-width=""10""", "inset(10px) stroke-box", false, false)]
    public void AStrokeBoxGrowsTheBoxByHalfTheStroke(string stroke, string clip, bool at57, bool at53)
    {
        var at = Render($@"<rect x=""50"" y=""50"" width=""50"" height=""50"" fill=""#ff0000"" {stroke} style=""clip-path: {clip}""/>");

        Assert.True(IsRed(at(75, 75)));
        Assert.Equal(at57, IsRed(at(57, 75)));
        Assert.Equal(at57, IsRed(at(93, 75)));   // grown on the right as on the left
        Assert.Equal(at53, IsRed(at(53, 75)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Hostile input: a polygon of ten thousand vertices is one path, written once, in the time a path takes.</summary>
    [Fact]
    public void AHostilePolygonOfManyVerticesIsOnePath()
    {
        string vertices = string.Join(", ", Enumerable.Range(0, 10000).Select(i => $"{i % 100}% {i % 7 * 14}%"));
        string[] lines = Lines(Square($"polygon({vertices})"));

        Assert.Equal(1, lines.Count(l => l == "W"));
        Assert.True(lines.Count(l => l.EndsWith(" l", StringComparison.Ordinal)) >= 9999);
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsRed((int R, int G, int B) p) => p.R > 250 && p.G < 5;

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int R, int G, int B)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    private static string[] Lines(string body)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Convert(body)));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()).Split('\n').Select(l => l.Trim()).ToArray();
    }
}
