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
/// <see cref="SvgToPdfConverter"/> maps a <c>viewBox</c> onto a viewport (SVG 1.1 §7.7, §7.8) for a
/// <c>&lt;symbol&gt;</c> drawn by a <c>&lt;use&gt;</c> and for a nested <c>&lt;svg&gt;</c>, by
/// <c>preserveAspectRatio</c>.
/// </summary>
/// <remarks>
/// A symbol was reported as not drawn, so an icon sprite — symbols with a viewBox, used at a size —
/// came out empty; a nested <c>&lt;svg&gt;</c> drew its content without its position or scale. The
/// expected rects below are worked by hand: the symbol's 24-unit box into a use's 48-unit viewport at
/// (10, 10), unflipped.
/// </remarks>
public class SvgViewportTests
{
    private static string Sprite(string symbolAttributes, string viewBox) =>
        $@"<defs><symbol id=""i"" viewBox=""{viewBox}"" {symbolAttributes}><rect width=""{Box(viewBox)[2]}"" height=""{Box(viewBox)[3]}""/></symbol></defs>" +
        @"<use href=""#i"" x=""10"" y=""10"" width=""48"" height=""48""/>";

    [Fact]
    public void ASymbolsViewBoxIsScaledToTheUse()
    {
        Assert.Equal(new[] { 10f, 10, 48, 48 }, Rect(Sprite("", "0 0 24 24")));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The default, xMidYMid meet: one scale, the smaller (2), centred on the other axis.</summary>
    [Fact]
    public void MeetKeepsTheAspectAndCentres()
    {
        Assert.Equal(new[] { 10f, 22, 48, 24 }, Rect(Sprite("", "0 0 24 12")));
    }

    [Fact]
    public void NoneStretches()
    {
        Assert.Equal(new[] { 10f, 10, 48, 48 }, Rect(Sprite(@"preserveAspectRatio=""none""", "0 0 24 12")));
    }

    /// <summary>slice takes the larger scale (4) and overflows the viewport, centred: x = 10 + (48 − 96) / 2.</summary>
    [Fact]
    public void SliceFillsAndCentres()
    {
        Assert.Equal(new[] { -14f, 10, 96, 48 }, Rect(Sprite(@"preserveAspectRatio=""xMidYMid slice""", "0 0 24 12")));
    }

    /// <summary>xMinYMax meet: flush left and to the bottom, y = 10 + (48 − 24).</summary>
    [Fact]
    public void TheAlignmentPlacesTheBox()
    {
        Assert.Equal(new[] { 10f, 34, 48, 24 }, Rect(Sprite(@"preserveAspectRatio=""xMinYMax meet""", "0 0 24 12")));
    }

    /// <summary>
    /// With slack across rather than down — a 12 × 24 box in 48 × 48, scale 2 — xMax pushes the box
    /// right, x = 10 + (48 − 24), and <c>defer</c> before the value is skipped.
    /// </summary>
    [Theory]
    [InlineData("xMaxYMid meet")]
    [InlineData("defer xMaxYMid meet")]
    public void TheHorizontalAlignmentPlacesTheBox(string par)
    {
        Assert.Equal(new[] { 34f, 10, 24, 48 }, Rect(Sprite($@"preserveAspectRatio=""{par}""", "0 0 12 24")));
    }

    /// <summary>A viewBox that is not four numbers is no viewBox: the symbol is placed at scale 1.</summary>
    [Fact]
    public void AnInvalidViewBoxIsIgnored()
    {
        string c = Content(
            @"<defs><symbol id=""i"" viewBox=""0 0 12 12 junk""><rect width=""12"" height=""12""/></symbol></defs>" +
            @"<use href=""#i"" x=""10"" y=""10"" width=""48"" height=""48""/>");

        Assert.Equal(new[] { 10f, 10, 12, 12 }, Assert.Single(DrawnRects(c)));
    }

    /// <summary>
    /// A percentage size is of the enclosing viewport (batch 50): 24% of the 200-wide root is 48, a
    /// scale of 2. It was read as the viewBox's size, scale 1, and reported.
    /// </summary>
    [Fact]
    public void APercentageSizeIsOfTheEnclosingViewport()
    {
        string c = Content(
            @"<defs><symbol id=""i"" viewBox=""0 0 24 24""><rect width=""24"" height=""24""/></symbol></defs>" +
            @"<use href=""#i"" x=""10"" y=""10"" width=""24%"" height=""24%""/>");

        Assert.Equal(new[] { 10f, 10, 48, 48 }, Assert.Single(DrawnRects(c)));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The viewBox's origin maps to the viewport's corner: a box from (12, 12) moves its content back by 12 × 2.</summary>
    [Fact]
    public void TheViewBoxOriginIsHonoured()
    {
        string c = Content(
            @"<defs><symbol id=""i"" viewBox=""12 12 24 24""><rect x=""12"" y=""12"" width=""24"" height=""24""/></symbol></defs>" +
            @"<use href=""#i"" x=""10"" y=""10"" width=""48"" height=""48""/>");

        Assert.Equal(new[] { 10f, 10, 48, 48 }, Assert.Single(DrawnRects(c)));
    }

    /// <summary>A symbol is drawn only through a use; on its own it draws nothing and is not reported.</summary>
    [Fact]
    public void ASymbolOnItsOwnIsNotDrawn()
    {
        string c = Content(@"<symbol id=""i""><rect width=""10"" height=""10""/></symbol><rect x=""50"" width=""1"" height=""1""/>");

        Assert.Single(DrawnRects(c));   // the control rect alone
        Assert.Null(SvgToPdfConverter.LastError);
    }

    [Fact]
    public void ANestedSvgMapsItsViewBoxToItsViewport()
    {
        Assert.Equal(new[] { 10f, 20, 40, 40 },
            Rect(@"<svg x=""10"" y=""20"" width=""40"" height=""40"" viewBox=""0 0 20 20""><rect width=""20"" height=""20""/></svg>"));
    }

    [Fact]
    public void ANestedSvgWithoutAViewBoxIsTranslated()
    {
        Assert.Equal(new[] { 5f, 5, 10, 10 }, Rect(@"<svg x=""5"" y=""5""><rect width=""10"" height=""10""/></svg>"));
    }

    // ------------------------------------------------------------------ helpers

    private static float[] Box(string viewBox) =>
        viewBox.Split(' ').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    private static float[] Rect(string body) => Assert.Single(DrawnRects(Content(body)));

    /// <summary>The rects drawn, not the viewport's clip: a clip's <c>re</c> is followed by <c>W</c>.</summary>
    private static float[][] DrawnRects(string content)
    {
        string[] lines = content.Split('\n').Select(l => l.Trim()).ToArray();
        return lines.Where((l, i) => l.EndsWith(" re", StringComparison.Ordinal) && (i + 1 >= lines.Length || lines[i + 1] != "W"))
            .Select(l => Operands(l, "re")[0]).ToArray();
    }

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static float[][] Operands(string content, string op)
        => content.Split('\n').Select(l => l.Trim())
            .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Select(parts => parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
}
