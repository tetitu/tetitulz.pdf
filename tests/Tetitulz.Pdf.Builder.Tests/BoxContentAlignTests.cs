// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.ContentAlign"/>: a box closing taller than its blocks — its minimum height —
/// moves them down by the room left, half of it centred, in the flow and in a table cell alike; its own
/// fill stays where it is, the block after it does not move, and a link moves with its text. A box as
/// tall as its blocks, one that breaks, and one with nothing in it are left alone (XFA's
/// <c>para vAlign</c> in a flowed draw or field).
/// </summary>
public class BoxContentAlignTests
{
    private static Paragraph Para(string text, string? link = null)
    {
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
        p.Runs.Add(new TextRun { Text = text, LinkUri = link });
        return p;
    }

    private static BoxStart Box(double minHeight, CellVerticalAlign align)
        => new BoxStart { Shading = "#0000FF", MinHeightPt = minHeight, ContentAlign = align };

    private static byte[] Pdf(params IDocElement[] blocks)
    {
        var section = new Section();
        section.Children.AddRange(blocks);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc.ToPdf();
    }

    private static double YOnPage(byte[] pdf, string text)
        => PdfContentProbe.DrawnTextsOnPage(pdf).First(d => d.Text == text).Y;

    [Fact]
    public void TheBlocksMoveDownByTheRoomLeft_HalfOfItCentred()
    {
        byte[] top = Pdf(Box(100, CellVerticalAlign.Top), Para("IN"), new BoxEnd(), Para("NEXT"));
        byte[] mid = Pdf(Box(100, CellVerticalAlign.Center), Para("IN"), new BoxEnd(), Para("NEXT"));
        byte[] bottom = Pdf(Box(100, CellVerticalAlign.Bottom), Para("IN"), new BoxEnd(), Para("NEXT"));

        double slack = YOnPage(top, "IN") - YOnPage(bottom, "IN");
        Assert.InRange(slack, 80, 95);                       // 100 less one 10 pt line
        Assert.Equal(slack / 2, YOnPage(top, "IN") - YOnPage(mid, "IN"), 2);
        // The box is as tall either way, so the block after it is where it was.
        Assert.Equal(YOnPage(top, "NEXT"), YOnPage(bottom, "NEXT"), 2);
        Assert.Equal(YOnPage(top, "NEXT"), YOnPage(mid, "NEXT"), 2);
    }

    /// <summary>
    /// The default is the top: a box that says nothing draws its blocks where a top-aligned one does,
    /// and writes no translation.
    /// </summary>
    [Fact]
    public void TheDefault_IsTheTop()
    {
        var plain = new BoxStart { Shading = "#0000FF", MinHeightPt = 100 };
        Assert.Equal(CellVerticalAlign.Top, plain.ContentAlign);
        byte[] pdf = Pdf(plain, Para("IN"), new BoxEnd());
        Assert.Equal(YOnPage(Pdf(Box(100, CellVerticalAlign.Top), Para("IN"), new BoxEnd()), "IN"), YOnPage(pdf, "IN"), 3);
        Assert.DoesNotMatch(@"1 0 0 1 0 -[\d.]+ cm", PdfContentProbe.DecodeStreams(pdf));
    }

    /// <summary>
    /// The translation wraps the blocks alone: the box's fill is written before it, so it is drawn
    /// where the box is, and the text after it.
    /// </summary>
    [Fact]
    public void TheBoxsFill_StaysOutsideTheMove()
    {
        string content = PdfContentProbe.DecodeStreams(Pdf(Box(100, CellVerticalAlign.Bottom), Para("IN"), new BoxEnd()));
        Match move = Regex.Match(content, @"q\s+1 0 0 1 0 -([\d.]+) cm");
        Assert.True(move.Success, "no translation in the content");
        int fill = content.IndexOf("0 0 1 rg", System.StringComparison.Ordinal);
        int text = content.IndexOf("(IN) Tj", System.StringComparison.Ordinal);
        Assert.InRange(fill, 0, move.Index - 1);
        Assert.True(text > move.Index, "the text is drawn after the translation");
    }

