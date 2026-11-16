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
/// <see cref="SvgToPdfConverter"/> applies <c>mask</c> (SVG 1.1 §14.4) as a luminosity soft mask
/// (ISO 32000-1 §11.6.5.2): the mask's content is a Form XObject whose luminance, over black, is the
/// alpha the element is painted through.
/// </summary>
/// <remarks>
/// A mask was reported and the element drawn unmasked, so a faded edge or a cut-out came out as the
/// whole shape. The mask below is white over the left half of a 100 × 100 rect.
/// </remarks>
public class SvgMaskTests
{
    private const string HalfMask = @"<mask id=""m""><rect width=""50"" height=""100"" fill=""white""/></mask>";

    [Fact]
    public void AMaskIsALuminositySoftMask()
    {
        using var doc = Open(HalfMask + @"<rect width=""100"" height=""100"" fill=""red"" mask=""url(#m)""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int gs = Array.FindIndex(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.True(gs >= 0 && gs < Array.IndexOf(page, "f"), "the mask was not set before the element");
        var smask = Assert.IsType<PdfDictionary>(doc.ResolveObject(State(doc, page[gs])[new PdfName("SMask")]));
        Assert.Equal("Luminosity", ((PdfName)smask[PdfName.S]).Value);
        var form = Assert.IsType<PdfStream>(doc.ResolveObject(smask[new PdfName("G")]));
        var group = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Group]));
        Assert.Equal("DeviceRGB", ((PdfName)group[new PdfName("CS")]).Value);
        Assert.Contains("f", Lines(form.GetDecodedData()));   // the mask's content is in the form
        Assert.Equal(1, page.Count(l => l == "f"));          // and not on the page
        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(Assert.IsType<PdfReference>(form[PdfName.Resources])));
        Assert.NotNull(resources[PdfName.Font]);            // the page's resources, shared
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Two elements masked by one mask are each masked, each in its own user space: the rect at 0
    /// shows on 0–50, the one moved to 100 on 100–150, and neither on the halves beyond.
    /// </summary>
    [Fact]
    public void TwoMaskedElementsAreEachMasked()
    {
        var at = Render(HalfMask +
            @"<rect width=""100"" height=""100"" fill=""#ff0000"" mask=""url(#m)""/>" +
            @"<rect width=""100"" height=""100"" fill=""#ff0000"" transform=""translate(100 0)"" mask=""url(#m)""/>");

        Assert.Equal((255, 0, 0), at(25, 50));
        Assert.Equal((255, 255, 255), at(75, 50));
        Assert.Equal((255, 0, 0), at(125, 50));
        Assert.Equal((255, 255, 255), at(175, 50));
    }

    /// <summary>
    /// Rendered over white: the red rect shows where the mask is white, the left half, and not where
    /// the mask has nothing, the right half. Unmasked, both halves were red.
    /// </summary>
    [Fact]
    public void TheElementShowsWhereTheMaskIsWhite()
    {
        var at = Render(HalfMask + @"<rect width=""100"" height=""100"" fill=""#ff0000"" mask=""url(#m)""/>");

        Assert.Equal((255, 0, 0), at(25, 50));
        Assert.Equal((255, 255, 255), at(75, 50));
    }

    /// <summary>
    /// The mask's content is in the element's user space, its <c>transform</c> included: moved 100
    /// right, the rect shows on 100–150 and not on 150–200. Drawn in the parent's space the mask
    /// would cover 0–50, where the rect is not, and the rect would not show at all.
    /// </summary>
    [Fact]
    public void TheMaskIsInTheElementsUserSpace()
    {
        var at = Render(HalfMask + @"<rect width=""100"" height=""100"" fill=""#ff0000"" transform=""translate(100 0)"" mask=""url(#m)""/>");

        Assert.Equal((255, 0, 0), at(125, 50));
        Assert.Equal((255, 255, 255), at(175, 50));
    }

    /// <summary>
    /// A group's mask is in the group's user space, its transform included: the group moved 100 right
    /// shows its rect on 100–150, where the mask is white, and not on 150–200.
    /// </summary>
    [Fact]
    public void AGroupsMaskIsInItsUserSpace()
    {
        var at = Render(@"<mask id=""m"" maskUnits=""userSpaceOnUse"" x=""0"" y=""0"" width=""200"" height=""200""><rect width=""50"" height=""100"" fill=""white""/></mask>" +
                        @"<g transform=""translate(100 0)"" mask=""url(#m)""><rect width=""100"" height=""100"" fill=""#ff0000""/></g>");

        Assert.Equal((255, 0, 0), at(125, 50));
        Assert.Equal((255, 255, 255), at(175, 50));
    }

    /// <summary>The mask ends with its element: every <c>q</c> is closed, and the next rect paints after it.</summary>
    [Fact]
    public void TheMaskEndsWithTheElement()
    {
        using var doc = Open(HalfMask + @"<rect width=""100"" height=""100"" mask=""url(#m)""/><rect x=""150"" width=""10"" height=""10""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        int gs = Array.FindIndex(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        int close = Array.IndexOf(page, "Q", gs);
        int[] fills = page.Select((l, i) => (l, i)).Where(p => p.l == "f").Select(p => p.i).ToArray();
        Assert.Equal(2, fills.Length);
        Assert.True(fills[0] < close && close < fills[1], "the mask must end before the next shape paints");
        Assert.Equal(page.Count(l => l == "q"), page.Count(l => l == "Q"));
    }

    /// <summary>
    /// What is not applied is reported, and the element drawn unmasked: a reference that names no
    /// mask. (Content in <c>objectBoundingBox</c> units is applied to a shape since batch 46:
    /// <c>SvgBoxUnitsTests</c>.)
    /// </summary>
    [Theory]
    [InlineData(@"<clipPath id=""m""><rect width=""50"" height=""100""/></clipPath>", "names no mask")]
    public void AMaskThatIsNotAppliedIsReported(string definition, string reason)
    {
        using var doc = Open(definition + @"<rect width=""100"" height=""100"" mask=""url(#m)""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.DoesNotContain(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.Contains("f", page);   // drawn, unmasked
        Assert.Contains(reason, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Hostile input: a mask whose content is masked by itself would recurse. The inner reference is
    /// not applied, and reported; the outer one is, once.
    /// </summary>
    [Fact]
    public void AMaskInsideItsOwnContentIsNotAppliedAgain()
    {
        using var doc = Open(@"<mask id=""m""><rect width=""50"" height=""100"" fill=""white"" mask=""url(#m)""/></mask>" +
                             @"<rect width=""100"" height=""100"" mask=""url(#m)""/>");

        var states = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]));
        Assert.Single(states.Keys);
        Assert.Contains("the mask itself", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A mask's content may be masked in turn: the outer mask's white square, masked to its left
    /// half, lets only that half of the element through — two mask states, and nothing said.
    /// </summary>
    [Fact]
    public void AMaskInsideAnotherMasksContentApplies()
    {
        string body = @"<mask id=""n""><rect width=""50"" height=""100"" fill=""white""/></mask>" +
                      @"<mask id=""m""><rect width=""100"" height=""100"" fill=""white"" mask=""url(#n)""/></mask>" +
                      @"<rect width=""100"" height=""100"" fill=""red"" mask=""url(#m)""/>";
        using var doc = Open(body);
        var at = Render(body);

        var states = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]));
        Assert.Equal(2, states.Keys.Count());
        Assert.Equal((255, 0, 0), at(25, 50));
        Assert.Equal((255, 255, 255), at(75, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A mask applies to every element that names it: being drawn is for the content's duration, not for good. (The mask's white spans both rects' user space.)</summary>
    [Fact]
    public void AMaskAppliesToEveryElementThatNamesIt()
    {
        var at = Render(@"<mask id=""m""><rect width=""50"" height=""200"" fill=""white""/></mask>" +
                        @"<rect width=""100"" height=""100"" fill=""red"" mask=""url(#m)""/><rect y=""100"" width=""100"" height=""100"" fill=""blue"" mask=""url(#m)""/>");

        Assert.Equal((255, 0, 0), at(25, 50));
        Assert.Equal((0, 0, 255), at(25, 150));
        Assert.Equal((255, 255, 255), at(75, 150));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Hostile input: two masks whose contents are masked with each other are drawn once each — two states — and reported.</summary>
    [Fact]
    public void AHostileMaskCycleIsBounded()
    {
        using var doc = Open(@"<mask id=""m""><rect width=""50"" height=""100"" fill=""white"" mask=""url(#n)""/></mask>" +
                             @"<mask id=""n""><rect width=""100"" height=""50"" fill=""white"" mask=""url(#m)""/></mask>" +
                             @"<rect width=""100"" height=""100"" mask=""url(#m)""/>");

        var states = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]));
        Assert.Equal(2, states.Keys.Count());
        Assert.Contains("the mask itself", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A shape drawn after a masked one is its container's geometry again: a group clipped to its left
    /// half in box units spans both its rects, so the clip's half is 100, the masked rect whole and the
    /// plain one cut — not the masked rect's half alone.
    /// </summary>
    [Fact]
    public void AShapeAfterAMaskedOneIsItsContainersGeometryAgain()
    {
        var at = Render(
            @"<clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath>" +
            @"<mask id=""m""><rect width=""200"" height=""200"" fill=""white""/></mask>" +
            @"<g clip-path=""url(#c)""><rect width=""100"" height=""100"" fill=""red"" mask=""url(#m)""/><rect x=""100"" width=""100"" height=""100"" fill=""blue""/></g>");

        Assert.Equal((255, 0, 0), at(75, 50));
        Assert.Equal((255, 255, 255), at(150, 50));
    }

    /// <summary>
    /// A mask's content is no container's geometry, after a nested mask as before it: a group clipped
    /// to its left half in box units keeps that half whatever the mask's content drew far away after
    /// its own masked shape finished.
    /// </summary>
    [Fact]
    public void AMasksContentIsNotItsContainersBoxAfterANestedMask()
    {
        var at = Render(
            @"<clipPath id=""c"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath>" +
            @"<mask id=""n""><rect width=""200"" height=""200"" fill=""white""/></mask>" +
            @"<mask id=""m""><rect width=""200"" height=""200"" fill=""white"" mask=""url(#n)""/><rect x=""1000"" y=""1000"" width=""10"" height=""10"" fill=""white""/></mask>" +
            @"<g clip-path=""url(#c)""><rect width=""200"" height=""100"" fill=""red"" mask=""url(#m)""/></g>");

        Assert.Equal((255, 0, 0), at(50, 50));
        Assert.Equal((255, 255, 255), at(150, 50));
    }

    // ------------------------------------------------------------------ helpers

    private static PdfDocument Open(string body)
        => PdfDocument.Open(new MemoryStream(Convert(body)));

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int, int, int)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    private static PdfDictionary State(PdfDocument doc, string gsLine)
    {
        var all = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]));
        return Assert.IsType<PdfDictionary>(doc.ResolveObject(all[new PdfName(gsLine.Split(' ')[0].TrimStart('/'))]));
    }

    private static string[] Lines(byte[] content) => Encoding.Latin1.GetString(content).Split('\n').Select(l => l.Trim()).ToArray();
}
