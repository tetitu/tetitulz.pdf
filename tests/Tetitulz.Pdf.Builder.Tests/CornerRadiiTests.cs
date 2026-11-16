// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Per-corner radii (<see cref="Paragraph.CornerRadiiPt"/>, <see cref="BoxStart.CornerRadiiPt"/>):
/// each corner rounded by its own radius, clamped to the box, square on an edge a page break cuts —
/// a Bootstrap panel heading rounds its top corners only, and drew all four.
/// </summary>
public class CornerRadiiTests
{
    // ------------------------------------------------------------------ the rule

    /// <summary>The four stated radii, in CSS's order, override the uniform one; each is clamped to half the shorter side.</summary>
    [Fact]
    public void StatedCornersOverrideTheUniformRadiusAndAreClamped()
    {
        var c = PdfExporter.Corners.Of(3, new double[] { 10, 2, 0, 100 }, null, 40, 20);
        Assert.Equal(new[] { 10.0, 2, 0, 10 }, new[] { c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft });

        var u = PdfExporter.Corners.Of(3, null, null, 40, 20);
        Assert.Equal(new[] { 3.0, 3, 3, 3 }, new[] { u.TopLeft, u.TopRight, u.BottomRight, u.BottomLeft });
    }

    /// <summary>A corner on an edge a page break cuts is square; the others keep their rounding.</summary>
    [Fact]
    public void ACutEdgesCornersAreSquare()
    {
        var first = PdfExporter.Corners.Of(4, null, null, 40, 20, top: true, bottom: false);
        Assert.Equal(new[] { 4.0, 4, 0, 0 }, new[] { first.TopLeft, first.TopRight, first.BottomRight, first.BottomLeft });
    }

    /// <summary>
    /// An elliptical corner is clamped whole, keeping its shape: 40 by 10 in a 40 × 20 box is halved
    /// to 20 by 5 — its horizontal radius at half the width — where clamping each radius alone would
    /// have left the vertical one at 10. A circular corner's vertical radius is its horizontal one.
    /// </summary>
    [Fact]
    public void AnEllipticalCornerIsClampedWhole()
    {
        var e = PdfExporter.Corners.Of(0, new double[] { 40, 6, 0, 0 }, new double[] { 10, 6, 0, 0 }, 40, 20);
        Assert.Equal(new[] { 20.0, 5 }, new[] { e.TopLeft, e.TopLeftY });
        Assert.Equal(new[] { 6.0, 6 }, new[] { e.TopRight, e.TopRightY });

        var c = PdfExporter.Corners.Of(0, new double[] { 15, 0, 0, 0 }, null, 40, 20);
        Assert.Equal(new[] { 10.0, 10 }, new[] { c.TopLeft, c.TopLeftY });
    }

    /// <summary>A corner with either radius 0 is square — both are 0 (CSS Backgrounds 3 §5.4).</summary>
    [Fact]
    public void ACornerWithAZeroRadiusIsSquare()
    {
        var e = PdfExporter.Corners.Of(0, new double[] { 6, 6, 0, 0 }, new double[] { 0, 6, 0, 0 }, 40, 20);
        Assert.Equal(new[] { 0.0, 0 }, new[] { e.TopLeft, e.TopLeftY });
        Assert.Equal(6, e.TopRightY);
    }

    /// <summary>A shadow's spread grows each rounded corner and leaves a square one square.</summary>
    [Fact]
    public void ASpreadGrowsOnlyTheRoundedCorners()
    {
        var c = new PdfExporter.Corners(4, 0, 4, 0).Grown(2);
        Assert.Equal(new[] { 6.0, 0, 6, 0 }, new[] { c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft });

        // An elliptical corner grows on both radii.
        var e = new PdfExporter.Corners(4, 0, 4, 0, 1, 0, 1, 0).Grown(2);
        Assert.Equal(new[] { 3.0, 0, 3, 0 }, new[] { e.TopLeftY, e.TopRightY, e.BottomRightY, e.BottomLeftY });
    }

    // ------------------------------------------------------------------ the drawing

    /// <summary>The y of each curve's end point, and the path's start (its bottom edge's y).</summary>
    private static (double Bottom, double[] CurveYs) Path(Paragraph p)
    {
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        var move = Regex.Match(c, @"([-\d.]+) ([-\d.]+) m\n");
        Assert.True(move.Success, "no path was drawn");
        double bottom = double.Parse(move.Groups[2].Value, CultureInfo.InvariantCulture);
        var ys = Regex.Matches(c, @"(?:[-\d.]+ ){5}([-\d.]+) c\n").Cast<Match>()
                      .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        return (bottom, ys);
    }

    /// <summary>
    /// A paragraph rounded at its top corners only draws two curves, both at its top — none at the
    /// bottom edge the path starts from.
    /// </summary>
    [Fact]
    public void OnlyTheStatedCornersAreRounded()
    {
        var p = new Paragraph { Shading = "#0000FF", CornerRadiiPt = new double[] { 6, 6, 0, 0 } };
        p.Runs.Add(new TextRun { Text = "A" });
        var (bottom, ys) = Path(p);

        Assert.Equal(2, ys.Length);
        Assert.All(ys, y => Assert.True(y > bottom + 6, $"a curve ends at {y}, at the bottom edge {bottom}"));
    }

    /// <summary>Each curve's horizontal and vertical extent: from the point before it to its end.</summary>
    private static List<(double Dx, double Dy)> CurveExtents(Paragraph p)
    {
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        var extents = new List<(double, double)>();
        double px = 0, py = 0;
        foreach (Match m in Regex.Matches(c, @"((?:[-\d.]+ )+)(m|l|c)\n"))
        {
            double[] v = m.Groups[1].Value.Trim().Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            double ex = v[v.Length - 2], ey = v[v.Length - 1];
            if (m.Groups[2].Value == "c") extents.Add((System.Math.Abs(ex - px), System.Math.Abs(ey - py)));
            px = ex; py = ey;
        }
        return extents;
    }

    /// <summary>
    /// An elliptical corner is a quarter ellipse: its curve spans its horizontal radius across and
    /// its vertical one down — 12 by 4 — where a circular one spans its radius both ways (the control).
    /// </summary>
    [Fact]
    public void AnEllipticalCornerSpansItsTwoRadii()
    {
        var p = new Paragraph { Shading = "#0000FF", CornerRadiiPt = new double[] { 12, 12, 12, 12 }, CornerRadiiYPt = new double[] { 4, 4, 4, 4 } };
        p.Runs.Add(new TextRun { Text = "A" });
        var e = CurveExtents(p);
        Assert.Equal(4, e.Count);
        Assert.All(e, x => { Assert.Equal(12, x.Dx, 2); Assert.Equal(4, x.Dy, 2); });

        // The control at 8, under half the one line's height, which a circle of 12 is clamped to.
        var q = new Paragraph { Shading = "#0000FF", CornerRadiiPt = new double[] { 8, 8, 8, 8 } };
        q.Runs.Add(new TextRun { Text = "A" });
        Assert.All(CurveExtents(q), x => { Assert.Equal(8, x.Dx, 2); Assert.Equal(8, x.Dy, 2); });
    }
}
