// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// No table column is narrower than its longest unbreakable word while the table can hold every
/// column's minimum — the floor CSS table layout keeps.
/// </summary>
/// <remarks>
/// <para>Found through HTML's <c>&lt;col style="width:100%"&gt;</c> beside an auto column: the
/// over-specified set left the auto column a flat tenth of the table, and a form's label column had
/// its words standing out of the cell.</para>
/// <para>A column's width is read from the text: in a left-to-right table the second column's text
/// starts exactly one first-column width after the first's, since both sit the same padding in.
/// The minimum is asserted against <see cref="PdfExporter.AdvanceWidth"/>, the layout's own advance
/// widths, plus the cell's padding on both sides.</para>
/// </remarks>
public class TableMinContentTests
{
    private const double Size = 12;
    private const string Long = "Supercalifragilistic";
    private const string Longer = "Antidisestablishmentarianism";

    private static TableCell Cell(string text)
    {
        var cell = new TableCell();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text, FontSize = Size });
        cell.Blocks.Add(p);
        return cell;
    }

    /// <summary>The first column's width, read from where the second column's text starts.</summary>
    private static double FirstColumnWidth(Table table, string first, string second)
    {
        var section = new Section();
        section.Children.Add(table);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        double a = PdfContentProbe.XOf(content, first), b = PdfContentProbe.XOf(content, second);
        Assert.False(double.IsNaN(a) || double.IsNaN(b), "a cell's text was not found");
        return b - a;
    }

    private static Table TwoColumns(string first, string second, params double[] widths)
    {
        var table = new Table();
        var row = new TableRow();
        row.Cells.Add(Cell(first));
        row.Cells.Add(Cell(second));
        table.Rows.Add(row);
        foreach (double w in widths) table.ColumnWidths.Add(w);
        return table;
    }

    private static double Minimum(string word) =>
        PdfExporter.AdvanceWidth(word, bold: false, italic: false, size: Size) + 2 * TableCell.DefaultPaddingPt;

    /// <summary>
    /// The case that found it: an auto column beside one declared at the table's whole width.
    /// </summary>
    [Fact]
    public void AnAutoColumnBesideAFullWidthOne_HoldsItsLongestWord()
    {
        double width = FirstColumnWidth(TwoColumns(Long, "Value", 0, 10000), Long, "Value");

        // Exactly the minimum: the width comes from the other column, which has plenty to spare.
        Assert.Equal(Minimum(Long), width, 1);
    }

    /// <summary>A declared width too narrow for its word is widened the same way.</summary>
    [Fact]
    public void ADeclaredColumnTooNarrowForItsWord_IsWidened()
    {
        double width = FirstColumnWidth(TwoColumns(Long, "Value", 20, 400), Long, "Value");

        Assert.Equal(Minimum(Long), width, 1);
    }

    /// <summary>The control: a table whose words fit keeps the widths it was given.</summary>
    [Fact]
    public void ATableWhoseWordsFit_KeepsItsWidths()
    {
        double first = FirstColumnWidth(TwoColumns("Name", "Value", 150, 300), "Name", "Value");

        Assert.Equal(150.0 * PageContentWidth() / 450, first, 1);
    }

    /// <summary>
    /// When the minimums cannot all fit, each column gets a share of the table in proportion to its
    /// minimum, rather than one column keeping all of the overflow.
    /// </summary>
    [Fact]
    public void MinimumsThatCannotAllFit_ShareTheTableInProportion()
    {
        var table = TwoColumns(Long, Longer);
        table.WidthPt = 120;

        double first = FirstColumnWidth(table, Long, Longer);

        Assert.Equal(120 * Minimum(Long) / (Minimum(Long) + Minimum(Longer)), first, 1);
    }

    /// <summary>A cell paragraph's indents are part of what its column must hold.</summary>
    [Fact]
    public void ACellParagraphsIndentsCountTowardsTheMinimum()
    {
        var table = TwoColumns(Long, "Value", 0, 10000);
        var indented = (Paragraph)table.Rows[0].Cells[0].Blocks[0];
        indented.IndentLeft = 20;
        indented.IndentRight = 10;

        // The word itself starts 20pt further in, so the column is the text-to-text distance + 20.
        double column = FirstColumnWidth(table, Long, "Value") + 20;
        Assert.Equal(Minimum(Long) + 30, column, 1);
    }

    /// <summary>
    /// A word inside a box (<see cref="BoxStart"/>) needs the box's margins, padding and borders
    /// beside it; after the box, the cell's own edge again.
    /// </summary>
    [Fact]
    public void AWordInABoxNeedsTheBoxsInsetsBesideIt()
    {
        var table = TwoColumns("Value", "End", 0, 10000);
        var cell = table.Rows[0].Cells[0];
        cell.Blocks.Insert(0, new BoxStart { PaddingLeft = 10, PaddingRight = 15, MarginLeft = 3, MarginRight = 4 });
        cell.Blocks.Insert(1, Cell(Long).Blocks[0]);
        cell.Blocks.Insert(2, new BoxEnd());

        // The "Value" paragraph after the box sits at the cell's edge; the column holds the boxed
        // word, its 25pt of padding and its 7pt of margins.
        Assert.Equal(Minimum(Long) + 32, FirstColumnWidth(table, "Value", "End"), 1);
    }

    /// <summary>After its box closes, a word needs only the cell's own padding.</summary>
    [Fact]
    public void AWordAfterABoxNeedsNoneOfItsInsets()
    {
        var table = TwoColumns(Long, "End", 0, 10000);
        var cell = table.Rows[0].Cells[0];
        cell.Blocks.Insert(0, new BoxStart { PaddingLeft = 10, PaddingRight = 15 });
        cell.Blocks.Insert(1, Cell("Tiny").Blocks[0]);
        cell.Blocks.Insert(2, new BoxEnd());

        Assert.Equal(Minimum(Long), FirstColumnWidth(table, Long, "End"), 1);
    }

    /// <summary>The floor is the longest word, not the whole line: a cell may still wrap.</summary>
    /// <remarks>
    /// Read from the FIRST word. Measured from the long word, a floor wrongly taken from the whole
    /// line reads the same: the cell stops wrapping and that word moves right by exactly the
    /// "Tiny " before it — the sweep's M13 survived that way.
    /// </remarks>
    [Fact]
    public void TheFloorIsTheLongestWord_NotTheLine()
    {
        double width = FirstColumnWidth(TwoColumns("Tiny " + Long, "Value", 0, 10000), "Tiny", "Value");

        Assert.Equal(Minimum(Long), width, 1);
    }

    /// <summary>The widest cell in a column sets its floor, wherever its row is.</summary>
    [Fact]
    public void TheWidestCellInAColumnSetsItsFloor()
    {
        var table = TwoColumns(Long, "Value", 0, 10000);
        var row = new TableRow();
        row.Cells.Add(Cell("Name"));
        row.Cells.Add(Cell("End"));
        table.Rows.Add(row);

        Assert.Equal(Minimum(Long), FirstColumnWidth(table, Long, "Value"), 1);
    }

    /// <summary>
    /// A cell spanning two columns sets neither one's floor: its word fits the two together, and a
    /// floor on the first alone would widen it for nothing.
    /// </summary>
    [Fact]
    public void ASpanningCellSetsNoColumnsFloor()
    {
        var table = TwoColumns("Name", "Value", 1, 1);
        table.WidthPt = 2 * Minimum(Long) - 20;   // each half narrower than the word, the two wider
        var spanning = Cell(Long);
        spanning.GridSpan = 2;
        var row = new TableRow();
        row.Cells.Add(spanning);
        table.Rows.Insert(0, row);

        Assert.Equal(table.WidthPt.Value / 2, FirstColumnWidth(table, "Name", "Value"), 1);
    }

    /// <summary>
    /// An empty cell sets no floor — not even its padding: a word processor's narrow spacer column
    /// keeps its width, and an indented paragraph with no text in it is still empty.
    /// </summary>
    [Fact]
    public void AnEmptyCellSetsNoFloor()
    {
        var table = new Table();
        var row = new TableRow();
        var spacer = new TableCell();
        spacer.Blocks.Add(new Paragraph { IndentLeft = 10 });
        row.Cells.Add(spacer);
        row.Cells.Add(Cell("Mid"));
        row.Cells.Add(Cell("End"));
        table.Rows.Add(row);
        table.ColumnWidths.Add(2);
        table.ColumnWidths.Add(200);
        table.ColumnWidths.Add(200);

        double mid = FirstColumnWidth(table, "Mid", "End");        // the second column, 200 shares
        double spacerWidth = mid * 2 / 200;
        Assert.True(spacerWidth < 2 * TableCell.DefaultPaddingPt, "the spacer column was already wide");
        // Its width, read as the distance from the page margin to the next column's text.
        Assert.Equal(PageLeft() + spacerWidth + TableCell.DefaultPaddingPt, XOf(table, "Mid"), 1);
    }

    /// <summary>A box with no words in it sets no floor either: its insets are room beside words, not a width.</summary>
    [Fact]
    public void AnEmptyBoxSetsNoFloor()
    {
        var table = new Table();
        var row = new TableRow();
        var spacer = new TableCell();
        spacer.Blocks.Add(new BoxStart { PaddingLeft = 10, PaddingRight = 15 });
        spacer.Blocks.Add(new Paragraph());
        spacer.Blocks.Add(new BoxEnd());
        row.Cells.Add(spacer);
        row.Cells.Add(Cell("Mid"));
        row.Cells.Add(Cell("End"));
        table.Rows.Add(row);
        table.ColumnWidths.Add(2);
        table.ColumnWidths.Add(200);
        table.ColumnWidths.Add(200);

        double mid = FirstColumnWidth(table, "Mid", "End");
        double spacerWidth = mid * 2 / 200;
        Assert.True(spacerWidth < 2 * TableCell.DefaultPaddingPt, "the spacer column was widened");
        Assert.Equal(PageLeft() + spacerWidth + TableCell.DefaultPaddingPt, XOf(table, "Mid"), 1);
    }

    // ----------------------------------------------------------------- the distribution itself

    [Fact]
    public void Widening_TakesFromTheSpareWidthInProportion()
    {
        double[] colW = { 10, 60, 30 };
        TableRenderer.WidenToMinimums(colW, new double[] { 30, 10, 10 }, 100);

        // 20pt short; 50pt and 20pt to spare give 20 × 5/7 and 20 × 2/7.
        Assert.Equal(30, colW[0], 6);
        Assert.Equal(60 - 20.0 * 5 / 7, colW[1], 6);
        Assert.Equal(30 - 20.0 * 2 / 7, colW[2], 6);
    }

    [Fact]
    public void Widening_LeavesColumnsThatFitAlone()
    {
        double[] colW = { 40, 60 };
        TableRenderer.WidenToMinimums(colW, new double[] { 40, 10 }, 100);

        Assert.Equal(new double[] { 40, 60 }, colW);
    }

    [Fact]
    public void Widening_PastTheTable_SharesItByTheMinimums()
    {
        double[] colW = { 50, 50 };
        TableRenderer.WidenToMinimums(colW, new double[] { 150, 50 }, 100);

        Assert.Equal(75, colW[0], 6);
        Assert.Equal(25, colW[1], 6);
    }

    private static double XOf(Table table, string text)
    {
        var section = new Section();
        section.Children.Add(table);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.XOf(PdfContentProbe.DecodeStreams(doc.ToPdf()), text);
    }

    /// <summary>The page's left margin, where an unindented paragraph starts.</summary>
    private static double PageLeft()
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Margin", FontSize = Size });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.XOf(PdfContentProbe.DecodeStreams(doc.ToPdf()), "Margin");
    }

    /// <summary>The page's content width: an all-explicit column set is normalised to fill it.</summary>
    private static double PageContentWidth()
    {
        double half = FirstColumnWidth(TwoColumns("Name", "Value", 1, 1), "Name", "Value");
        return 2 * half;
    }
}
