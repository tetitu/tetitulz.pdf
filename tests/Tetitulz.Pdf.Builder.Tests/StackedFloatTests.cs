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
/// Two <see cref="Paragraph.Float"/>s on one side of one line (CSS 2.1 §9.5.1): the second sits
/// inward of the first, or — where that would leave the text less than an inch — drops below the
/// nearest float it would have sat beside. Read back from the drawn runs' positions.
/// </summary>
/// <remarks>
/// Every float sat against its band's edge, so a second on a side was drawn over the first; the
/// HTML mapper therefore left a second floated inline element in its line, reported.
/// </remarks>
public sealed class StackedFloatTests
{
    private const double PageWidth = 612;
    private const double Margin = 72;
    private const double RightEdge = PageWidth - Margin;   // 540

    /// <summary>Two right floats of 100: the first at the right edge, the second against its left, on the same line; the lines beside them are broken shorter than beside one.</summary>
    [Fact]
    public void ASecondRightFloatSitsInwardOfTheFirst()
    {
        Document two = NewDoc();
        two.Sections[0].Children.Add(Float(FloatSide.Right, 100, "FLOATA"));
        two.Sections[0].Children.Add(Float(FloatSide.Right, 100, "FLOATB"));
        AddBody(two);
        Document one = NewDoc();
        one.Sections[0].Children.Add(Float(FloatSide.Right, 100, "FLOATA"));
        AddBody(one);

        var runs = Runs(two);
        Assert.Equal(RightEdge - 100, Run(runs, "FLOATA").X, 1);
        Assert.Equal(RightEdge - 200, Run(runs, "FLOATB").X, 1);
        Assert.Equal(Run(runs, "FLOATA").Y, Run(runs, "FLOATB").Y, 1);
        Assert.Equal(Run(runs, "FLOATA").Y, FirstBodyLine(runs).Y, 1);
        Assert.Equal(Margin, FirstBodyLine(runs).X, 1);
        Assert.True(FirstBodyChars(runs) < FirstBodyChars(Runs(one)), "the line beside two floats was not broken shorter than beside one");
    }

    /// <summary>Two left floats of 100: the second starts where the first ends, and the text after both.</summary>
    [Fact]
    public void ASecondLeftFloatSitsAfterTheFirst()
    {
        Document doc = NewDoc();
        doc.Sections[0].Children.Add(Float(FloatSide.Left, 100, "FLOATA"));
        doc.Sections[0].Children.Add(Float(FloatSide.Left, 100, "FLOATB"));
        AddBody(doc);

        var runs = Runs(doc);
        Assert.Equal(Margin, Run(runs, "FLOATA").X, 1);
        Assert.Equal(Margin + 100, Run(runs, "FLOATB").X, 1);
        Assert.Equal(Run(runs, "FLOATA").Y, Run(runs, "FLOATB").Y, 1);
        Assert.Equal(Margin + 200, FirstBodyLine(runs).X, 1);
    }

    /// <summary>
    /// Two right floats of 200 in a 468 band leave 68 beside them, under the inch text needs: the
    /// second drops below the first, at the edge, and the text still starts beside the first, where
    /// the flow was. Floats of 150 leave 168 and stay level — the control.
    /// </summary>
    [Fact]
    public void ASecondFloatThatLeavesNoMeasureDropsBelowTheFirst()
    {
        Document wide = NewDoc();
        wide.Sections[0].Children.Add(Float(FloatSide.Right, 200, "FLOATA"));
        wide.Sections[0].Children.Add(Float(FloatSide.Right, 200, "FLOATB"));
        AddBody(wide);
        Document narrow = NewDoc();
        narrow.Sections[0].Children.Add(Float(FloatSide.Right, 150, "FLOATA"));
        narrow.Sections[0].Children.Add(Float(FloatSide.Right, 150, "FLOATB"));
        AddBody(narrow);

        var runs = Runs(wide);
        Assert.Equal(RightEdge - 200, Run(runs, "FLOATA").X, 1);
        Assert.Equal(RightEdge - 200, Run(runs, "FLOATB").X, 1);
        Assert.True(Run(runs, "FLOATB").Y < Run(runs, "FLOATA").Y - 8, "the second float did not drop below the first");
        Assert.Equal(Run(runs, "FLOATA").Y, FirstBodyLine(runs).Y, 1);

        var level = Runs(narrow);
        Assert.Equal(Run(level, "FLOATA").Y, Run(level, "FLOATB").Y, 1);
        Assert.Equal(RightEdge - 300, Run(level, "FLOATB").X, 1);
    }

