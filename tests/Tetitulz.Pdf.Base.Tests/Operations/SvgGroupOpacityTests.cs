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
/// <see cref="SvgToPdfConverter"/> draws a group with <c>opacity</c> as a transparency group (SVG 1.1
/// §14.5, ISO 32000-1 §11.6.6): its content is a Form XObject with <c>/Group</c>, painted once at the
/// group's opacity, so where its elements overlap they do not show through each other.
/// </summary>
/// <remarks>
/// The group's opacity was multiplied into each element it drew (batch 21), and reported: over an
/// overlap the lower element showed through the upper one.
/// </remarks>
public class SvgGroupOpacityTests
{
    [Fact]
    public void AGroupsOpacityIsATransparencyGroup()
    {
        using var doc = Open(@"<g opacity=""0.5""><rect width=""10"" height=""10""/></g>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());
        var form = Form(doc, "Fm0");

        int gs = Array.FindIndex(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.True(gs >= 0 && gs < Array.IndexOf(page, "/Fm0 Do"), "the group's opacity was not set before it");
        var state = State(doc, page[gs]);
        Assert.Equal(0.5, state.Fill, 4);
        Assert.Equal(0.5, state.Stroke, 4);
        var group = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Group]));
        Assert.Equal("Transparency", ((PdfName)group[PdfName.S]).Value);
        Assert.True(Assert.IsType<PdfBoolean>(group[PdfName.I]).Value);   // isolated, as SVG composites a group
        Assert.DoesNotContain("f", page);                         // the rect is not on the page...
        Assert.Contains("f", Lines(form.GetDecodedData()));       // ...but in the group
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Rendered: a red and a blue rect overlapping in a group at 0.5, over white. Where they overlap
    /// the group is blue at 0.5 — (127, 127, 255) — since the blue covers the red inside the group.
    /// Applied to each rect, the red showed through: (64, 32, 191) or thereabouts.
    /// </summary>
    [Fact]
    public void OverlapsInsideAGroupDoNotShowThrough()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200""><g opacity=""0.5"">" +
            @"<rect width=""100"" height=""100"" fill=""#ff0000""/><rect x=""50"" width=""100"" height=""100"" fill=""#0000ff""/></g></svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var pixels = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        (int R, int G, int B) At(int x, int y) { int i = y * pixels.Stride + x * 4; return (pixels.Pixels[i], pixels.Pixels[i + 1], pixels.Pixels[i + 2]); }
        var overlap = At(75, 50);
        var redOnly = At(25, 50);
        Assert.InRange(overlap.R, 120, 135);
        Assert.InRange(overlap.G, 120, 135);
        Assert.InRange(overlap.B, 250, 255);
        Assert.InRange(redOnly.R, 250, 255);   // the control: the group is at half strength
        Assert.InRange(redOnly.G, 120, 135);
    }

    /// <summary>
    /// The group's opacity is applied once, at the group: an element in it keeps its own
    /// <c>fill-opacity</c> alone (0.5), where it was 0.25 with the group's multiplied in.
    /// </summary>
    [Fact]
    public void AnElementInAGroupKeepsItsOwnOpacity()
    {
        using var doc = Open(@"<g opacity=""0.5""><rect width=""10"" height=""10"" fill-opacity=""0.5""/></g>");
        string[] inner = Lines(Form(doc, "Fm0").GetDecodedData());

        string gs = Assert.Single(inner, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.Equal(0.5, State(doc, gs).Fill, 4);
    }

    /// <summary>
    /// A group in a group is a form in a form: the inner one is drawn by the outer one's content. Both
    /// name one resources object, and the inner one's text finds its font there.
    /// </summary>
    [Fact]
    public void NestedGroupsAreNestedForms()
    {
        using var doc = Open(@"<g opacity=""0.5""><g opacity=""0.5""><text x=""10"" y=""20"" font-family=""serif"">A</text></g></g>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        string outer = Assert.Single(page, l => l.EndsWith(" Do", StringComparison.Ordinal)).Split(' ')[0].TrimStart('/');
        string[] outerContent = Lines(Form(doc, outer).GetDecodedData());
        string inner = Assert.Single(outerContent, l => l.EndsWith(" Do", StringComparison.Ordinal)).Split(' ')[0].TrimStart('/');
        Assert.NotEqual(outer, inner);
        Assert.Contains("BT", Lines(Form(doc, inner).GetDecodedData()));

        var outerResources = Assert.IsType<PdfReference>(Form(doc, outer)[PdfName.Resources]);
        var innerResources = Assert.IsType<PdfReference>(Form(doc, inner)[PdfName.Resources]);
        Assert.Equal(outerResources.ReferenceObjectNumber, innerResources.ReferenceObjectNumber);
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(Assert.IsType<PdfDictionary>(doc.ResolveObject(innerResources))[PdfName.Font]));
        Assert.NotNull(fonts[new PdfName("F2")]);
    }

    /// <summary>
    /// The group's content finds its resources: text in a serif face inside a group is set in the
    /// page's <c>/F2</c>, which the form's own <c>/Resources</c> names too.
    /// </summary>
    [Fact]
    public void AGroupsContentFindsItsResources()
    {
        using var doc = Open(@"<g opacity=""0.5""><text x=""10"" y=""20"" font-family=""serif"">A</text></g>");
        var form = Form(doc, "Fm0");

        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Resources]));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.Font]));
        var f2 = Assert.IsType<PdfDictionary>(doc.ResolveObject(fonts[new PdfName("F2")]));
        Assert.Equal("Times-Roman", ((PdfName)f2[PdfName.BaseFont]).Value);
        Assert.Contains(Lines(form.GetDecodedData()), l => l.StartsWith("/F2 ", StringComparison.Ordinal));
    }

    /// <summary>
    /// An opaque group is no form, and neither is a translucent one inside a clipPath, where only
    /// the geometry counts: both draw as they did.
    /// </summary>
    [Theory]
    [InlineData(@"<g><rect width=""10"" height=""10""/></g>")]
    [InlineData(@"<g opacity=""1""><rect width=""10"" height=""10""/></g>")]
    [InlineData(@"<clipPath id=""c""><g opacity=""0.5""><rect width=""10"" height=""10""/></g></clipPath><rect width=""50"" height=""50"" clip-path=""url(#c)""/>")]
    public void OnlyAPaintedTranslucentGroupIsAForm(string body)
    {
        using var doc = Open(body);
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.DoesNotContain(page, l => l.EndsWith(" Do", StringComparison.Ordinal));
        Assert.Contains("f", page);   // the potency control: it was drawn
    }

    // ------------------------------------------------------------------ helpers

    private static PdfDocument Open(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        return PdfDocument.Open(new MemoryStream(pdf));
    }

    private static PdfStream Form(PdfDocument doc, string name)
    {
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]));
        var form = Assert.IsType<PdfStream>(doc.ResolveObject(xobjects[new PdfName(name)]));
        Assert.Equal("Form", ((PdfName)form[PdfName.Subtype]).Value);
        return form;
    }

    private static (double Fill, double Stroke) State(PdfDocument doc, string gsLine)
    {
        var all = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]));
        var gs = Assert.IsType<PdfDictionary>(doc.ResolveObject(all[new PdfName(gsLine.Split(' ')[0].TrimStart('/'))]));
        return (((PdfNumber)gs[PdfName.ca]).DoubleValue, ((PdfNumber)gs[PdfName.CA]).DoubleValue);
    }

    private static string[] Lines(byte[] content) => Encoding.Latin1.GetString(content).Split('\n').Select(l => l.Trim()).ToArray();
}
