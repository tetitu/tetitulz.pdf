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
/// <see cref="SvgToPdfConverter"/> draws a shape that both fills and strokes under an <c>opacity</c>
/// as a transparency group (SVG 1.1 §14.5, ISO 32000-1 §11.6.6), as it draws a container: inside the
/// group the stroke's inner half covers the fill, and the group is painted once at the opacity. A
/// shape with one paint keeps the constant alpha, which is exact for it.
/// </summary>
/// <remarks>
/// Before batch 83 the opacity was multiplied into each paint's alpha, so over the stroke's inner
/// half the fill showed through the stroke — a red rect with a blue stroke at 0.5 had a purple band
/// inside its edge, where a browser draws blue at 0.5.
/// </remarks>
public class SvgShapeOpacityTests
{
    [Fact]
    public void AFilledAndStrokedShapesOpacityIsATransparencyGroup()
    {
        using var doc = Open(@"<rect width=""10"" height=""10"" fill=""red"" stroke=""blue"" opacity=""0.5""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());
        var form = Form(doc, "Fm0");
        string[] inner = Lines(form.GetDecodedData());

        int gs = Array.FindIndex(page, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.True(gs >= 0 && gs < Array.IndexOf(page, "/Fm0 Do"), "the shape's opacity was not set before its group");
        var state = State(doc, page[gs]);
        Assert.Equal(0.5, state.Fill, 4);
        Assert.Equal(0.5, state.Stroke, 4);
        var group = Assert.IsType<PdfDictionary>(doc.ResolveObject(form[PdfName.Group]));
        Assert.Equal("Transparency", ((PdfName)group[PdfName.S]).Value);
        Assert.DoesNotContain("B", page);                                             // the rect is not on the page...
        Assert.Contains("B", inner);                                                  // ...but filled and stroked in the group
        Assert.DoesNotContain(inner, l => l.EndsWith(" gs", StringComparison.Ordinal)); // at its own paints' full alpha
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Rendered: a red rect with a blue stroke 20 wide at 0.5 over white. Five points inside the
    /// edge is in the stroke's inner half: the group is blue at 0.5 there — (127, 127, 255) — since
    /// the stroke covers the fill inside the group. As a constant alpha on each paint the red showed
    /// through: (127, 64, 191). The centre is the control, red at 0.5.
    /// </summary>
    [Fact]
    public void TheStrokesInnerHalfCoversTheFillInsideTheGroup()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" +
            @"<rect x=""50"" y=""50"" width=""100"" height=""100"" fill=""#ff0000"" stroke=""#0000ff"" stroke-width=""20"" opacity=""0.5""/></svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        var pixels = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        (int R, int G, int B) At(int x, int y) { int i = y * pixels.Stride + x * 4; return (pixels.Pixels[i], pixels.Pixels[i + 1], pixels.Pixels[i + 2]); }
        var innerHalf = At(55, 100);
        var centre = At(100, 100);
        Assert.InRange(innerHalf.R, 120, 135);
        Assert.InRange(innerHalf.G, 120, 135);
        Assert.InRange(innerHalf.B, 250, 255);
        Assert.InRange(centre.R, 250, 255);   // the control: the group is at half strength
        Assert.InRange(centre.G, 120, 135);
        Assert.InRange(centre.B, 120, 135);
    }

    /// <summary>
    /// The shape's opacity is applied once, at the group: inside it each paint keeps its own
    /// <c>fill-opacity</c> and <c>stroke-opacity</c> (0.5 and 0.8), where they were 0.25 and 0.4 with
    /// the opacity multiplied in.
    /// </summary>
    [Fact]
    public void ThePaintsKeepTheirOwnOpacitiesInsideTheGroup()
    {
        using var doc = Open(@"<rect width=""10"" height=""10"" fill-opacity=""0.5"" stroke=""black"" stroke-opacity=""0.8"" opacity=""0.5""/>");
        string[] page = Lines(doc.Pages[0].GetContentStreamData());
        string[] inner = Lines(Form(doc, "Fm0").GetDecodedData());

        var outer = State(doc, Assert.Single(page, l => l.EndsWith(" gs", StringComparison.Ordinal)));
        var own = State(doc, Assert.Single(inner, l => l.EndsWith(" gs", StringComparison.Ordinal)));
        Assert.Equal(0.5, outer.Fill, 4);
        Assert.Equal(0.5, outer.Stroke, 4);
        Assert.Equal(0.5, own.Fill, 4);
        Assert.Equal(0.8, own.Stroke, 4);
    }

    /// <summary>
    /// A shape with one paint — a fill alone, a stroke alone, a stroke that paints nothing, a line
    /// (never filled) — is no group: the constant alpha is exact for it, and stays. Nor is a shape
    /// at opacity 1, or one inside a clipPath, where only the geometry counts.
    /// </summary>
    [Theory]
    [InlineData(@"<rect width=""10"" height=""10"" fill=""red"" opacity=""0.5""/>", "f")]
    [InlineData(@"<rect width=""10"" height=""10"" fill=""none"" stroke=""blue"" opacity=""0.5""/>", "S")]
    [InlineData(@"<rect width=""10"" height=""10"" fill=""red"" stroke=""transparent"" opacity=""0.5""/>", "f")]
    [InlineData(@"<line x2=""10"" y2=""10"" fill=""red"" stroke=""blue"" opacity=""0.5""/>", "S")]
    [InlineData(@"<rect width=""10"" height=""10"" fill=""red"" stroke=""blue"" opacity=""1""/>", "B")]
    [InlineData(@"<clipPath id=""c""><rect width=""10"" height=""10"" fill=""red"" stroke=""blue"" opacity=""0.5""/></clipPath><rect width=""50"" height=""50"" clip-path=""url(#c)""/>", "f")]
    public void AShapeWithOnePaintKeepsTheConstantAlpha(string body, string painter)
    {
        using var doc = Open(body);
        string[] page = Lines(doc.Pages[0].GetContentStreamData());

        Assert.DoesNotContain(page, l => l.EndsWith(" Do", StringComparison.Ordinal));
        Assert.Contains(painter, page);   // the potency control: it was drawn, on the page
        if (body.Contains(@"opacity=""0.5""") && !body.Contains("clipPath"))
            Assert.Equal(0.5, State(doc, Assert.Single(page, l => l.EndsWith(" gs", StringComparison.Ordinal))).Fill, 4);
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
        return Assert.IsType<PdfStream>(doc.ResolveObject(xobjects[new PdfName(name)]));
    }

    private static (double Fill, double Stroke) State(PdfDocument doc, string gsLine)
    {
        var states = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]));
        var state = Assert.IsType<PdfDictionary>(doc.ResolveObject(states[new PdfName(gsLine.Split(' ')[0].TrimStart('/'))]));
        return (((PdfNumber)state[PdfName.ca]).DoubleValue, ((PdfNumber)state[PdfName.CA]).DoubleValue);
    }

    private static string[] Lines(byte[] content) => Encoding.Latin1.GetString(content).Split('\n').Select(l => l.Trim()).ToArray();
}
