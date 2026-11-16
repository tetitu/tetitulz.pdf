// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> paints nothing for a fully transparent paint — the
/// <c>transparent</c> keyword, <c>rgba(…, 0)</c> — as it paints nothing for <c>none</c>.
/// </summary>
/// <remarks>
/// <c>transparent</c> was not a colour the converter read, so <c>fill="transparent"</c> — common in
/// files from tools that write SVG 2 — was reported as not a colour and filled <b>black</b>. And
/// text whose fill painted nothing was drawn anyway, in whatever colour the graphics state held.
/// </remarks>
public class SvgTransparentPaintTests
{
    /// <summary>
    /// A transparent fill beside a stroke is stroked only; a transparent stroke beside a fill is
    /// filled only; nothing is reported. The controls paint both.
    /// </summary>
    [Theory]
    [InlineData(@"fill=""transparent"" stroke=""black""", "S")]
    [InlineData(@"fill=""rgba(255, 0, 0, 0)"" stroke=""black""", "S")]
    [InlineData(@"fill=""red"" stroke=""transparent""", "f")]
    [InlineData(@"fill=""transparent""", "n")]
    [InlineData(@"fill=""red"" stroke=""black""", "B")]
    public void ATransparentPaintIsNotPainted(string paint, string op)
    {
        string c = Content(@"<rect width=""10"" height=""10"" " + paint + "/>");

        Assert.Equal(op, Lines(c).Single(l => l is "S" or "f" or "B" or "n" or "f*" or "B*"));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A transparent fill writes no fill colour; the control writes red.</summary>
    [Fact]
    public void ATransparentFillWritesNoColour()
    {
        Assert.DoesNotContain(Lines(Content(@"<rect width=""10"" height=""10"" fill=""transparent""/>")), l => l.EndsWith(" rg", StringComparison.Ordinal));
        Assert.Contains("1.0000 0.0000 0.0000 rg", Lines(Content(@"<rect width=""10"" height=""10"" fill=""red""/>")));
    }

    /// <summary>
    /// Text whose fill paints nothing is not drawn — <c>none</c> and <c>transparent</c> alike; the
    /// control draws its run.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("transparent")]
    public void TextWithNoFillIsNotDrawn(string fill)
    {
        string hidden = Content(@"<text x=""10"" y=""20"" fill=""" + fill + @""">A</text>");
        string shown = Content(@"<text x=""10"" y=""20"">A</text>");

        Assert.DoesNotContain(Lines(hidden), l => l.EndsWith(" Tj", StringComparison.Ordinal));
        Assert.Contains("(A) Tj", Lines(shown));
    }

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
