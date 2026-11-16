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
/// <see cref="BoxStart.MinHeightPt"/>: a box whose blocks are shorter grows to it — an empty one
/// (a progress bar's track) is drawn at it — one whose blocks are taller is as tall as they are, and
/// the blocks after it start below it. In the flow and in a table cell alike.
/// </summary>
public class BoxMinHeightTests
{
    private static Paragraph Para(string text)
    {
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static BoxStart Box(double minHeight) => new BoxStart { Shading = "#0000FF", MinHeightPt = minHeight };

    private static string Render(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    /// <summary>The filled rectangles' heights, in drawing order.</summary>
    private static List<double> FillHeights(string content) =>
        Regex.Matches(content, @"[-\d.]+ [-\d.]+ [-\d.]+ ([-\d.]+) re\s+f").Cast<Match>()
             .Select(m => System.Math.Abs(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))).ToList();

    [Fact]
    public void AnEmptyBoxIsDrawnAtItsMinimumHeight()
        => Assert.Equal(30, Assert.Single(FillHeights(Render(Box(30), new BoxEnd()))), 2);

    /// <summary>Blocks taller than the minimum decide: the box is as tall as with none.</summary>
    [Fact]
    public void TallerBlocksWin()
    {
        double natural = Assert.Single(FillHeights(Render(Box(0), Para("A"), Para("B"), Para("C"), new BoxEnd())));
        Assert.True(natural > 5);
        Assert.Equal(natural, Assert.Single(FillHeights(Render(Box(5), Para("A"), Para("B"), Para("C"), new BoxEnd()))), 2);
    }

    /// <summary>The block after the box starts below its minimum height: 40pt further down than after an empty box of none.</summary>
    [Fact]
    public void TheNextBlockStartsBelowIt()
    {
        string plain = Render(Para("TOP"), Box(0), new BoxEnd(), Para("NEXT"));
        string tall = Render(Para("TOP"), Box(40), new BoxEnd(), Para("NEXT"));
        Assert.Equal(PdfContentProbe.YOf(plain, "NEXT") - 40, PdfContentProbe.YOf(tall, "NEXT"), 2);
    }

    /// <summary>In a table cell the row is sized for it: the text after the table is 40pt further down.</summary>
    [Fact]
    public void ACellsRowIsSizedForIt()
    {
        Table TableWith(double min)
        {
            var cell = new TableCell { InnerPaddingPt = 0 };
            cell.Blocks.Add(Box(min));
            cell.Blocks.Add(new BoxEnd());
            cell.Blocks.Add(Para("X"));
            var row = new TableRow();
            row.Cells.Add(cell);
            var t = new Table { MinRowHeightPt = 0, SpacingAfter = 0, BorderWidth = 0 };
            t.Rows.Add(row);
            return t;
        }
        string plain = Render(TableWith(0), Para("NEXT"));
        string tall = Render(TableWith(40), Para("NEXT"));
        Assert.Equal(PdfContentProbe.YOf(plain, "NEXT") - 40, PdfContentProbe.YOf(tall, "NEXT"), 2);
    }

    /// <summary>
    /// A box exactly as tall as the page area, opened at its top — CSS's <c>min-height: 100vh</c> —
    /// fits: it is drawn at that height, and closing it opens no blank second page. It lands on the
    /// limit give or take the arithmetic, and a hair under dropped its height, or broke the page.
    /// </summary>
    [Fact]
    public void ABoxAsTallAsThePageAreaFitsOnOnePage()
    {
        var section = new Section();
        double area = section.PageHeightPt - section.MarginTop - section.MarginBottom;
        section.Children.Add(Box(area));
        section.Children.Add(Para("IN"));
        section.Children.Add(new BoxEnd());
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
        Assert.Equal(area, FillHeights(PdfContentProbe.DecodeStreams(pdf)).Single(), 1);
    }

    /// <summary>
    /// A box that breaks across pages is as tall as its blocks on the next: seventy 10pt lines in a
    /// box of 100pt minimum leave a few lines on page two, and that fragment is not grown to 100pt.
    /// </summary>
    [Fact]
    public void ABrokenBoxIsNotGrownOnItsNextPage()
    {
        var blocks = new List<IDocElement> { Box(100) };
        for (int i = 0; i < 70; i++) blocks.Add(Para("L" + i));
        blocks.Add(new BoxEnd());
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
        double lastFragment = FillHeights(PdfContentProbe.DecodeStreams(pdf)).Last();
        Assert.InRange(lastFragment, 1, 90);
    }

    /// <summary>
    /// A minimum that does not fit the rest of the page is not carried to the next: the box near
    /// the bottom is as tall as its blocks, and nothing moves to a second page.
    /// </summary>
    [Fact]
    public void AMinimumThatDoesNotFitThePageIsDropped()
    {
        var blocks = new List<IDocElement>();
        for (int i = 0; i < 60; i++) blocks.Add(Para("L" + i));
        blocks.Add(Box(2000));
        blocks.Add(Para("IN"));
        blocks.Add(new BoxEnd());
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
        Assert.True(FillHeights(PdfContentProbe.DecodeStreams(pdf)).Single() < 20);
    }
}
