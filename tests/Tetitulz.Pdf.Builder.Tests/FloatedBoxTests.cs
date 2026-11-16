// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.Float"/> (batch 125): a box of blocks pinned to one edge of the band, the
/// blocks after it flowing beside it and back to the full measure past its bottom, as a floated
/// paragraph's do — in the section's flow and inside a table cell; moved to the next page when it
/// does not fit the rest of its own; laid out in the flow, reported, when taller than a page; and a
/// block of its float width on its side when nothing fits beside it. <see cref="BoxStart.ClearFloats"/>
/// starts a box below the floats before it.
/// </summary>
/// <remarks>
/// Read back from the drawn runs' positions and the box's fill rectangle, as <c>FloatReflowTests</c>
/// reads a paragraph float: a line beside the float starts clear of it, a line below it at the
/// margin, and only both together tell a float from a two-column band. Letter page, 72pt margins: the
/// band is 468pt from x = 72.
/// </remarks>
public sealed class FloatedBoxTests
{
    private const double PageWidth = 612;
    private const double Margin = 72;
    private const double Band = PageWidth - 2 * Margin;
    private const double FloatWidth = 150;
    private const string Body = "BODY";

    // ------------------------------------------------------------------ the flow

    /// <summary>A left floated box pushes the body's edge right and then releases it; the first body line is beside the box's first paragraph; the box's fill is its float width at the margin.</summary>
    [Fact]
    public void ALeftFloatedBox_PushesTheEdgeRightAndThenReleasesIt()
    {
        string content = Render(FloatedBox(FloatSide.Left, shading: "#0000FF"), BodyParagraph(90));
        var runs = PdfContentProbe.DrawnTexts(content);

        double[] lefts = LineLefts(runs);
        Assert.True(lefts.Length >= 6, $"expected several body lines, got {lefts.Length}");
        Assert.Contains(lefts, x => x > Margin + FloatWidth - 2);
        Assert.Contains(lefts, x => x < Margin + 2);
        Assert.Equal(runs.Single(t => t.Text == "SIDEONE").Y, runs.Where(t => t.Text.StartsWith(Body, StringComparison.Ordinal)).Max(t => t.Y), 1);

        var fill = Assert.Single(Rects(content), r => Math.Abs(r.W - FloatWidth) < 0.5);
        Assert.Equal(Margin, fill.X, 1);
    }

    /// <summary>A right floated box sits at the band's right edge and shortens the lines beside it without moving their start.</summary>
    [Fact]
    public void ARightFloatedBox_SitsAtTheRightEdge_AndShortensTheLinesBesideIt()
    {
        string content = Render(FloatedBox(FloatSide.Right, shading: "#0000FF"), BodyParagraph(90));
        var runs = PdfContentProbe.DrawnTexts(content);

        var fill = Assert.Single(Rects(content), r => Math.Abs(r.W - FloatWidth) < 0.5);
        Assert.Equal(PageWidth - Margin - FloatWidth, fill.X, 1);
        List<(double Left, int Chars)> lines = BodyLines(runs);
        Assert.True(lines.Count >= 6);
        Assert.All(lines, l => Assert.True(l.Left < Margin + 2, $"a right float moved a line's start to {l.Left:F1}"));
        Assert.True(lines[0].Chars < lines.Skip(3).Max(l => l.Chars), "the first line, beside the box, was not shortened");
    }

    /// <summary>The exclusion is the margin box: a box with 10pt and 20pt side margins is drawn 10pt in, and the text beside it starts 20pt past its edge.</summary>
    [Fact]
    public void TheMargins_AreOutsideTheFloatWidth_AndInsideTheExclusion()
    {
        var box = FloatedBox(FloatSide.Left, shading: "#0000FF");
        ((BoxStart)box[0]).MarginLeft = 10;
        ((BoxStart)box[0]).MarginRight = 20;
        string content = Render(box, BodyParagraph(90));

        var fill = Assert.Single(Rects(content), r => Math.Abs(r.W - FloatWidth) < 0.5);
        Assert.Equal(Margin + 10, fill.X, 1);
        double beside = LineLefts(PdfContentProbe.DrawnTexts(content)).Max();
        Assert.Equal(Margin + 10 + FloatWidth + 20, beside, 1);
    }

