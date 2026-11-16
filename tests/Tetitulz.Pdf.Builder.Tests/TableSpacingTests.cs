// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The space a table's layout adds: <see cref="TableCell.InnerPaddingPt"/>, the renderer's own
/// padding round a cell (3pt a side by default, so a Builder or DOCX table is unchanged; the HTML
/// converter, stating its padding exactly, sets 0), and <see cref="Table.SpacingBefore"/> /
/// <see cref="Table.SpacingAfter"/> round the table.
/// </summary>
public class TableSpacingTests
{
    /// <summary>The line height — above the renderer's 16pt minimum row, so the floor plays no part.</summary>
    private const double Line = 20;

    /// <summary>The distance between the text of a two-row table's rows, with every cell's inner padding set by <paramref name="pad"/>.</summary>
    private static double RowPitch(double? pad)
    {
        var table = new Table();
        foreach (string text in new[] { "A", "B" })
        {
            var cell = new TableCell();
            if (pad is double p) cell.InnerPaddingPt = p;
            var para = new Paragraph { SpacingAfter = 0, LineSpacingPt = Line };
            para.Runs.Add(new TextRun { Text = text });
            cell.Blocks.Add(para);
            var row = new TableRow();
            row.Cells.Add(cell);
            table.Rows.Add(row);
        }
        var section = new Section();
        section.Children.Add(table);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        return PdfContentProbe.YOf(content, "A") - PdfContentProbe.YOf(content, "B");
    }

    /// <summary>By default a row is its line plus 3pt above and below.</summary>
    [Fact]
    public void TheDefaultIsThreePointsASide()
    {
        Assert.Equal(TableCell.DefaultPaddingPt, new TableCell().InnerPaddingPt);
        Assert.Equal(Line + 2 * TableCell.DefaultPaddingPt, RowPitch(null), 2);
    }

    /// <summary>At 0 the row is its line alone — the room the default added is gone.</summary>
    [Fact]
    public void ZeroLeavesOnlyTheLine()
        => Assert.Equal(Line, RowPitch(0), 2);

    private static Paragraph Para(string text)
    {
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = Line };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static TableCell Cell(string text, double? pad)
    {
        var cell = new TableCell();
        if (pad is double p) cell.InnerPaddingPt = p;
        cell.Blocks.Add(Para(text));
        return cell;
    }

