// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.Float"/> inside a table cell: the float sits at the cell's edge, the cell's
/// text flows beside it and back to the full measure past it, and the row is as tall as the float.
/// </summary>
/// <remarks>
/// A cell laid its blocks out with no float: a floated paragraph in one was an ordinary block, so the
/// HTML mapper left a floated inline element in a cell in its line, reported. The cell's draw now
/// places the float as the section's flow does, and its measure registers the float where it will
/// be drawn, so the blocks after it are measured beside it and the row reaches its bottom. Read back
/// from the drawn runs' positions, as <c>FloatReflowTests</c> does.
/// </remarks>
public sealed class TableCellFloatTests
{
    private const double PageWidth = 612;
    private const double Margin = 72;
    private const double FloatWidth = 150;
    private const string FloatText = "SIDEBAR";

    /// <summary>A right float in a cell sits at the cell's right edge, and the first line beside it is broken shorter than the lines below it.</summary>
    [Fact]
    public void ARightFloatInACellShortensTheLinesBesideIt()
    {
        string content = Content(CellWithFloat(FloatSide.Right, floatLines: 1, bodyRepeats: 6));
        var runs = PdfContentProbe.DrawnTexts(content);

        double sidebar = runs.Single(t => t.Text.Contains(FloatText, StringComparison.Ordinal)).X;
        Assert.True(sidebar > PageWidth - Margin - FloatWidth - 20, $"the float sat at x={sidebar:F1}, not at the cell's right edge");

        List<(double Left, int Chars)> lines = BodyLines(runs);
        Assert.True(lines.Count >= 3, $"expected several body lines, got {lines.Count}");
        Assert.True(lines[0].Chars < lines.Skip(1).Max(l => l.Chars), "the first line, beside the float, was not broken shorter");
        Assert.All(lines, l => Assert.True(l.Left < Margin + 12, $"a right float moved a line's start to x={l.Left:F1}"));

        // The row was measured with the same wrap: the next row starts below the last body line,
        // not over it — a cell measured at the full width is shorter than the text drawn beside a float.
        double lastBody = runs.Where(t => !t.Text.Contains(FloatText, StringComparison.Ordinal) && !t.Text.Contains("NEXTROW", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Text)).Min(t => t.Y);
        double nextRow = runs.Single(t => t.Text.Contains("NEXTROW", StringComparison.Ordinal)).Y;
        Assert.True(nextRow < lastBody - 5, $"the next row started at y={nextRow:F1}, over the body's last line at y={lastBody:F1}");
    }