    /// <summary>A floated box that does not fit the rest of its page moves to the next, and the body starts beside it there — not on the first page below the filler.</summary>
    [Fact]
    public void ABoxThatDoesNotFitThePage_MovesToTheNextPage_WithTheTextBesideIt()
    {
        // One filler 560pt down the 648pt page leaves less than the six-paragraph box's ~84pt.
        var filler = Para("FILLER");
        filler.SpacingBefore = 560;
        var box = FloatedBox(FloatSide.Left, shading: "#0000FF", paragraphs: 6);
        var blocks = new List<IDocElement> { filler };
        blocks.AddRange(box);
        blocks.Add(BodyParagraph(20));
        string content = Render(blocks.ToArray());
        var runs = PdfContentProbe.DrawnTexts(content);

        double side = runs.Single(t => t.Text == "SIDEONE").Y;
        double lastFiller = runs.Where(t => t.Text.StartsWith("FILLER", StringComparison.Ordinal)).Min(t => t.Y);
        Assert.True(side > lastFiller, $"the box at y={side:F1} stayed on the first page under the filler ending at y={lastFiller:F1}");
        Assert.True(side > 600, $"the box at y={side:F1} is not at the top of a page");
        Assert.Equal(side, runs.Where(t => t.Text.StartsWith(Body, StringComparison.Ordinal)).Max(t => t.Y), 1);
    }

    /// <summary>A floated box taller than a page is laid out in the flow — every paragraph kept, the body below it — and says so.</summary>
    [Fact]
    public void ABoxTallerThanAPage_IsLaidOutInTheFlow_Reported()
    {
        var reports = new List<BuilderDiagnostic>();
        // Filtered to the calling thread: the event is process-wide, and a neighbouring test's
        // report added while this one reads the list failed it once.
        int mine = Environment.CurrentManagedThreadId;
        void Collect(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != mine) return;
            lock (reports) reports.Add(d);
        }
        BuilderDiagnostics.OnEvent += Collect;
        string content;
        try { content = Render(FloatedBox(FloatSide.Left, shading: "#0000FF", paragraphs: 70), BodyParagraph(10)); }
        finally { BuilderDiagnostics.OnEvent -= Collect; }
        var runs = PdfContentProbe.DrawnTexts(content);