    /// <summary>A paragraph "P", then a one-row table of <paramref name="cells"/>; the decoded content.</summary>
    private static string AfterAParagraph(params TableCell[] cells)
    {
        var table = new Table();
        var row = new TableRow();
        foreach (var c in cells) row.Cells.Add(c);
        table.Rows.Add(row);
        var section = new Section();
        section.Children.Add(Para("P"));
        section.Children.Add(table);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    /// <summary>
    /// A cell's text starts its inner padding below the row's top and in from its left edge: at 0
    /// it sits 3pt higher and 3pt further left than at the default. (Both rows of a table moving
    /// together keep the row pitch, so the pitch cannot see this.)
    /// </summary>
    [Fact]
    public void TheTextStartsItsInnerPaddingIn()
    {
        string padded = AfterAParagraph(Cell("A", null));
        string flush = AfterAParagraph(Cell("A", 0));

        double DropOf(string c) => PdfContentProbe.YOf(c, "P") - PdfContentProbe.YOf(c, "A");
        double IndentOf(string c) => PdfContentProbe.XOf(c, "A") - PdfContentProbe.XOf(c, "P");

        Assert.Equal(TableCell.DefaultPaddingPt, DropOf(padded) - DropOf(flush), 2);
        Assert.Equal(TableCell.DefaultPaddingPt, IndentOf(padded) - IndentOf(flush), 2);
    }

    /// <summary>
    /// A column widened to its longest word is widened to the word plus the cell's inner padding
    /// on both sides — none at 0. Read as how far the next column's text starts after the word's.
    /// </summary>
    [Fact]
    public void AColumnsMinimumCountsTheInnerPadding()
    {
        string word = new string('W', 25);   // wider than half the table, narrower than all of it: widened to it
        string padded = AfterAParagraph(Cell(word, null), Cell("B", null));
        string flush = AfterAParagraph(Cell(word, 0), Cell("B", 0));

        double Span(string c) => PdfContentProbe.XOf(c, "B") - PdfContentProbe.XOf(c, word);

        Assert.Equal(2 * TableCell.DefaultPaddingPt, Span(padded) - Span(flush), 2);
    }

    /// <summary>
    /// A table's <see cref="Table.SpacingBefore"/> and <see cref="Table.SpacingAfter"/> are the
    /// space round it: the paragraph after a table stating 10pt and 20pt is 26pt lower than after
    /// one stating neither, whose space after is the 4pt default.
    /// </summary>
    [Fact]
    public void ATablesSpacingIsTheRoomRoundIt()
    {
        static double After(Table table)
        {
            var section = new Section();
            section.Children.Add(Para("P"));
            section.Children.Add(table);
            section.Children.Add(Para("Q"));
            var doc = new Document();
            doc.Sections.Add(section);
            string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
            return PdfContentProbe.YOf(c, "P") - PdfContentProbe.YOf(c, "Q");
        }
        static Table OneCell()
        {
            var t = new Table();
            var row = new TableRow();
            row.Cells.Add(Cell("A", 0));
            t.Rows.Add(row);
            return t;
        }

        Assert.Equal(Table.DefaultSpacingAfterPt, new Table().SpacingAfter);
        double plain = After(OneCell());
        var spaced = OneCell();
        spaced.SpacingBefore = 10;
        spaced.SpacingAfter = 20;
        Assert.Equal(plain + 10 + 20 - Table.DefaultSpacingAfterPt, After(spaced), 2);
    }

    /// <summary>
    /// A table nested in a cell is measured with its spacing, so the outer row holds it: 20pt
    /// after the inner table rather than the default 4 moves what follows the outer table 16pt.
    /// </summary>
    [Fact]
    public void ANestedTablesSpacingIsInItsCellsHeight()
    {
        static double After(double innerSpacingAfter)
        {
            var inner = new Table { SpacingAfter = innerSpacingAfter };
            var innerRow = new TableRow();
            innerRow.Cells.Add(Cell("A", 0));
            inner.Rows.Add(innerRow);

            var outerCell = new TableCell { InnerPaddingPt = 0 };
            outerCell.Blocks.Add(inner);
            var outerRow = new TableRow();
            outerRow.Cells.Add(outerCell);
            var outer = new Table();
            outer.Rows.Add(outerRow);

            var section = new Section();
            section.Children.Add(Para("P"));
            section.Children.Add(outer);
            section.Children.Add(Para("Q"));
            var doc = new Document();
            doc.Sections.Add(section);
            string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
            return PdfContentProbe.YOf(c, "P") - PdfContentProbe.YOf(c, "Q");
        }

        Assert.Equal(After(Table.DefaultSpacingAfterPt) + 16, After(20), 2);
    }

    /// <summary>
    /// A paragraph kept with a table goes to the next page when the table's start — its space
    /// before and first row — does not fit under it: 45pt left, a 20pt paragraph, 10pt before a
    /// 20pt row. Without the space before, 40pt fits and the paragraph stays at the page's foot.
    /// </summary>
    [Fact]
    public void KeepingWithATableCountsItsSpaceBefore()
    {
        var section = new Section();
        double content = section.PageHeightPt - section.MarginTop - section.MarginBottom;
        var spacer = Para("S");
        spacer.SpacingBefore = content - Line - 45;
        section.Children.Add(spacer);

        var kept = Para("P");
        kept.KeepWithNext = true;
        section.Children.Add(kept);

        var table = new Table { SpacingBefore = 10 };
        var row = new TableRow();
        row.Cells.Add(Cell("A", 0));
        table.Rows.Add(row);
        section.Children.Add(table);

        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());

        // On the next page it is the first line, at the top; kept on this one, at the foot.
        Assert.True(PdfContentProbe.YOf(c, "P") > section.PageHeightPt / 2,
            $"the kept paragraph is at y={PdfContentProbe.YOf(c, "P")}, at the foot of the first page");
    }

    /// <summary>
    /// A row is at least <see cref="Table.MinRowHeightPt"/> tall — 16pt by default, so a 10pt line
    /// with no padding still takes 16 — and only as tall as its content when a table sets 0.
    /// </summary>
    [Fact]
    public void ARowsFloorIsTheTablesMinimum()
    {
        static double Pitch(double? min)
        {
            var table = new Table();
            if (min is double m) table.MinRowHeightPt = m;
            foreach (string text in new[] { "A", "B" })
            {
                var cell = new TableCell { InnerPaddingPt = 0 };
                var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
                p.Runs.Add(new TextRun { Text = text });
                cell.Blocks.Add(p);
                var row = new TableRow();
                row.Cells.Add(cell);
                table.Rows.Add(row);
            }
            var section = new Section();
            section.Children.Add(table);
            var doc = new Document();
            doc.Sections.Add(section);
            string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
            return PdfContentProbe.YOf(c, "A") - PdfContentProbe.YOf(c, "B");
        }

        Assert.Equal(Table.DefaultMinRowHeightPt, new Table().MinRowHeightPt);
        Assert.Equal(Table.DefaultMinRowHeightPt, Pitch(null), 2);
        Assert.Equal(10, Pitch(0), 2);
    }

    /// <summary>
    /// <see cref="Paragraph.LineHeightFactor"/> is the line's size times the factor with no floor —
    /// a 10pt line at 1.15 advances 11.5pt — where the default (the control) floors it at 14pt.
    /// </summary>
    [Fact]
    public void ALineHeightFactorHasNoFloor()
    {
        static double Pitch(double factor)
        {
            var p = new Paragraph { SpacingAfter = 0, LineHeightFactor = factor };
            p.Runs.Add(new TextRun { Text = "A", FontSize = 10 });
            p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "B", FontSize = 10 });
            var section = new Section();
            section.Children.Add(p);
            var doc = new Document();
            doc.Sections.Add(section);
            string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
            return PdfContentProbe.YOf(c, "A") - PdfContentProbe.YOf(c, "B");
        }
        Assert.Equal(11.5, Pitch(1.15), 2);
        Assert.Equal(14, Pitch(0), 2);
    }
}
