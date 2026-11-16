// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> keeps an <c>&lt;a&gt;</c>'s link (SVG 1.1 §17.1) as a PDF link
/// annotation over what the anchor draws, with a URI action.
/// </summary>
/// <remarks>
/// Batch 24 drew an anchor's content and reported its link as not kept, because the one-page skeleton
/// had no room for an annotation. The writer now takes annotation dictionaries as objects of their
/// own. The rectangle is the bounds of what the anchor drew, in page space; only an http, https or
/// mailto link is kept, since a <c>javascript:</c> or <c>file:</c> one is not a link to follow.
/// </remarks>
public class SvgLinkTests
{
    [Fact]
    public void AnAnchorIsALinkOverWhatItDraws()
    {
        var links = Links(@"<a href=""https://example.org/""><rect x=""10"" y=""20"" width=""30"" height=""40""/></a>", flip: false);

        var (rect, uri) = Assert.Single(links);
        Assert.Equal(new[] { 10.0, 20, 40, 60 }, rect);
        Assert.Equal("https://example.org/", uri);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Flipped, as by default: the rect's y of 20 to 60 is page y 140 to 180.</summary>
    [Fact]
    public void TheRectangleIsInPageSpace()
    {
        var (rect, _) = Assert.Single(Links(@"<a href=""https://example.org/""><rect x=""10"" y=""20"" width=""30"" height=""40""/></a>", flip: true));

        Assert.Equal(new[] { 10.0, 140, 40, 180 }, rect);
    }

    /// <summary>A linked text's rectangle is its run: 10 to 23.34 across (AB at size 10), baseline to size above.</summary>
    [Fact]
    public void ALinkedTextIsCoveredByItsRun()
    {
        var (rect, _) = Assert.Single(Links(@"<a href=""mailto:someone@example.org""><text x=""10"" y=""20"" font-size=""10"">AB</text></a>", flip: false));

        Assert.Equal(10, rect[0], 2);
        Assert.Equal(10, rect[1], 2);
        Assert.Equal(23.34, rect[2], 2);
        Assert.Equal(20, rect[3], 2);
    }

    [Fact]
    public void TwoAnchorsAreTwoLinks()
    {
        var links = Links(
            @"<a href=""https://a.example/""><rect width=""10"" height=""10""/></a>" +
            @"<a xlink:href=""https://b.example/""><rect x=""50"" width=""10"" height=""10""/></a>", flip: false,
            rootAttributes: @" xmlns:xlink=""http://www.w3.org/1999/xlink""");

        Assert.Equal(new[] { "https://a.example/", "https://b.example/" }, links.Select(l => l.Uri));
    }

    /// <summary>What is not a link to follow is refused and reported; the content is still drawn.</summary>
    [Theory]
    [InlineData("javascript:alert(1)", "javascript")]
    [InlineData("file:///etc/passwd", "file")]
    [InlineData("#section", "no element has that id")]
    public void ALinkThatIsNotKeptIsReported(string href, string named)
    {
        var links = Links($@"<a href=""{href}""><rect width=""10"" height=""10""/></a>", flip: false);

        Assert.Empty(links);
        Assert.Contains(named, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A link inside the drawing (batch 78) is a link annotation over the anchor with a <c>/GoTo</c>
    /// to the page at the target's top-left corner (<c>/XYZ</c>), wherever the target is drawn — before
    /// the anchor or after it, since the annotation is written once the drawing is done. The target's
    /// box is every point it mapped to the page: the rect's corners, (10, 20) to (40, 60), so its top
    /// left is (10, 60) unflipped.
    /// </summary>
    [Theory]
    [InlineData(@"<rect id=""t"" x=""10"" y=""20"" width=""30"" height=""40""/><a href=""#t""><rect x=""100"" y=""100"" width=""10"" height=""10""/></a>")]
    [InlineData(@"<a href=""#t""><rect x=""100"" y=""100"" width=""10"" height=""10""/></a><rect id=""t"" x=""10"" y=""20"" width=""30"" height=""40""/>")]
    public void ALinkInsideTheDrawingGoesToItsTarget(string body)
    {
        var (rect, left, top) = Assert.Single(GoTos(body, flip: false));

        Assert.Equal(new[] { 100.0, 100, 110, 110 }, rect);
        Assert.Equal(10, left, 2);
        Assert.Equal(60, top, 2);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>An <c>xlink:href</c> names a target as <c>href</c> does.</summary>
    [Fact]
    public void AnXlinkHrefInsideTheDrawingGoesToItsTarget()
    {
        var (_, left, top) = Assert.Single(GoTos(
            @"<rect id=""t"" x=""10"" y=""20"" width=""30"" height=""40""/><a xlink:href=""#t""><rect width=""10"" height=""10""/></a>",
            flip: false, rootAttributes: @" xmlns:xlink=""http://www.w3.org/1999/xlink"""));

        Assert.Equal(10, left, 2);
        Assert.Equal(60, top, 2);
    }

    /// <summary>
    /// The destination is where the target is first drawn: a copy a later <c>&lt;use&gt;</c> makes
    /// keeps the id and is drawn too, and must not move it.
    /// </summary>
    [Fact]
    public void ALaterCopyOfTheTargetDoesNotMoveTheDestination()
    {
        var (_, left, top) = Assert.Single(GoTos(
            @"<rect id=""t"" x=""10"" y=""20"" width=""30"" height=""40""/><use href=""#t"" x=""100"" y=""100""/><a href=""#t""><rect width=""10"" height=""10""/></a>",
            flip: false));

        Assert.Equal(10, left, 2);
        Assert.Equal(60, top, 2);
    }

    /// <summary>Flipped, as by default: the target's top, y = 20, is page y 180; a transformed target's box follows its transform.</summary>
    [Theory]
    [InlineData("", 10.0, 180.0)]
    [InlineData(@" transform=""translate(5 5)""", 15.0, 175.0)]
    public void TheDestinationIsInPageSpace(string transform, double left, double top)
    {
        var (_, l, t) = Assert.Single(GoTos(@"<rect id=""t""" + transform + @" x=""10"" y=""20"" width=""30"" height=""40""/><a href=""#t""><rect width=""10"" height=""10""/></a>", flip: true));

        Assert.Equal(left, l, 2);
        Assert.Equal(top, t, 2);
    }

    /// <summary>A target that is never drawn — a definition — leaves no destination: the link is not kept, reported.</summary>
    [Fact]
    public void ALinkToAnUndrawnTargetIsReported()
    {
        var links = GoTos(@"<defs><rect id=""t"" width=""10"" height=""10""/></defs><a href=""#t""><rect width=""10"" height=""10""/></a>", flip: false);

        Assert.Empty(links);
        Assert.Contains("was not drawn", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A target inside a linked anchor still widens that anchor's rectangle — the target's bounds are
    /// folded outward once captured — and the anchor to it still finds them: two annotations.
    /// </summary>
    [Fact]
    public void ATargetInsideAnAnchorCountsForTheAnchor()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" +
            @"<a href=""https://example.org/""><rect id=""t"" x=""10"" y=""20"" width=""30"" height=""40""/><rect x=""60"" y=""60"" width=""5"" height=""5""/></a>" +
            @"<a href=""#t""><rect x=""100"" y=""100"" width=""10"" height=""10""/></a></svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));

        Assert.Equal(2, annots.Count);
        var outer = Assert.IsType<PdfDictionary>(doc.ResolveObject(annots[0]));
        Assert.Equal(new[] { 10.0, 20, 65, 65 }, Assert.IsType<PdfArray>(doc.ResolveObject(outer[PdfName.Rect])).Select(n => ((PdfNumber)n).DoubleValue));
        var inner = Assert.IsType<PdfDictionary>(doc.ResolveObject(annots[1]));
        Assert.Equal(PdfName.GoTo, ((PdfDictionary)doc.ResolveObject(inner[PdfName.A]))[PdfName.S]);
    }

    /// <summary>A linked path is covered by its points: (10, 10) to (30, 40).</summary>
    [Fact]
    public void ALinkedPathIsCoveredByItsPoints()
    {
        var (rect, _) = Assert.Single(Links(@"<a href=""https://example.org/""><path d=""M 10 10 L 30 40"" stroke=""black""/></a>", flip: false));

        Assert.Equal(new[] { 10.0, 10, 30, 40 }, rect);
    }

    /// <summary>
    /// A linked circle or ellipse is covered by its box: centre (50, 40), radii 20 and 10. Its link was
    /// the centre point alone, since the unrotated ellipse was mapped by arithmetic from its centre.
    /// </summary>
    [Theory]
    [InlineData(@"<ellipse cx=""50"" cy=""40"" rx=""20"" ry=""10""/>", 30.0, 30.0, 70.0, 50.0)]
    [InlineData(@"<circle cx=""50"" cy=""40"" r=""10""/>", 40.0, 30.0, 60.0, 50.0)]
    public void ALinkedEllipseIsCoveredByItsBox(string shape, double minX, double minY, double maxX, double maxY)
    {
        var (rect, _) = Assert.Single(Links(@"<a href=""https://example.org/"">" + shape + "</a>", flip: false));

        Assert.Equal(new[] { minX, minY, maxX, maxY }, rect);
    }

    /// <summary>A linked image is covered by its box: 20 × 10 at (5, 5).</summary>
    [Fact]
    public void ALinkedImageIsCoveredByItsBox()
    {
        const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAAEElEQVR4nGP4z8DAwMDAAAAN/QH/jqhOTAAAAABJRU5ErkJggg==";
        var (rect, _) = Assert.Single(Links(
            $@"<a href=""https://example.org/""><image href=""data:image/png;base64,{Png}"" x=""5"" y=""5"" width=""20"" height=""10""/></a>", flip: false));

        Assert.Equal(new[] { 5.0, 5, 25, 15 }, rect);

        // Unclipped, the image's own corners are what the link covers: no clip rect stands in.
        var (visible, _) = Assert.Single(Links(
            $@"<a href=""https://example.org/""><image href=""data:image/png;base64,{Png}"" x=""5"" y=""5"" width=""20"" height=""10"" overflow=""visible""/></a>", flip: false));
        Assert.Equal(new[] { 5.0, 5, 25, 15 }, visible);
    }

    /// <summary>
    /// A nested anchor is a link of its own, and the outer one covers what both drew: the outer's
    /// rect spans (0, 0) to (60, 10), the inner's (50, 0) to (60, 10).
    /// </summary>
    [Fact]
    public void ANestedAnchorCountsForTheOuterOne()
    {
        var links = Links(
            @"<a href=""https://outer.example/""><rect width=""10"" height=""10""/>" +
            @"<a href=""https://inner.example/""><rect x=""50"" width=""10"" height=""10""/></a></a>", flip: false);

        var outer = Assert.Single(links, l => l.Uri == "https://outer.example/");
        var inner = Assert.Single(links, l => l.Uri == "https://inner.example/");
        Assert.Equal(new[] { 0.0, 0, 60, 10 }, outer.Rect);
        Assert.Equal(new[] { 50.0, 0, 60, 10 }, inner.Rect);
    }

    /// <summary>A URI is ASCII: anything else is percent-encoded as UTF-8 (Hebrew shin is D7 A9).</summary>
    [Fact]
    public void ANonAsciiLinkIsPercentEncoded()
    {
        var (_, uri) = Assert.Single(Links("<a href=\"https://example.org/ש\"><rect width=\"1\" height=\"1\"/></a>", flip: false));

        Assert.Equal("https://example.org/%D7%A9", uri);
    }

    /// <summary>An anchor that draws nothing makes no link.</summary>
    [Fact]
    public void AnEmptyAnchorMakesNoLink()
    {
        Assert.Empty(Links(@"<a href=""https://example.org/""></a><rect width=""1"" height=""1""/>", flip: false));
    }

    /// <summary>
    /// A link round a curved path covers the curve, not its control points' hull: the bowl
    /// <c>M0 0 C0 200 200 200 200 0</c> reaches y = 150 though its control points reach 200.
    /// </summary>
    [Fact]
    public void ALinkRoundACurve_CoversTheCurveNotItsControlPoints()
    {
        var (rect, _) = Assert.Single(Links(@"<a href=""https://example.org/""><path d=""M0 0 C0 200 200 200 200 0""/></a>", flip: false));
        Assert.Equal(new[] { 0.0, 0, 200, 150 }, rect);

        // Flipped, the page's turning point is the mapped curve's (y = 200 − 150), not the user
        // space's carried over unmapped.
        var (flipped, _) = Assert.Single(Links(@"<a href=""https://example.org/""><path d=""M0 0 C0 200 200 200 200 0""/></a>", flip: true));
        Assert.Equal(new[] { 0.0, 50, 200, 200 }, flipped);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The page's link annotations with a <c>/GoTo</c> action: the rectangle and the <c>/XYZ</c> destination's left and top.</summary>
    private static (double[] Rect, double Left, double Top)[] GoTos(string body, bool flip, string rootAttributes = "")
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg""" + rootAttributes + @" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = flip });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        if (!(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) is PdfArray annots)) return Array.Empty<(double[], double, double)>();
        return annots.Select(a =>
        {
            var annot = Assert.IsType<PdfDictionary>(doc.ResolveObject(a));
            var rect = Assert.IsType<PdfArray>(doc.ResolveObject(annot[PdfName.Rect]));
            var action = Assert.IsType<PdfDictionary>(doc.ResolveObject(annot[PdfName.A]));
            Assert.Equal(PdfName.GoTo, action[PdfName.S]);
            var dest = Assert.IsType<PdfArray>(doc.ResolveObject(action[PdfName.D]));
            // [page /XYZ left top zoom]: the page is this page, the zoom is left as it is.
            Assert.Same(doc.Pages[0].Dictionary, doc.ResolveObject(dest[0]));
            Assert.Equal(PdfName.XYZ, dest[1]);
            Assert.IsType<PdfNull>(dest[4]);
            return (rect.Select(n => Math.Round(((PdfNumber)n).DoubleValue, 2)).ToArray(),
                ((PdfNumber)dest[2]).DoubleValue, ((PdfNumber)dest[3]).DoubleValue);
        }).ToArray();
    }

    private static (double[] Rect, string Uri)[] Links(string body, bool flip, string rootAttributes = "")
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg""" + rootAttributes + @" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = flip });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        if (!(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) is PdfArray annots)) return Array.Empty<(double[], string)>();
        return annots.Select(a =>
        {
            Assert.IsType<PdfReference>(a);   // an annotation is an indirect object (Table 30)
            var annot = Assert.IsType<PdfDictionary>(doc.ResolveObject(a));
            Assert.Equal(PdfName.Link, annot[PdfName.Subtype]);
            var rect = Assert.IsType<PdfArray>(doc.ResolveObject(annot[PdfName.Rect]));
            var action = Assert.IsType<PdfDictionary>(doc.ResolveObject(annot[PdfName.A]));
            var uri = Assert.IsType<PdfString>(doc.ResolveObject(action[PdfName.URI]));
            return (rect.Select(n => Math.Round(((PdfNumber)n).DoubleValue, 2)).ToArray(), uri.GetText());
        }).ToArray();
    }
}
