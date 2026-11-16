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
/// <see cref="SvgToPdfConverter"/> applies <c>letter-spacing</c> (SVG 1.1 §10.11) as the character
/// spacing <c>Tc</c>, and counts it in the run's advance.
/// </summary>
/// <remarks>
/// It was not read: spaced-out lettering came out tight, and a spaced run's anchor and the run after
/// it were placed by the unspaced width.
/// </remarks>
public class SvgLetterSpacingTests
{
    /// <summary>
    /// The spacing is <c>Tc</c> inside the run's text object, and set back to 0 after its <c>Tj</c>:
    /// <c>Tc</c> is graphics state, which <c>ET</c> does not reset, so the next run would inherit it.
    /// </summary>
    [Fact]
    public void LetterSpacingIsTcForItsRunOnly()
    {
        string[] lines = Lines(Content(@"<text x=""10"" y=""20"" letter-spacing=""2"">ab</text><text x=""10"" y=""40"">cd</text>"));

        int[] tcs = Enumerable.Range(0, lines.Length).Where(i => lines[i].EndsWith(" Tc", StringComparison.Ordinal)).ToArray();
        int tj = Array.IndexOf(lines, "(ab) Tj");
        Assert.Equal(new[] { 2f, 0f }, tcs.Select(i => Operand(lines[i])));
        Assert.True(Array.IndexOf(lines, "BT") < tcs[0] && tcs[0] < tj, "the spacing was not set before the run");
        Assert.True(tj < tcs[1] && tcs[1] < Array.IndexOf(lines, "ET"), "the spacing was not reset after the run");
    }

    /// <summary>
    /// "ab" at 10 in Helvetica is 11.12 wide, and 2 after each character makes 15.12: an end anchor
    /// at 100 starts it at 84.88, where it started at 88.88.
    /// </summary>
    [Fact]
    public void TheAnchorCountsTheSpacing()
    {
        string c = Content(@"<text x=""100"" y=""20"" font-size=""10"" letter-spacing=""2"" text-anchor=""end"">ab</text>");

        Assert.Equal(84.88f, TdX(c)[0], 3);
    }

    /// <summary>The spacing is inherited, and a <c>style</c> declaration sets it; the next run starts after it.</summary>
    [Fact]
    public void TheSpacingIsInheritedAndTheNextRunFollowsIt()
    {
        string c = Content(@"<g style=""letter-spacing: 2px""><text x=""10"" y=""20"" font-size=""10"">ab<tspan>c</tspan></text></g>");

        float[] xs = TdX(c);
        Assert.Equal(new[] { 2f, 0f, 2f, 0f }, Lines(c).Where(l => l.EndsWith(" Tc", StringComparison.Ordinal)).Select(Operand));
        Assert.Equal(10f, xs[0], 3);
        Assert.Equal(25.12f, xs[1], 3);   // 10 + 11.12 + 2 × 2
    }

    /// <summary>
    /// <c>normal</c>, or no spacing, writes no <c>Tc</c>: such text is written as it was. And
    /// <c>normal</c> is a keyword, not a bad length to report.
    /// </summary>
    [Theory]
    [InlineData(@"letter-spacing=""normal""")]
    [InlineData(@"letter-spacing=""0""")]
    [InlineData("")]
    public void NoSpacingWritesNoTc(string attribute)
    {
        string c = Content(@"<text x=""10"" y=""20"" " + attribute + ">ab</text>");

        Assert.Contains("(ab) Tj", Lines(c));
        Assert.DoesNotContain(Lines(c), l => l.EndsWith(" Tc", StringComparison.Ordinal));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    // ------------------------------------------------------------------ helpers

    private static float Operand(string line) => float.Parse(line.Split(' ')[0], CultureInfo.InvariantCulture);

    private static float[] TdX(string content)
        => Lines(content).Where(l => l.EndsWith(" Td", StringComparison.Ordinal))
            .Select(l => float.Parse(l.Split(' ')[0], CultureInfo.InvariantCulture)).ToArray();

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}
