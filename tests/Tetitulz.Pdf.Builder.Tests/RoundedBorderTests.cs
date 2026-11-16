// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A rounded box's border follows its corners: one stroked outline when every edge it draws is
/// the same border. Four straight strokes met at square corners over the rounded fill, so every
/// rounded Bootstrap box — a panel, an alert, a form control — printed square (found against Chrome).
/// </summary>
public class RoundedBorderTests
{
    private static string Render(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static List<(double Dx, double Dy)> CurveExtents(string c)
    {
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

    private static Paragraph Bordered(double radius, Border? top = null)
    {
        var border = new Border(1, "#000000");
        var p = new Paragraph { CornerRadiusPt = radius, BorderTop = top ?? border, BorderRight = border, BorderBottom = border, BorderLeft = border };
        p.Runs.Add(new TextRun { Text = "A" });
        return p;
    }

    /// <summary>
    /// A rounded paragraph with one border all round strokes one outline, rounded as its fill is —
    /// four curves of its 6pt radius, and no straight edge; a square one strokes its four edges
    /// (the control).
    /// </summary>
    [Fact]
    public void ARoundedParagraphsBorderIsOneOutline()
    {
        string rounded = Render(Bordered(6));
        var e = CurveExtents(rounded);
        Assert.Equal(4, e.Count);
        Assert.All(e, x => { Assert.Equal(6, x.Dx, 2); Assert.Equal(6, x.Dy, 2); });
        Assert.Empty(PdfContentProbe.StrokedSegments(rounded));

        string square = Render(Bordered(0));
        Assert.Empty(CurveExtents(square));
        Assert.Equal(4, PdfContentProbe.StrokedSegments(square).Count);
    }

    /// <summary>Edges that differ keep their straight strokes: CSS blends two borders round a corner, which one path cannot.</summary>
    [Fact]
    public void DifferingEdgesKeepTheirStraightStrokes()
    {
        string c = Render(Bordered(6, new Border(1, "#FF0000")));
        Assert.Equal(4, PdfContentProbe.StrokedSegments(c).Count);
        Assert.Empty(CurveExtents(c));
    }

    /// <summary>
    /// A rounded box with a border on one side only — a panel heading, its top corners rounded and
    /// a rule under it — keeps that one straight stroke: an edge with no border is not one border.
    /// </summary>
    [Fact]
    public void ABorderOnOneSideStaysAStraightStroke()
    {
        var p = new Paragraph { CornerRadiiPt = new double[] { 6, 6, 0, 0 }, BorderBottom = new Border(1, "#000000") };
        p.Runs.Add(new TextRun { Text = "A" });
        string c = Render(p);
        var segment = Assert.Single(PdfContentProbe.StrokedSegments(c));
        Assert.True(segment.IsHorizontal, "the one stroke is not the bottom rule");
        Assert.Empty(CurveExtents(c));
    }

    private static BoxStart Box(double radius) => new BoxStart
    {
        CornerRadiusPt = radius,
        BorderTop = new Border(2, "#000000"), BorderRight = new Border(2, "#000000"),
        BorderBottom = new Border(2, "#000000"), BorderLeft = new Border(2, "#000000"),
    };

    /// <summary>
    /// A box of blocks with one border all round strokes one outline too, its centre line half a
    /// border inside the box, so its corners are the radius less half the border: 5 of a 6pt
    /// radius under a 2pt border.
    /// </summary>
    [Fact]
    public void ABoxsBorderIsOneOutlineHalfABorderIn()
    {
        var a = new Paragraph();
        a.Runs.Add(new TextRun { Text = "A" });
        string c = Render(Box(6), a, new BoxEnd());
        var e = CurveExtents(c);
        Assert.Equal(4, e.Count);
        Assert.All(e, x => { Assert.Equal(5, x.Dx, 2); Assert.Equal(5, x.Dy, 2); });
        Assert.Empty(PdfContentProbe.StrokedSegments(c));
    }

    /// <summary>
    /// A box broken across pages is rounded only where it really ends — two curves on its first
    /// page's top, two on its last page's bottom, none between — and each fragment's outline is
    /// clipped where a page cuts it, so no edge is drawn across the cut.
    /// </summary>
    [Fact]
    public void ABrokenBoxsOutlineIsClippedAtTheCut()
    {
        var blocks = new List<IDocElement> { Box(6) };
        for (int i = 0; i < 60; i++)   // ends partway down its last page, clear of the break
        {
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = "Line " + i });
            blocks.Add(p);
        }
        blocks.Add(new BoxEnd());
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 2, "the box did not break, so this proves nothing");

        string c = PdfContentProbe.DecodeStreams(pdf);
        Assert.Equal(4, CurveExtents(c).Count);
        Assert.Equal(pages, Regex.Matches(c, @"re\s+W\s+n").Count);   // one clip a fragment
    }
}
