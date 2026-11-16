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
/// Two abutting cell backgrounds meet mid-pixel, and each is anti-aliased against the page: every
/// viewer showed a light seam between an invoice's header cells, where Chrome shows one band. A
/// cell's background now runs 1pt on under the next cell's, which is drawn after it and covers the
/// overlap — only where that next cell paints a background of its own.
/// </summary>
public class TableCellSeamTests
{
    private const double Width = 200;

    private static TableCell Cell(string text, string? background = "#0000FF", VerticalMerge merge = VerticalMerge.None)
    {
        var cell = new TableCell { InnerPaddingPt = 0, BackgroundColor = background, VMerge = merge };
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
        p.Runs.Add(new TextRun { Text = text });
        cell.Blocks.Add(p);
        return cell;
    }

    private static List<(double X, double Y, double W, double H)> Fills(Table t)
    {
        var section = new Section();
        section.Children.Add(t);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        return Regex.Matches(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\s+f").Cast<Match>()
                    .Select(m => (D(m, 1), D(m, 2), D(m, 3), D(m, 4))).ToList();
    }

    private static double D(Match m, int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

    private static Table NewTable(double spacing = 0, bool rtl = false, params TableCell[][] rows)
    {
        var t = new Table { WidthPt = Width, AutoLayout = false, MinRowHeightPt = 0, CellSpacingPt = spacing, RtlColumns = rtl };
        foreach (var cells in rows)
        {
            var row = new TableRow();
            row.Cells.AddRange(cells);
            t.Rows.Add(row);
        }
        return t;
    }

    /// <summary>The first of two 100pt cells fills 101pt, under the second; the second, last in its row, fills its own 100pt.</summary>
    [Fact]
    public void ABackgroundRunsUnderTheNextCells()
    {
        var fills = Fills(NewTable(rows: new[] { Cell("A"), Cell("B") }));
        Assert.Equal(2, fills.Count);
        Assert.Equal(101, fills[0].W, 3);
        Assert.Equal(100, fills[1].W, 3);
        Assert.Equal(fills[0].X + 100, fills[1].X, 3);
    }

    /// <summary>Right to left the next cell is on the left: the first cell's fill starts 1pt left of its edge and keeps its right one.</summary>
    [Fact]
    public void RightToLeftItRunsLeftward()
    {
        var ltr = Fills(NewTable(rows: new[] { Cell("A"), Cell("B") }));
        var rtl = Fills(NewTable(rtl: true, rows: new[] { Cell("A"), Cell("B") }));
        Assert.Equal(101, rtl[0].W, 3);
        Assert.Equal(ltr[0].X + 99, rtl[0].X, 3);                // (left + 100) − 1
        Assert.Equal(ltr[0].X + Width, rtl[0].X + rtl[0].W, 3);  // the right edge is the table's
    }

    /// <summary>A next cell with no background would show the overlap: the fill keeps its own width.</summary>
    [Fact]
    public void NotUnderACellWithNoBackground()
    {
        var fills = Fills(NewTable(rows: new[] { Cell("A"), Cell("B", background: null) }));
        Assert.Equal(100, Assert.Single(fills).W, 3);
    }

    /// <summary>A spaced table's cells do not abut: nothing to cover, each fill its own width.</summary>
    [Fact]
    public void NotInASpacedTable()
    {
        var fills = Fills(NewTable(spacing: 10, rows: new[] { Cell("A"), Cell("B") }));
        Assert.Equal(2, fills.Count);
        Assert.All(fills, f => Assert.Equal(85, f.W, 3));
    }

    /// <summary>
    /// Beside a vertically merged cell the overlap would lie under the rows below, drawn later: a
    /// merge on either side leaves both fills their own width; a plain row beneath it (the control) overlaps.
    /// </summary>
    [Fact]
    public void NotBesideAMergedCell()
    {
        var fills = Fills(NewTable(rows: new[]
        {
            new[] { Cell("A", merge: VerticalMerge.Restart), Cell("B") },
            new[] { Cell("", merge: VerticalMerge.Continue), Cell("C") },
            new[] { Cell("D"), Cell("E", merge: VerticalMerge.Restart) },
            new[] { Cell("F"), Cell("", merge: VerticalMerge.Continue) },
            new[] { Cell("G"), Cell("H") },
        }));
        Assert.Equal(new double[] { 100, 100, 100, 100, 100, 100, 101, 100 }, fills.Select(f => System.Math.Round(f.W, 3)));
    }
}