    /// <summary>
    /// A float that fits nowhere on its line drops to the nearest bottom among the floats there,
    /// not the lowest: between a one-line right float of 100 and a three-line left float of 100 a
    /// right float of 200 has 68, so it drops one line, to where the short float ends, and sits at
    /// the right edge beside the tall one. (A second right float bounds the segment whatever ends
    /// above it, so only a left-and-right pair can show which bottom was taken.)
    /// </summary>
    [Fact]
    public void AFloatDropsToTheNearestBottomFirst()
    {
        Document doc = NewDoc();
        doc.Sections[0].Children.Add(Float(FloatSide.Right, 100, "FLOATA"));
        var tall = Float(FloatSide.Left, 100, "FLOATB1");
        tall.Runs.Add(new LineBreak()); tall.Runs.Add(new TextRun { Text = "FLOATB2" });
        tall.Runs.Add(new LineBreak()); tall.Runs.Add(new TextRun { Text = "FLOATB3" });
        doc.Sections[0].Children.Add(tall);
        doc.Sections[0].Children.Add(Float(FloatSide.Right, 200, "FLOATC"));
        AddBody(doc);

        var runs = Runs(doc);
        double lineHeight = Run(runs, "FLOATB1").Y - Run(runs, "FLOATB2").Y;
        Assert.Equal(Margin, Run(runs, "FLOATB1").X, 1);
        Assert.Equal(RightEdge - 200, Run(runs, "FLOATC").X, 1);
        Assert.Equal(Run(runs, "FLOATA").Y - lineHeight, Run(runs, "FLOATC").Y, 1);
        Assert.Equal(Margin + 100, FirstBodyLine(runs).X, 1);
    }

    /// <summary>
    /// A cell's floats are measured where they are drawn: a right float of 150 and one of 250 in a
    /// cell stack (68 would be left beside both), and the row is measured with the text as it wraps
    /// — two lines, the first beside the narrow float, the second beside the wide one below it — so
    /// the next row starts as close under the body's last line as it does with one float. An
    /// exclusion registered where the float was asked for rather than where it dropped would narrow
    /// the first line by the wide float too, and size the row a line taller than what is drawn.
    /// </summary>
    [Fact]
    public void TwoFloatsInACellAreMeasuredWhereTheyAreDrawn()
    {
        var two = Runs(CellWithFloats(2));
        var one = Runs(CellWithFloats(1));

        Assert.True(Run(two, "FLOAT2").Y < Run(two, "FLOAT1").Y - 8, "the second float in the cell did not drop below the first");
        Assert.Equal(Run(two, "FLOAT1").X + 150 - 250, Run(two, "FLOAT2").X, 1);
        Assert.Equal(GapToNextRow(one), GapToNextRow(two), 1);
    }

    /// <summary>
    /// The row reaches a dropped float's bottom: with a one-line body beside the first float, the
    /// next row still starts below the second float, which is measured where it dropped to, a line
    /// down — not where it was asked for, which would size the row a line short and draw the next
    /// row over it.
    /// </summary>
    [Fact]
    public void TheRowReachesADroppedFloatsBottom()
    {
        var two = Runs(CellWithFloats(2, body: "Body"));

        Assert.True(Run(two, "FLOAT2").Y < Run(two, "FLOAT1").Y - 8, "the second float in the cell did not drop below the first");
        Assert.True(Run(two, "NEXTROW").Y < Run(two, "FLOAT2").Y - 8, $"the next row started at y={Run(two, "NEXTROW").Y:F1}, over the dropped float at y={Run(two, "FLOAT2").Y:F1}");
    }

