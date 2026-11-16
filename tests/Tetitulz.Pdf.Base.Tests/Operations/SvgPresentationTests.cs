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
/// <see cref="SvgToPdfConverter"/> reads the presentation SVG documents actually use: a shape's own
/// <c>transform</c>, every transform function, <c>stroke-width</c>, <c>fill-rule</c>, and paint
/// inherited from an ancestor or given in a <c>style</c> attribute.
/// </summary>
/// <remarks>
/// <para>Only a <c>&lt;g&gt;</c> applied its <c>transform</c>, so <c>&lt;path transform="…"&gt;</c> —
/// how an editor exports nearly every shape — drew untransformed with nothing reported. Of the six
/// transform functions only <c>translate</c> and <c>scale</c> were read, and a rect or an ellipse used
/// only the scale part of a transform, so a rotation vanished. <c>stroke-width</c> was never read, so
/// every stroke was the PDF default; <c>fill-rule</c> was never read, so an even-odd ring was filled
/// solid. And paint did not inherit (SVG 1.1 §6.1: <c>fill</c>, <c>stroke</c> and the rest are
/// inherited properties), so <c>&lt;g fill="red"&gt;</c> drew its children black, and a
/// <c>style</c> attribute was not read at all.</para>
/// <para>Every fixture draws at <c>FlipY = false</c> on a 200×200 page with no viewBox.</para>
/// </remarks>
public class SvgPresentationTests
{
    private static readonly SvgConversionOptions Unflipped =
        new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false };

    [Fact]
    public void AShapesOwnTransformIsApplied()
    {
        string c = Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" transform=""translate(5,7)""/>");

        Assert.Equal(new[] { 5f, 7 }, Operands(c, "m")[0]);
        Assert.Equal(new[] { 15f, 7 }, Operands(c, "l")[0]);
    }

    /// <summary><c>rotate(90)</c> takes (10, 0) to (0, 10); <c>rotate(90 5 5)</c> turns about (5, 5).</summary>
    [Fact]
    public void RotateIsApplied()
    {
        var about0 = Operands(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" transform=""rotate(90)""/>"), "l")[0];
        var aboutC = Operands(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" transform=""rotate(90 5 5)""/>"), "l")[0];

        Assert.Equal(0f, about0[0], 3); Assert.Equal(10f, about0[1], 3);
        Assert.Equal(10f, aboutC[0], 3); Assert.Equal(10f, aboutC[1], 3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    [Fact]
    public void MatrixAndSkewAreApplied()
    {
        var matrix = Operands(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" transform=""matrix(1 2 3 4 5 6)""/>"), "l")[0];
        var skewX = Operands(Content(@"<path d=""M 0 0 L 0 10"" stroke=""black"" transform=""skewX(45)""/>"), "l")[0];

        Assert.Equal(new[] { 15f, 26 }, matrix);   // (1·10 + 3·0 + 5, 2·10 + 4·0 + 6)
        Assert.Equal(10f, skewX[0], 3); Assert.Equal(10f, skewX[1], 3);
    }

    [Fact]
    public void SkewYIsApplied()
    {
        var skewY = Operands(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" transform=""skewY(45)""/>"), "l")[0];

        Assert.Equal(10f, skewY[0], 3); Assert.Equal(10f, skewY[1], 3);
    }

    /// <summary>
    /// A rotated circle is built in user space and every point mapped: its first point, (10, 0),
    /// turns to (0, 10). The scale-only arithmetic took the rotation's cosine, 0, for the radius.
    /// </summary>
    [Fact]
    public void ARotatedCircleKeepsItsRadius()
    {
        var start = Operands(Content(@"<circle cx=""0"" cy=""0"" r=""10"" transform=""rotate(90)""/>"), "m")[0];

        Assert.Equal(0f, start[0], 3); Assert.Equal(10f, start[1], 3);
    }

    /// <summary>
    /// A rotated rect cannot be <c>re</c>, which is axis-aligned: its corners are drawn as a path.
    /// An unrotated one stays <c>re</c>.
    /// </summary>
    [Fact]
    public void ARotatedRectIsDrawnByItsCorners()
    {
        string rotated = Content(@"<rect x=""0"" y=""0"" width=""10"" height=""20"" transform=""rotate(90)""/>");
        string plain = Content(@"<rect x=""0"" y=""0"" width=""10"" height=""20""/>");

        Assert.Empty(Operands(rotated, "re"));
        var corners = Operands(rotated, "l");
        Assert.Equal(0f, corners[0][0], 3); Assert.Equal(10f, corners[0][1], 3);    // (10,0) turned to (0,10)
        Assert.Single(Operands(plain, "re"));
    }

    /// <summary>A stroke width is set, in the page's units: a width of 2 under scale(3) is 6.</summary>
    [Fact]
    public void StrokeWidthIsSet()
    {
        Assert.Equal(new[] { 2f }, Operands(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-width=""2""/>"), "w")[0]);
        Assert.Equal(new[] { 6f }, Operands(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-width=""2"" transform=""scale(3)""/>"), "w")[0]);
    }

    [Fact]
    public void AnEvenOddFillRuleIsHonoured()
    {
        string evenOdd = Content(@"<path d=""M 0 0 L 10 0 L 10 10 z"" fill-rule=""evenodd""/>");
        string nonZero = Content(@"<path d=""M 0 0 L 10 0 L 10 10 z""/>");

        Assert.Contains("f*", Lines(evenOdd));
        Assert.Contains("f", Lines(nonZero));
        Assert.DoesNotContain("f*", Lines(nonZero));
        Assert.Contains("B*", Lines(Content(@"<path d=""M 0 0 L 10 0 L 10 10 z"" fill-rule=""evenodd"" stroke=""black""/>")));
    }

    /// <summary>
    /// <c>fill</c> is inherited (§6.1): a group's red reaches its rect, and the rect's own colour
    /// wins over it. A <c>style</c> attribute is read, and wins over a presentation attribute (§6.4).
    /// </summary>
    [Fact]
    public void PaintIsInheritedAndStyleIsRead()
    {
        Assert.Contains("1.0000 0.0000 0.0000 rg", Lines(Content(@"<g fill=""red""><rect width=""5"" height=""5""/></g>")));
        Assert.Contains("0.0000 0.0000 1.0000 rg", Lines(Content(@"<g fill=""red""><rect width=""5"" height=""5"" fill=""blue""/></g>")));
        Assert.Contains("0.0000 0.5020 0.0000 rg", Lines(Content(@"<rect width=""5"" height=""5"" fill=""red"" style=""fill: green""/>")));
    }

    /// <summary>
    /// <c>inherit</c> takes the parent's value rather than being read as a colour; and of two
    /// declarations in one <c>style</c>, the later wins, as CSS has it.
    /// </summary>
    [Fact]
    public void InheritDefersToTheParentAndTheLastDeclarationWins()
    {
        Assert.Contains("1.0000 0.0000 0.0000 rg", Lines(Content(@"<g fill=""red""><rect width=""5"" height=""5"" fill=""inherit""/></g>")));
        Assert.Contains("0.0000 0.0000 1.0000 rg", Lines(Content(@"<rect width=""5"" height=""5"" style=""fill: red; fill: blue""/>")));
    }

    // ------------------------------------------------------------------ helpers

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>", Unflipped);
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