        Assert.Contains(reports, d => d.Kind == BuilderDiagnosticKind.ContentApproximated && d.Detail.Contains("taller than a page", StringComparison.Ordinal));
        var missing = Enumerable.Range(1, 70).Where(i => !runs.Any(t => t.Text == (i == 1 ? "SIDEONE" : i == 2 ? "SIDETWO" : "SIDE" + i))).ToList();
        Assert.True(missing.Count == 0, $"paragraphs {string.Join(",", missing)} of the box were lost; {runs.Count} runs: {string.Join(" ", runs.Select(t => t.Text))}");
        // In the flow, a block of its float width at the margin: the body is below it, not beside it.
        Assert.All(LineLefts(runs), x => Assert.True(x < Margin + 2, $"the body was laid out beside the box at x={x:F1}"));
        Assert.Contains(Rects(content), r => Math.Abs(r.W - FloatWidth) < 0.5 && Math.Abs(r.X - Margin) < 0.5);
    }

    /// <summary>A floated box leaving less than an inch beside it is a block of its float width on its side: a right float's fill starts where the spare room ends, and the body is below it.</summary>
    [Fact]
    public void ABoxWithNoRoomBesideIt_IsABlockOfItsWidthOnItsSide()
    {
        var box = FloatedBox(FloatSide.Right, shading: "#0000FF");
        ((BoxStart)box[0]).FloatWidth = Band - 40;
        string content = Render(box, BodyParagraph(30));
        var runs = PdfContentProbe.DrawnTexts(content);

        var fill = Assert.Single(Rects(content), r => Math.Abs(r.W - (Band - 40)) < 0.5);
        Assert.Equal(Margin + 40, fill.X, 1);
        double sideBottom = runs.Single(t => t.Text == "SIDETWO").Y;
        Assert.All(runs.Where(t => t.Text.StartsWith(Body, StringComparison.Ordinal)), t => Assert.True(t.Y < sideBottom, "the body was laid out beside a box with no room for it"));
    }

    /// <summary>A floated box with no float width is not floated: a plain box the full band wide, the body below it.</summary>
    [Fact]
    public void AFloatedBoxWithoutAWidth_IsAPlainBox()
    {
        var box = FloatedBox(FloatSide.Left, shading: "#0000FF");
        ((BoxStart)box[0]).FloatWidth = 0;
        string content = Render(box, BodyParagraph(10));
        var runs = PdfContentProbe.DrawnTexts(content);

        var fill = Assert.Single(Rects(content), r => r.H > 10);
        Assert.Equal(Band, fill.W, 1);
        Assert.Equal(Margin, fill.X, 1);
        double sideBottom = runs.Single(t => t.Text == "SIDETWO").Y;
        Assert.All(runs.Where(t => t.Text.StartsWith(Body, StringComparison.Ordinal)), t => Assert.True(t.Y < sideBottom, "the body was laid out beside a box with no float width"));
    }

    /// <summary>A box that clears starts below a float taller than the text before it, back at the margin, where without the clear it sits beside the float.</summary>
    [Fact]
    public void ABoxThatClears_StartsBelowTheFloat()
    {
        string cleared = Render(TallFloat(), Para("ADDR"), new BoxStart { Shading = "#0000FF", ClearFloats = true }, Para("BELOW"), new BoxEnd());
        string beside = Render(TallFloat(), Para("ADDR"), new BoxStart { Shading = "#0000FF" }, Para("BELOW"), new BoxEnd());

        var runs = PdfContentProbe.DrawnTexts(cleared);
        double floatBottom = runs.Where(t => t.Text.StartsWith("TALL", StringComparison.Ordinal)).Min(t => t.Y);
        var below = runs.Single(t => t.Text == "BELOW");
        Assert.True(below.Y < floatBottom, $"BELOW at y={below.Y:F1} is not below the float ending at y={floatBottom:F1}");
        Assert.True(below.X < Margin + 2, $"BELOW at x={below.X:F1} is not at the margin");
        Assert.Contains(Rects(cleared), r => Math.Abs(r.W - Band) < 0.5);

        Assert.True(PdfContentProbe.DrawnTexts(beside).Single(t => t.Text == "BELOW").X > Margin + FloatWidth - 2, "without the clear the box should sit beside the float");
    }

    /// <summary>A floated box that clears starts below the float before it rather than beside it.</summary>
    [Fact]
    public void AFloatedBoxThatClears_StartsBelowTheFloatBeforeIt()
    {
        var box = FloatedBox(FloatSide.Left);
        ((BoxStart)box[0]).ClearFloats = true;
        string content = Render(new IDocElement[] { TallFloat() }.Concat(box).Append(BodyParagraph(10)).ToArray());
        var runs = PdfContentProbe.DrawnTexts(content);

        double floatBottom = runs.Where(t => t.Text.StartsWith("TALL", StringComparison.Ordinal)).Min(t => t.Y);
        var side = runs.Single(t => t.Text == "SIDEONE");
        Assert.True(side.Y < floatBottom, $"the clearing box at y={side.Y:F1} is not below the float ending at y={floatBottom:F1}");
        Assert.Equal(Margin, side.X, 1);
    }

    // ------------------------------------------------------------------ in a cell

    /// <summary>In a table cell the floated box sits at the cell's edge, the cell's text flows beside it and past it, and the row reaches the text's last line.</summary>
    [Fact]
    public void AFloatedBoxInACell_LetsTheCellsBlocksFlowBesideIt()
    {
        var doc = NewDoc();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell { InnerPaddingPt = 0 };
        cell.Blocks.AddRange(FloatedBox(FloatSide.Left, shading: "#0000FF", width: 100));
        cell.Blocks.Add(BodyParagraph(60));
        row.Cells.Add(cell);
        table.Rows.Add(row);
        var next = new TableRow();
        var nextCell = new TableCell();
        nextCell.Blocks.Add(Para("NEXTROW"));
        next.Cells.Add(nextCell);
        table.Rows.Add(next);
        doc.Sections[0].Children.Add(table);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        var runs = PdfContentProbe.DrawnTexts(content);

        double side = runs.Single(t => t.Text == "SIDEONE").X;
        double[] lefts = LineLefts(runs);
        Assert.True(lefts.Length >= 5, $"expected several body lines, got {lefts.Length}");
        Assert.Contains(lefts, x => x > side + 100 - 2);
        Assert.Contains(lefts, x => Math.Abs(x - side) < 2);
        double lastBody = runs.Where(t => t.Text.StartsWith(Body, StringComparison.Ordinal)).Min(t => t.Y);
        Assert.True(runs.Single(t => t.Text == "NEXTROW").Y < lastBody - 5, "the next row started over the body's last line: the row was not measured beside the float");
    }

    // ------------------------------------------------------------------

    /// <summary>A Letter page — the model's default section is A4 — so the band is 468pt from x = 72.</summary>
    private static Document NewDoc()
    {
        var doc = new Document();
        doc.Sections.Add(new Section { PageWidthPt = PageWidth, PageHeightPt = 792 });
        return doc;
    }

    private static string Render(params IDocElement[] blocks)
    {
        var doc = NewDoc();
        doc.Sections[0].Children.AddRange(blocks);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static string Render(IEnumerable<IDocElement> first, params IDocElement[] rest)
        => Render(first.Concat(rest).ToArray());

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    /// <summary>A paragraph of <paramref name="words"/> body words, each a drawn run of its own so every line's start can be read.</summary>
    private static Paragraph BodyParagraph(int words)
    {
        var p = new Paragraph();
        for (int i = 0; i < words; i++) p.Runs.Add(new TextRun { Text = Body + i + " " });
        return p;
    }

    /// <summary>A left float of eight lines, 150pt wide: taller than the one-line text beside it.</summary>
    private static Paragraph TallFloat()
    {
        var p = new Paragraph { Float = FloatSide.Left, FloatWidth = FloatWidth };
        for (int i = 0; i < 8; i++)
        {
            if (i > 0) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "TALL" + i });
        }
        return p;
    }

    /// <summary>A floated box of <paramref name="paragraphs"/> paragraphs SIDE1…, the first two named SIDEONE and SIDETWO.</summary>
    private static List<IDocElement> FloatedBox(FloatSide side, string shading = "", int paragraphs = 2, double width = FloatWidth)
    {
        var blocks = new List<IDocElement> { new BoxStart { Float = side, FloatWidth = width, Shading = shading } };
        for (int i = 1; i <= paragraphs; i++)
            blocks.Add(Para(i == 1 ? "SIDEONE" : i == 2 ? "SIDETWO" : "SIDE" + i));
        blocks.Add(new BoxEnd());
        return blocks;
    }

    /// <summary>The body's lines, top first: each line's first run's x.</summary>
    private static double[] LineLefts(List<PdfContentProbe.DrawnText> runs)
        => BodyLines(runs).Select(l => l.Left).ToArray();

    private static List<(double Left, int Chars)> BodyLines(List<PdfContentProbe.DrawnText> runs)
        => runs.Where(t => t.Text.StartsWith(Body, StringComparison.Ordinal))
               .GroupBy(t => Math.Round(t.Y, 1)).OrderByDescending(g => g.Key)
               .Select(g => (g.Min(t => t.X), g.Sum(t => t.Text.Length))).ToList();

    /// <summary>Every <c>re</c> rectangle in the content: x, y, width, height.</summary>
    private static List<(double X, double Y, double W, double H)> Rects(string content)
        => Regex.Matches(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
                .Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value))).ToList();

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
