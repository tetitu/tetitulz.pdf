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
/// <see cref="SvgToPdfConverter"/> reads <c>text-anchor</c> (SVG 1.1 §10.9.1): <c>x</c> is the start,
/// the middle or the end of the run, measured as drawn in Helvetica, the one font the converter sets.
/// </summary>
/// <remarks>
/// It was not read, so every run started at <c>x</c>: a label centred on a bar began at the bar's
/// middle, and a right-aligned axis value ran off past its tick. The widths below are Helvetica's AFM
/// advances, pinned as numbers: A and B 667, <c>?</c> 556, the em dash 1000, per 1000 units of a
/// 10-unit font.
/// </remarks>
public class SvgTextAnchorTests
{
    /// <summary>"AB" is 13.34 units wide: <c>middle</c> starts it 6.67 left of x, <c>end</c> 13.34.</summary>
    [Theory]
    [InlineData(@"text-anchor=""middle""", 93.33f)]
    [InlineData(@"text-anchor=""end""", 86.66f)]
    [InlineData(@"text-anchor=""start""", 100f)]
    [InlineData(@"", 100f)]
    public void TheAnchorPlacesTheRun(string attribute, float expectedX)
    {
        float[] td = Assert.Single(Operands(Content(Svg(@"<text x=""100"" y=""20"" font-size=""10"" " + attribute + ">AB</text>")), "Td"));

        Assert.Equal(expectedX, td[0], 3);
        Assert.Equal(180f, td[1], 3);
    }

    /// <summary><c>text-anchor</c> is inherited, and a <c>style</c> declaration sets it (§6.4).</summary>
    [Fact]
    public void TheAnchorIsInheritedAndReadFromStyle()
    {
        float inherited = Assert.Single(Operands(Content(Svg(@"<g text-anchor=""end""><text x=""100"" y=""20"" font-size=""10"">AB</text></g>")), "Td"))[0];
        float styled = Assert.Single(Operands(Content(Svg(@"<text x=""100"" y=""20"" font-size=""10"" style=""text-anchor: middle"">AB</text>")), "Td"))[0];

        Assert.Equal(86.66f, inherited, 3);
        Assert.Equal(93.33f, styled, 3);
    }

    /// <summary>
    /// The shift is along the baseline in user space, before the transform: under
    /// <c>rotate(90)</c> the end-anchored run's start (86.66, 20) turns to (−20, 86.66), page
    /// (−20, 113.34).
    /// </summary>
    [Fact]
    public void TheShiftFollowsTheTransform()
    {
        float[] tm = Assert.Single(Operands(Content(Svg(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end"" transform=""rotate(90)"">AB</text>")), "Tm"));

        Assert.Equal(-20f, tm[4], 3);
        Assert.Equal(113.34f, tm[5], 3);
    }

    /// <summary>
    /// The run is measured as drawn: an em dash, WinAnsi code 0x97, is 1000 wide, not the width of
    /// whatever code 0x97 is in Latin-1; a character WinAnsi cannot carry is drawn as <c>?</c> and
    /// measured as one.
    /// </summary>
    [Theory]
    [InlineData("A—", 83.33f)]   // 667 + 1000
    [InlineData("Aא", 87.77f)]   // 667 + 556, the '?' it is drawn as
    public void TheRunIsMeasuredAsDrawn(string run, float expectedX)
    {
        float[] td = Assert.Single(Operands(Content(Svg(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end"">" + run + "</text>")), "Td"));

        Assert.Equal(expectedX, td[0], 3);
    }

    // ------------------------------------------------------------------ helpers

    private static string Svg(string body) =>
        @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>";

    private static string Content(string svg)
    {
        byte[] pdf = SvgToPdfConverter.Convert(svg, new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static float[][] Operands(string content, string op)
        => content.Split('\n').Select(l => l.Trim())
            .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Select(parts => parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
}
