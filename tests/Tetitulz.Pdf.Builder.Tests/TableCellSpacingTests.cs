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
/// <see cref="Table.CellSpacingPt"/> and <see cref="Table.RowSpacingPt"/> — CSS's
/// <c>border-spacing</c>: the cells set apart from each other and from the table's edge, the
/// columns sharing what is left, a spanning cell spanning the spacing between its columns, and the
/// table's own border a frame outside the spacing. Both 0 by default, so a Builder or DOCX table is
/// unchanged.
/// </summary>
public class TableCellSpacingTests
{
    private const double Width = 200;

    private static TableCell Cell(string text, int span = 1, VerticalMerge merge = VerticalMerge.None)
    {
        var cell = new TableCell { InnerPaddingPt = 0, BackgroundColor = "#0000FF", GridSpan = span, VMerge = merge };
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
        p.Runs.Add(new TextRun { Text = text });
        cell.Blocks.Add(p);
        return cell;
    }

    private static Table NewTable(double h, double v, params TableCell[][] rows)
    {
        var t = new Table { WidthPt = Width, AutoLayout = false, MinRowHeightPt = 0, CellSpacingPt = h, RowSpacingPt = v };
        foreach (var cells in rows)
        {
            var row = new TableRow();
            row.Cells.AddRange(cells);
            t.Rows.Add(row);
        }
        return t;
    }

