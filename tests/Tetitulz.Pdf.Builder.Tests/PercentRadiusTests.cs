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
/// <c>CornerRadiusPercent</c> — CSS's <c>border-radius: 50%</c>, a share of each side — on a
/// paragraph, a box of blocks and an inline box, and an image clipped to its rounded box
/// (Bootstrap's <c>.img-circle</c>, which rounded nothing: a percentage read as 0 and an image's
/// box, had it one, was painted round a square picture).
/// </summary>
public class PercentRadiusTests
{
    // ------------------------------------------------------------------ the rule

    /// <summary>A percentage is that share of the width across and of the height down; 50% of a 40 × 20 box is 20 by 10, and it overrides the lengths.</summary>
    [Fact]
    public void APercentIsAShareOfEachSide()
    {
        var half = PdfExporter.Corners.Of(3, new double[] { 1, 1, 1, 1 }, null, 40, 20, percent: 50);
        Assert.Equal(new[] { 20.0, 10, 20, 10 }, new[] { half.TopLeft, half.TopLeftY, half.BottomRight, half.BottomRightY });

        var quarter = PdfExporter.Corners.Of(0, null, null, 40, 20, percent: 25);
        Assert.Equal(new[] { 10.0, 5 }, new[] { quarter.TopRight, quarter.TopRightY });

        var none = PdfExporter.Corners.Of(3, null, null, 40, 20);   // the control: the length
        Assert.Equal(new[] { 3.0, 3 }, new[] { none.TopLeft, none.TopLeftY });
    }

    /// <summary>
    /// The inner curve is each radius less the inset on its side (CSS Backgrounds 3 §5.2); a corner
    /// the inset reaches past is square.
    /// </summary>
    [Fact]
    public void AnInsetCornerLosesItsSidesInset()
    {
        var c = new PdfExporter.Corners(10, 10, 10, 10).Inset(2, 3, 4, 5);
        Assert.Equal(new[] { 8.0, 7 }, new[] { c.TopLeft, c.TopLeftY });
        Assert.Equal(new[] { 6.0, 7 }, new[] { c.TopRight, c.TopRightY });
        Assert.Equal(new[] { 6.0, 5 }, new[] { c.BottomRight, c.BottomRightY });
        Assert.Equal(new[] { 8.0, 5 }, new[] { c.BottomLeft, c.BottomLeftY });

        var past = new PdfExporter.Corners(2, 2, 2, 2).Inset(3, 1, 0, 0);
        Assert.Equal(new[] { 0.0, 0 }, new[] { past.TopLeft, past.TopLeftY });
        Assert.Equal(new[] { 2.0, 1 }, new[] { past.TopRight, past.TopRightY });
    }

    // ------------------------------------------------------------------ the drawing

