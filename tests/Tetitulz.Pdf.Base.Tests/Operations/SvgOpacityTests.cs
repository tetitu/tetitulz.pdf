// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> applies opacity (SVG 1.1 §14.5): <c>fill-opacity</c>,
/// <c>stroke-opacity</c>, <c>opacity</c> and a colour's alpha, through an <c>/ExtGState</c>'s
/// <c>/ca</c> and <c>/CA</c>, scoped to the one shape by <c>q</c>/<c>Q</c>.
/// </summary>
/// <remarks>
/// The converter wrote no <c>/ExtGState</c>, so every one of these drew opaque. Batch 17 made that
/// visible on <see cref="SvgToPdfConverter.LastError"/>; this applies them. A group's
/// <c>opacity</c> is a transparency group (<see cref="SvgGroupOpacityTests"/>).
/// </remarks>
public class SvgOpacityTests
{
    [Fact]
    public void FillOpacityIsApplied()
    {
        var (content, states) = Convert(@"<rect width=""10"" height=""10"" fill=""red"" fill-opacity=""0.5""/>");

        var (ca, CA) = Assert.Single(states).Value;
        Assert.Equal(0.5, ca, 4);
        Assert.Equal(1.0, CA, 4);
        Assert.Contains("/" + states.Keys.Single() + " gs", Lines(content));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    [Fact]
    public void StrokeOpacityIsApplied()
    {
        var (_, states) = Convert(@"<rect width=""10"" height=""10"" fill=""none"" stroke=""black"" stroke-opacity=""0.25""/>");

        var (ca, CA) = Assert.Single(states).Value;
        Assert.Equal(1.0, ca, 4);
        Assert.Equal(0.25, CA, 4);
    }

    /// <summary>
    /// On a shape with one paint, <c>opacity</c> multiplies into it, and a colour's alpha into its
    /// own paint. (With a fill and a stroke the opacity is a transparency group's:
    /// <see cref="SvgShapeOpacityTests"/>.)
    /// </summary>
    [Fact]
    public void TheFactorsMultiplyOnASinglePaint()
    {
        var (_, states) = Convert(
            @"<rect width=""10"" height=""10"" fill=""rgba(255, 0, 0, 0.5)"" fill-opacity=""0.5"" opacity=""0.5""/>");

        var (ca, CA) = Assert.Single(states).Value;
        Assert.Equal(0.125, ca, 4);   // 0.5 alpha × 0.5 fill-opacity × 0.5 opacity
        Assert.Equal(0.5, CA, 4);     // the opacity alone: no stroke-opacity, no stroke
    }

    /// <summary>A colour's alpha is applied, not reported, and the colour is still read.</summary>
    [Theory]
    [InlineData("rgba(255, 0, 0, 0.5)")]
    [InlineData("#ff000080")]
    public void AColoursAlphaIsApplied(string colour)
    {
        var (content, states) = Convert(@"<rect width=""10"" height=""10"" fill=""" + colour + @"""/>");

        Assert.Equal(0.5, Assert.Single(states).Value.Fill, 2);
        Assert.Contains("1.0000 0.0000 0.0000 rg", Lines(content));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// <c>fill-opacity</c> is inherited. (A group's <c>opacity</c> is a transparency group since
    /// batch 37: <see cref="SvgGroupOpacityTests"/>.)
    /// </summary>
    [Fact]
    public void InheritedFillOpacityApplies()
    {
        var (_, inherited) = Convert(@"<g fill-opacity=""0.5""><rect width=""10"" height=""10""/></g>");
        Assert.Equal(0.5, Assert.Single(inherited).Value.Fill, 4);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>An opaque drawing writes no graphics state and no <c>/ExtGState</c>.</summary>
    [Theory]
    [InlineData(@"fill=""red""")]
    [InlineData(@"fill=""red"" opacity=""1"" fill-opacity=""1.0""")]
    [InlineData(@"fill=""red"" fill-opacity=""2""")]   // clamped to 1
    public void AnOpaqueShapeWritesNoState(string paint)
    {
        var (content, states) = Convert(@"<rect width=""10"" height=""10"" " + paint + "/>");

        Assert.Empty(states);
        Assert.DoesNotContain(Lines(content), l => l.EndsWith(" gs", StringComparison.Ordinal));
        Assert.Contains("f", Lines(content));   // the potency control: it was drawn
    }

    /// <summary>
    /// The state is the shape's alone: it is set inside a <c>q</c>/<c>Q</c> pair that closes
    /// before the next shape paints, and two shapes with the same alphas share one state.
    /// </summary>
    [Fact]
    public void TheStateIsScopedToTheShapeAndShared()
    {
        var (content, states) = Convert(
            @"<rect width=""10"" height=""10"" fill-opacity=""0.5""/>" +
            @"<rect x=""20"" width=""10"" height=""10""/>" +
            @"<rect x=""40"" width=""10"" height=""10"" fill-opacity=""0.5""/>");

        Assert.Single(states);
        string[] lines = Lines(content);
        int gs = Array.FindIndex(lines, l => l.EndsWith(" gs", StringComparison.Ordinal));
        int close = Array.IndexOf(lines, "Q", gs);
        int[] fills = lines.Select((l, i) => (l, i)).Where(p => p.l == "f").Select(p => p.i).ToArray();
        Assert.Equal(3, fills.Length);
        Assert.Equal("q", lines[gs - 1]);
        Assert.True(fills[0] < close && close < fills[1], "the first shape's state must close before the second paints");
        // Its own q, not an enclosing one: every q is matched by a Q. Without the shape's q, its Q
        // would pop the drawing's and the line before the gs would still read "q".
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    /// <summary>Text takes its fill's opacity.</summary>
    [Fact]
    public void TextTakesItsFillOpacity()
    {
        var (content, states) = Convert(@"<text x=""10"" y=""20"" fill-opacity=""0.5"">A</text>");

        Assert.Equal(0.5, Assert.Single(states).Value.Fill, 4);
        string[] lines = Lines(content);
        Assert.True(Array.FindIndex(lines, l => l.EndsWith(" gs", StringComparison.Ordinal)) < Array.IndexOf(lines, "BT"));
    }

    /// <summary>An opacity may be a percentage (CSS Color 4, SVG 2).</summary>
    [Fact]
    public void AnOpacityPercentageIsRead()
    {
        var (_, states) = Convert(@"<rect width=""10"" height=""10"" opacity=""25%""/>");

        Assert.Equal(0.25, Assert.Single(states).Value.Fill, 4);
    }

    /// <summary>An opacity below 0 is 0, not a negative alpha written into the file.</summary>
    [Fact]
    public void ANegativeOpacityIsZero()
    {
        var (_, states) = Convert(@"<rect width=""10"" height=""10"" fill-opacity=""-1""/>");

        Assert.Equal(0.0, Assert.Single(states).Value.Fill, 4);
    }

    // ------------------------------------------------------------------ helpers

    private static (string Content, Dictionary<string, (double Fill, double Stroke)> States) Convert(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var page = doc.Pages[0];
        var states = new Dictionary<string, (double, double)>();
        if (doc.ResolveObject(page.Resources[PdfName.ExtGState]) is PdfDictionary all)
            foreach (var key in all.Keys)
            {
                var gs = Assert.IsType<PdfDictionary>(doc.ResolveObject(all[key]));
                double ca = doc.ResolveObject(gs[PdfName.ca]) is PdfNumber f ? f.DoubleValue : 1;
                double CA = doc.ResolveObject(gs[PdfName.CA]) is PdfNumber s ? s.DoubleValue : 1;
                states[key.Value] = (ca, CA);
            }
        return (Encoding.Latin1.GetString(page.GetContentStreamData()), states);
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}
