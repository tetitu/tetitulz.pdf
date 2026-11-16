// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> reads SVG's number grammar and CSS's colours.
/// </summary>
/// <remarks>
/// <para><b>Numbers.</b> The path tokenizer took every digit and point it met, so <c>1.5.5</c>
/// (which illustration tools write for <c>1.5 .5</c>) was one token that parsed as 0. An exponent
/// split at its <c>e</c>, which then went through as a path command. Point lists and transform
/// arguments split on separators only, so <c>10-5</c> was dropped. <c>char.IsDigit</c> took
/// non-ASCII digits into tokens the number parse rejected.</para>
/// <para><b>Colours.</b> Sixteen names, no percentages, no <c>hsl()</c>, and black for anything
/// else, with no report for an unknown name. The colour reading is now
/// <c>Core.Text.CssColor</c>, which HtmlToPdf reads too.</para>
/// <para>Every fixture here draws at <c>FlipY = false</c> on a 200×200 page with no viewBox, so a
/// user unit is a point and the operands are the SVG's own numbers.</para>
/// </remarks>
public class SvgNumberAndColourTests
{
    private static readonly SvgConversionOptions Unflipped =
        new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false };

    // ── numbers ──────────────────────────────────────────────────────────────

    [Fact]
    public void ARunOfTwoPointsIsTwoNumbers()
    {
        string content = Content(@"<path d=""M1.5.5L10 10"" stroke=""black"" fill=""none""/>");

        Assert.Equal(new[] { 1.5f, 0.5f }, Operands(content, "m"));
        Assert.Equal(new[] { 10f, 10f }, Operands(content, "l"));
    }

    [Fact]
    public void AnExponentIsPartOfItsNumberAndNotACommand()
    {
        string content = Content(@"<path d=""M1e1 2E1L3e0 4"" stroke=""black"" fill=""none""/>");

        Assert.Equal(new[] { 10f, 20f }, Operands(content, "m"));
        Assert.Equal(new[] { 3f, 4f }, Operands(content, "l"));
        Assert.DoesNotContain("unsupported path command", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignStartsANewNumberInAPointList()
    {
        string content = Content(@"<polyline points=""10-5 20-10"" stroke=""black"" fill=""none""/>");

        Assert.Equal(new[] { 10f, -5f }, Operands(content, "m"));
        Assert.Equal(new[] { 20f, -10f }, Operands(content, "l"));
    }

    [Fact]
    public void ASignStartsANewNumberInTransformArguments()
    {
        string content = Content(
            @"<g transform=""translate(10-5)""><rect x=""0"" y=""0"" width=""4"" height=""4""/></g>");

        Assert.Equal(new[] { 10f, -5f, 4f, 4f }, Operands(content, "re"));
    }

    /// <summary>
    /// A non-ASCII digit is not an SVG digit. The moveto it would have written is not written, and
    /// the path reports its half-written pair rather than placing a point at 0.
    /// </summary>
    [Fact]
    public void ANonAsciiDigitIsNotANumber()
    {
        string content = Content("<path d=\"M" + (char)0x0661 + (char)0x0660 + " 10\" stroke=\"black\" fill=\"none\"/>");

        Assert.Empty(OperandLines(content, "m"));
        Assert.Contains("half-written coordinate pair", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void AViewBoxWithExponentsIsRead()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0,0,1e2,1e2""><rect x=""0"" y=""0"" width=""10"" height=""10""/></svg>",
            Unflipped);

        // A 100-unit viewBox on a 200-point page: the rectangle doubles.
        Assert.Equal(new[] { 0f, 0f, 20f, 20f }, Operands(ContentOf(pdf), "re"));
    }

    /// <summary>
    /// A viewBox with something after its four numbers is not a viewBox, as before.
    /// </summary>
    [Fact]
    public void AViewBoxWithTrailingJunkIsIgnored()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 100 100x""><rect x=""0"" y=""0"" width=""10"" height=""10""/></svg>",
            Unflipped);

        Assert.Equal(new[] { 0f, 0f, 10f, 10f }, Operands(ContentOf(pdf), "re"));
    }

    // ── colours ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("steelblue", 0x46, 0x82, 0xB4)]
    [InlineData("SteelBlue", 0x46, 0x82, 0xB4)]
    [InlineData("rebeccapurple", 0x66, 0x33, 0x99)]
    [InlineData("rgb(100%, 50%, 0%)", 255, 127.5, 0)]
    [InlineData("hsl(120, 100%, 25%)", 0, 127.5, 0)]
    [InlineData("rgba(0, 0, 255, 1)", 0, 0, 255)]   // an alpha below 1 is reported: SvgUnappliedPropertyTests
    public void ACssColourIsReadAndNothingIsReported(string fill, double r, double g, double b)
    {
        string content = Content($@"<rect x=""0"" y=""0"" width=""4"" height=""4"" fill=""{fill}""/>");

        float[] rgb = Operands(content, "rg");
        Assert.Equal(r / 255, rgb[0], tolerance: 1e-3);
        Assert.Equal(g / 255, rgb[1], tolerance: 1e-3);
        Assert.Equal(b / 255, rgb[2], tolerance: 1e-3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// A name that is not a colour is drawn black and reported. It was drawn black with nothing
    /// said.
    /// </summary>
    [Fact]
    public void AnUnknownColourNameIsReported()
    {
        string content = Content(@"<rect x=""0"" y=""0"" width=""4"" height=""4"" fill=""notacolour""/>");

        Assert.Equal(new[] { 0f, 0f, 0f }, Operands(content, "rg"));
        Assert.Contains("notacolour", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static string Content(string body)
        => ContentOf(SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            Unflipped));

    private static string ContentOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    /// <summary>The operand lines of every <paramref name="op"/> in the content, in order.</summary>
    private static string[] OperandLines(string content, string op)
        => content.Split('\n')
                  .Select(l => l.Trim())
                  .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal) && !l.StartsWith("%", StringComparison.Ordinal))
                  .Select(l => l.Substring(0, l.Length - op.Length - 1))
                  .ToArray();

    /// <summary>The numbers of the first <paramref name="op"/> in the content.</summary>
    private static float[] Operands(string content, string op)
    {
        string line = OperandLines(content, op).FirstOrDefault();
        Assert.True(line != null, $"no `{op}` was written");
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
                   .ToArray();
    }
}
