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
/// <see cref="SvgToPdfConverter"/>: a stroke's cap, join, miter limit and dash pattern (SVG 1.1
/// §11.4), a rect's rounded corners (§9.2), and a report for an element it does not draw.
/// </summary>
/// <remarks>
/// None of the four stroke properties was read, so every stroke had PDF's butt caps, miter joins
/// and no dashes. A rect's <c>rx</c> and <c>ry</c> were ignored, so a rounded button drew square.
/// And an element outside the handful the converter knows — <c>&lt;image&gt;</c>,
/// <c>&lt;switch&gt;</c> — was dropped without a word.
/// </remarks>
public class SvgStrokeAndShapeTests
{
    private static readonly SvgConversionOptions Unflipped =
        new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false };

    [Fact]
    public void CapJoinAndMiterLimitAreSet()
    {
        string c = Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-linecap=""round"" stroke-linejoin=""bevel"" stroke-miterlimit=""7""/>");

        Assert.Contains("1 J", Lines(c));
        Assert.Contains("2 j", Lines(c));
        Assert.Equal(new[] { 7f }, Operands(c, "M")[0]);
    }

    /// <summary>
    /// A dash array is set in page units, like the width: <c>5,3</c> under <c>scale(2)</c> is
    /// <c>[10 6]</c>. An odd-length list repeats to an even one (§11.4).
    /// </summary>
    [Fact]
    public void ADashArrayIsSetInPageUnits()
    {
        var plain = Dash(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""5,3""/>"));
        var scaled = Dash(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""5 3"" stroke-dashoffset=""2"" transform=""scale(2)""/>"));
        var odd = Dash(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""1 2 3""/>"));

        Assert.Equal(new[] { 5f, 3 }, plain.Array); Assert.Equal(0f, plain.Phase);
        Assert.Equal(new[] { 10f, 6 }, scaled.Array); Assert.Equal(4f, scaled.Phase);
        Assert.Equal(new[] { 1f, 2, 3, 1, 2, 3 }, odd.Array);
    }

    /// <summary>
    /// A dash is a length (§11.4): a percentage is of the viewport's normalised diagonal, 5% of 200
    /// being 10, and a unit converts, 2mm being about 7.559. Either stopped the list, which then drew
    /// solid without a word.
    /// </summary>
    [Fact]
    public void ADashIsALength()
    {
        var percent = Dash(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""5% 3""/>"));
        var units = Dash(Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""2mm, 1mm""/>"));

        Assert.Equal(new[] { 10f, 3 }, percent.Array);
        Assert.Equal(2, units.Array.Length);
        Assert.Equal(7.559f, units.Array[0], 3);
        Assert.Equal(3.780f, units.Array[1], 3);
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary><c>none</c> is the initial value, not a broken list: drawn solid and not reported.</summary>
    [Fact]
    public void ANoneDashIsNotReported()
    {
        string c = Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""none""/>");

        Assert.DoesNotContain(Lines(c), l => l.EndsWith(" d", StringComparison.Ordinal));
        Assert.Contains(Lines(c), l => l.EndsWith(" w", StringComparison.Ordinal));   // the stroke is set
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A dash list with something not a length in it is not a list: drawn solid, and now reported.</summary>
    [Fact]
    public void ADashListThatIsNotOneIsReported()
    {
        string c = Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""5 x""/>");

        Assert.DoesNotContain(Lines(c), l => l.EndsWith(" d", StringComparison.Ordinal));
        Assert.Contains("stroke-dasharray", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A dash list that is not one draws solid (§11.4): <c>none</c>, a negative length, all zeros, and
    /// a list that stops at a non-number. A miter limit below 1 is an error and is not written.
    /// </summary>
    [Theory]
    [InlineData(@"stroke-dasharray=""none""")]
    [InlineData(@"stroke-dasharray=""5 -3""")]
    [InlineData(@"stroke-dasharray=""0 0""")]
    [InlineData(@"stroke-dasharray=""5 x""")]
    [InlineData(@"stroke-miterlimit=""0.5""")]
    public void AnInvalidDashOrMiterLimitWritesNone(string attribute)
    {
        string c = Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" " + attribute + "/>");
        string control = Content(@"<path d=""M 0 0 L 10 0"" stroke=""black"" stroke-dasharray=""5 3"" stroke-miterlimit=""2""/>");

        Assert.DoesNotContain(Lines(c), l => l.EndsWith(" d", StringComparison.Ordinal) || l.EndsWith(" M", StringComparison.Ordinal));
        Assert.Contains(Lines(control), l => l.EndsWith(" d", StringComparison.Ordinal));   // the potency control
        Assert.Contains(Lines(control), l => l.EndsWith(" M", StringComparison.Ordinal));
    }

    /// <summary>The initial values write nothing: a stroke with none of the properties is unchanged.</summary>
    [Fact]
    public void AStrokeWithoutThePropertiesWritesNone()
    {
        string c = Content(@"<path d=""M 0 0 L 10 0"" stroke=""black""/>");

        Assert.DoesNotContain(Lines(c), l => l.EndsWith(" J", StringComparison.Ordinal) || l.EndsWith(" d", StringComparison.Ordinal));
        Assert.Contains(Lines(c), l => l.EndsWith(" w", StringComparison.Ordinal));   // the potency control
    }

    /// <summary>
    /// A rect with <c>rx</c> is drawn with four quarter-ellipse corners: it starts at
    /// (x + rx, y), and a missing <c>ry</c> takes <c>rx</c> (§9.2).
    /// </summary>
    [Fact]
    public void ARoundedRectHasArcCorners()
    {
        string c = Content(@"<rect x=""10"" y=""20"" width=""40"" height=""30"" rx=""5""/>");

        Assert.Empty(Operands(c, "re"));
        Assert.Equal(new[] { 15f, 20 }, Operands(c, "m")[0]);
        Assert.Equal(4, Operands(c, "c").Length);
        var firstCorner = Operands(c, "c")[0];
        Assert.Equal(50f, firstCorner[4], 3);   // ends at (x + width, y + ry)
        Assert.Equal(25f, firstCorner[5], 3);
        // It bulges outward: the first control point is on the top edge, right of the start. An
        // inward corner leaves the edge downward instead.
        Assert.Equal(20f, firstCorner[1], 3);
        Assert.True(firstCorner[0] > 45f, $"control x {firstCorner[0]}");

        // Clockwise round the rect: each edge ends where the next corner starts, and the path closes.
        Assert.Equal(new[] { 45f, 20, 50, 45, 15, 50, 10, 25 }, Operands(c, "l").SelectMany(p => p));
        Assert.Equal(new[] { 50f, 25, 45, 50, 10, 45, 15, 20 },
            Operands(c, "c").SelectMany(p => new[] { p[4], p[5] }).Select(v => (float)Math.Round(v, 3)));
        Assert.Contains("h", Lines(c));
    }

    /// <summary>A missing <c>rx</c> takes <c>ry</c>, as a missing <c>ry</c> takes <c>rx</c> (§9.2).</summary>
    [Fact]
    public void ARoundedRectsMissingRxTakesRy()
    {
        string c = Content(@"<rect x=""10"" y=""20"" width=""40"" height=""30"" ry=""5""/>");

        Assert.Empty(Operands(c, "re"));
        Assert.Equal(new[] { 15f, 20 }, Operands(c, "m")[0]);
    }

    /// <summary>
    /// A radius over half the side is clamped to it (§9.2), each against its own side: <c>rx=100</c>
    /// on 40 × 30 is 20 across and, taken by <c>ry</c>, 15 down.
    /// </summary>
    [Fact]
    public void ARoundedRectsRadiusIsClamped()
    {
        string c = Content(@"<rect x=""10"" y=""20"" width=""40"" height=""30"" rx=""100""/>");

        Assert.Equal(new[] { 30f, 20 }, Operands(c, "m")[0]);
        var firstCorner = Operands(c, "c")[0];
        Assert.Equal(50f, firstCorner[4], 3);
        Assert.Equal(35f, firstCorner[5], 3);   // y + ry, ry clamped to 30 / 2
    }

    /// <summary>
    /// An element the converter does not draw is reported, once per name; the ones that draw nothing
    /// by definition (<c>defs</c>, <c>title</c>, <c>desc</c>, <c>metadata</c>, <c>style</c>) are
    /// not, and neither is a comment or text.
    /// </summary>
    [Fact]
    public void AnElementThatIsNotDrawnIsReported()
    {
        Content(@"<image href=""a.png""/><image href=""b.png""/><defs><rect id=""a"" width=""1"" height=""1""/></defs><title>t</title><!-- c -->text");
        string error = SvgToPdfConverter.LastError ?? "";

        Assert.Contains("<image>", error, StringComparison.Ordinal);
        Assert.Equal(1, error.Split(new[] { "<image>" }, StringSplitOptions.None).Length - 1);   // once, for two
        Assert.DoesNotContain("<defs>", error, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>", error, StringComparison.Ordinal);
        Assert.DoesNotContain("<#", error, StringComparison.Ordinal);   // a comment or text is not an element
    }

    // ------------------------------------------------------------------ helpers

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>", Unflipped);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    /// <summary>The array and phase of the one <c>d</c> operator in <paramref name="content"/>.</summary>
    private static (float[] Array, float Phase) Dash(string content)
    {
        string line = Assert.Single(Lines(content), l => l.StartsWith("[", StringComparison.Ordinal) && l.EndsWith(" d", StringComparison.Ordinal));
        int close = line.IndexOf(']');
        float[] array = line.Substring(1, close - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        float phase = float.Parse(line.Substring(close + 1, line.Length - close - 3).Trim(), CultureInfo.InvariantCulture);
        return (array, phase);
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
