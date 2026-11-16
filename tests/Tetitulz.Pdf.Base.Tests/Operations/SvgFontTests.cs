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
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> sets each text run in the standard face its <c>font-family</c>,
/// <c>font-weight</c> and <c>font-style</c> choose (SVG 1.1 §10.10), and measures it in that face.
/// </summary>
/// <remarks>
/// Every run was Helvetica: a serif or monospaced label, or a bold heading, came out in plain
/// Helvetica, and <c>text-anchor</c> placed it by Helvetica's widths.
/// </remarks>
public class SvgFontTests
{
    [Theory]
    [InlineData(@"font-family=""serif""", "Times-Roman")]
    [InlineData(@"font-family=""monospace""", "Courier")]
    [InlineData(@"font-family=""sans-serif""", "Helvetica")]
    [InlineData(@"font-family=""'Times New Roman', serif""", "Times-Roman")]
    [InlineData(@"font-family=""Arial""", "Helvetica")]
    [InlineData(@"font-family=""Courier New""", "Courier")]
    [InlineData(@"font-family=""'Courier New'""", "Courier")]
    [InlineData(@"font-family=""Roboto, monospace""", "Courier")]
    [InlineData(@"style=""font-family: serif""", "Times-Roman")]
    public void TheFamilyChoosesTheFace(string attribute, string face)
    {
        Assert.Equal(new[] { face }, Faces(@"<text x=""10"" y=""20"" " + attribute + ">Hi</text>"));
    }

    [Theory]
    [InlineData(@"font-weight=""bold""", "Helvetica-Bold")]
    [InlineData(@"font-weight=""bolder""", "Helvetica-Bold")]
    [InlineData(@"font-weight=""700""", "Helvetica-Bold")]
    [InlineData(@"font-weight=""600""", "Helvetica-Bold")]
    [InlineData(@"font-weight=""500""", "Helvetica")]
    [InlineData(@"font-weight=""normal""", "Helvetica")]
    [InlineData(@"font-style=""italic""", "Helvetica-Oblique")]
    [InlineData(@"font-style=""oblique""", "Helvetica-Oblique")]
    [InlineData(@"font-family=""serif"" font-style=""italic""", "Times-Italic")]
    [InlineData(@"font-family=""serif"" font-weight=""bold""", "Times-Bold")]
    [InlineData(@"font-family=""monospace"" font-weight=""bold"" font-style=""italic""", "Courier-BoldOblique")]
    public void TheWeightAndStyleChooseTheFace(string attribute, string face)
    {
        Assert.Equal(new[] { face }, Faces(@"<text x=""10"" y=""20"" " + attribute + ">Hi</text>"));
    }

    /// <summary>The three are inherited: a group's family and a tspan's weight reach its run.</summary>
    [Fact]
    public void TheFaceIsInheritedPerRun()
    {
        Assert.Equal(new[] { "Times-Roman", "Times-Bold" },
            Faces(@"<g font-family=""serif""><text x=""10"" y=""20"">a<tspan font-weight=""bold"">b</tspan></text></g>"));
    }

    /// <summary>
    /// Helvetica is still <c>/F1</c>, alone in <c>/Font</c> when nothing else is used; each other face
    /// is one resource, however many runs use it.
    /// </summary>
    [Fact]
    public void EachFaceIsOneResource()
    {
        var (_, plainFonts) = Convert(@"<text x=""10"" y=""20"">a</text>");
        var (content, fonts) = Convert(
            @"<text x=""10"" y=""20"" font-family=""serif"">a</text><text x=""10"" y=""40"" font-family=""monospace"">b</text>" +
            @"<text x=""10"" y=""60"" font-family=""serif"">c</text><text x=""10"" y=""80"">d</text>");

        Assert.Equal(new[] { "F1" }, plainFonts.Keys.Select(k => k.Value));
        Assert.Equal(new[] { "F1", "F2", "F3" }, fonts.Keys.Select(k => k.Value).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "F2", "F3", "F2", "F1" }, Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal)).Select(l => l.Split(' ')[0].TrimStart('/')));
        Assert.Equal("Times-Roman", BaseFont(fonts, "F2"));
        Assert.Equal("Courier", BaseFont(fonts, "F3"));
        Assert.Equal("WinAnsiEncoding", ((PdfName)((PdfDictionary)fonts[new PdfName("F2")])[PdfName.Encoding]).Value);
    }

    /// <summary>
    /// The run is measured in its face: "ab" at 10 is 12 wide in Courier (600 each), so an end
    /// anchor at 100 starts it at 88. In Helvetica's widths (556 each) it started at 88.88.
    /// </summary>
    [Fact]
    public void TheAnchorMeasuresInTheFace()
    {
        var (content, _) = Convert(@"<text x=""100"" y=""20"" font-size=""10"" font-family=""monospace"" text-anchor=""end"">ab</text>");

        string td = Assert.Single(Lines(content), l => l.EndsWith(" Td", StringComparison.Ordinal));
        Assert.Equal(88f, float.Parse(td.Split(' ')[0], CultureInfo.InvariantCulture), 3);
    }

    /// <summary>
    /// A family none of the standard faces stands for is set in Helvetica and reported; a list that
    /// names one after it is not reported.
    /// </summary>
    [Fact]
    public void AnUnknownFamilyIsSetInHelveticaAndReported()
    {
        Assert.Equal(new[] { "Helvetica-Bold" }, Faces(@"<text x=""10"" y=""20"" font-family=""Roboto"" font-weight=""bold"">a</text>"));
        Assert.Contains("Roboto", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);

        Assert.Equal(new[] { "Times-Roman" }, Faces(@"<text x=""10"" y=""20"" font-family=""Roboto, serif"">a</text>"));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>
    /// Symbol and ZapfDingbats are standard faces, but symbolic: their codes are not WinAnsi's, so a
    /// run in either would draw other glyphs. It is set in Helvetica and reported.
    /// </summary>
    [Fact]
    public void ASymbolicFamilyIsNotUsed()
    {
        Assert.Equal(new[] { "Helvetica" }, Faces(@"<text x=""10"" y=""20"" font-family=""Symbol"">a</text>"));
        Assert.Contains("Symbol", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static string[] Faces(string body)
    {
        var (content, fonts) = Convert(body);
        return Lines(content).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal))
            .Select(l => BaseFont(fonts, l.Split(' ')[0].TrimStart('/'))).ToArray();
    }

    private static string BaseFont(PdfDictionary fonts, string name)
        => ((PdfName)((PdfDictionary)fonts[new PdfName(name)])[PdfName.BaseFont]).Value;

    private static (string Content, PdfDictionary Fonts) Convert(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]));
        return (Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), fonts);
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}
