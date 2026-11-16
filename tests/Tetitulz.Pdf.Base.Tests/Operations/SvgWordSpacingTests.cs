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
/// <see cref="SvgToPdfConverter"/> applies <c>word-spacing</c> (SVG 1.1 §10.11) as the word spacing
/// <c>Tw</c>, which a simple font adds at each byte 32 — the WinAnsi space — and counts it in the
/// run's advance.
/// </summary>
/// <remarks>
/// It was not read: widely spaced words came out at the font's own space, and a spaced run's anchor
/// and the run after it were placed by the unspaced width.
/// </remarks>
public class SvgWordSpacingTests
{
    /// <summary>
    /// The spacing is <c>Tw</c> inside the run's text object, and set back to 0 after its <c>Tj</c>:
    /// <c>Tw</c> is graphics state, which <c>ET</c> does not reset.
    /// </summary>
    [Fact]
    public void WordSpacingIsTwForItsRunOnly()
    {
        string[] lines = Lines(Content(@"<text x=""10"" y=""20"" word-spacing=""5"">a b</text><text x=""10"" y=""40"">c d</text>"));

        int[] tws = Enumerable.Range(0, lines.Length).Where(i => lines[i].EndsWith(" Tw", StringComparison.Ordinal)).ToArray();
        int tj = Array.IndexOf(lines, "(a b) Tj");
        Assert.Equal(new[] { 5f, 0f }, tws.Select(i => Operand(lines[i])));
        Assert.True(Array.IndexOf(lines, "BT") < tws[0] && tws[0] < tj, "the spacing was not set before the run");
        Assert.True(tj < tws[1] && tws[1] < Array.IndexOf(lines, "ET"), "the spacing was not reset after the run");
    }

    /// <summary>
    /// "a b c" at 10 in Helvetica is 556 + 278 + 556 + 278 + 500 = 2168, 21.68 wide, and 5 at each of
    /// its two spaces makes 31.68: an end anchor at 100 starts it at 68.32. The spacing is per space,
    /// not per character, so "abc" beside it is not moved.
    /// </summary>
    [Fact]
    public void TheAnchorCountsTheSpacingAtEachSpace()
    {
        string spaced = Content(@"<text x=""100"" y=""20"" font-size=""10"" word-spacing=""5"" text-anchor=""end"">a b c</text>");
        string word = Content(@"<text x=""100"" y=""20"" font-size=""10"" word-spacing=""5"" text-anchor=""end"">abc</text>");

        Assert.Equal(68.32f, TdX(spaced)[0], 3);
        Assert.Equal(83.88f, TdX(word)[0], 3);   // 556 + 556 + 500, no space to widen
    }

    /// <summary>The spacing is inherited, and a <c>style</c> declaration sets it; the next run starts after it.</summary>
    [Fact]
    public void TheSpacingIsInheritedAndTheNextRunFollowsIt()
    {
        string c = Content(@"<g style=""word-spacing: 5px""><text x=""10"" y=""20"" font-size=""10"">a b<tspan>c</tspan></text></g>");

        float[] xs = TdX(c);
        Assert.Equal(new[] { 5f, 0f, 5f, 0f }, Lines(c).Where(l => l.EndsWith(" Tw", StringComparison.Ordinal)).Select(Operand));
        Assert.Equal(10f, xs[0], 3);
        Assert.Equal(28.9f, xs[1], 3);   // 10 + (556 + 278 + 556) / 100 + 5
    }

    /// <summary>
    /// <c>normal</c>, or no spacing, writes no <c>Tw</c>: such text is written as it was. And
    /// <c>normal</c> is a keyword, not a bad length to report.
    /// </summary>
    [Theory]
    [InlineData(@"word-spacing=""normal""")]
    [InlineData(@"word-spacing=""0""")]
    [InlineData("")]
    public void NoSpacingWritesNoTw(string attribute)
    {
        string c = Content(@"<text x=""10"" y=""20"" " + attribute + ">a b</text>");

        Assert.Contains("(a b) Tj", Lines(c));
        Assert.DoesNotContain(Lines(c), l => l.EndsWith(" Tw", StringComparison.Ordinal));
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
