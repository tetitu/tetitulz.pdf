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
/// <see cref="SvgToPdfConverter"/> turns each character by a text's <c>rotate</c> list (SVG 1.1
/// §10.5): the i-th value is the i-th character's, the last value the rest's, a descendant's list
/// wins for its characters, and a glyph turns about its own origin without changing its advance.
/// </summary>
/// <remarks>
/// The list was ignored without a report, so every glyph was set upright. At the default mapping (the
/// page flipped, 200 high), a character at (x, 20) turned 90° is set by <c>Tm</c> <c>0 -1 1 0 x 180</c>:
/// its baseline runs down the page, as SVG's y-down rotation has it. Widths are Helvetica's at size 10
/// (A and B 667).
/// </remarks>
public class SvgTextRotateTests
{
    [Fact]
    public void ARotateTurnsItsCharacter()
    {
        string c = Content(@"<text x=""10"" y=""20"" font-size=""10"" rotate=""90"">A</text>");

        Assert.Equal(new[] { 0f, -1, 1, 0, 10, 180 }, Single(c, "Tm"));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// The last value turns the rest: A upright at 10, B and C each turned 90° at their own places,
    /// which the turn does not move: 16.67 and 23.34.
    /// </summary>
    [Fact]
    public void TheLastValueTurnsTheRest()
    {
        string c = Content(@"<text x=""10"" y=""20"" font-size=""10"" rotate=""0 90"">ABC</text>");

        Assert.Equal(new[] { "(A) Tj", "(B) Tj", "(C) Tj" }, Shown(c));
        Assert.Equal(10f, Single(c, "Td")[0], 2);
        float[][] tm = All(c, "Tm");
        Assert.Equal(2, tm.Length);
        Assert.Equal(16.67f, tm[0][4], 2);
        Assert.Equal(23.34f, tm[1][4], 2);
        Assert.All(tm, m => Assert.Equal(new[] { 0f, -1, 1, 0 }, m.Take(4)));
    }

    /// <summary>
    /// An ancestor's list reaches a tspan's characters; a tspan's own list wins for them, its last
    /// value turning the rest of the tspan: B and C at 45°.
    /// </summary>
    [Theory]
    [InlineData(@"<text x=""10"" y=""20"" font-size=""10"" rotate=""0 90 180"">A<tspan>BC</tspan></text>", 90f, 180f)]
    [InlineData(@"<text x=""10"" y=""20"" font-size=""10"" rotate=""0 90 180"">A<tspan rotate=""45"">BC</tspan></text>", 45f, 45f)]
    [InlineData(@"<text x=""10"" y=""20"" font-size=""10"" rotate=""0"">A<tspan rotate=""10 20"">BC</tspan></text>", 10f, 20f)]   // counted from the tspan's first character
    public void ATspanTakesTheNearestList(string body, float b, float cAngle)
    {
        float[][] tm = All(Content(body), "Tm");

        Assert.Equal(2, tm.Length);
        Assert.Equal(b, Angle(tm[0]), 1);
        Assert.Equal(cAngle, Angle(tm[1]), 1);
    }

    /// <summary>
    /// A turned character does not turn the ones after it: with <c>rotate="90 0"</c> A is turned and
    /// BC, upright, is one run set by <c>Td</c> after it, at 10 + 6.67.
    /// </summary>
    [Fact]
    public void ATurnedCharacterDoesNotTurnTheNext()
    {
        string c = Content(@"<text x=""10"" y=""20"" font-size=""10"" rotate=""90 0"">ABC</text>");

        Assert.Equal(new[] { "(A) Tj", "(BC) Tj" }, Shown(c));
        Assert.Single(All(c, "Tm"));
        Assert.Equal(16.67f, Single(c, "Td")[0], 2);
    }

    /// <summary>A rotation of 0, or none, sets the text upright by <c>Td</c>, as it was.</summary>
    [Theory]
    [InlineData(@"rotate=""0""")]
    [InlineData("")]
    public void NoRotationIsUpright(string attribute)
    {
        string c = Content(@"<text x=""10"" y=""20"" font-size=""10"" " + attribute + ">AB</text>");

        Assert.Equal(new[] { "(AB) Tj" }, Shown(c));
        Assert.Empty(All(c, "Tm"));
        Assert.Single(All(c, "Td"));
    }

    /// <summary>
    /// The turn composes with the element's transform: under <c>rotate(90)</c> a character turned
    /// 90° more is turned 180° in all, upside down, at its mapped place.
    /// </summary>
    [Fact]
    public void TheTurnComposesWithTheTransform()
    {
        float[] tm = Single(Content(@"<text x=""10"" y=""20"" font-size=""10"" rotate=""90"" transform=""rotate(90)"">A</text>"), "Tm");

        Assert.Equal(180f, Angle(tm), 1);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The turn a text matrix gives its glyph in SVG's y-down terms, in degrees [0, 360): the page's
    /// y is flipped, so the baseline direction (a, b) is (cos θ, −sin θ).
    /// </summary>
    private static float Angle(float[] tm)
    {
        double degrees = Math.Atan2(-tm[1], tm[0]) * 180 / Math.PI;
        return (float)((degrees + 360) % 360);
    }

    private static float[] Single(string content, string op) => Assert.Single(All(content, op));

    private static float[][] All(string content, string op)
        => Lines(content).Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => (float)Math.Round(float.Parse(p, CultureInfo.InvariantCulture), 3)).ToArray())
            .ToArray();

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
