// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.CornerRadiiPercent"/> and <see cref="BoxStart.CornerRadiiYPercent"/> (batch
/// 108): a corner's horizontal radius as a share of the box's width and its vertical one as a share
/// of the height, corner by corner, beside the lengths. The box is 200pt wide, so a 50% corner is
/// 100pt across; its curve's extent is what is read.
/// </summary>
public class CornerPercentTests
{
    /// <summary>One corner at 50%: one curve, 100pt across on a 200pt box, half the height down.</summary>
    [Fact]
    public void OneCornerAtFiftyPercent_IsHalfTheWidthAcross()
    {
        var box = new BoxStart { Shading = "#0000FF", WidthPt = 200, CornerRadiiPercent = new double[] { 50, 0, 0, 0 } };
        var e = Curves(box);

        var corner = Assert.Single(e);
        Assert.Equal(100, corner.Dx, 1);
        Assert.True(corner.Dy > 5 && corner.Dy < 50, $"the vertical radius {corner.Dy} is not half the box's height");
    }

    /// <summary>A vertical percentage is of the height: 50% across and 10% down gives corners 100pt across and far less down than a 50% vertical share would.</summary>
    [Fact]
    public void AVerticalPercent_IsOfTheHeight()
    {
        var both = Curves(new BoxStart { Shading = "#0000FF", WidthPt = 200, CornerRadiiPercent = new double[] { 50, 50, 50, 50 }, CornerRadiiYPercent = new double[] { 10, 10, 10, 10 } });
        var acrossOnly = Curves(new BoxStart { Shading = "#0000FF", WidthPt = 200, CornerRadiiPercent = new double[] { 50, 50, 50, 50 } });

        Assert.Equal(4, both.Count);
        Assert.All(both, x => Assert.Equal(100, x.Dx, 1));
        Assert.True(both[0].Dy < acrossOnly[0].Dy / 3, $"a 10% vertical share ({both[0].Dy}) is not far less than the 50% one ({acrossOnly[0].Dy})");
        Assert.True(acrossOnly[0].Dy < 60, $"the 50% vertical share {acrossOnly[0].Dy} is of the width, not the height");
    }

    /// <summary>A percentage corner beside a length corner: the percentage one 100pt across, the length one its 8pt.</summary>
    [Fact]
    public void APercentCorner_BesideALengthCorner()
    {
        var e = Curves(new BoxStart { Shading = "#0000FF", WidthPt = 200, CornerRadiiPt = new double[] { 0, 8, 0, 8 }, CornerRadiiPercent = new double[] { 50, 0, 50, 0 } });

        Assert.Equal(4, e.Count);
        Assert.Equal(2, e.Count(x => Math.Abs(x.Dx - 100) < 1));
        Assert.Equal(2, e.Count(x => Math.Abs(x.Dx - 8) < 0.05 && Math.Abs(x.Dy - 8) < 0.05));
    }

    private static List<(double Dx, double Dy)> Curves(BoxStart box)
    {
        var section = new Section();
        section.Children.Add(box);
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Inside" });
        section.Children.Add(p);
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());

        var extents = new List<(double, double)>();
        double px = 0, py = 0;
        foreach (Match m in Regex.Matches(c, @"((?:[-\d.]+ )+)(m|l|c)\n"))
        {
            double[] v = m.Groups[1].Value.Trim().Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            double ex = v[v.Length - 2], ey = v[v.Length - 1];
            if (m.Groups[2].Value == "c") extents.Add((Math.Abs(ex - px), Math.Abs(ey - py)));
            px = ex; py = ey;
        }
        return extents;
    }
}
