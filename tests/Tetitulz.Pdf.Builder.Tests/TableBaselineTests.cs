// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="CellVerticalAlign.Baseline"/> (CSS 2.1 §17.5.3): a row's baseline cells are lowered so
/// their first baselines share a line, the row grown to hold them; and
/// <see cref="PdfExporter.MeasureFirstBaseline(IReadOnlyList{IDocElement}, double)"/>, the first
/// baseline of a run of blocks — a table's its first row's (CSS Tables 3 §3.10.2).
/// </summary>
public class TableBaselineTests
{
    private static Paragraph Para(string text, double size, double spacingAfter = 0)
        => new Paragraph { SpacingAfter = spacingAfter, Runs = { new TextRun { Text = text, FontSize = size } } };

    private static TableCell Cell(CellVerticalAlign align, params Paragraph[] paragraphs)
    {
        var cell = new TableCell { VAlign = align };
        foreach (Paragraph p in paragraphs) cell.Blocks.Add(p);
        return cell;
    }

    /// <summary>A two-row table: a large word beside three small lines, then a row of one word.</summary>
    private static Table Rows(CellVerticalAlign align)
    {
        var table = new Table();
        var top = new TableRow();
        top.Cells.Add(Cell(align, Para("LARGE", 30)));
        top.Cells.Add(Cell(align, Para("SMALL", 10), Para("second", 10), Para("third", 10)));
        table.Rows.Add(top);
        var next = new TableRow();
        next.Cells.Add(Cell(CellVerticalAlign.Top, Para("UNDER", 10)));
        next.Cells.Add(Cell(CellVerticalAlign.Top, Para("x", 10)));
        table.Rows.Add(next);
        return table;
    }

