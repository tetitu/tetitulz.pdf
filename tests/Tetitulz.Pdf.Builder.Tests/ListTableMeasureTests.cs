// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExporter.MeasureShrinkToFitWidth(ListBlock, double)"/> and
/// <see cref="PdfExporter.MeasureShrinkToFitWidth(Table, double)"/>, with their min-content
/// counterparts (batch 110): a list is its widest item at the item's text start, a nested list at
/// its edge further in; a table its stated width, else its cell spacing and columns. The HTML
/// mapper's flex items holding a list or a table take their size from these.
/// </summary>
public class ListTableMeasureTests
{
    private const string Long = "Supercalifragilisticexpialidocious";

    /// <summary>A list's width is its widest item's unwrapped text past the list's text indent — not the text alone.</summary>
    [Fact]
    public void AList_IsItsWidestItem_AtItsTextStart()
    {
        var exporter = new PdfExporter();
        var list = List(30, "tiny", Long);

        double width = exporter.MeasureShrinkToFitWidth(list, 1000);

        double word = exporter.MeasureShrinkToFitWidth(Para(Long), 1000);
        Assert.Equal(30 + word, width, 1);
        Assert.True(word > 100, $"the long word measured {word:F1}");
    }

    /// <summary>A nested list hangs from its item's text start, so the same word one level down is wider by the stated indent.</summary>
    [Fact]
    public void ANestedList_IsWiderByItsIndent()
    {
        var exporter = new PdfExporter();
        var flat = List(30, Long);
        var nested = List(30, "A");
        nested.Items[0].Sublist = List(30, Long);

        double flatWidth = exporter.MeasureShrinkToFitWidth(flat, 1000);
        double nestedWidth = exporter.MeasureShrinkToFitWidth(nested, 1000);

        Assert.Equal(30, nestedWidth - flatWidth, 1);
    }

    /// <summary>The min-content width is the longest word at the text start, short of the whole line.</summary>
    [Fact]
    public void AListsMinContent_IsItsLongestWord_AtItsTextStart()
    {
        var exporter = new PdfExporter();
        var list = List(30, "tiny " + Long + " tiny");

        double min = exporter.MeasureMinContentWidth(list, 1000);
        double whole = exporter.MeasureShrinkToFitWidth(list, 1000);

        Assert.Equal(30 + exporter.MeasureMinContentWidth(Para("tiny " + Long + " tiny"), 1000), min, 1);
        Assert.True(whole > min + 20, $"min-content {min:F1} is not short of the whole line {whole:F1}");
    }

    /// <summary>With no text indent stated, the text starts a marker's room in — 14pt for a bullet.</summary>
    [Fact]
    public void AnUnstatedIndent_IsAMarkersRoom()
    {
        var exporter = new PdfExporter();
        var list = List(null, Long);

        double width = exporter.MeasureShrinkToFitWidth(list, 1000);

        Assert.Equal(14 + exporter.MeasureShrinkToFitWidth(Para(Long), 1000), width, 1);
    }

    /// <summary>A marker with no text after it still reaches the text start; an empty list is 0; the answer is capped at the band.</summary>
    [Fact]
    public void AMarkerAlone_ReachesTheTextStart_AndTheBandCaps()
    {
        var exporter = new PdfExporter();
        var markerOnly = List(30, "");

        Assert.Equal(30.5, exporter.MeasureShrinkToFitWidth(markerOnly, 1000), 1);
        Assert.Equal(0, exporter.MeasureShrinkToFitWidth(new ListBlock(), 1000), 6);
        Assert.Equal(20, exporter.MeasureShrinkToFitWidth(List(30, Long), 20), 6);
    }

    /// <summary>A table is its cell spacing and each column's unwrapped content with the cell's padding.</summary>
    [Fact]
    public void ATable_IsItsColumnsAndSpacing()
    {
        var exporter = new PdfExporter();
        var table = Table("AAAA", Long);
        table.CellSpacingPt = 4;
        double pad = table.Rows[0].Cells[0].InnerPaddingPt;

        double? width = exporter.MeasureShrinkToFitWidth(table, 1000);

        double a = exporter.MeasureShrinkToFitWidth(Para("AAAA"), 1000) - PdfExporter.ShrinkToFitSlackPt;
        double l = exporter.MeasureShrinkToFitWidth(Para(Long), 1000) - PdfExporter.ShrinkToFitSlackPt;
        Assert.NotNull(width);
        Assert.Equal(3 * 4 + a + l + 4 * pad + PdfExporter.ShrinkToFitSlackPt, width!.Value, 1);
    }

