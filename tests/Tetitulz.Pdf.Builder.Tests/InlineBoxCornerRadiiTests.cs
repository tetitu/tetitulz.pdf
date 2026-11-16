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
/// An <see cref="InlineBox"/>'s four per-corner readings — <see cref="InlineBox.CornerRadiiPt"/>,
/// <see cref="InlineBox.CornerRadiiYPt"/>, <see cref="InlineBox.CornerRadiiPercent"/> and
/// <see cref="InlineBox.CornerRadiiYPercent"/> — reach its fill, its border's stroke and the clip of
/// a picture it frames, as a paragraph's do (batch 124). Before, an inline box carried one radius or
/// one percentage for all four corners.
/// </summary>
/// <remarks>Each rounded corner is one <c>c</c> operator; its extent along x is the corner's horizontal radius, along y its vertical one.</remarks>
public class InlineBoxCornerRadiiTests
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

    private static string? ClipBeforeImage(string content)
    {
        int image = Regex.Match(content, @"cm\s*/\w+\s+Do\b").Index;
        int clip = content.LastIndexOf("W", image, System.StringComparison.Ordinal);
        if (clip < 0 || !Regex.IsMatch(content.Substring(clip, image - clip), @"^W\s*n\b")) return null;
        int save = content.LastIndexOf("q\n", clip, System.StringComparison.Ordinal);
        return content.Substring(save, clip - save);
    }

    private static Paragraph Boxed(InlineBox box)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "BBBBBB", Box = box });
        return p;
    }

    /// <summary>Four radii round their own corners: <c>{ 5, 0, 0, 0 }</c> is one curve of 5, the other three corners square; the one radius for all four (the control) is four.</summary>
    [Fact]
    public void FourRadii_RoundTheirOwnCorners()
    {
        var e = CurveExtents(Render(Boxed(new InlineBox { Background = "#0000FF", PaddingLeft = 6, PaddingRight = 6, CornerRadiiPt = new double[] { 5, 0, 0, 0 } })));
        var curve = Assert.Single(e);
        Assert.Equal(5.0, curve.Dx, 2);
        Assert.Equal(5.0, curve.Dy, 2);

        Assert.Equal(4, CurveExtents(Render(Boxed(new InlineBox { Background = "#0000FF", PaddingLeft = 6, PaddingRight = 6, CornerRadiusPt = 5 }))).Count);
    }

    /// <summary>The vertical radii make each corner a quarter ellipse: 6 across by 3 down.</summary>
    [Fact]
    public void VerticalRadii_MakeEachCornerAnEllipse()
    {
        var box = new InlineBox { Background = "#0000FF", PaddingLeft = 6, PaddingRight = 6, CornerRadiiPt = new double[] { 6, 6, 6, 6 }, CornerRadiiYPt = new double[] { 3, 3, 3, 3 } };
        var e = CurveExtents(Render(Boxed(box)));
        Assert.Equal(4, e.Count);
        Assert.All(e, x => { Assert.Equal(6.0, x.Dx, 2); Assert.Equal(3.0, x.Dy, 2); });
    }

    /// <summary>
    /// A per-corner percentage is that share of the box there: 50% at the bottom-right alone is one
    /// curve, half the box's width across and half its height down — wider than tall on a wide box;
    /// with <see cref="InlineBox.CornerRadiiYPercent"/> of 25 the vertical radius is a quarter of the height.
    /// </summary>
    [Fact]
    public void APerCornerPercent_IsThatShareOfTheBoxThere()
    {
        var half = CurveExtents(Render(Boxed(new InlineBox { Background = "#0000FF", PaddingLeft = 6, PaddingRight = 6, CornerRadiiPercent = new double[] { 0, 0, 50, 0 } })));
        var curve = Assert.Single(half);
        Assert.True(curve.Dx > 1.5 * curve.Dy, $"the corner {curve.Dx:F2} × {curve.Dy:F2} is not half a wide box's width by half its height");

        var quarterY = CurveExtents(Render(Boxed(new InlineBox { Background = "#0000FF", PaddingLeft = 6, PaddingRight = 6, CornerRadiiPercent = new double[] { 0, 0, 50, 0 }, CornerRadiiYPercent = new double[] { 0, 0, 25, 0 } })));
        var y = Assert.Single(quarterY);
        Assert.Equal(curve.Dx, y.Dx, 2);
        Assert.Equal(curve.Dy / 2, y.Dy, 2);
    }

    /// <summary>The border's stroke follows the same corners, half a border inside: one rounded corner in the fill and one in the stroke.</summary>
    [Fact]
    public void TheBorder_FollowsTheSameCorners()
    {
        var box = new InlineBox { Background = "#0000FF", Border = new Border(2, "#FF0000"), PaddingLeft = 6, PaddingRight = 6, CornerRadiiPt = new double[] { 0, 8, 0, 0 } };
        var e = CurveExtents(Render(Boxed(box)));
        Assert.Equal(2, e.Count);
        Assert.Equal(8.0, e[0].Dx, 2);
        Assert.Equal(7.0, e[1].Dx, 2);
    }

    /// <summary>A picture in a box rounded at one corner is clipped at that corner alone.</summary>
    [Fact]
    public void APicture_IsClippedToTheBoxsOwnCorners()
    {
        var p = new Paragraph();
        p.Runs.Add(new ImageRun { Data = ExifJpeg.Build(8, 8, 1), WidthPt = 100, HeightPt = 50, Box = new InlineBox { CornerRadiiPt = new double[] { 0, 0, 0, 10 } } });
        string? clip = ClipBeforeImage(Render(p));
        Assert.NotNull(clip);
        var curve = Assert.Single(CurveExtents(clip!));
        Assert.Equal(10.0, curve.Dx, 2);
        Assert.Equal(10.0, curve.Dy, 2);
    }
}