    private static Dictionary<string, double> Drawn(Table table)
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(table);
        doc.Sections.Add(section);
        var y = new Dictionary<string, double>();
        foreach (var t in PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()))) y.TryAdd(t.Text, t.Y);
        return y;
    }

    /// <summary>
    /// Baseline cells put their first lines on one baseline — the small word lowered to the large one's —
    /// where top-aligned cells, the control, leave the small word higher; and the row grows to hold the
    /// lowered cell, so the next row starts lower than under the top-aligned control.
    /// </summary>
    [Fact]
    public void BaselineCells_ShareTheirFirstBaseline_AndTheRowGrows()
    {
        var baseline = Drawn(Rows(CellVerticalAlign.Baseline));
        var top = Drawn(Rows(CellVerticalAlign.Top));

        Assert.Equal(baseline["LARGE"], baseline["SMALL"], 1);
        Assert.True(top["SMALL"] > top["LARGE"] + 5, $"top-aligned, the small word at {top["SMALL"]:F1} is not above the large word's baseline {top["LARGE"]:F1}");
        Assert.Equal(top["LARGE"], baseline["LARGE"], 1);   // the deepest-baseline cell is not moved
        double lowered = top["SMALL"] - baseline["SMALL"];
        Assert.True(lowered > 5, $"the small cell was lowered {lowered:F1}");
        Assert.True(baseline["third"] < top["third"] - 5);
        Assert.True(baseline["UNDER"] < baseline["third"], "the next row overlaps the lowered cell");
        Assert.True(top["UNDER"] - baseline["UNDER"] > 1, "the row did not grow to hold the lowered cell");
    }

    /// <summary>
    /// A run of blocks' first baseline: a paragraph's under a box's top margin, border and padding; a
    /// table's with no baseline cell is its lowest cell's content bottom, with one the cells' shared
    /// baseline; none for no blocks or a floated box.
    /// </summary>
    [Fact]
    public void MeasureFirstBaseline_ReadsBoxesTablesAndLists()
    {
        var exporter = new PdfExporter();
        var para = Para("Word", 12);
        double alone = exporter.MeasureFirstBaseline(para, 300);

        var boxed = new List<IDocElement> { new BoxStart { MarginTop = 5, PaddingTop = 7 }, para, new BoxEnd() };
        Assert.Equal(alone + 12, exporter.MeasureFirstBaseline(boxed, 300)!.Value, 3);

        var table = new Table { SpacingBefore = 3 };
        var row = new TableRow();
        row.Cells.Add(Cell(CellVerticalAlign.Top, Para("Word", 12)));
        table.Rows.Add(row);
        var cell = table.Rows[0].Cells[0];
        double rowHeight = Math.Max(exporter.MeasureHeight(new IDocElement[] { Para("Word", 12) }, 300) + 2 * cell.InnerPaddingPt, table.MinRowHeightPt);
        double contentBottom = 3 + rowHeight - cell.InnerPaddingPt;
        Assert.Equal(contentBottom, exporter.MeasureFirstBaseline(new IDocElement[] { table }, 1000)!.Value, 1);

        // Baseline cells beside a taller one: their shared first line, not the tall cell's content bottom.
        cell.VAlign = CellVerticalAlign.Baseline;
        row.Cells.Add(Cell(CellVerticalAlign.Baseline, Para("Word", 12), Para("two", 12), Para("three", 12)));
        Assert.Equal(3 + cell.InnerPaddingPt + alone, exporter.MeasureFirstBaseline(new IDocElement[] { table }, 1000)!.Value, 1);

        var list = new ListBlock { SpacingBefore = 4 };
        var li = new ListItem();
        li.Runs.Add(new TextRun { Text = "Item", FontSize = 12 });
        list.Items.Add(li);
        Assert.Equal(4 + alone, exporter.MeasureFirstBaseline(new IDocElement[] { list }, 300)!.Value, 1);

        Assert.Null(exporter.MeasureFirstBaseline(Array.Empty<IDocElement>(), 300));
        Assert.Null(exporter.MeasureFirstBaseline(new IDocElement[] { new BoxStart { Float = FloatSide.Left, FloatWidth = 50 }, para, new BoxEnd() }, 300));

        // A positioned paragraph is out of the flow: the next one gives the baseline.
        var positioned = Para("Elsewhere", 30);
        positioned.Placement = new AbsolutePlacement { LeftPt = 10 };
        Assert.Equal(alone, exporter.MeasureFirstBaseline(new IDocElement[] { positioned, Para("Word", 12) }, 300)!.Value, 3);
    }

    /// <summary>
    /// A box narrows the band its blocks are laid out in: a line that fits beside nothing wraps inside
    /// the box's margin, and its first line — without the large word that wrapped away — is shallower.
    /// </summary>
    [Fact]
    public void ABoxNarrowsTheBand_ItsFirstLineIsLaidOutIn()
    {
        var exporter = new PdfExporter();
        var para = new Paragraph { Runs = { new TextRun { Text = "small ", FontSize = 10 }, new TextRun { Text = "LARGE", FontSize = 30 } } };
        double width = exporter.MeasureShrinkToFitWidth(para, 1000) + 1;

        double open = exporter.MeasureFirstBaseline(new IDocElement[] { para }, width)!.Value;
        double boxed = exporter.MeasureFirstBaseline(new IDocElement[] { new BoxStart { MarginLeft = width / 2 }, para, new BoxEnd() }, width)!.Value;

        Assert.True(open - boxed > 10, $"the first line's baseline is {boxed:F1} in the box, {open:F1} beside nothing");
    }

    /// <summary>
    /// A merged cell's content bottom is in the last row it spans: with no baseline cell, the table's
    /// baseline is there — the lowest cell's, though a cell after it in the row ends higher — under the
    /// spacing before every row.
    /// </summary>
    [Fact]
    public void AMergedCell_IsTheLowest()
    {
        var exporter = new PdfExporter();
        var table = new Table { MinRowHeightPt = 0, RowSpacingPt = 2, BorderWidth = 0 };
        var first = new TableRow();
        first.Cells.Add(new TableCell { VMerge = VerticalMerge.Restart, Blocks = { Para("Merged", 12) } });
        first.Cells.Add(Cell(CellVerticalAlign.Top, Para("Top", 12)));
        var second = new TableRow();
        second.Cells.Add(new TableCell { VMerge = VerticalMerge.Continue });
        second.Cells.Add(Cell(CellVerticalAlign.Top, Para("Next", 12)));
        table.Rows.Add(first);
        table.Rows.Add(second);
        double pad = first.Cells[1].InnerPaddingPt;
        double row = exporter.MeasureHeight(new IDocElement[] { Para("Word", 12) }, 300) + 2 * pad;

        double? baseline = exporter.MeasureFirstBaseline(new IDocElement[] { table }, 1000);

        Assert.Equal(2 + row + 2 + row - pad, baseline!.Value, 1);
    }

    /// <summary>
    /// A baseline cell with no line has its baseline at its content's bottom (CSS 2.1 §17.5.3): beside a
    /// 40pt empty box, a word is lowered to the box's bottom — its own baseline 40pt down, less the
    /// distance it already had — where top-aligned cells, the control, leave it at the top.
    /// </summary>
    [Fact]
    public void ABaselineCellWithNoLine_PutsTheBaselineAtItsContentsBottom()
    {
        Table Row(CellVerticalAlign align)
        {
            var table = new Table();
            var row = new TableRow();
            var empty = new TableCell { VAlign = align };
            empty.Blocks.Add(new BoxStart { MinHeightPt = 40 });
            empty.Blocks.Add(new BoxEnd());
            row.Cells.Add(empty);
            row.Cells.Add(Cell(align, Para("WORD", 10)));
            table.Rows.Add(row);
            return table;
        }
        double own = new PdfExporter().MeasureFirstBaseline(Para("WORD", 10), 300);

        double lowered = Drawn(Row(CellVerticalAlign.Top))["WORD"] - Drawn(Row(CellVerticalAlign.Baseline))["WORD"];

        Assert.Equal(40 - own, lowered, 0.5);
    }
}