    /// <summary>A table's min-content width takes each column's longest word, its shrink-to-fit width the whole line.</summary>
    [Fact]
    public void ATablesMinContent_IsItsLongestWords()
    {
        var exporter = new PdfExporter();
        var table = Table("tiny " + Long + " tiny");
        double pad = table.Rows[0].Cells[0].InnerPaddingPt;

        double? min = exporter.MeasureMinContentWidth(table, 1000);
        double? whole = exporter.MeasureShrinkToFitWidth(table, 1000);

        Assert.NotNull(min);
        Assert.NotNull(whole);
        Assert.Equal(exporter.MeasureMinContentWidth(Para("tiny " + Long + " tiny"), 1000) + 2 * pad, min!.Value, 1);
        Assert.Equal(exporter.MeasureShrinkToFitWidth(Para("tiny " + Long + " tiny"), 1000) + 2 * pad, whole!.Value, 1);
        Assert.True(whole.Value > min.Value + 20);
    }

    /// <summary>A stated table width is the answer; the band caps a stated and a measured width alike; a table with no rows is 0.</summary>
    [Fact]
    public void AStatedTableWidth_IsTheAnswer_AndTheBandCaps()
    {
        var exporter = new PdfExporter();
        var stated = Table(Long);
        stated.WidthPt = 123;

        Assert.Equal(123, exporter.MeasureShrinkToFitWidth(stated, 1000)!.Value, 6);
        Assert.Equal(50, exporter.MeasureShrinkToFitWidth(stated, 50)!.Value, 6);
        Assert.Equal(50, exporter.MeasureShrinkToFitWidth(Table(Long), 50)!.Value, 6);
        Assert.Equal(0, exporter.MeasureShrinkToFitWidth(new Table(), 1000)!.Value, 6);
    }

    /// <summary>
    /// A column whose only content is a nested table is the nested table's width with the cell's
    /// padding — its shrink-to-fit width for the outer's, its min-content width for the outer's
    /// min-content (CSS 2.1 §17.5.2.2: whatever a cell holds sizes its column). A list in a cell the same.
    /// </summary>
    [Fact]
    public void ANestedTableOrList_SizesItsColumn()
    {
        var exporter = new PdfExporter();
        var inner = Table("tiny " + Long + " tiny");
        var outer = OneCellHolding(inner);
        double pad = outer.Rows[0].Cells[0].InnerPaddingPt;
        double slack = PdfExporter.ShrinkToFitSlackPt;

        Assert.Equal(exporter.MeasureShrinkToFitWidth(inner, 1000)!.Value + 2 * pad + slack, exporter.MeasureShrinkToFitWidth(outer, 1000)!.Value, 1);
        Assert.Equal(exporter.MeasureMinContentWidth(inner, 1000)!.Value + 2 * pad + slack, exporter.MeasureMinContentWidth(outer, 1000)!.Value, 1);
        Assert.True(exporter.MeasureShrinkToFitWidth(outer, 1000)!.Value > exporter.MeasureMinContentWidth(outer, 1000)!.Value + 20);

        // Inside a box in the cell, the box's padding beside it.
        var boxed = OneCellHolding(new BoxStart { PaddingLeft = 10, PaddingRight = 15 });
        boxed.Rows[0].Cells[0].Blocks.Add(inner);
        boxed.Rows[0].Cells[0].Blocks.Add(new BoxEnd());
        Assert.Equal(exporter.MeasureShrinkToFitWidth(outer, 1000)!.Value + 25, exporter.MeasureShrinkToFitWidth(boxed, 1000)!.Value, 1);

        var list = List(30, "tiny " + Long + " tiny");
        var holding = OneCellHolding(list);
        Assert.Equal(exporter.MeasureShrinkToFitWidth(list, 1000) + 2 * pad, exporter.MeasureShrinkToFitWidth(holding, 1000)!.Value, 1);
        Assert.Equal(exporter.MeasureMinContentWidth(list, 1000) + 2 * pad, exporter.MeasureMinContentWidth(holding, 1000)!.Value, 1);
    }

    /// <summary>
    /// A column whose only content is tables nested past <see cref="PdfLimits.MaxTableNestingDepth"/>
    /// has no width this can know — the draw drops them: null — unless the column's width is stated,
    /// which then counts. The same chain one table short of the cap is measured, the control.
    /// </summary>
    [Fact]
    public void AColumnWhoseWidthNothingSays_IsNull_UnlessStated()
    {
        var exporter = new PdfExporter();
        var outer = OneCellHolding(Chain(PdfLimits.MaxTableNestingDepth));

        Assert.Null(exporter.MeasureShrinkToFitWidth(outer, 1000));
        Assert.Null(exporter.MeasureMinContentWidth(outer, 1000));

        outer.ColumnWidths.Add(40);
        Assert.Equal(40 + PdfExporter.ShrinkToFitSlackPt, exporter.MeasureShrinkToFitWidth(outer, 1000)!.Value, 1);

        Assert.NotNull(exporter.MeasureShrinkToFitWidth(OneCellHolding(Chain(PdfLimits.MaxTableNestingDepth - 1)), 1000));
    }