    [Fact]
    public void BlocksTallerThanTheMinimum_StayWhereTheyAre()
    {
        byte[] top = Pdf(Box(5, CellVerticalAlign.Top), Para("IN"), new BoxEnd());
        byte[] bottom = Pdf(Box(5, CellVerticalAlign.Bottom), Para("IN"), new BoxEnd());
        Assert.Equal(YOnPage(top, "IN"), YOnPage(bottom, "IN"), 3);
        Assert.DoesNotMatch(@"1 0 0 1 0 -[\d.]+ cm", PdfContentProbe.DecodeStreams(bottom));
    }

    /// <summary>A box that breaks across pages is as tall as its blocks, so nothing is aligned: seventy lines stay where they were.</summary>
    [Fact]
    public void ABoxThatBreaks_IsNotAligned()
    {
        IDocElement[] Blocks(CellVerticalAlign align)
        {
            var list = new List<IDocElement> { Box(100, align) };
            for (int i = 0; i < 70; i++) list.Add(Para("L" + i));
            list.Add(new BoxEnd());
            return list.ToArray();
        }
        byte[] top = Pdf(Blocks(CellVerticalAlign.Top)), bottom = Pdf(Blocks(CellVerticalAlign.Bottom));
        Assert.Equal(2, PdfContentProbe.CountPages(bottom));
        Assert.Equal(YOnPage(top, "L0"), YOnPage(bottom, "L0"), 3);
        Assert.Equal(YOnPage(top, "L69"), YOnPage(bottom, "L69"), 3);
    }

    [Fact]
    public void AnEmptyBox_WritesNoMove()
    {
        Assert.DoesNotMatch(@"1 0 0 1 0 -[\d.]+ cm", PdfContentProbe.DecodeStreams(Pdf(Box(100, CellVerticalAlign.Bottom), new BoxEnd())));
        Assert.Matches(@"1 0 0 1 0 -[\d.]+ cm", PdfContentProbe.DecodeStreams(Pdf(Box(100, CellVerticalAlign.Bottom), Para("IN"), new BoxEnd())));
    }

    [Fact]
    public void InATableCell_TheBlocksMoveToo()
    {
        Table TableWith(CellVerticalAlign align)
        {
            var cell = new TableCell { InnerPaddingPt = 0 };
            cell.Blocks.Add(Box(100, align));
            cell.Blocks.Add(Para("CELL", "https://example.com/"));
            cell.Blocks.Add(new BoxEnd());
            var row = new TableRow();
            row.Cells.Add(cell);
            var t = new Table { MinRowHeightPt = 0, SpacingAfter = 0, BorderWidth = 0 };
            t.Rows.Add(row);
            return t;
        }
        byte[] top = Pdf(TableWith(CellVerticalAlign.Top), Para("NEXT"));
        byte[] mid = Pdf(TableWith(CellVerticalAlign.Center), Para("NEXT"));
        double moved = YOnPage(top, "CELL") - YOnPage(mid, "CELL");
        Assert.InRange(moved, 40, 47.5);                     // half of 100 less one 10 pt line
        Assert.Equal(YOnPage(top, "NEXT"), YOnPage(mid, "NEXT"), 2);
        // The link in the cell moves with it.
        Assert.Equal(moved, LinkRect(top)[1] - LinkRect(mid)[1], 2);
    }

    /// <summary>A link among the moved blocks moves with its text: its rectangle is as far lower as the text is.</summary>
    [Fact]
    public void ALinkInsideTheBox_MovesWithItsText()
    {
        byte[] top = Pdf(Box(100, CellVerticalAlign.Top), Para("LINK", "https://example.com/"), new BoxEnd());
        byte[] bottom = Pdf(Box(100, CellVerticalAlign.Bottom), Para("LINK", "https://example.com/"), new BoxEnd());
        double textMoved = YOnPage(top, "LINK") - YOnPage(bottom, "LINK");
        Assert.InRange(textMoved, 80, 95);
        double[] a = LinkRect(top), b = LinkRect(bottom);
        Assert.Equal(a[0], b[0], 2);
        Assert.Equal(textMoved, a[1] - b[1], 2);
        Assert.Equal(textMoved, a[3] - b[3], 2);
    }

    /// <summary>The first link annotation's <c>/Rect</c>, as four numbers.</summary>
    private static double[] LinkRect(byte[] pdf)
    {
        var m = Regex.Match(Encoding.Latin1.GetString(pdf), @"/Rect\s*\[([^\]]*)\]");
        Assert.True(m.Success, "no link annotation");
        return m.Groups[1].Value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }
}
