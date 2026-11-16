// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> sets text through the mapping every shape goes through — a
/// <c>viewBox</c>'s scale and a transform's scale, rotation and skew (SVG 1.1 §7) — and reads
/// <c>font-size</c> as the inherited property it is, from <c>style</c> too (§10.10).
/// </summary>
/// <remarks>
/// Only the text's anchor point was mapped. The glyphs were set at the user-unit size with no text
/// matrix, so under a <c>viewBox</c> or a transform the shapes scaled and turned and the text kept its
/// size and stayed upright: a label on a scaled-up chart came out at a fraction of its size. The font
/// size was read from the element's own attribute only.
/// </remarks>
public class SvgTextPlacementTests
{
    /// <summary>
    /// Untransformed text is placed with <c>Td</c> as before: user (10, 20) on a flipped 200-point
    /// page is (10, 180), and no text matrix is written.
    /// </summary>
    [Fact]
    public void UntransformedTextIsPlacedWithTd()
    {
        string c = Content(Svg(@"<text x=""10"" y=""20"">A</text>"));

        Assert.Equal(new[] { 10f, 180 }, Assert.Single(Operands(c, "Td")));
        Assert.Empty(Operands(c, "Tm"));
    }

    /// <summary>
    /// A <c>viewBox</c> of 100 × 50 on a 200-point page scales by 2 across and 4 down. <c>Tf</c> keeps
    /// the size in user units and the text matrix carries the scale, as the geometry's mapping does:
    /// the anchor (10, 20) is page (20, 200 − 80).
    /// </summary>
    [Fact]
    public void TextScalesWithTheViewBox()
    {
        string c = Content(@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 100 50""><text x=""10"" y=""20"" font-size=""10"">A</text></svg>");

        Assert.Equal(new[] { 2f, 0, 0, 4, 20, 120 }, Assert.Single(Operands(c, "Tm")));
        Assert.Equal(10f, TfSize(c));
    }

    /// <summary>
    /// Rotated under that non-uniform scale, the baseline runs down the page at the vertical scale
    /// and the glyphs' up to the right at the horizontal one.
    /// </summary>
    [Fact]
    public void RotatedTextTakesEachAxisScale()
    {
        string c = Content(@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 100 50""><text x=""0"" y=""0"" transform=""rotate(90)"">A</text></svg>");

        float[] tm = Assert.Single(Operands(c, "Tm"));
        Assert.Equal(new[] { 0f, -4, 2, 0 }, tm.Take(4).Select(v => (float)Math.Round(v, 4)));
    }

    /// <summary>
    /// <c>rotate(90)</c> turns the baseline down the page and the glyphs' up to the right; the anchor
    /// (10, 20) rotates to (−20, 10), page (−20, 190).
    /// </summary>
    [Fact]
    public void RotatedTextIsRotated()
    {
        string c = Content(Svg(@"<text x=""10"" y=""20"" transform=""rotate(90)"">A</text>"));

        float[] tm = Assert.Single(Operands(c, "Tm"));
        Assert.Equal(new[] { 0f, -1, 1, 0 }, tm.Take(4).Select(v => (float)Math.Round(v, 4)));
        Assert.Equal(-20f, tm[4], 3);
        Assert.Equal(190f, tm[5], 3);
    }

    /// <summary>
    /// <c>skewX(45)</c> moves a point right by its y, and SVG's y runs down, so the glyphs' up axis
    /// leans left: on the page it is (−1, 1), the baseline unchanged. <c>skewY(45)</c> tilts the
    /// baseline down the page instead, (1, −1), and leaves the up axis alone — each skew changes one
    /// entry the identity test must look at.
    /// </summary>
    [Fact]
    public void SkewedTextIsSkewed()
    {
        string x = Content(Svg(@"<text x=""0"" y=""20"" transform=""skewX(45)"">A</text>"));
        string y = Content(Svg(@"<text x=""0"" y=""20"" transform=""skewY(45)"">A</text>"));

        Assert.Equal(new[] { 1f, 0, -1, 1 }, Assert.Single(Operands(x, "Tm")).Take(4).Select(v => (float)Math.Round(v, 4)));
        Assert.Equal(new[] { 1f, -1, 0, 1 }, Assert.Single(Operands(y, "Tm")).Take(4).Select(v => (float)Math.Round(v, 4)));
    }

    /// <summary>
    /// <c>font-size</c> is inherited from a group, and a <c>style</c> declaration wins over the
    /// attribute (§6.4).
    /// </summary>
    [Fact]
    public void FontSizeIsInheritedAndReadFromStyle()
    {
        Assert.Equal(20f, TfSize(Content(Svg(@"<g font-size=""20""><text x=""0"" y=""20"">A</text></g>"))));
        Assert.Equal(18f, TfSize(Content(Svg(@"<text x=""0"" y=""20"" font-size=""9"" style=""font-size: 18px"">A</text>"))));
        Assert.Equal(12f, TfSize(Content(Svg(@"<text x=""0"" y=""20"">A</text>"))));   // the default, unchanged
    }

    /// <summary>
    /// Unflipped, the page takes SVG's y-down coordinates as they are, and the text follows the
    /// geometry: its up axis is page −y, as a shape's is.
    /// </summary>
    [Fact]
    public void UnflippedTextFollowsTheGeometry()
    {
        byte[] pdf = SvgToPdfConverter.Convert(Svg(@"<text x=""10"" y=""20"">A</text>"),
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });

        Assert.Equal(new[] { 1f, 0, 0, -1, 10, 20 }, Assert.Single(Operands(ContentOf(pdf), "Tm")));
    }

    // ------------------------------------------------------------------ helpers

    private static string Svg(string body) =>
        @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>";

    private static string Content(string svg) =>
        ContentOf(SvgToPdfConverter.Convert(svg, new SvgConversionOptions { PageWidth = 200, PageHeight = 200 }));

    private static string ContentOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static float TfSize(string content)
    {
        string tf = Assert.Single(Lines(content), l => l.EndsWith(" Tf", StringComparison.Ordinal));
        return float.Parse(tf.Split(' ')[1], CultureInfo.InvariantCulture);
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();

    private static float[][] Operands(string content, string op)
        => Lines(content)
            .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Select(parts => parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
}