    /// <summary>
    /// A column of empty cells is 0 wide, not unknown: a flex line's filler after its items (an
    /// empty cell with no stated width) leaves the table as wide as its items, where tables nested
    /// past the cap in the same place would leave the width unknowable. The two differ only in what
    /// the cell holds.
    /// </summary>
    [Fact]
    public void AColumnOfEmptyCells_IsZeroWide_NotUnknown()
    {
        var exporter = new PdfExporter();
        var table = new Table();
        var row = new TableRow();
        var content = new TableCell();
        content.Blocks.Add(Para("AAAA"));
        row.Cells.Add(content);
        row.Cells.Add(new TableCell { InnerPaddingPt = 0 });   // the filler: empty, no stated width
        table.Rows.Add(row);
        table.ColumnWidths.Add(50);
        table.ColumnWidths.Add(0);

        Assert.Equal(50 + PdfExporter.ShrinkToFitSlackPt, exporter.MeasureShrinkToFitWidth(table, 1000)!.Value, 1);

        var nested = new TableCell { InnerPaddingPt = 0 };
        nested.Blocks.Add(Chain(PdfLimits.MaxTableNestingDepth));
        row.Cells[1] = nested;
        Assert.Null(exporter.MeasureShrinkToFitWidth(table, 1000));
    }

    /// <summary>
    /// A table cell holding a three-item list is sized by the items' bodies: the row below starts
    /// under the third item, not over it — the cell measures the list by the paragraphs it draws.
    /// </summary>
    [Fact]
    public void AListInACell_SizesItsRow()
    {
        var table = new Table();
        var top = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(List(30, "one", "two", "three"));
        top.Cells.Add(cell);
        var bottom = new TableRow();
        var below = new TableCell();
        below.Paragraphs.Add(Para("Below"));
        bottom.Cells.Add(below);
        table.Rows.Add(top);
        table.Rows.Add(bottom);

        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(Doc(table).ToPdf()));

        PdfContentProbe.DrawnText three = drawn.First(d => d.Text.Contains("three", StringComparison.Ordinal));
        PdfContentProbe.DrawnText under = drawn.First(d => d.Text.Contains("Below", StringComparison.Ordinal));
        PdfContentProbe.DrawnText one = drawn.First(d => d.Text.Contains("one", StringComparison.Ordinal));
        Assert.True(under.Y < three.Y - 8, $"the row below starts at {under.Y:F1}, over the third item at {three.Y:F1}");
        Assert.True(one.Y - three.Y > 20, $"the three items span {one.Y - three.Y:F1}pt");
    }

    /// <summary>
    /// A nested list is drawn from its item's text start: with a 30pt text indent at both levels, the
    /// nested text sits 30pt in from its parent's, and each marker before its own text, moved in with it.
    /// </summary>
    [Fact]
    public void ANestedList_IsDrawnAtItsItemsTextStart()
    {
        var list = List(30, "outer");
        list.Items[0].Sublist = List(30, "inner");

        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(Doc(list).ToPdf()));

        PdfContentProbe.DrawnText outer = drawn.First(d => d.Text.Contains("outer", StringComparison.Ordinal));
        PdfContentProbe.DrawnText inner = drawn.First(d => d.Text.Contains("inner", StringComparison.Ordinal));
        // The bullets: the two drawn texts that are not the items' words (a standard font writes the
        // bullet as WinAnsi 0x95, so the probe does not hand back U+2022).
        var markers = drawn.Where(d => !d.Text.Contains("outer", StringComparison.Ordinal) && !d.Text.Contains("inner", StringComparison.Ordinal))
            .OrderBy(d => d.X).ToList();
        Assert.Equal(30, inner.X - outer.X, 1);
        Assert.Equal(2, markers.Count);
        // Each marker before its own text, and the nested one moved in with it.
        Assert.True(markers[0].X < outer.X && markers[1].X < inner.X, $"markers at {markers[0].X:F1} and {markers[1].X:F1}, texts at {outer.X:F1} and {inner.X:F1}");
        Assert.Equal(30, markers[1].X - markers[0].X, 1);
    }

    private static Document Doc(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (IDocElement child in children) section.Children.Add(child);
        doc.Sections.Add(section);
        return doc;
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static ListBlock List(double? textIndent, params string[] items)
    {
        var list = new ListBlock { TextIndentPt = textIndent };
        foreach (string text in items)
        {
            var li = new ListItem();
            if (text.Length > 0) li.Runs.Add(new TextRun { Text = text });
            list.Items.Add(li);
        }
        return list;
    }

    private static Table OneCellHolding(IDocElement content)
    {
        var cell = new TableCell();
        cell.Blocks.Add(content);
        var row = new TableRow();
        row.Cells.Add(cell);
        var table = new Table();
        table.Rows.Add(row);
        return table;
    }

    /// <summary><paramref name="tables"/> tables, each the one cell of the one before, "BBB" in the innermost.</summary>
    private static Table Chain(int tables)
    {
        Table table = Table("BBB");
        for (int i = 1; i < tables; i++) table = OneCellHolding(table);
        return table;
    }

    private static Table Table(params string[] cells)
    {
        var table = new Table();
        var row = new TableRow();
        foreach (string text in cells)
        {
            var cell = new TableCell();
            cell.Paragraphs.Add(Para(text));
            row.Cells.Add(cell);
        }
        table.Rows.Add(row);
        return table;
    }
}
