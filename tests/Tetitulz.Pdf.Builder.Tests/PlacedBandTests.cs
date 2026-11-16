// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="AbsolutePlacement.BandHeightPt"/> and <see cref="AbsolutePlacement.BandAlign"/>: a placed
/// block set in a band is moved down by the band's slack — half of it centred, all of it at the bottom —
/// and a transform among it is taken about the band, not about where the move put the text (XFA's
/// <c>para vAlign</c> inside a turned field).
/// </summary>
public class PlacedBandTests
{
    [Fact]
    public void ABand_MovesItsBlockDownByItsSlack()
    {
        string content = Content(
            Placed("NOBAND", 50, null, CellVerticalAlign.Top),
            Placed("TOP", 150, 100, CellVerticalAlign.Top),
            Placed("MID", 250, 100, CellVerticalAlign.Center),
            Placed("BOT", 350, 100, CellVerticalAlign.Bottom));
        double none = PdfContentProbe.YOf(content, "NOBAND"), top = PdfContentProbe.YOf(content, "TOP");
        double mid = PdfContentProbe.YOf(content, "MID"), bottom = PdfContentProbe.YOf(content, "BOT");

        Assert.Equal(none, top, 3);
        double slack = top - bottom;
        Assert.InRange(slack, 60, 100);              // the band less one line of 12 pt text
        Assert.Equal(slack / 2, top - mid, 1);
    }

    [Fact]
    public void ABlockTallerThanItsBand_IsNotMoved()
    {
        string content = Content(Placed("TOP", 150, null, CellVerticalAlign.Top), Placed("BOT", 350, 2, CellVerticalAlign.Bottom));
        Assert.Equal(PdfContentProbe.YOf(content, "TOP"), PdfContentProbe.YOf(content, "BOT"), 3);
    }

    /// <summary>
    /// A quarter turn about the paragraph's top-left, in a band that centres it: the turn's fixed point
    /// is the band's top-left — where the producer measured the origin from — not the moved text's.
    /// </summary>
    [Fact]
    public void ATransformInABand_IsTakenAboutTheBand()
    {
        var turned = Placed("TURNED", 100, 100, CellVerticalAlign.Center);
        turned.Transform = new[] { 0.0, 1, -1, 0, 0, 0 };
        turned.TransformOrigin = new[] { 0.0, 0, 0, 0 };
        var section = new Section();
        string content = Content(turned);

        Match cm = Regex.Match(content, @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+cm");
        Assert.True(cm.Success, "no cm in the content");
        double[] m = Enumerable.Range(1, 6).Select(i => double.Parse(cm.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(1, m[1], 6);
        double fx = 100, fy = section.PageHeightPt - 100;
        Assert.Equal(fx, m[0] * fx + m[2] * fy + m[4], 1);
        Assert.Equal(fy, m[1] * fx + m[3] * fy + m[5], 1);
    }

    [Fact]
    public void ABandHeight_ThatIsNotANumber_IsRefused()
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(Placed("X", 100, double.NaN, CellVerticalAlign.Center));
        doc.Sections.Add(section);
        var ex = Assert.ThrowsAny<ArgumentException>(() => doc.ToPdf());
        Assert.Contains("BandHeightPt", ex.Message, StringComparison.Ordinal);

        section.Children[0] = Placed("X", 100, 50, CellVerticalAlign.Center);
        Assert.False(double.IsNaN(PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(doc.ToPdf()), "X")), "the control's text is drawn");
    }

    private static Paragraph Placed(string text, double left, double? band, CellVerticalAlign align)
    {
        var p = new Paragraph
        {
            SpacingAfter = 0,
            Placement = new AbsolutePlacement { LeftPt = left, TopPt = 100, WidthPt = 90, BandHeightPt = band, BandAlign = align },
        };
        p.Runs.Add(new TextRun { Text = text, FontSize = 12 });
        return p;
    }

    private static string Content(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }
}
