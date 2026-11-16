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
/// <see cref="SvgToPdfConverter"/> paints each text run at its own fill alpha (SVG 1.1 §14.5): a
/// tspan's <c>fill-opacity</c> and colour alpha, times the text element's <c>opacity</c> chain.
/// </summary>
/// <remarks>
/// The alpha was the text element's alone, so a tspan's own <c>fill-opacity</c> was not applied: a
/// faded word in a sentence came out as opaque as the rest.
/// </remarks>
public class SvgTspanOpacityTests
{
    [Fact]
    public void ATspansFillOpacityIsItsOwn()
    {
        var (lines, states) = Convert(@"<text x=""10"" y=""20"">a<tspan fill-opacity=""0.5"">b</tspan></text>");

        Assert.Null(AlphaBefore(lines, states, "(a) Tj"));
        Assert.Equal(0.5, AlphaBefore(lines, states, "(b) Tj"));
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    /// <summary>A tspan's colour alpha counts too, as a shape's does.</summary>
    [Fact]
    public void ATspansColourAlphaCounts()
    {
        var (lines, states) = Convert(@"<text x=""10"" y=""20"">a<tspan fill=""rgba(255,0,0,0.25)"">b</tspan></text>");

        Assert.Equal(0.25, AlphaBefore(lines, states, "(b) Tj"));
    }

    /// <summary>
    /// The text element's <c>opacity</c> reaches every run: 0.5 of it, and the tspan's 0.5
    /// <c>fill-opacity</c>, paints at 0.25, while the run beside it paints at the element's 0.5.
    /// </summary>
    [Fact]
    public void TheElementsOpacityMultiplies()
    {
        var (lines, states) = Convert(@"<text x=""10"" y=""20"" opacity=""0.5"">a<tspan fill-opacity=""0.5"">b</tspan></text>");

        Assert.Equal(0.5, AlphaBefore(lines, states, "(a) Tj"));
        Assert.Equal(0.25, AlphaBefore(lines, states, "(b) Tj"));
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    /// <summary>
    /// A run that paints at its element's alpha — inheriting it — gets no state of its own: one
    /// <c>gs</c>, the element's, for both runs.
    /// </summary>
    [Fact]
    public void ARunAtItsElementsAlphaGetsNoStateOfItsOwn()
    {
        var (lines, states) = Convert(@"<text x=""10"" y=""20"" fill-opacity=""0.5"">a<tspan>b</tspan></text>");

        Assert.Single(lines, l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.Equal(0.5, AlphaBefore(lines, states, "(b) Tj"));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The <c>/ca</c> of the last <c>gs</c> before <paramref name="tj"/>, or <c>null</c> when none is.</summary>
    private static double? AlphaBefore(string[] lines, PdfDictionary states, string tj)
    {
        int at = Array.IndexOf(lines, tj);
        Assert.True(at >= 0, tj + " was not drawn");
        string gs = lines.Take(at).LastOrDefault(l => l.EndsWith(" gs", StringComparison.Ordinal));
        if (gs == null) return null;
        var state = (PdfDictionary)states[new PdfName(gs.Split(' ')[0].TrimStart('/'))];
        return ((PdfNumber)state[PdfName.ca]).DoubleValue;
    }

    private static (string[] Lines, PdfDictionary States) Convert(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var states = doc.ResolveObject(doc.Pages[0].Resources[PdfName.ExtGState]) as PdfDictionary ?? new PdfDictionary();
        string content = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
        return (content.Split('\n').Select(l => l.Trim()).ToArray(), states);
    }
}