    private static string Render(Table t, bool paragraphAfter = false)
    {
        var section = new Section();
        section.Children.Add(t);
        if (paragraphAfter)
        {
            var q = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
            q.Runs.Add(new TextRun { Text = "Q" });
            section.Children.Add(q);
        }
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    /// <summary>The filled rectangles — the cells' backgrounds — in drawing order.</summary>
    private static List<(double X, double Y, double W, double H)> Fills(string content) =>
        Regex.Matches(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\s+f").Cast<Match>()
             .Select(m => (D(m, 1), D(m, 2), D(m, 3), D(m, 4))).ToList();

    private static double D(Match m, int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

    /// <summary>The model's default spaces nothing.</summary>
    [Fact]
    public void TheDefaultIsNoSpacing()
    {
        Assert.Equal(0, new Table().CellSpacingPt);
        Assert.Equal(0, new Table().RowSpacingPt);
    }

    /// <summary>
    /// Two columns of a 200pt table spaced 10pt: each is (200 − 3 × 10) / 2 = 85pt, the first 10pt
    /// in from the table's edge and the second 10pt after it; unspaced (the control) they touch.
    /// </summary>
    [Fact]
    public void ColumnsShareTheWidthTheSpacingLeaves()
    {
        var spaced = Fills(Render(NewTable(10, 0, new[] { Cell("A"), Cell("B") })));
        var flush = Fills(Render(NewTable(0, 0, new[] { Cell("A"), Cell("B") })));
        double left = flush[0].X;

        Assert.Equal(new[] { 85.0, 85 }, spaced.Select(f => f.W));
        Assert.Equal(left + 10, spaced[0].X, 2);
        Assert.Equal(left + 10 + 85 + 10, spaced[1].X, 2);
        Assert.Equal(flush[0].X + 100, flush[1].X, 2);
    }

    /// <summary>A cell spanning both columns spans the spacing between them too: as wide as the two cells and their gap.</summary>
    [Fact]
    public void ASpanningCellSpansTheSpacingBetweenItsColumns()
    {
        var f = Fills(Render(NewTable(10, 0, new[] { Cell("A"), Cell("B") }, new[] { Cell("C", span: 2) })));
        Assert.Equal(85 + 10 + 85, f[2].W, 2);
        Assert.Equal(f[0].X, f[2].X, 2);
    }

    /// <summary>A right-to-left table mirrors the spaced columns: the first is 10pt in from the right edge.</summary>
    [Fact]
    public void ARightToLeftTableMirrorsTheSpacing()
    {
        var t = NewTable(10, 0, new[] { Cell("A"), Cell("B") });
        t.RtlColumns = true;
        var rtl = Fills(Render(t));
        var ltr = Fills(Render(NewTable(10, 0, new[] { Cell("A"), Cell("B") })));
        Assert.Equal(ltr[1].X, rtl[0].X, 2);
        Assert.Equal(ltr[0].X, rtl[1].X, 2);
    }

    /// <summary>
    /// Rows are the vertical spacing apart — 6pt between the first's bottom and the second's top —
    /// and a table's height counts it above, between and below them: a paragraph after the table is
    /// 3 × 6pt lower than after an unspaced one.
    /// </summary>
    [Fact]
    public void RowsAreTheVerticalSpacingApart()
    {
        var f = Fills(Render(NewTable(0, 6, new[] { Cell("A") }, new[] { Cell("B") })));
        Assert.Equal(6, f[0].Y - (f[1].Y + f[1].H), 2);

        static double QOf(double v) => PdfContentProbe.YOf(Render(NewTable(0, v, new[] { Cell("A") }, new[] { Cell("B") }), paragraphAfter: true), "Q");
        Assert.Equal(3 * 6, QOf(0) - QOf(6), 2);
    }

    /// <summary>A row-merged cell spans the spacing between its rows: two 10pt rows and the 6pt between them.</summary>
    [Fact]
    public void ARowMergedCellSpansTheRowSpacing()
    {
        var f = Fills(Render(NewTable(0, 6,
            new[] { Cell("A", merge: VerticalMerge.Restart), Cell("B") },
            new[] { Cell("", merge: VerticalMerge.Continue), Cell("C") })));
        Assert.Equal(10 + 6 + 10, f[0].H, 2);
    }

    /// <summary>
    /// A table framed by its own border (<see cref="Table.OuterBorderOnly"/>) and spaced draws the
    /// frame at the table's edge, outside the spacing — two vertical rules, at 0 and 200 from the
    /// table's left — and not on its cells; unspaced, the frame is its outer cells' edges (the control).
    /// </summary>
    [Fact]
    public void ASpacedTablesFrameIsAtItsEdge()
    {
        static List<double> VerticalRules(double h)
        {
            var t = NewTable(h, h, new[] { Cell("A"), Cell("B") });
            t.BorderWidth = 1;
            t.OuterBorderOnly = true;
            string c = Render(t);
            return PdfContentProbe.StrokedSegments(c).Where(s => s.IsVertical).Select(s => s.X0).Distinct().OrderBy(x => x).ToList();
        }
        var spaced = VerticalRules(10);
        var flush = VerticalRules(0);

        Assert.Equal(2, spaced.Count);
        Assert.Equal(Width, spaced[1] - spaced[0], 2);
        Assert.Equal(spaced, flush);   // the table's edge either way; only the cells between have moved
    }

    /// <summary>A spaced frame is also its top and bottom: four segments round a one-row table, the rows' spacing inside it.</summary>
    [Fact]
    public void ASpacedFrameEnclosesTheRowSpacing()
    {
        var t = NewTable(10, 10, new[] { Cell("A") });
        t.BorderWidth = 1;
        t.OuterBorderOnly = true;
        string c = Render(t);
        var horizontal = PdfContentProbe.StrokedSegments(c).Where(s => !s.IsVertical).Select(s => s.Y0).Distinct().OrderBy(y => y).ToList();
        var cell = Fills(c)[0];

        Assert.Equal(2, horizontal.Count);
        Assert.Equal(cell.Y - 10, horizontal[0], 2);
        Assert.Equal(cell.Y + cell.H + 10, horizontal[1], 2);
    }

    /// <summary>
    /// A spaced table broken across pages is framed on each: the continuation starts the spacing
    /// below the page's top, and there is a frame on both pages.
    /// </summary>
    [Fact]
    public void ABrokenSpacedTableIsFramedOnEachPage()
    {
        var rows = Enumerable.Range(0, 120).Select(i => new[] { Cell("R" + i) }).ToArray();
        var t = NewTable(4, 4, rows);
        t.BorderWidth = 1;
        t.OuterBorderOnly = true;
        var section = new Section();
        var before = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
        before.Runs.Add(new TextRun { Text = "P" });
        section.Children.Add(before);
        section.Children.Add(t);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        Assert.True(PdfContentProbe.CountPages(pdf) >= 2, "the table did not break, so this proves nothing");

        string c = PdfContentProbe.DecodeStreams(pdf);
        double top = section.PageHeightPt - section.MarginTop;
        // Each continuation page's frame starts at the page's top; the first page's, below the paragraph.
        int continuations = PdfContentProbe.CountPages(pdf) - 1;
        int framesAtTop = PdfContentProbe.StrokedSegments(c).Count(s => !s.IsVertical && System.Math.Abs(s.Y0 - top) < 0.01);
        Assert.Equal(continuations, framesAtTop);

        // The first row on a continuation page is the spacing below the top.
        var tops = Fills(c).Select(f => f.Y + f.H).Where(y => System.Math.Abs(y - (top - 4)) < 0.01).Count();
        Assert.Equal(continuations, tops);
    }

    /// <summary>
    /// A repeated header row is spaced from the row after it like any other: every row below
    /// another on a page is 4pt under it, the repeated header's next row too.
    /// </summary>
    [Fact]
    public void ARepeatedHeaderIsSpacedFromTheNextRow()
    {
        var rows = new[] { new[] { Cell("H") } }.Concat(Enumerable.Range(0, 120).Select(i => new[] { Cell("R" + i) })).ToArray();
        var t = NewTable(0, 4, rows);
        t.Rows[0].IsHeader = true;
        t.RepeatHeaderRow = true;
        var section = new Section();
        section.Children.Add(t);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();
        var f = Fills(PdfContentProbe.DecodeStreams(pdf));
        Assert.True(f.Count > rows.Length, "no header was repeated, so this proves nothing");

        for (int k = 0; k + 1 < f.Count; k++)
            if (f[k + 1].Y < f[k].Y)   // the next row on the same page
                Assert.Equal(4, f[k].Y - (f[k + 1].Y + f[k + 1].H), 2);
    }

    /// <summary>
    /// A spaced table nested in a cell is measured with its spacing, so the outer row holds it:
    /// two inner rows spaced 6pt move what follows the outer table 3 × 6pt.
    /// </summary>
    [Fact]
    public void ANestedSpacedTableIsMeasuredWithItsSpacing()
    {
        static double QOf(double v)
        {
            var outerCell = new TableCell { InnerPaddingPt = 0 };
            outerCell.Blocks.Add(NewTable(0, v, new[] { Cell("A") }, new[] { Cell("B") }));
            var outer = new Table { MinRowHeightPt = 0 };
            var row = new TableRow();
            row.Cells.Add(outerCell);
            outer.Rows.Add(row);
            return PdfContentProbe.YOf(Render(outer, paragraphAfter: true), "Q");
        }
        Assert.Equal(3 * 6, QOf(0) - QOf(6), 2);
    }

    /// <summary>
    /// A paragraph kept with a spaced table goes to the next page when the table's start — the
    /// spacing above its first row, the row and the spacing below it — does not fit under it: 45pt
    /// left, a 20pt paragraph, 4 + 20 + 4. Unspaced (the control), 40pt fits and it stays.
    /// </summary>
    [Fact]
    public void KeepingWithASpacedTableCountsItsSpacing()
    {
        static double YOfKept(double spacing)
        {
            var section = new Section();
            double content = section.PageHeightPt - section.MarginTop - section.MarginBottom;
            var spacer = new Paragraph { SpacingAfter = 0, LineSpacingPt = 20, SpacingBefore = content - 20 - 45 };
            spacer.Runs.Add(new TextRun { Text = "S" });
            section.Children.Add(spacer);
            var kept = new Paragraph { SpacingAfter = 0, LineSpacingPt = 20, KeepWithNext = true };
            kept.Runs.Add(new TextRun { Text = "P" });
            section.Children.Add(kept);

            var cell = new TableCell { InnerPaddingPt = 0 };
            var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 20 };
            p.Runs.Add(new TextRun { Text = "A" });
            cell.Blocks.Add(p);
            var t = new Table { RowSpacingPt = spacing };
            var row = new TableRow();
            row.Cells.Add(cell);
            t.Rows.Add(row);
            section.Children.Add(t);

            var doc = new Document();
            doc.Sections.Add(section);
            return PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(doc.ToPdf()), "P");
        }
        double half = new Section().PageHeightPt / 2;
        Assert.True(YOfKept(0) < half, "unspaced, the kept paragraph already left its page, so this proves nothing");
        Assert.True(YOfKept(4) > half, $"the kept paragraph stayed at y={YOfKept(4)}, at the foot of the page");
    }

    /// <summary>
    /// A spanning cell's text wraps across the spacing between its columns: in a 200pt table
    /// spaced 10pt it is 180pt wide, and wraps as a one-cell 180pt table does — at every length of
    /// text and two word widths (a line of one breaks at the same count at 170pt and at 180pt), not
    /// 10pt short of it.
    /// </summary>
    [Fact]
    public void ASpanningCellWrapsAcrossTheSpacing()
    {
        var heights = new List<double>();
        foreach (string word in new[] { "a", "ab" })
        {
            for (int n = 1; n <= 60; n++)
            {
                string text = string.Join(" ", Enumerable.Repeat(word, n));
                var spanned = Fills(Render(NewTable(10, 0, new[] { Cell("x"), Cell("y") }, new[] { Cell(text, span: 2) })))[2].H;
                var single = NewTable(0, 0, new[] { Cell(text) });
                single.WidthPt = 180;
                Assert.Equal(Fills(Render(single))[0].H, spanned, 2);
                heights.Add(spanned);
            }
        }
        Assert.True(heights.Distinct().Count() > 2, "the text never wrapped, so this proves nothing");
    }
}
