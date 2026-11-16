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
/// <see cref="SvgToPdfConverter"/> draws the <c>opacity</c> of every container — <c>a</c>,
/// <c>switch</c>, <c>use</c> and a nested <c>svg</c>, as a <c>g</c> since batch 37 — as a transparency
/// group (SVG 1.1 §14.5): what it draws is composited first, then painted once at its opacity, so its
/// overlapping elements do not show through each other.
/// </summary>
/// <remarks>
/// A container other than a <c>g</c> had its opacity multiplied into each element it drew: where a
/// blue rect overlapped a red one, the red showed through. Each case below draws the two, overlapping
/// on 50–100, at 0.5 over white: the overlap is blue at half strength, (127, 127, 255).
/// </remarks>
public class SvgContainerOpacityTests
{
    private const string Pair = @"<rect width=""100"" height=""100"" fill=""#ff0000""/><rect x=""50"" width=""100"" height=""100"" fill=""#0000ff""/>";

    [Theory]
    [InlineData(@"<a href=""https://example.org/"" opacity=""0.5"">" + Pair + "</a>")]
    [InlineData(@"<switch opacity=""0.5""><g>" + Pair + "</g></switch>")]
    [InlineData(@"<defs><g id=""pair"">" + Pair + @"</g></defs><use href=""#pair"" opacity=""0.5""/>")]
    [InlineData(@"<svg x=""0"" y=""0"" width=""200"" height=""200"" opacity=""0.5"">" + Pair + "</svg>")]
    [InlineData(@"<defs><symbol id=""s"" opacity=""0.5"">" + Pair + @"</symbol></defs><use href=""#s"" width=""200"" height=""200""/>")]
    public void AContainersOpacityIsATransparencyGroup(string body)
    {
        var at = Render(body);

        var overlap = at(75, 50);
        Assert.InRange(overlap.R, 120, 135);
        Assert.InRange(overlap.G, 120, 135);
        Assert.InRange(overlap.B, 250, 255);
        var red = at(25, 50);
        Assert.InRange(red.R, 250, 255);   // the control: half strength
        Assert.InRange(red.G, 120, 135);
    }

    /// <summary>
    /// The container's opacity is applied once, at the group: an element in it keeps its own
    /// <c>opacity</c>, one state of 0.5 for the group and the element alike, not 0.25 for the element.
    /// </summary>
    [Fact]
    public void AnElementKeepsItsOwnOpacity()
    {
        byte[] pdf = Convert(@"<a href=""https://example.org/"" opacity=""0.5""><rect width=""10"" height=""10"" opacity=""0.5""/></a>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var states = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.ExtGState]);
        var state = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(states[states.Keys.Single()]);

        Assert.Equal(0.5, ((Tetitulz.Pdf.Core.Objects.PdfNumber)state[Tetitulz.Pdf.Core.Objects.PdfName.ca]).DoubleValue, 4);
    }

    /// <summary>A link drawn as a group keeps its link: the annotation still covers what it drew.</summary>
    [Fact]
    public void ALinkInAGroupKeepsItsLink()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Convert(@"<a href=""https://example.org/"" opacity=""0.5"">" + Pair + "</a>")));
        var annots = (Tetitulz.Pdf.Core.Objects.PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.Annots]);

        Assert.Single(annots);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static Func<int, int, (int R, int G, int B)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