    /// <summary>
    /// The row is no taller than its float either: a three-line float beside one word ends where a
    /// three-line paragraph alone would, since the word is beside the float, not below it.
    /// </summary>
    [Fact]
    public void TheRowIsNoTallerThanItsFloat()
    {
        double withFloat = NextRowY(CellWithFloat(FloatSide.Right, floatLines: 3, bodyRepeats: 0));

        Document control = NewDoc();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        var lines = new Paragraph();
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) lines.Runs.Add(new LineBreak());
            lines.Runs.Add(new TextRun { Text = FloatText + (i + 1) });
        }
        cell.Blocks.Add(lines);
        row.Cells.Add(cell);
        table.Rows.Add(row);
        var next = new TableRow();
        var nextCell = new TableCell();
        nextCell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "NEXTROW" } } });
        next.Cells.Add(nextCell);
        table.Rows.Add(next);
        control.Sections[0].Children.Add(table);

        Assert.Equal(NextRowY(control), withFloat, 1);
    }

    /// <summary>
    /// The row is measured with the text as it wraps beside the float: the gap between the body's
    /// last line and the next row is the gap a cell without a float has — a cell measured at the
    /// full width is a line short, and its last drawn line sits a line lower than it was sized for.
    /// </summary>
    [Fact]
    public void TheRowIsMeasuredWithTheWrappedText()
    {
        Document control = NewDoc();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "Body" } } });
        row.Cells.Add(cell);
        table.Rows.Add(row);
        var next = new TableRow();
        var nextCell = new TableCell();
        nextCell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "NEXTROW" } } });
        next.Cells.Add(nextCell);
        table.Rows.Add(next);
        control.Sections[0].Children.Add(table);
        var controlRuns = PdfContentProbe.DrawnTexts(Content(control));
        double controlGap = controlRuns.Single(t => t.Text.Contains("Body", StringComparison.Ordinal)).Y - controlRuns.Single(t => t.Text.Contains("NEXTROW", StringComparison.Ordinal)).Y;

        var runs = PdfContentProbe.DrawnTexts(Content(CellWithFloat(FloatSide.Right, floatLines: 1, bodyRepeats: 6)));
        double lastBody = runs.Where(t => !t.Text.Contains(FloatText, StringComparison.Ordinal) && !t.Text.Contains("NEXTROW", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.Text)).Min(t => t.Y);
        double gap = lastBody - runs.Single(t => t.Text.Contains("NEXTROW", StringComparison.Ordinal)).Y;

        Assert.Equal(controlGap, gap, 1);
    }

    /// <summary>
    /// A float that leaves no usable measure beside it is a block in a cell too: the body goes below
    /// it, at the cell's left, not into a one-point column beside it.
    /// </summary>
    [Fact]
    public void AFloatLeavingNoRoomIsABlockInTheCell()
    {
        Document doc = NewDoc();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(new Paragraph { Float = FloatSide.Right, FloatWidth = 420, Runs = { new TextRun { Text = FloatText } } });
        cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "Body" } } });
        row.Cells.Add(cell);
        table.Rows.Add(row);
        doc.Sections[0].Children.Add(table);

        var runs = PdfContentProbe.DrawnTexts(Content(doc));
        var body = runs.Single(t => t.Text.Contains("Body", StringComparison.Ordinal));
        var sidebar = runs.Single(t => t.Text.Contains(FloatText, StringComparison.Ordinal));
        Assert.True(body.Y < sidebar.Y - 5, $"the body at y={body.Y:F1} is not below the block at y={sidebar.Y:F1}");
        Assert.True(body.X < Margin + 12, $"the body started at x={body.X:F1}, beside the block");
    }

    private static double NextRowY(Document doc)
        => PdfContentProbe.DrawnTexts(Content(doc)).Single(t => t.Text.Contains("NEXTROW", StringComparison.Ordinal)).Y;

    /// <summary>A left float in a cell pushes the lines beside it right, and the lines below it come back to the cell's left.</summary>
    [Fact]
    public void ALeftFloatInACellPushesTheEdgeRightAndThenReleasesIt()
    {
        var runs = PdfContentProbe.DrawnTexts(Content(CellWithFloat(FloatSide.Left, floatLines: 1, bodyRepeats: 6)));
        double[] lefts = BodyLines(runs).Select(l => l.Left).ToArray();

        Assert.Contains(lefts, x => x > Margin + FloatWidth - 5);
        Assert.Contains(lefts, x => x < Margin + 12);
    }

    /// <summary>
    /// The row is as tall as its float: a three-line float beside one word of text, and the next
    /// row starts below the float's last line, not over it.
    /// </summary>
    [Fact]
    public void TheRowIsAsTallAsItsFloat()
    {
        var runs = PdfContentProbe.DrawnTexts(Content(CellWithFloat(FloatSide.Right, floatLines: 3, bodyRepeats: 0)));

        double lastFloatLine = runs.Where(t => t.Text.Contains(FloatText, StringComparison.Ordinal)).Min(t => t.Y);
        double nextRow = runs.Single(t => t.Text.Contains("NEXTROW", StringComparison.Ordinal)).Y;
        Assert.True(nextRow < lastFloatLine - 5, $"the next row started at y={nextRow:F1}, over the float whose last line is at y={lastFloatLine:F1}");
    }

    /// <summary>
    /// A cell's float does not narrow the cell beside it, nor the text below the table: the
    /// exclusion is the cell's own band.
    /// </summary>
    [Fact]
    public void ACellsFloatStaysInItsCell()
    {
        Document doc = NewDoc();
        var table = new Table();
        var row = new TableRow();
        row.Cells.Add(Cell(FloatSide.Right, floatLines: 3, bodyRepeats: 0));
        var beside = new TableCell();
        beside.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = string.Join(" ", Enumerable.Repeat("beside", 30)) } } });
        row.Cells.Add(beside);
        table.Rows.Add(row);
        doc.Sections[0].Children.Add(table);
        doc.Sections[0].Children.Add(new Paragraph { Runs = { new TextRun { Text = string.Join(" ", Enumerable.Repeat("after", 40)) } } });

        var runs = PdfContentProbe.DrawnTexts(Content(doc));
        double cellRight = PageWidth / 2;   // two equal columns
        Assert.All(runs.Where(t => t.Text.Contains("beside", StringComparison.Ordinal)),
            t => Assert.True(t.X < cellRight - 10 || t.X > cellRight, "a run of the cell beside was not in that cell"));
        double[] afterLefts = LineLefts(runs.Where(t => t.Text.Contains("after", StringComparison.Ordinal)));
        Assert.True(afterLefts.Length >= 2, "the paragraph after the table has fewer lines than expected");
        Assert.All(afterLefts, x => Assert.True(x < Margin + 5, $"a line after the table started at x={x:F1}: the cell's float narrowed it"));
    }

    // ------------------------------------------------------------------ fixtures

    private static Document NewDoc()
    {
        var doc = new Document();
        var section = new Section { PageWidthPt = PageWidth, PageHeightPt = 792, MarginLeft = Margin, MarginRight = Margin, MarginTop = Margin, MarginBottom = Margin };
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>A one-column table: a cell with a float and body text, then a row saying NEXTROW.</summary>
    private static Document CellWithFloat(FloatSide side, int floatLines, int bodyRepeats)
    {
        Document doc = NewDoc();
        var table = new Table();
        var row = new TableRow();
        row.Cells.Add(Cell(side, floatLines, bodyRepeats));
        table.Rows.Add(row);
        var next = new TableRow();
        var nextCell = new TableCell();
        nextCell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "NEXTROW" } } });
        next.Cells.Add(nextCell);
        table.Rows.Add(next);
        doc.Sections[0].Children.Add(table);
        return doc;
    }

    private static TableCell Cell(FloatSide side, int floatLines, int bodyRepeats)
    {
        var cell = new TableCell();
        var floated = new Paragraph { Float = side, FloatWidth = FloatWidth };
        for (int i = 0; i < floatLines; i++)
        {
            if (i > 0) floated.Runs.Add(new LineBreak());
            floated.Runs.Add(new TextRun { Text = FloatText + (i + 1) });
        }
        cell.Blocks.Add(floated);
        string body = bodyRepeats == 0 ? "Body" : string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog and keeps on running", bodyRepeats));
        cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = body } } });
        return cell;
    }

    private static string Content(Document doc) => PdfContentProbe.DecodeStreams(doc.ToPdf());

    /// <summary>The cell's body lines — not the float's, not the next row's — as (left edge, characters), top first.</summary>
    private static List<(double Left, int Chars)> BodyLines(List<PdfContentProbe.DrawnText> runs)
    {
        var byBaseline = new Dictionary<int, (double Left, int Chars)>();
        foreach (var t in runs)
        {
            if (string.IsNullOrWhiteSpace(t.Text) || t.Text.Contains(FloatText, StringComparison.Ordinal) || t.Text.Contains("NEXTROW", StringComparison.Ordinal)) continue;
            int key = (int)Math.Round(t.Y);
            byBaseline[key] = byBaseline.TryGetValue(key, out var e) ? (Math.Min(e.Left, t.X), e.Chars + t.Text.Length) : (t.X, t.Text.Length);
        }
        return byBaseline.OrderByDescending(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    private static double[] LineLefts(IEnumerable<PdfContentProbe.DrawnText> runs)
    {
        var byBaseline = new Dictionary<int, double>();
        foreach (var t in runs)
        {
            int key = (int)Math.Round(t.Y);
            byBaseline[key] = byBaseline.TryGetValue(key, out double left) ? Math.Min(left, t.X) : t.X;
        }
        return byBaseline.OrderByDescending(kv => kv.Key).Select(kv => kv.Value).ToArray();
    }
}
