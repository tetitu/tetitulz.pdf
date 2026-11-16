// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> draws a clipPath that one path under one rule cannot say — text in
/// it, a child with a <c>clip-path</c> of its own, children whose <c>clip-rule</c>s differ (SVG 1.1
/// §14.3.5) — as a luminosity soft mask: its children filled white, each under its own rule.
/// </summary>
/// <remarks>
/// Text in a clipPath was reported and left out, a child's clip-path reported and left out, and mixed
/// rules approximated under the first child's rule. Every case clips a red 200 × 200 square on a white
/// page: a point the clip keeps is red, one it cuts is the white page.
/// </remarks>
public class SvgClipMaskTests
{
    private const string Square = @"<rect width=""200"" height=""200"" fill=""#ff0000"" clip-path=""url(#c)""/>";

    /// <summary>A rect drawn twice over itself, same winding: a hole under evenodd, none under nonzero.</summary>
    private static string Doubled(int x, string rule)
        => $@"<path d=""M{x},0 h40 v40 h-40 z M{x + 10},10 h20 v20 h-20 z"" clip-rule=""{rule}""/>";

    /// <summary>
    /// Text clips: a 200-point Helvetica <c>I</c> keeps its stem and cuts everything else, and says
    /// nothing. Its <c>fill</c> is not read — the clip is geometry — so <c>none</c> clips the same.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(@" fill=""none""")]
    [InlineData(@" fill-opacity=""0.5""")]
    public void TextInAClipPathClips(string fill)
    {
        var at = Render($@"<defs><clipPath id=""c""><text x=""50"" y=""150"" font-size=""200""{fill}>I</text></clipPath></defs>" + Square);

        AssertRed(at(78, 80));      // the stem, about x 50 + 27.8, between the baseline and the cap
        AssertWhite(at(20, 80));    // left of the glyph
        AssertWhite(at(78, 170));   // below the baseline
        AssertWhite(at(150, 80));   // right of it
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Each child under its own rule: the doubled rect with evenodd has a hole at its centre, the one
    /// with nonzero has none — whichever comes first. Under one rule, the first child's, the second
    /// followed the first.
    /// </summary>
    [Theory]
    [InlineData("evenodd", "nonzero")]
    [InlineData("nonzero", "evenodd")]
    public void MixedClipRulesAreEachTheirOwn(string firstRule, string secondRule)
    {
        var at = Render($@"<defs><clipPath id=""c"">{Doubled(0, firstRule)}{Doubled(100, secondRule)}</clipPath></defs>" + Square);

        int holeX = firstRule == "evenodd" ? 20 : 120, solidX = firstRule == "evenodd" ? 120 : 20;
        AssertWhite(at(holeX, 20));
        AssertRed(at(solidX, 20));
        AssertRed(at(5, 20));       // both rings are kept
        AssertRed(at(105, 20));
        AssertWhite(at(60, 20));    // between the shapes, outside the clip
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A masked clipPath's own transform applies: translate(100, 0) moves the doubled rects right.</summary>
    [Fact]
    public void AMaskedClipPathsTransformApplies()
    {
        var at = Render($@"<defs><clipPath id=""c"" transform=""translate(100, 0)"">{Doubled(0, "evenodd")}{Doubled(50, "nonzero")}</clipPath></defs>" + Square);

        AssertRed(at(105, 20));
        AssertWhite(at(5, 20));
    }

    /// <summary>
    /// A masked clipPath in box units is measured in the element's box: text at x 0.5 of a 100-wide
    /// square starts at 50 — on a 200-wide one it would start at 100.
    /// </summary>
    [Fact]
    public void AMaskedClipPathInBoxUnitsIsInTheBox()
    {
        var at = Render(
            @"<defs><clipPath id=""c"" clipPathUnits=""objectBoundingBox""><text x=""0.5"" y=""0.75"" font-size=""1"">I</text></clipPath></defs>" +
            @"<rect width=""100"" height=""100"" fill=""#ff0000"" clip-path=""url(#c)""/>");

        AssertRed(at(64, 40));      // the stem of a 100-point I at x 50: 50 + 13.9
        AssertWhite(at(114, 40));   // where a 200-point I from x 100 would put it
    }

    /// <summary>
    /// A filter, a mask or a group's opacity on a masked clipPath's content does not count: the
    /// clip is geometry, so each is left out and the kept region is solid red. The mask is empty —
    /// applied, it would hide the shape; a mask that draws would draw white here and hide nothing.
    /// </summary>
    [Theory]
    [InlineData(@"<rect width=""40"" height=""40"" filter=""url(#f)""/>")]
    [InlineData(@"<rect width=""40"" height=""40"" mask=""url(#m)""/>")]
    [InlineData(@"<g opacity=""0.5""><rect width=""40"" height=""40""/></g>")]
    public void EffectsDoNotCountInTheMask(string child)
    {
        var at = Render(
            @"<defs><filter id=""f""><feFlood flood-color=""#000000""/></filter><mask id=""m""/>" +
            $@"<clipPath id=""c"">{child}<text x=""100"" y=""40"" font-size=""40"">I</text></clipPath></defs>" + Square);

        AssertRed(at(20, 20));
        AssertWhite(at(60, 20));
    }

    /// <summary>A used shape's own clip-path applies inside a clipPath, as a direct child's does.</summary>
    [Fact]
    public void AUsedShapesClipPathApplies()
    {
        var at = Render(
            @"<defs><clipPath id=""c2""><rect width=""10"" height=""10""/></clipPath>" +
            @"<rect id=""r"" width=""100"" height=""100"" clip-path=""url(#c2)""/>" +
            @"<clipPath id=""c""><use href=""#r""/></clipPath></defs>" + Square);

        AssertRed(at(5, 5));
        AssertWhite(at(50, 50));
    }

    /// <summary>
    /// What is left: a used group is not looked into, so text or a clipped shape reached only through
    /// it stays a path clip, and is reported as before.
    /// </summary>
    [Theory]
    [InlineData(@"<text x=""50"" y=""150"" font-size=""200"">I</text>", "text in a clipPath")]
    [InlineData(@"<rect width=""100"" height=""100"" clip-path=""url(#c2)""/>", "inside a clipPath")]
    public void ThroughAUsedGroupTheClipIsStillReported(string content, string report)
    {
        Render(
            @"<defs><clipPath id=""c2""><rect width=""10"" height=""10""/></clipPath>" +
            $@"<g id=""g"">{content}<rect x=""150"" width=""20"" height=""20""/></g>" +
            @"<clipPath id=""c""><use href=""#g""/></clipPath></defs>" + Square);

        Assert.Contains(report, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A shape's opacity, stroke and marker do not count in a clip: the kept region is solid red.</summary>
    [Fact]
    public void PaintPropertiesDoNotCountInTheMask()
    {
        var at = Render(
            @"<defs><marker id=""m"" markerWidth=""50"" markerHeight=""50""><rect width=""50"" height=""50""/></marker>" +
            @"<clipPath id=""c""><rect width=""40"" height=""40"" opacity=""0.5"" fill=""none"" stroke=""#0000ff"" stroke-width=""40"" clip-rule=""evenodd""/>" +
            @"<path d=""M100,20 L140,20"" stroke=""#0000ff"" stroke-width=""40"" marker-end=""url(#m)""/></clipPath></defs>" + Square);

        AssertRed(at(20, 20));      // the rect, its fill none and its opacity not read
        AssertWhite(at(60, 20));    // its stroke would reach here
        AssertWhite(at(120, 20));   // the line has no area, and its stroke does not count
        AssertWhite(at(150, 20));   // nor does its marker
    }

    /// <summary>
    /// A filled and stroked shape's opacity — a transparency group on the page since batch 83 — does
    /// not count in the mask either: the kept region is solid red, not red at half strength, and the
    /// stroke still does not reach past the rect.
    /// </summary>
    [Fact]
    public void AFilledAndStrokedShapesOpacityDoesNotCountInTheMask()
    {
        var at = Render(
            @"<defs><clipPath id=""c""><rect width=""40"" height=""40"" opacity=""0.5"" fill=""#0000ff"" stroke=""#0000ff"" stroke-width=""40"" clip-rule=""evenodd""/>" +
            @"<rect x=""100"" width=""40"" height=""40""/></clipPath></defs>" + Square);

        AssertRed(at(20, 20));
        AssertRed(at(120, 20));    // the mixed rules make the clip a mask
        AssertWhite(at(60, 20));   // the stroke would reach here
    }

    /// <summary>A child's own clip-path applies inside a clipPath: the 100-square cut to the 10-square.</summary>
    [Fact]
    public void AChildsClipPathAppliesInsideAClipPath()
    {
        var at = Render(
            @"<defs><clipPath id=""c2""><rect width=""10"" height=""10""/></clipPath>" +
            @"<clipPath id=""c""><rect width=""100"" height=""100"" clip-path=""url(#c2)""/></clipPath></defs>" + Square);

        AssertRed(at(5, 5));
        AssertWhite(at(50, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Two masked clipPaths in one chain intersect: each has mixed rules, the first over 0–100 and the
    /// second over 50–150; only 50–100 is kept.
    /// </summary>
    [Fact]
    public void TwoMaskedClipPathsInAChainIntersect()
    {
        var at = Render(
            @"<defs><clipPath id=""c"" clip-path=""url(#c2)""><rect width=""50"" height=""100"" clip-rule=""evenodd""/><rect x=""50"" width=""50"" height=""100""/></clipPath>" +
            @"<clipPath id=""c2""><rect x=""50"" width=""50"" height=""150"" clip-rule=""evenodd""/><rect x=""100"" width=""50"" height=""150""/></clipPath></defs>" + Square);

        AssertRed(at(75, 50));
        AssertWhite(at(25, 50));
        AssertWhite(at(125, 50));
    }

    /// <summary>A path clip and a masked clip in one chain intersect too: the path's 0–100 and the text's stem.</summary>
    [Fact]
    public void APathClipAndAMaskedClipIntersect()
    {
        var at = Render(
            @"<defs><clipPath id=""c"" clip-path=""url(#c2)""><rect width=""100"" height=""100""/></clipPath>" +
            @"<clipPath id=""c2""><text x=""50"" y=""150"" font-size=""200"">I</text></clipPath></defs>" + Square);

        AssertRed(at(78, 80));
        AssertWhite(at(78, 120));   // in the stem, below the path clip
        AssertWhite(at(20, 80));
    }

    /// <summary>A use of a text is text: the clipPath is a mask, and nothing is reported.</summary>
    [Fact]
    public void AUsedTextClips()
    {
        var at = Render(
            @"<defs><text id=""t"" x=""50"" y=""150"" font-size=""200"">I</text>" +
            @"<clipPath id=""c""><use href=""#t""/></clipPath></defs>" + Square);

        AssertRed(at(78, 80));
        AssertWhite(at(20, 80));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Shapes under one rule stay a path clip, <c>W n</c>, not a mask: the vector clip is kept where it can be.</summary>
    [Fact]
    public void OneRuleStaysAPathClip()
    {
        string content = Content(@"<defs><clipPath id=""c""><rect width=""40"" height=""40""/><circle cx=""100"" cy=""20"" r=""20""/></clipPath></defs>" + Square);
        string masked = Content(@"<defs><clipPath id=""c"">" + Doubled(0, "evenodd") + Doubled(100, "nonzero") + "</clipPath></defs>" + Square);

        Assert.Contains("\nW\n", content.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain(" gs", content, StringComparison.Ordinal);
        Assert.Contains(" gs", masked, StringComparison.Ordinal);   // the control: the mixed one is a mask
    }

    /// <summary>
    /// A hostile clipPath whose child names the clipPath itself: each mask draws the child, which
    /// opens the mask again. The nesting ceiling ends it, reported, and the conversion returns.
    /// </summary>
    [Fact]
    public void AHostileSelfReferencingClipPathEnds()
    {
        Convert(@"<defs><clipPath id=""c""><rect width=""100"" height=""100"" clip-path=""url(#c)""/></clipPath></defs>" + Square);

        Assert.Contains("nesting", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
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
    }

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static string Content(string body)
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(Convert(body)));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static Func<int, int, (int R, int G, int B)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
