// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The path a paragraph's rounded background box is actually built from — the corner radius clamp
/// and the Bézier corner constant.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over the PDF exporter made two changes that
/// <b>all 438 Builder tests survived</b>: it deleted the corner-radius clamp
/// (<c>return radius</c> in place of <c>Math.Min(radius, Math.Min(width, height) / 2)</c>), and it
/// set the Bézier corner constant <c>K</c> to zero. Both are documented hazards — the clamp's own
/// summary names the self-intersecting "bow tie" fill, and the constant's names a visibly
/// non-circular corner as "the whole thing a reader would notice about this feature" — and neither
/// had a test.</para>
///
/// <para><b>Why they were invisible.</b> Rounded corners are exercised only for whether a box is
/// <i>painted</i>, and a bow tie and a square corner both paint. Asserting a rendered pixel cannot
/// separate them either at any sane resolution, so these read the emitted path operators: the
/// coordinates the exporter wrote are the claim, and they are checked as geometry rather than as
/// a byte sequence.</para>
/// </remarks>
public class RoundedBoxGeometryTests
{
    /// <summary>One path operator and the operands that preceded it.</summary>
    private readonly record struct Op(string Name, double[] Args);

    /// <summary>
    /// The page's path operators in order. Operands accumulate until an operator token ends them,
    /// which is exactly how a content stream is read; any other token (a name, a string, another
    /// operator) discards the pending operands.
    /// </summary>
    private static List<Op> PathOps(string content)
    {
        var ops = new List<Op>();
        var pending = new List<double>();
        foreach (string token in content.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                pending.Add(v);
                continue;
            }
            if (token == "m" || token == "l" || token == "c" || token == "re")
                ops.Add(new Op(token, pending.ToArray()));
            pending.Clear();
        }
        return ops;
    }

    private static string Content(double radiusPt)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Shading = "#cccccc", CornerRadiusPt = radiusPt };
        p.Runs.Add(new TextRun { Text = "X" });
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static Op First(List<Op> ops, string name)
    {
        foreach (var op in ops)
            if (op.Name == name) return op;
        Assert.Fail($"the page emitted no '{name}' operator; operators seen: " +
                    string.Join(" ", ops.ConvertAll(o => o.Name)));
        return default;
    }

    // ── the corner is actually round ──────────────────────────────────────────────────

    /// <summary>
    /// The first corner's Bézier control points are strictly <b>inside</b> the corner, not on it.
    /// A quadrant drawn as one cubic needs its controls at 4/3·(√2−1)·r from the corner; with the
    /// constant at zero they collapse onto the curve's own endpoints and the corner is drawn as a
    /// straight chamfer that still fills, still closes, and still looks like a box.
    /// </summary>
    [Fact]
    public void ARoundedCorner_HasControlPointsOffItsEndpoints()
    {
        var ops = PathOps(Content(radiusPt: 8));

        Op line = First(ops, "l");     // the top edge, ending where the first corner starts
        Op curve = First(ops, "c");    // that corner

        Assert.Equal(2, line.Args.Length);
        Assert.Equal(6, curve.Args.Length);

        // The curve starts where the line ended; its first control point must have moved on.
        Assert.True(curve.Args[0] > line.Args[0],
            $"the first control point (x={curve.Args[0]}) must lie beyond the corner's start " +
            $"(x={line.Args[0]}); equal means the corner was drawn as a straight chamfer.");

        // And the second control point must stop short of the curve's end point.
        Assert.True(curve.Args[3] < curve.Args[5],
            $"the second control point (y={curve.Args[3]}) must stop short of the curve's end " +
            $"(y={curve.Args[5]}); equal means the corner was drawn as a straight chamfer.");
    }

    // ── the radius cannot exceed half the box ─────────────────────────────────────────

    /// <summary>
    /// An over-large radius is scaled down so opposite corners never overlap. Unclamped, the top
    /// edge runs <i>backwards</i> — the path doubles back on itself and fills as a bow tie.
    /// </summary>
    [Theory]
    [InlineData(10_000)]
    [InlineData(400)]
    public void AnOverLargeRadius_IsClampedSoTheEdgeNeverDoublesBack(double radiusPt)
    {
        var ops = PathOps(Content(radiusPt));

        Op move = First(ops, "m");   // starts at (x + r, y)
        Op line = First(ops, "l");   // runs to  (x + w - r, y)

        Assert.Equal(2, move.Args.Length);
        Assert.Equal(2, line.Args.Length);

        Assert.True(line.Args[0] >= move.Args[0],
            $"the top edge runs from x={move.Args[0]} to x={line.Args[0]}, i.e. backwards: " +
            "the radius exceeded half the box and the path self-intersects.");
    }

    /// <summary>
    /// The acceptance control. A box with no radius is a plain rectangle — one <c>re</c>, and no
    /// curves at all — so the assertions above are pinning the rounded path rather than describing
    /// whatever the exporter happens to emit.
    /// </summary>
    [Fact]
    public void ABoxWithNoRadius_IsAPlainRectangle()
    {
        var ops = PathOps(Content(radiusPt: 0));

        Assert.Contains(ops, o => o.Name == "re");
        Assert.DoesNotContain(ops, o => o.Name == "c");
    }
}
