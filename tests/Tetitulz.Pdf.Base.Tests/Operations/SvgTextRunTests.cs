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
/// <see cref="SvgToPdfConverter"/> lays a <c>&lt;text&gt;</c> out as runs (SVG 1.1 §10.4, §10.5):
/// each <c>&lt;tspan&gt;</c> with its own position and style, each run after the one before it,
/// white space handled as §10.15 says, and <c>text-anchor</c> applied per text chunk.
/// </summary>
/// <remarks>
/// The whole element's <c>InnerText</c> was one run at the element's position: a tspan's
/// <c>x</c>/<c>y</c>/<c>dx</c>/<c>dy</c>, fill and size were ignored, so a two-line label printed as
/// one line in one colour; and the source's newlines and indentation reached the PDF string. Widths
/// below are Helvetica's (A and B 667, C 722) at size 10; the page is 200 high and flipped, so a
/// y of 20 is 180.
/// </remarks>
public class SvgTextRunTests
{
    [Fact]
    public void WhiteSpaceIsCollapsed()
    {
        string c = Content("<text x=\"10\" y=\"20\">\n    Hello\t  world  \n</text>");

        Assert.Equal(new[] { "(Hello world) Tj" }, Shown(c));
    }

    /// <summary>With <c>xml:space="preserve"</c> a newline or a tab is a space, and nothing is collapsed.</summary>
    [Fact]
    public void PreservedWhiteSpaceIsKept()
    {
        string c = Content("<text x=\"10\" y=\"20\" xml:space=\"preserve\">  a\tb </text>");

        Assert.Equal(new[] { "(  a b ) Tj" }, Shown(c));
    }

    /// <summary>A tspan with no position continues where the run before it ended: 10 + 13.34.</summary>
    [Fact]
    public void ATspanContinuesAfterTheRunBeforeIt()
    {
        string c = Content(@"<text x=""10"" y=""20"" font-size=""10"">AB<tspan>C</tspan></text>");

        Assert.Equal(new[] { "(AB) Tj", "(C) Tj" }, Shown(c));
        AssertPositions(c, (10, 180), (23.34f, 180));
    }

    /// <summary>
    /// <c>x</c> is absolute and <c>dy</c> relative; the run after the tspan continues from its end,
    /// on its line: B at (50, 25), then C at (50 + 6.67, 25).
    /// </summary>
    [Fact]
    public void ATspansPositionMovesTheRunsThatFollow()
    {
        string c = Content(@"<text x=""10"" y=""20"" font-size=""10"">A<tspan x=""50"" dy=""5"">B</tspan>C</text>");

        Assert.Equal(new[] { "(A) Tj", "(B) Tj", "(C) Tj" }, Shown(c));
        AssertPositions(c, (10, 180), (50, 175), (56.67f, 175));
    }

    [Fact]
    public void ATspansStyleIsItsOwn()
    {
        string c = Content(@"<text x=""0"" y=""20"" fill=""#ff0000"">A<tspan fill=""#0000ff"" font-size=""20"">B</tspan></text>");

        string[] lines = Lines(c);
        int a = Array.IndexOf(lines, "(A) Tj"), b = Array.IndexOf(lines, "(B) Tj");
        Assert.Equal("1.0000 0.0000 0.0000 rg", lines.Take(a).Last(l => l.EndsWith(" rg", StringComparison.Ordinal)));
        Assert.Equal("0.0000 0.0000 1.0000 rg", lines.Take(b).Last(l => l.EndsWith(" rg", StringComparison.Ordinal)));
        Assert.Equal("/F1 12.0000 Tf", lines.Take(a).Last(l => l.EndsWith(" Tf", StringComparison.Ordinal)));
        Assert.Equal("/F1 20.0000 Tf", lines.Take(b).Last(l => l.EndsWith(" Tf", StringComparison.Ordinal)));
    }

    /// <summary><c>text-anchor</c> places the chunk — both runs — not each run: "AB" ends at 100.</summary>
    [Fact]
    public void TheAnchorPlacesTheWholeChunk()
    {
        string c = Content(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end"">A<tspan>B</tspan></text>");

        AssertPositions(c, (86.66f, 180), (93.33f, 180));
    }

    /// <summary>An absolute position starts a new chunk, anchored on its own: "A" centred on 100 at y 40.</summary>
    [Fact]
    public void ANewChunkIsAnchoredOnItsOwn()
    {
        string c = Content(@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""middle"">AB<tspan x=""100"" y=""40"">A</tspan></text>");

        AssertPositions(c, (93.33f, 180), (96.665f, 160));
    }

    /// <summary>A run that paints nothing is not drawn but still takes its width: C starts after A and B.</summary>
    [Fact]
    public void ARunWithNoFillStillAdvances()
    {
        string c = Content(@"<text x=""10"" y=""20"" font-size=""10"">A<tspan fill=""none"">B</tspan>C</text>");

        Assert.Equal(new[] { "(A) Tj", "(C) Tj" }, Shown(c));
        AssertPositions(c, (10, 180), (23.34f, 180));
    }

    /// <summary>The space between two tspans is text: it was dropped with the document's whitespace-only nodes.</summary>
    [Fact]
    public void TheSpaceBetweenTwoTspansIsKept()
    {
        string c = Content(@"<text x=""0"" y=""20""><tspan>Hello</tspan> <tspan>world</tspan></text>");

        Assert.Equal(new[] { "(Hello) Tj", "( ) Tj", "(world) Tj" }, Shown(c));
    }

    /// <summary>A comment inside a text element is not text.</summary>
    [Fact]
    public void ACommentIsNotDrawn()
    {
        string c = Content(@"<text x=""0"" y=""20"">A<!--hidden-->B</text>");

        Assert.Equal(new[] { "(A) Tj", "(B) Tj" }, Shown(c));
    }

    /// <summary>A tspan's position is where its text starts, not every run in it: C follows B at 50 + 6.67.</summary>
    [Fact]
    public void ATspansPositionAppliesToItsFirstRunOnly()
    {
        string c = Content(@"<text x=""0"" y=""20"" font-size=""10""><tspan x=""50"">B<!--c-->C</tspan></text>");

        AssertPositions(c, (50, 180), (56.67f, 180));
    }

    /// <summary><c>dx</c> on the text element itself moves its first run.</summary>
    [Fact]
    public void TheTextElementsOwnDxMovesItsFirstRun()
    {
        AssertPositions(Content(@"<text x=""10"" y=""20"" dx=""5"">A</text>"), (15, 180));
    }

    /// <summary>
    /// Hostile input: tspans nested 100 000 deep. The walk stops at the depth limit and says so,
    /// instead of recursing until the stack overflows.
    /// </summary>
    [Fact]
    public void DeeplyNestedTspansAreBoundedAndReported()
    {
        const int Depth = 100_000;
        var svg = new StringBuilder(@"<text x=""0"" y=""20"">A");
        for (int i = 0; i < Depth; i++) svg.Append("<tspan>");
        svg.Append('x');
        for (int i = 0; i < Depth; i++) svg.Append("</tspan>");
        svg.Append("</text>");

        string c = Content(svg.ToString());

        Assert.Equal(new[] { "(A) Tj" }, Shown(c));
        Assert.Contains("text nesting exceeded", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertPositions(string content, params (float X, float Y)[] expected)
    {
        var td = Operands(content, "Td");
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

    private static float[][] Operands(string content, string op)
        => Lines(content)
            .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Select(parts => parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
}
