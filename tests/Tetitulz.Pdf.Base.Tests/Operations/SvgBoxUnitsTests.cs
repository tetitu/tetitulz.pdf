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
/// <see cref="SvgToPdfConverter"/> applies what is measured in the element's bounding box (SVG 1.1
/// §14.3.5, §14.4): a clipPath's and a mask's content in <c>objectBoundingBox</c> units, and a mask's
/// region, in either units, the default one included. A shape's or a text's box is known once it is
/// drawn, so such an element is drawn first and its clip or mask set around it after.
/// </summary>
/// <remarks>
/// Each was reported and the element drawn unclipped or unmasked. A group's box, the union of its
/// children's, is collected since batch 47 (<see cref="SvgGroupBoxTests"/>).
/// </remarks>
public class SvgBoxUnitsTests
{
    private const string RightHalf = @"<rect x=""100"" width=""100"" height=""100"" fill=""#ff0000""";

    /// <summary>A clip of half the box, (0, 0)–(0.5, 1), keeps the left half of a rect at 100–200: 100–150.</summary>
    [Fact]
    public void ABoundingBoxClipIsInTheShapesBox()
    {
        var at = Render(@"<clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath>" +
                        RightHalf + @" clip-path=""url(#c)""/>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A curved path's box is its geometry, not its control points' hull: the bowl
    /// <c>M0 0 C0 200 200 200 200 0</c> reaches y = 150, though its control points reach 200. A clip
    /// of the box's lower half, (0, 0.5)–(1, 1), is then 75–150; over the hull it was 100–200, and
    /// the bowl's band from 75 to 100 was clipped away.
    /// </summary>
    [Fact]
    public void ACurvedPathsBoxIsItsGeometry_NotItsControlPoints()
    {
        var at = Render(@"<clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect y=""0.5"" width=""1"" height=""0.5""/></clipPath>" +
                        @"<path d=""M0 0 C0 200 200 200 200 0 Z"" fill=""#ff0000"" clip-path=""url(#c)""/>");

        AssertColour((255, 0, 0), at(100, 85));      // inside the bowl, below half its true height
        AssertColour((255, 0, 0), at(100, 140));
        AssertColour((255, 255, 255), at(100, 65));  // above the clip
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// The extrema: a bowl turns once in y (at its middle), a monotone curve not at all, and an S
    /// curve twice in x — each turning point handed over, the ends left to the caller.
    /// </summary>
    [Fact]
    public void ACubicsTurningPointsAreItsExtrema()
    {
        var bowl = new System.Collections.Generic.List<(float X, float Y)>();
        SvgToPdfConverter.TrackCubicExtrema(0, 0, 0, 200, 200, 200, 200, 0, (x, y) => bowl.Add((x, y)));
        var (bx, by) = Assert.Single(bowl);
        Assert.Equal(100f, bx, 3);
        Assert.Equal(150f, by, 3);

        var monotone = new System.Collections.Generic.List<(float, float)>();
        SvgToPdfConverter.TrackCubicExtrema(0, 0, 10, 10, 20, 20, 30, 30, (x, y) => monotone.Add((x, y)));
        Assert.Empty(monotone);

        var s = new System.Collections.Generic.List<(float X, float Y)>();
        SvgToPdfConverter.TrackCubicExtrema(0, 0, 400, 0, -300, 100, 100, 100, (x, y) => s.Add((x, y)));
        Assert.Equal(2, s.Count);
        Assert.True(s.Max(p => p.X) > 100 && s.Min(p => p.X) < 0, "the S curve's two turning points in x lie outside its ends");
    }

    /// <summary>A mask whose content is in the box, white over half of it, shows the left half.</summary>
    [Fact]
    public void ABoundingBoxMaskIsInTheShapesBox()
    {
        var at = Render(@"<mask id=""m"" maskContentUnits=""objectBoundingBox""><rect width=""0.5"" height=""1"" fill=""white""/></mask>" +
                        RightHalf + @" mask=""url(#m)""/>");

        AssertColour((255, 0, 0), at(125, 50));
        AssertColour((255, 255, 255), at(175, 50));
    }

    /// <summary>
    /// A mask's region clips its content: white over everything, but only 0–150 of it in the region,
    /// in user space here and in the box's fractions there.
    /// </summary>
    [Theory]
    [InlineData(@"maskUnits=""userSpaceOnUse"" x=""0"" y=""0"" width=""150"" height=""200""")]
    [InlineData(@"x=""0"" y=""0"" width=""0.5"" height=""1""")]
    public void AMasksRegionClipsItsContent(string region)
    {
        var at = Render($@"<mask id=""m"" {region}><rect width=""200"" height=""200"" fill=""white""/></mask>" +
                        RightHalf + @" mask=""url(#m)""/>");

        AssertColour((255, 0, 0), at(110, 50));
        AssertColour((255, 255, 255), at(175, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// The default region is the box widened by a tenth on each side: a stroke 40 wide round a rect
    /// from 50 to 150 reaches 30 and 170, and the region stops it at 40 and 160. Inside it shows.
    /// </summary>
    [Fact]
    public void TheDefaultRegionStopsAWideStroke()
    {
        var at = Render(@"<mask id=""m""><rect width=""200"" height=""200"" fill=""white""/></mask>" +
                        @"<rect x=""50"" y=""50"" width=""100"" height=""100"" fill=""none"" stroke=""#000000"" stroke-width=""40"" mask=""url(#m)""/>");

        AssertColour((0, 0, 0), at(45, 100));
        AssertColour((255, 255, 255), at(35, 100));
        AssertColour((0, 0, 0), at(155, 100));
        AssertColour((255, 255, 255), at(165, 100));
    }

    /// <summary>
    /// Text has a box too: a clip of its left half keeps "H" and cuts the second "H" of "HH" at 50
    /// from x = 10 (72.2 wide).
    /// </summary>
    [Fact]
    public void TextsBoxServesAClip()
    {
        var at = Render(@"<clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath>" +
                        @"<text x=""10"" y=""100"" font-size=""50"" font-family=""monospace"" font-weight=""bold"" fill=""#000000"" clip-path=""url(#c)"">HH</text>");

        Assert.Contains(Enumerable.Range(10, 25), x => at(x, 85).Item1 < 100);          // the first H is drawn
        Assert.DoesNotContain(Enumerable.Range(45, 25), x => at(x, 85).Item1 < 100);    // the second is cut
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertColour((int R, int G, int B) expected, (int R, int G, int B) actual)
    {
        Assert.InRange(actual.R, expected.R - 12, expected.R + 12);
        Assert.InRange(actual.G, expected.G - 12, expected.G + 12);
        Assert.InRange(actual.B, expected.B - 12, expected.B + 12);
    }

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int, int, int)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
