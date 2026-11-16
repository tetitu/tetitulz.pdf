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
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> reads <c>direction</c> (SVG 1.1 §10.7.4): in a right-to-left
/// chunk the runs advance leftwards from the chunk's position, <c>text-anchor</c>'s start and end
/// swap (§10.9.1), and the base direction for shaping is right to left; <c>ltr</c>, the default,
/// is the base direction otherwise.
/// </summary>
/// <remarks>
/// <c>direction</c> was not read: a Hebrew label anchored at its start ran rightwards from its
/// position, and a run's base direction was guessed from its first strong character.
/// </remarks>
public class SvgTextDirectionTests
{
    private const string Hebrew = "שלום";

    /// <summary>
    /// Four glyphs of 600 at size 10 are 24 wide. Left to right the chunk starts at 100; right to
    /// left its start is its right end, so it runs from 76 to 100 — and an end anchor puts its left
    /// end at 100.
    /// </summary>
    [Theory]
    [InlineData("", "", 100f)]
    [InlineData(@" direction=""rtl""", "", 76f)]
    [InlineData(@" direction=""rtl""", @" text-anchor=""end""", 100f)]
    [InlineData(@" direction=""rtl""", @" text-anchor=""middle""", 88f)]
    [InlineData("", @" text-anchor=""end""", 76f)]
    public void TheAnchorFollowsTheDirection(string direction, string anchor, float start)
    {
        string content = Convert(@"<text x=""100"" y=""20"" font-size=""10""" + direction + anchor + ">" + Hebrew + "</text>", Hebrew);

        string td = Assert.Single(Lines(content), l => l.EndsWith(" Td", StringComparison.Ordinal));
        Assert.Equal(start, float.Parse(td.Split(' ')[0], CultureInfo.InvariantCulture), 3);
    }

    /// <summary>
    /// Runs in a right-to-left chunk advance leftwards: two tspans of 12 (Courier, two characters at
    /// 10) from 100 sit at 88 and 76; left to right, at 100 and 112. The direction is inherited.
    /// </summary>
    [Theory]
    [InlineData("", 100f, 112f)]
    [InlineData(@" direction=""rtl""", 88f, 76f)]
    public void RunsInARightToLeftChunkAdvanceLeftwards(string direction, float first, float second)
    {
        string content = Convert(@"<g font-family=""monospace"" font-size=""10""" + direction + @"><text x=""100"" y=""20""><tspan>ab</tspan><tspan>cd</tspan></text></g>", null);

        float[] starts = Lines(content).Where(l => l.EndsWith(" Td", StringComparison.Ordinal))
            .Select(l => float.Parse(l.Split(' ')[0], CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(new[] { first, second }, starts);
    }

    /// <summary>
    /// The base direction for shaping is the element's: "שלום abc" left to right keeps the Hebrew
    /// word at the left, reversed (ם ו ל ש then a b c); right to left the Latin comes first and the
    /// Hebrew is at the right. Glyph <i>i</i> is the <i>i</i>-th covered character in code-point
    /// order: space 1, a–c 2–4, ו ל ם ש 5–8.
    /// </summary>
    [Theory]
    [InlineData("", "<00070005000600080001000200030004> Tj")]
    [InlineData(@" direction=""rtl""", "<00020003000400010007000500060008> Tj")]
    public void TheBaseDirectionFollowsTheDirection(string direction, string glyphs)
    {
        string content = Convert(@"<text x=""10"" y=""20""" + direction + ">" + Hebrew + " abc</text>", Hebrew + " abc");

        Assert.Equal(new[] { glyphs }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A bidi override draws the characters in the direction whatever their class (§10.7.4): right to
    /// left, "שלום abc" is wholly reversed — c b a, the space, ם ו ל ש — where the algorithm keeps
    /// "abc" reading forwards; left to right, the logical order, the Hebrew unreversed. An inherited
    /// override and <c>isolate-override</c> do the same; <c>embed</c> is the algorithm's order (the
    /// control). It was reported and not applied.
    /// </summary>
    [Theory]
    [InlineData(@" direction=""rtl"" unicode-bidi=""bidi-override""", "", "<00040003000200010007000500060008> Tj")]
    [InlineData(@" direction=""rtl"" unicode-bidi=""isolate-override""", "", "<00040003000200010007000500060008> Tj")]
    [InlineData(@" direction=""rtl""", @" unicode-bidi=""bidi-override""", "<00040003000200010007000500060008> Tj")]
    [InlineData(@" unicode-bidi=""bidi-override""", "", "<00080006000500070001000200030004> Tj")]
    [InlineData(@" direction=""rtl"" unicode-bidi=""embed""", "", "<00020003000400010007000500060008> Tj")]
    public void ABidiOverrideOrdersTheCharactersByTheDirection(string onText, string onGroup, string glyphs)
    {
        string content = Convert(@"<g" + onGroup + @"><text x=""10"" y=""20""" + onText + ">" + Hebrew + " abc</text></g>", Hebrew + " abc");

        Assert.Equal(new[] { glyphs }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A standard face's text, which nothing else reorders, is reversed under a right-to-left
    /// override too, and left as it is under a left-to-right one.
    /// </summary>
    [Theory]
    [InlineData(@" direction=""rtl"" unicode-bidi=""bidi-override""", "(cba) Tj")]
    [InlineData(@" direction=""rtl""", "(abc) Tj")]
    [InlineData(@" unicode-bidi=""bidi-override""", "(abc) Tj")]
    public void ABidiOverrideReversesAStandardFacesText(string attributes, string shown)
    {
        string content = Convert(@"<text x=""10"" y=""20""" + attributes + ">abc</text>", null);

        Assert.Equal(new[] { shown }, Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ helpers

    private static string Convert(string body, string provided)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FontProvider = provided == null ? null : _ => MinimalTrueType.Build(provided) });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}
