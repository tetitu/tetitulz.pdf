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
/// <see cref="SvgToPdfConverter"/> reads a position list — <c>x</c>, <c>y</c>, <c>dx</c>, <c>dy</c>
/// with one value per character (SVG 1.1 §10.4, §10.5): the i-th value is the i-th character's,
/// counted across the element's descendants, and a character past the list takes the nearest
/// ancestor's value for it, if any; else it follows the character before it.
/// </summary>
/// <remarks>
/// A list was read as its first value and reported, so text a tool had laid out character by
/// character — kerned headings, charts' tick labels, anything exported with per-glyph positions —
/// was set as one run from its first position. Widths are Helvetica's at size 10 (A and B 667,
/// C 722); the page is 200 high and flipped, so a y of 20 is 180.
/// </remarks>
public class SvgTextPositionListTests
{
    [Fact]
    public void APositionListPlacesEachCharacter()
    {
        string c = Content(@"<text x=""10 20 30"" y=""20"" font-size=""10"">ABC</text>");

        Assert.Equal(new[] { "(A) Tj", "(B) Tj", "(C) Tj" }, Shown(c));
        AssertPositions(c, (10, 180), (20, 180), (30, 180));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>Past the list, the characters follow the last one it placed: B at 20, then C after it.</summary>
    [Fact]
    public void CharactersPastTheListFollowOn()
    {
        string c = Content(@"<text x=""10 20"" y=""20"" font-size=""10"">ABC</text>");

        Assert.Equal(new[] { "(A) Tj", "(BC) Tj" }, Shown(c));
        AssertPositions(c, (10, 180), (20, 180));
    }

    /// <summary><c>dx</c> is relative, per character: A at 10, B at 10 + 6.67 + 5, C 6.67 + 5 after that.</summary>
    [Fact]
    public void ADxListShiftsEachCharacter()
    {
        string c = Content(@"<text x=""10"" y=""20"" dx=""0 5 5"" font-size=""10"">ABC</text>");

        AssertPositions(c, (10, 180), (21.67f, 180), (33.34f, 180));
    }

    /// <summary>A <c>y</c> list moves each character to its own line, the x following on: B at (16.67, 30).</summary>
    [Fact]
    public void AYListPlacesEachCharacterOnItsLine()
    {
        string c = Content(@"<text x=""10"" y=""20 30"" font-size=""10"">AB</text>");

        AssertPositions(c, (10, 180), (16.67f, 170));
    }

    /// <summary>
    /// The list counts the characters of the element's descendants too: in a tspan with no list of
    /// its own, B and C take the text element's second and third values.
    /// </summary>
    [Fact]
    public void AnAncestorsListReachesATspan()
    {
        string c = Content(@"<text x=""10 20 30"" y=""20"" font-size=""10"">A<tspan>BC</tspan></text>");

        AssertPositions(c, (10, 180), (20, 180), (30, 180));
    }

    /// <summary>
    /// A tspan's own list wins for its characters, and past it the nearest ancestor's value for that
    /// character applies: B at the tspan's 100, C at the text element's third value, 30.
    /// </summary>
    [Fact]
    public void ATspansListWinsAndTheAncestorsFillsIn()
    {
        string c = Content(@"<text x=""10 20 30"" y=""20"" font-size=""10"">A<tspan x=""100"">BC</tspan></text>");

        AssertPositions(c, (10, 180), (100, 180), (30, 180));
    }

    /// <summary>Each absolutely placed character is a text chunk, anchored on its own: A centred on 100, B on 150.</summary>
    [Fact]
    public void EachPlacedCharacterIsAChunk()
    {
        string c = Content(@"<text x=""100 150"" y=""20"" font-size=""10"" text-anchor=""middle"">AB</text>");

        AssertPositions(c, (96.665f, 180), (146.665f, 180));
    }

    /// <summary>
    /// The characters counted are the ones drawn, after white space is collapsed: the leading
    /// newline and indentation are not characters, so A takes 10 and B 20.
    /// </summary>
    [Fact]
    public void TheListCountsCollapsedCharacters()
    {
        string c = Content("<text x=\"10 20\" y=\"20\" font-size=\"10\">\n    AB</text>");

        AssertPositions(c, (10, 180), (20, 180));
    }

    /// <summary>
    /// A list longer than its element's text stops there: the tspan's 20 and 30 have no character, and
    /// B and C after it follow on from A at 10 (§10.5: extra values have no effect).
    /// </summary>
    [Fact]
    public void AListLongerThanItsTextStopsThere()
    {
        string c = Content(@"<text x=""0"" y=""20"" font-size=""10""><tspan x=""10 20 30"">A</tspan>BC</text>");

        Assert.Equal(new[] { "(A) Tj", "(BC) Tj" }, Shown(c));
        AssertPositions(c, (10, 180), (16.67f, 180));
    }

    /// <summary>
    /// A trailing space that white space handling strips is no character, and takes no value: the
    /// list's third value has nothing to place.
    /// </summary>
    [Fact]
    public void AStrippedTrailingSpaceTakesNoValue()
    {
        string c = Content(@"<text x=""10 20 30"" y=""20"" font-size=""10"">AB </text>");

        Assert.Equal(new[] { "(A) Tj", "(B) Tj" }, Shown(c));
        AssertPositions(c, (10, 180), (20, 180));
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertPositions(string content, params (float X, float Y)[] expected)
    {
        var td = Lines(content).Where(l => l.EndsWith(" Td", StringComparison.Ordinal))
            .Select(l => l.Split(' ').Take(2).Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        Assert.Equal(expected.Length, td.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, td[i][0], 2);
            Assert.Equal(expected[i].Y, td[i][1], 2);
        }
    }

    private static string[] Shown(string content) => Lines(content).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal)).ToArray();

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
