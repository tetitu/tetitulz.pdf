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
/// <see cref="SvgToPdfConverter"/> reads a length's unit rather than cutting it off.
/// </summary>
/// <remarks>
/// <para>A user unit is a CSS pixel (SVG 1.1 §7.10), so <c>10mm</c> is 96 ÷ 25.4 × 10 ≈ 37.8 user
/// units. The converter trimmed the letters <c>p</c>, <c>t</c>, <c>x</c>, <c>e</c>, <c>m</c> and
/// <c>%</c> off the end and kept the number, so <c>10mm</c> was 10 user units, <c>1in</c> did not
/// parse at all and fell back to the default, and no unit was ever reported. The absolute units go
/// through <c>Core.Text.CssLength</c>, the table HtmlToPdf reads too. A relative unit (<c>em</c>,
/// <c>ex</c>, <c>%</c>) needs a font size or a viewport this converter does not model: it is read as
/// its number, as before, and reported.</para>
/// <para>Every fixture draws at <c>FlipY = false</c> on a 200×200 page with no viewBox, so the
/// rectangle's operands are its user-unit lengths.</para>
/// </remarks>
public class SvgLengthUnitTests
{
    private static readonly SvgConversionOptions Unflipped =
        new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false };

    [Theory]
    [InlineData("10", 10)]
    [InlineData("10px", 10)]
    [InlineData("1in", 96)]
    [InlineData("2.54cm", 96)]
    [InlineData("25.4mm", 96)]
    [InlineData("72pt", 96)]
    [InlineData("6pc", 96)]
    [InlineData("10mm", 37.795)]
    [InlineData("1e1mm", 37.795)]
    public void AnAbsoluteUnitIsConvertedToUserUnits(string width, double expected)
    {
        float[] re = Rect($@"<rect x=""0"" y=""0"" width=""{width}"" height=""1""/>");

        Assert.Equal(expected, re[2], tolerance: 1e-3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A font-relative unit is of the element's font size (§10.10): an em the size itself, an ex half
    /// of it — inherited from a group, or 12 where nothing sets one. A percentage is of the viewport
    /// since batch 50 (<see cref="SvgPercentLengthTests"/>). It was read as its number and reported.
    /// </summary>
    [Theory]
    [InlineData("3em", @"font-size=""10""", 30)]
    [InlineData("3ex", @"font-size=""10""", 15)]
    [InlineData("3em", "", 36)]
    [InlineData("2em", @"font-size=""150%""", 36)]
    public void AFontRelativeUnitIsOfTheElementsFontSize(string width, string groupAttributes, float expected)
    {
        float[] re = Rect($@"<g {groupAttributes}><rect x=""0"" y=""0"" width=""{width}"" height=""1""/></g>");

        Assert.Equal(expected, re[2], 3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A font size is resolved down the tree: each value against the size inherited so far — a
    /// percentage or an em of the parent's, a step of 1.2 for larger and smaller, the absolute
    /// keywords from medium, 12 — and a plain length as it is.
    /// </summary>
    [Theory]
    [InlineData(@"<g font-size=""10""><text font-size=""2em"">A</text></g>", 20)]
    [InlineData(@"<g font-size=""20""><text font-size=""50%"">A</text></g>", 10)]
    [InlineData(@"<g font-size=""10""><text font-size=""3ex"">A</text></g>", 15)]
    [InlineData(@"<g font-size=""10""><text font-size=""larger"">A</text></g>", 12)]
    [InlineData(@"<g font-size=""12""><text font-size=""smaller"">A</text></g>", 10)]
    [InlineData(@"<text font-size=""large"">A</text>", 14.4)]
    [InlineData(@"<text font-size=""xx-small"">A</text>", 6.944)]
    [InlineData(@"<g font-size=""200%""><g font-size=""50%""><text>A</text></g></g>", 12)]
    [InlineData(@"<g font-size=""20""><text font-size=""12pt"">A</text></g>", 16)]
    public void AFontSizeIsResolvedDownTheTree(string body, double expected)
    {
        string content = Content(body.Replace("<text", @"<text x=""0"" y=""20"""));

        Assert.Equal(expected, FontSize(content), 2);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A stroke width, a dash array and a letter spacing in em follow the element's font size.</summary>
    [Fact]
    public void StrokeDashAndSpacingInEmFollowTheFontSize()
    {
        string stroke = Content(@"<rect x=""0"" y=""0"" width=""10"" height=""10"" font-size=""20"" stroke=""black"" stroke-width=""1em"" stroke-dasharray=""1em 0.5em"" stroke-dashoffset=""1ex""/>");
        string text = Content(@"<text x=""0"" y=""20"" font-size=""10"" letter-spacing=""0.5em"">AB</text>");

        string[] lines = stroke.Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Contains(lines, l => l.EndsWith(" w", StringComparison.Ordinal) && Operand(l, 0) == 20f);
        Assert.Contains("[20.0000 10.0000] 10.0000 d", lines);
        string tc = text.Split('\n').Select(l => l.Trim()).First(l => l.EndsWith(" Tc", StringComparison.Ordinal));
        Assert.Equal(5f, Operand(tc, 0), 3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A percentage letter or word spacing is a share of the element's font size, as Chromium draws it
    /// (Edge's print): at font-size 20, letter-spacing 10% is 2 and word-spacing 100% is 20; at the
    /// default 12, 50% is 6 — nothing reported, and not read as that many user units.
    /// </summary>
    [Fact]
    public void APercentageSpacingIsAShareOfTheFontSize()
    {
        string Line(string content, string op) => content.Split('\n').Select(l => l.Trim()).First(l => l.EndsWith(" " + op, StringComparison.Ordinal));

        string sized = Content(@"<text x=""0"" y=""20"" font-size=""20"" letter-spacing=""10%"" word-spacing=""100%"">AB CD</text>");
        Assert.Equal(2f, Operand(Line(sized, "Tc"), 0), 3);
        Assert.Equal(20f, Operand(Line(sized, "Tw"), 0), 3);
        Assert.Null(SvgToPdfConverter.LastError);

        string unsized = Content(@"<text x=""0"" y=""20"" letter-spacing=""50%"">AB</text>");
        Assert.Equal(6f, Operand(Line(unsized, "Tc"), 0), 3);
        Assert.Null(SvgToPdfConverter.LastError);

        // A percentage only when the whole value is a number and its sign: "5px%" is no length, reported.
        Content(@"<text x=""0"" y=""20"" font-size=""20"" letter-spacing=""5px%"">AB</text>");
        Assert.Contains("5px%", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A unit that is not a unit, or a value that is not a number, falls back to the default and says so.</summary>
    [Theory]
    [InlineData("10furlong")]
    [InlineData("wide")]
    public void AnUnreadableLengthFallsBackAndIsReported(string width)
    {
        float[] re = Rect($@"<rect x=""0"" y=""0"" width=""{width}"" height=""1""/>");

        Assert.Equal(0f, re[2]);
        Assert.Contains(width, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A font size takes a unit too: <c>12pt</c> is 16 user units.</summary>
    [Fact]
    public void AFontSizeInPointsIsConverted()
    {
        string content = Content(@"<text x=""0"" y=""20"" font-size=""12pt"">A</text>");

        string tf = content.Split('\n').Select(l => l.Trim()).First(l => l.EndsWith(" Tf", StringComparison.Ordinal));
        Assert.Equal(16f, float.Parse(tf.Split(' ')[1], CultureInfo.InvariantCulture), 3);
    }

    // ------------------------------------------------------------------ helpers

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            Unflipped);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    /// <summary>The size operand of the first <c>Tf</c>.</summary>
    private static float FontSize(string content)
    {
        string tf = content.Split('\n').Select(l => l.Trim()).First(l => l.EndsWith(" Tf", StringComparison.Ordinal));
        return Operand(tf, 1);
    }

    private static float Operand(string line, int index)
        => float.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[index], CultureInfo.InvariantCulture);

    /// <summary>The four operands of the first <c>re</c>.</summary>
    private static float[] Rect(string body)
    {
        string line = Content(body).Split('\n').Select(l => l.Trim())
                                   .First(l => l.EndsWith(" re", StringComparison.Ordinal));
        return line.Substring(0, line.Length - 3).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }
}