    private static string Render(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    /// <summary>Each curve's horizontal and vertical extent in <paramref name="c"/>, from the point before it to its end.</summary>
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

    /// <summary>The clipping path drawn before the image: from the last <c>q</c> before the clip to the clip.</summary>
    private static string? ClipBeforeImage(string content)
    {
        int image = Regex.Match(content, @"cm\s*/\w+\s+Do\b").Index;
        int clip = content.LastIndexOf("W", image, System.StringComparison.Ordinal);
        if (clip < 0 || !Regex.IsMatch(content.Substring(clip, image - clip), @"^W\s*n\b")) return null;
        int save = content.LastIndexOf("q\n", clip, System.StringComparison.Ordinal);
        return content.Substring(save, clip - save);
    }

    private static ImageRun Picture(InlineBox? box) =>
        new ImageRun { Data = ExifJpeg.Build(8, 8, 1), WidthPt = 100, HeightPt = 50, Box = box };

    private static Paragraph Holding(IInlineElement run)
    {
        var p = new Paragraph();
        p.Runs.Add(run);
        return p;
    }

    /// <summary>
    /// A picture in a box of 50% is clipped to an ellipse its own size — four curves, 50 across and
    /// 25 down on a 100 × 50 picture; a square box clips nothing (the control).
    /// </summary>
    [Fact]
    public void APictureInARoundedBoxIsClippedToIt()
    {
        string? clip = ClipBeforeImage(Render(Holding(Picture(new InlineBox { CornerRadiusPercent = 50 }))));
        Assert.NotNull(clip);
        var e = CurveExtents(clip!);
        Assert.Equal(4, e.Count);
        Assert.All(e, x => { Assert.Equal(50, x.Dx, 2); Assert.Equal(25, x.Dy, 2); });

        Assert.Null(ClipBeforeImage(Render(Holding(Picture(new InlineBox())))));
    }

    /// <summary>
    /// The clip is the inner curve of the content box: a 10pt radius round a 4pt border and 2pt of
    /// padding leaves the picture 4 by 4 of rounding at each corner.
    /// </summary>
    [Fact]
    public void TheClipIsTheContentBoxsInnerCurve()
    {
        var frame = new InlineBox
        {
            CornerRadiusPt = 10, Border = new Border(4, "#FF0000"),
            PaddingLeft = 2, PaddingRight = 2, PaddingTop = 2, PaddingBottom = 2,
        };
        var e = CurveExtents(ClipBeforeImage(Render(Holding(Picture(frame))))!);
        Assert.Equal(4, e.Count);
        Assert.All(e, x => { Assert.Equal(4, x.Dx, 2); Assert.Equal(4, x.Dy, 2); });
    }

    /// <summary>A paragraph at 50% is rounded by half its width across and half its height down: four curves, all alike.</summary>
    [Fact]
    public void AParagraphAtFiftyPercentIsAnEllipse()
    {
        var p = new Paragraph { Shading = "#0000FF", CornerRadiusPercent = 50 };
        p.Runs.Add(new TextRun { Text = "A" });
        var square = new Paragraph { Shading = "#0000FF" };   // the same box, unrounded: its width from its rectangle
        square.Runs.Add(new TextRun { Text = "A" });
        var re = Regex.Match(Render(square), @"[-\d.]+ [-\d.]+ ([-\d.]+) [-\d.]+ re
");
        Assert.True(re.Success, "the unrounded box drew no rectangle");
        double width = double.Parse(re.Groups[1].Value, CultureInfo.InvariantCulture);
        var e = CurveExtents(Render(p));
        Assert.Equal(4, e.Count);
        Assert.All(e, x => Assert.Equal(width / 2, x.Dx, 1));
        Assert.All(e, x => Assert.Equal(e[0].Dy, x.Dy, 2));
        Assert.True(e[0].Dy < width / 4, $"the vertical radius {e[0].Dy} is not half the line's height");
    }

    /// <summary>A box of blocks at 50% likewise: half its width across.</summary>
    [Fact]
    public void ABoxOfBlocksAtFiftyPercentIsAnEllipse()
    {
        var a = new Paragraph();
        a.Runs.Add(new TextRun { Text = "A" });
        var section = new Section();
        double width = section.PageWidthPt - section.MarginLeft - section.MarginRight;
        var e = CurveExtents(Render(new BoxStart { Shading = "#0000FF", CornerRadiusPercent = 50 }, a, new BoxEnd()));
        Assert.Equal(4, e.Count);
        Assert.All(e, x => Assert.Equal(width / 2, x.Dx, 1));
    }

    /// <summary>An inline box at 50% — a round badge — is rounded by half its own width, with its border's stroke half a border inside.</summary>
    [Fact]
    public void AnInlineBoxAtFiftyPercentIsRoundedByItsSides()
    {
        var run = new TextRun { Text = "B", Box = new InlineBox { Background = "#0000FF", CornerRadiusPercent = 50, PaddingLeft = 4, PaddingRight = 4, Border = new Border(2, "#FF0000") } };
        var e = CurveExtents(Render(Holding(run)));
        Assert.Equal(8, e.Count);   // the fill, then the border's stroke
        var fill = e.Take(4).ToList();
        var stroke = e.Skip(4).ToList();
        Assert.All(fill, x => Assert.Equal(fill[0].Dx, x.Dx, 2));
        Assert.All(stroke, x => Assert.Equal(fill[0].Dx - 1, x.Dx, 2));
        Assert.All(stroke, x => Assert.Equal(fill[0].Dy - 1, x.Dy, 2));
    }
}
