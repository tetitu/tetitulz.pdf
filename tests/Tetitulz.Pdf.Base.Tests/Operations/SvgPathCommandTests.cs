// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> draws every SVG path command (SVG 1.1 §8.3), not five of them.
/// </summary>
/// <remarks>
/// <para>The converter handled <c>M</c>, <c>L</c>, <c>H</c>, <c>V</c> and <c>Z</c>. Every curve —
/// cubic <c>C</c>/<c>S</c>, quadratic <c>Q</c>/<c>T</c>, elliptical arc <c>A</c> — became a PDF
/// comment, reported nowhere, so an icon or a logo came out as the straight segments between its
/// curves. Two rules of the path grammar were wrong too: coordinate pairs after a moveto are implicit
/// linetos (§8.3.2), and they were drawn as further movetos; and <c>closepath</c> returns the current
/// point to the subpath's start (§8.3.3), which a relative command after it relies on.</para>
/// <para>Every fixture draws at <c>FlipY = false</c> on a 200×200 page with no viewBox, so the
/// operands are the user-space coordinates.</para>
/// </remarks>
public class SvgPathCommandTests
{
    private static readonly SvgConversionOptions Unflipped =
        new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false };

    [Fact]
    public void ACubicIsDrawn()
    {
        Assert.Equal(new[] { new[] { 10f, 20, 30, 40, 50, 60 } }, Ops("M 0 0 C 10 20 30 40 50 60", "c"));
        Assert.Equal(new[] { new[] { 11f, 12, 13, 14, 15, 16 } }, Ops("M 10 10 c 1 2 3 4 5 6", "c"));
    }

    /// <summary>
    /// <c>S</c> reflects the previous cubic's second control point about the current point, and
    /// takes the current point itself when the previous command was not a cubic (§8.3.6).
    /// </summary>
    [Fact]
    public void ASmoothCubicReflectsThePreviousControlPoint()
    {
        var after = Ops("M 0 0 C 10 20 30 40 50 60 S 90 100 110 120", "c");
        Assert.Equal(new[] { 70f, 80, 90, 100, 110, 120 }, after[1]);

        var alone = Ops("M 5 5 S 90 100 110 120", "c");
        Assert.Equal(new[] { 5f, 5, 90, 100, 110, 120 }, alone[0]);
    }

    /// <summary>
    /// A quadratic is its exact cubic: control points two thirds of the way from each end to the
    /// quadratic's one. <c>T</c> reflects the previous quadratic's control point.
    /// </summary>
    [Fact]
    public void AQuadraticIsDrawnAsItsCubic()
    {
        var ops = Ops("M 0 0 Q 30 30 60 0 T 120 0", "c");

        AssertOp(new[] { 20f, 20, 40, 20, 60, 0 }, ops[0]);
        AssertOp(new[] { 80f, -20, 100, -20, 120, 0 }, ops[1]);
    }

    /// <summary>
    /// A half circle of radius 50 from (0,0) to (100,0), positive sweep, is two quarter-circle
    /// cubics through (50,−50). Radii too small to reach are scaled up (§F.6.6), so radius 1 draws
    /// the same arc; the compact flag spelling <c>01</c> is the two flags <c>0 1</c>.
    /// </summary>
    [Theory]
    [InlineData("M 0 0 A 50 50 0 0 1 100 0")]
    [InlineData("M 0 0 A 1 1 0 0 1 100 0")]
    [InlineData("M0,0A50,50,0,01,100,0")]
    public void AnArcIsDrawnAsCubics(string d)
    {
        var ops = Ops(d, "c");

        Assert.Equal(2, ops.Length);
        Assert.Equal(50f, ops[0][4], 3);
        Assert.Equal(-50f, ops[0][5], 3);
        Assert.Equal(100f, ops[1][4], 3);
        Assert.Equal(0f, ops[1][5], 3);
        // The quarter circle's control points sit 0.5523 × radius along the tangents.
        Assert.Equal(-27.614f, ops[0][1], 2);
    }

    /// <summary>The other sweep goes the other way round: through (50, 50).</summary>
    [Fact]
    public void AnArcsSweepFlagChoosesTheSide()
    {
        var ops = Ops("M 0 0 A 50 50 0 0 0 100 0", "c");

        Assert.Equal(50f, ops[0][5], 3);
    }

    /// <summary>
    /// From (0,0) to (50,50) with radius 50 there are two circles and four arcs. The large-arc flag
    /// picks the long way round: one quarter-turn cubic for the small arc, three for the large one,
    /// both ending at (50, 50). A half circle cannot tell the two apart — both centres coincide.
    /// </summary>
    [Fact]
    public void TheLargeArcFlagChoosesTheLongWayRound()
    {
        var small = Ops("M 0 0 A 50 50 0 0 1 50 50", "c");
        var large = Ops("M 0 0 A 50 50 0 1 1 50 50", "c");

        Assert.Single(small);
        Assert.Equal(3, large.Length);
        Assert.Equal(50f, large[2][4], 3);
        Assert.Equal(50f, large[2][5], 3);
    }

    /// <summary>
    /// The large arc with the negative sweep: its centre's raw angle between the ends is a quarter
    /// turn the positive way, and the negative sweep has to take it the other way round, three
    /// quarters (§F.6.5's Δθ adjustment). A half circle, the other sweep-0 fixture, needs no
    /// adjustment, which is why this case exists.
    /// </summary>
    [Fact]
    public void TheNegativeSweepGoesTheLongWayWhenAsked()
    {
        var large = Ops("M 0 0 A 50 50 0 1 0 50 50", "c");

        Assert.Equal(3, large.Length);
        Assert.Equal(50f, large[2][4], 3);
        Assert.Equal(50f, large[2][5], 3);
    }

    /// <summary>A zero radius makes the arc a straight line to its end (§F.6.2).</summary>
    [Fact]
    public void AnArcWithAZeroRadiusIsAStraightLine()
    {
        Assert.Empty(Ops("M 0 0 A 0 10 0 0 1 100 0", "c"));
        Assert.Equal(new[] { new[] { 100f, 0 } }, Ops("M 0 0 A 0 10 0 0 1 100 0", "l"));
    }

    /// <summary>Pairs after a moveto are implicit linetos (§8.3.2), relative ones for <c>m</c>.</summary>
    [Fact]
    public void PairsAfterAMovetoAreLinetos()
    {
        Assert.Single(Ops("M 0 0 10 10 20 0", "m"));
        Assert.Equal(new[] { new[] { 10f, 10 }, new[] { 20f, 0 } }, Ops("M 0 0 10 10 20 0", "l"));
        Assert.Equal(new[] { new[] { 15f, 15 }, new[] { 25f, 15 } }, Ops("m 5 5 10 10 10 0", "l"));
    }

    /// <summary>
    /// <c>z</c> returns the current point to the subpath's start (§8.3.3), so the relative lineto
    /// after it starts from (10, 10), not from where the subpath's last segment ended.
    /// </summary>
    [Fact]
    public void AClosepathReturnsToTheSubpathsStart()
    {
        var lines = Ops("M 10 10 l 10 0 z l 0 10", "l");

        Assert.Equal(new[] { 10f, 20 }, lines[1]);
    }

    /// <summary>A command the grammar does not have is reported, and its numbers skipped.</summary>
    [Fact]
    public void AnUnknownCommandIsReported()
    {
        Assert.Equal(new[] { new[] { 10f, 10 } }, Ops("M 0 0 X 5 5 L 10 10", "l"));
        Assert.Contains("'X'", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertOp(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 3);
    }

    /// <summary>The operands of every <paramref name="op"/> the path draws, in order.</summary>
    private static float[][] Ops(string d, string op)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200""><path d=""" + d + @""" fill=""none"" stroke=""black""/></svg>",
            Unflipped);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        string content = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());

        return content.Split('\n').Select(l => l.Trim())
            .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal) && !l.StartsWith("%", StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Select(parts => parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
    }
}