    /// <summary>
    /// A float that ended higher on the page is not among those a dropping float asks about: after
    /// a float the body has cleared, two right floats of 200 on a later line still stack — the
    /// second below the first, not squeezed beside it.
    /// </summary>
    [Fact]
    public void AFloatAboveTheLineDoesNotStopTheDrop()
    {
        Document doc = NewDoc();
        doc.Sections[0].Children.Add(Float(FloatSide.Right, 100, "FLOATA"));
        AddBody(doc);
        doc.Sections[0].Children.Add(Float(FloatSide.Right, 200, "FLOATB"));
        doc.Sections[0].Children.Add(Float(FloatSide.Right, 200, "FLOATC"));
        doc.Sections[0].Children.Add(new Paragraph { Runs = { new TextRun { Text = "Tail" } } });

        var runs = Runs(doc);
        Assert.True(Run(runs, "FLOATB").Y < Run(runs, "FLOATA").Y - 30, "the fixture's later floats are not below the first");
        Assert.Equal(RightEdge - 200, Run(runs, "FLOATB").X, 1);
        Assert.Equal(RightEdge - 200, Run(runs, "FLOATC").X, 1);
        Assert.True(Run(runs, "FLOATC").Y < Run(runs, "FLOATB").Y - 8, "the third float did not drop below the second");
    }

    // ================================================================ fixtures

    private static Document NewDoc()
    {
        var doc = new Document();
        var section = new Section
        {
            PageWidthPt = PageWidth, PageHeightPt = 792,
            MarginLeft = Margin, MarginRight = Margin, MarginTop = Margin, MarginBottom = Margin,
        };
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>A float with no spacing about it, so a float dropped to its bottom is exactly a line below its top.</summary>
    private static Paragraph Float(FloatSide side, double width, string text)
        => new Paragraph { Float = side, FloatWidth = width, SpacingBefore = 0, SpacingAfter = 0, Runs = { new TextRun { Text = text } } };

    private static void AddBody(Document doc)
    {
        string text = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog and keeps on running", 8));
        doc.Sections[0].Children.Add(new Paragraph { Runs = { new TextRun { Text = text } } });
    }

    private static Document CellWithFloats(int count, string? body = null)
    {
        Document doc = NewDoc();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        // 150 then 250: the second cannot sit beside the first, and is wider, so an exclusion
        // registered at the wrong height would narrow the first line by more than the first float.
        for (int i = 1; i <= count; i++) cell.Blocks.Add(Float(FloatSide.Right, i == 1 ? 150 : 250, "FLOAT" + i));
        // By default two lines of body: one beside the first float, one beside the second — and no
        // spacing, so the body's bottom is where the floats' is.
        string text = body ?? string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog", 2));
        cell.Blocks.Add(new Paragraph { SpacingBefore = 0, SpacingAfter = 0, Runs = { new TextRun { Text = text } } });
        row.Cells.Add(cell);
        table.Rows.Add(row);
        var next = new TableRow();
        var nextCell = new TableCell();
        nextCell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "NEXTROW" } } });
        next.Cells.Add(nextCell);
        table.Rows.Add(next);
        doc.Sections[0].Children.Add(table);
        return doc;
    }

    private static List<PdfContentProbe.DrawnText> Runs(Document doc)
        => PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf()));

    private static PdfContentProbe.DrawnText Run(List<PdfContentProbe.DrawnText> runs, string text)
        => runs.Single(t => t.Text.Contains(text, StringComparison.Ordinal));

    private static bool IsBody(PdfContentProbe.DrawnText t)
        => !string.IsNullOrWhiteSpace(t.Text) && !t.Text.Contains("FLOAT", StringComparison.Ordinal) && !t.Text.Contains("NEXTROW", StringComparison.Ordinal);

    /// <summary>From the cell body's last line down to the next row's text: the cell's bottom padding when the row is sized as drawn.</summary>
    private static double GapToNextRow(List<PdfContentProbe.DrawnText> runs)
        => runs.Where(IsBody).Min(t => t.Y) - Run(runs, "NEXTROW").Y;

    /// <summary>The first run of the body's first line: the highest baseline, leftmost.</summary>
    private static PdfContentProbe.DrawnText FirstBodyLine(List<PdfContentProbe.DrawnText> runs)
    {
        double top = runs.Where(IsBody).Max(t => t.Y);
        return runs.Where(t => IsBody(t) && Math.Abs(t.Y - top) < 0.5).OrderBy(t => t.X).First();
    }

    private static int FirstBodyChars(List<PdfContentProbe.DrawnText> runs)
    {
        double top = runs.Where(IsBody).Max(t => t.Y);
        return runs.Where(t => IsBody(t) && Math.Abs(t.Y - top) < 0.5).Sum(t => t.Text.Length);
    }
}
