// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExporter.MeasureHeight"/> (batch 111): the height a run of blocks takes in a band of
/// a given width, by the cell layout that draws them — lines wrapped at that width, spacing, a box's
/// vertical insets, a list's items, a table's rows. The HTML mapper balances a multi-column block by it.
/// </summary>
public class BlockHeightMeasureTests
{
    /// <summary>Four long words in a band one word wide are four lines, four times the one-line height; in a wide band they are one line.</summary>
    [Fact]
    public void AParagraph_IsMeasuredAtTheWidthGiven()
    {
        var exporter = new PdfExporter();
        var fourWords = Para("Supercalifragilistic Supercalifragilistic Supercalifragilistic Supercalifragilistic");
        var oneWord = Para("Supercalifragilistic");
        double wordWidth = exporter.MeasureShrinkToFitWidth(oneWord, 1000);

        double narrow = exporter.MeasureHeight(new IDocElement[] { fourWords }, wordWidth + 2);
        double wide = exporter.MeasureHeight(new IDocElement[] { fourWords }, 1000);
        double oneLine = exporter.MeasureHeight(new IDocElement[] { oneWord }, 1000);

        Assert.True(oneLine > 8, $"one line measured {oneLine:F1}");
        Assert.Equal(oneLine, wide, 1);
        Assert.Equal(4 * oneLine - 3 * oneWord.SpacingAfter, narrow, 1);
    }

    /// <summary>Two paragraphs are their heights in sequence; a box round one adds its vertical margins, padding and borders.</summary>
    [Fact]
    public void ABox_AddsItsVerticalInsets()
    {
        var exporter = new PdfExporter();
        var p = Para("alpha");
        double bare = exporter.MeasureHeight(new IDocElement[] { p }, 300);
        var box = new BoxStart { MarginTop = 5, MarginBottom = 7, PaddingTop = 10, PaddingBottom = 11, BorderTop = new Border(2, "#000000"), BorderBottom = new Border(3, "#000000") };

        double boxed = exporter.MeasureHeight(new IDocElement[] { box, Para("alpha"), new BoxEnd() }, 300);
        double two = exporter.MeasureHeight(new IDocElement[] { Para("alpha"), Para("beta") }, 300);

        Assert.Equal(bare + 5 + 7 + 10 + 11 + 2 + 3, boxed, 1);
        Assert.Equal(2 * bare, two, 1);
    }

    /// <summary>A list is its items' lines; a table its rows; nothing, or no width, is 0.</summary>
    [Fact]
    public void AList_AndATable_AreMeasured_AndNothingIsZero()
    {
        var exporter = new PdfExporter();
        var list = new ListBlock();
        for (int i = 0; i < 3; i++)
        {
            var li = new ListItem();
            li.Runs.Add(new TextRun { Text = "item" });
            list.Items.Add(li);
        }
        var one = new ListBlock();
        var only = new ListItem();
        only.Runs.Add(new TextRun { Text = "item" });
        one.Items.Add(only);
        var table = new Table();
        for (int r = 0; r < 2; r++)
        {
            var row = new TableRow();
            var cell = new TableCell();
            cell.Paragraphs.Add(Para("cell"));
            row.Cells.Add(cell);
            table.Rows.Add(row);
        }

        double three = exporter.MeasureHeight(new IDocElement[] { list }, 300);
        double single = exporter.MeasureHeight(new IDocElement[] { one }, 300);
        double rows = exporter.MeasureHeight(new IDocElement[] { table }, 300);

        Assert.Equal(3 * single, three, 1);
        Assert.True(rows >= 2 * Table.DefaultMinRowHeightPt, $"two rows measured {rows:F1}");
        Assert.Equal(0, exporter.MeasureHeight(new IDocElement[0], 300), 6);
        Assert.Equal(0, exporter.MeasureHeight(new IDocElement[] { Para("alpha") }, 0), 6);
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }
}
