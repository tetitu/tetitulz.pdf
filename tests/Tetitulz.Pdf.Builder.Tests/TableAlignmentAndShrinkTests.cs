// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Table.Alignment"/> places a table narrower than its band, and
/// <see cref="Table.ShrinkToContent"/> makes a table with no width as wide as its content — HTML
/// e-mail's centred 600px table and its centred button, found against Chrome. Both default to the
/// old behaviour: a table at the band's left, as wide as the band.
/// </summary>
public class TableAlignmentAndShrinkTests
{
    /// <summary>The default section's band: its page less its margins.</summary>
    private static readonly double Band = new Section().PageWidthPt - new Section().MarginLeft - new Section().MarginRight;
    private static readonly double Left = new Section().MarginLeft;

    private static TableCell Cell(string text, string? bg = "#0000FF")
    {
        var c = new TableCell { InnerPaddingPt = 0, BackgroundColor = bg };
        c.Blocks.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = text } } });
        return c;
    }

    private static (double X, double W) Fill(Table t)
    {
        var section = new Section();
        section.Children.Add(t);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        var m = Regex.Match(c, @"([-\d.]+) [-\d.]+ ([-\d.]+) [-\d.]+ re\s+f");
        Assert.True(m.Success, "no fill");
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static Table OneCell(double? width = 200, TextAlignment? align = null, bool shrink = false)
    {
        var t = new Table { WidthPt = width, Alignment = align, ShrinkToContent = shrink, MinRowHeightPt = 0, BorderWidth = 0 };
        var row = new TableRow();
        row.Cells.Add(Cell("Word"));
        t.Rows.Add(row);
        return t;
    }

    [Fact]
    public void ANarrowTableSitsAtTheLeftByDefault() => Assert.Equal(Left, Fill(OneCell()).X, 1);

    [Fact]
    public void CentredItHasEqualRoomEachSide() => Assert.Equal(Left + (Band - 200) / 2, Fill(OneCell(align: TextAlignment.Center)).X, 1);

    [Fact]
    public void RightItEndsAtTheBandsRight() => Assert.Equal(Left + Band - 200, Fill(OneCell(align: TextAlignment.Right)).X, 1);

    /// <summary>End is the right in a left-to-right section; Left and Justify are the default's.</summary>
    [Fact]
    public void EndIsTheRightInALeftToRightSection()
    {
        Assert.Equal(Left + Band - 200, Fill(OneCell(align: TextAlignment.End)).X, 1);
        Assert.Equal(Left, Fill(OneCell(align: TextAlignment.Left)).X, 1);
    }

    /// <summary>With no width and shrinking, the table is its word's width; without shrinking (the control) the band's.</summary>
    [Fact]
    public void AShrinkingTableIsItsContentsWidth()
    {
        Assert.Equal(Band, Fill(OneCell(width: null)).W, 1);
        double shrunk = Fill(OneCell(width: null, shrink: true)).W;
        Assert.InRange(shrunk, 20, 40);   // "Word" at 12pt
    }

    /// <summary>A stated width wins over shrinking.</summary>
    [Fact]
    public void AStatedWidthWinsOverShrinking() => Assert.Equal(200, Fill(OneCell(width: 200, shrink: true)).W, 1);

    /// <summary>A column's stated width counts where it is wider than its content: 150pt for a column of one word.</summary>
    [Fact]
    public void AColumnsStatedWidthCounts()
    {
        var t = OneCell(width: null, shrink: true);
        t.ColumnWidths.Add(150);
        Assert.Equal(150, Fill(t).W, 0);
    }

    /// <summary>
    /// A shrunk table's width counts the spacing between and round its cells: with 10pt of spacing
    /// the cell is as wide as with none (the control), and the table 20pt wider.
    /// </summary>
    [Fact]
    public void AShrunkTableCountsItsCellSpacing()
    {
        double bare = Fill(OneCell(width: null, shrink: true)).W;
        var spaced = OneCell(width: null, shrink: true);
        spaced.CellSpacingPt = 10;
        Assert.Equal(bare, Fill(spaced).W, 1);
    }

    /// <summary>A shrunk cell of several words holds them on one line: the width it was measured at is the width it is drawn at.</summary>
    [Fact]
    public void AShrunkCellKeepsItsWordsOnOneLine()
    {
        var t = new Table { ShrinkToContent = true, MinRowHeightPt = 0, BorderWidth = 0 };
        var row = new TableRow();
        row.Cells.Add(Cell("Alpha Beta Gamma Delta"));
        t.Rows.Add(row);
        var section = new Section();
        section.Children.Add(t);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        Assert.Equal(PdfContentProbe.YOf(c, "Alpha"), PdfContentProbe.YOf(c, "Delta"), 1);
        Assert.InRange(Fill(t).W, 60, 200);
    }

    /// <summary>
    /// The same over many sizes and three columns, where the share each column is given back —
    /// <c>max · avail / sumMax</c>, then less its padding — can round a hair under what was measured.
    /// </summary>
    [Fact]
    public void ShrunkCellsKeepTheirWordsOnOneLineAtAnySize()
    {
        string[] texts = { "Alpha Beta", "Order total due", "Ship to address", "Qty Unit Price" };
        var wrapped = new List<string>();
        for (int k = 0; k < 120; k++)
        {
            double size = 7 + k * 0.089;   // to 17.6pt: the three columns always fit the band
            var t = new Table { ShrinkToContent = true, MinRowHeightPt = 0, BorderWidth = 0, AutoLayout = true };
            var row = new TableRow();
            for (int col = 0; col < 3; col++)
            {
                var c = new TableCell { InnerPaddingPt = 1.3 + (k % 7) * 0.71 };
                c.Blocks.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = texts[(k + col) % 4], FontSize = size } } });
                row.Cells.Add(c);
            }
            t.Rows.Add(row);
            var section = new Section();
            section.Children.Add(t);
            var doc = new Document();
            doc.Sections.Add(section);
            var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));
            if (drawn.Select(d => Math.Round(d.Y, 1)).Distinct().Count() != 1)
                wrapped.Add(size.ToString("0.###", CultureInfo.InvariantCulture));
        }
        Assert.Empty(wrapped);
    }

    /// <summary>A shrinking table whose one cell holds <paramref name="inner"/>, the cell filled green.</summary>
    private static Table Holding(Table inner)
    {
        var outerCell = new TableCell { InnerPaddingPt = 0, BackgroundColor = "#00FF00" };
        outerCell.Blocks.Add(inner);
        var t = new Table { ShrinkToContent = true, MinRowHeightPt = 0, BorderWidth = 0 };
        var row = new TableRow();
        row.Cells.Add(outerCell);
        t.Rows.Add(row);
        return t;
    }

    /// <summary>
    /// A column whose only content is a nested table is as wide as that table's content: the
    /// shrinking table around a one-word table is a word wide, not the band (before 2026-10-19 the
    /// column was unknown and the table kept the band).
    /// </summary>
    [Fact]
    public void ANestedTablesColumnShrinksTheTable()
    {
        double w = Fill(Holding(OneCell(width: null))).W;
        Assert.True(w > 10 && w < 80, $"the table around a one-word table is {w:F1} wide");
    }

    /// <summary>
    /// A column with nothing measurable — only tables nested past <c>PdfLimits.MaxTableNestingDepth</c>,
    /// which the draw drops — leaves the table the band's width.
    /// </summary>
    [Fact]
    public void AnUnmeasurableColumnKeepsTheBand()
    {
        Table chain = OneCell(width: null);
        for (int i = 1; i < Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTableNestingDepth; i++)
        {
            var cell = new TableCell { InnerPaddingPt = 0 };
            cell.Blocks.Add(chain);
            var row = new TableRow();
            row.Cells.Add(cell);
            chain = new Table { MinRowHeightPt = 0, BorderWidth = 0, Rows = { row } };
        }
        Assert.Equal(Band, Fill(Holding(chain)).W, 1);
    }
}
