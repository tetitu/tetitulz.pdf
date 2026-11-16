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
/// <see cref="AbsolutePlacement.ContainingBox"/> (batch 127): a positioned paragraph measured from a
/// box of blocks — its offsets from the box's padding box, drawn with the box's fragment on the page
/// its anchor falls on, over the box's blocks, in z order among the box's positioned paragraphs; the
/// right and bottom anchors, a right-anchored block's shrink-to-fit width, a box in a table cell, and
/// a box not open where the paragraph stands, which leaves the page as the containing block.
/// </summary>
/// <remarks>
/// Letter page, 72pt margins: the band is 468pt from x = 72, the text block's top at y = 720. Read
/// back from the drawn runs and the boxes' fill rectangles; a page-relative placement at the same
/// point is the control a box-relative one must agree with.
/// </remarks>
public sealed class PositionedInBoxTests
{
    private const double PageWidth = 612, PageHeight = 792, Margin = 72;
    private const double Band = PageWidth - 2 * Margin;

    /// <summary>
    /// Offsets are from the box's padding box: a box 30pt in with a 2pt border and a child at
    /// <c>left: 5, top: 7</c> draws the child where a page-relative placement at the same point does
    /// — x = 72 + 30 + 2 + 5, 7pt below the box's top padding edge — and not 5pt from the page's edge.
    /// </summary>
    [Fact]
    public void OffsetsAreMeasuredFromTheBoxsPaddingBox()
    {
        var box = Card(marginTop: 20, marginLeft: 30, border: 2, padding: 10);
        var child = Positioned("TAG", new AbsolutePlacement { ContainingBox = box, LeftPt = 5, TopPt = 7 });
        string relative = Render(box, Para("Body"), child, new BoxEnd());

        // The box's border box top: 720 less the 20pt margin; its top padding edge 2pt below.
        double padTop = PageHeight - Margin - 20 - 2;
        string control = Render(Positioned("TAG", new AbsolutePlacement { LeftPt = Margin + 30 + 2 + 5, TopPt = PageHeight - (padTop - 7) }));

        var tag = Run(relative, "TAG");
        var expected = Run(control, "TAG");
        Assert.Equal(expected.X, tag.X, 1);
        Assert.Equal(expected.Y, tag.Y, 1);
        Assert.True(tag.X > 100, $"TAG at x={tag.X:F1} was measured from the page, not the box");
    }

    /// <summary>
    /// <c>right</c> and <c>bottom</c> anchor the far edges: with a 100pt width and <c>right: 10</c> the
    /// block starts 110pt short of the box's right padding edge; with <c>bottom: 5</c> its baseline is
    /// a descent above a point 5pt over the box's bottom padding edge, below the box's last line.
    /// </summary>
    [Fact]
    public void RightAndBottom_AnchorTheFarEdges()
    {
        var box = Card(border: 2, padding: 10, shading: "#0000FF");
        var child = Positioned("TAG", new AbsolutePlacement { ContainingBox = box, AnchorsLeft = false, RightPt = 10, WidthPt = 100, AnchorsTop = false, BottomPt = 5 });
        string content = Render(box, Para("Body"), Para("More"), child, new BoxEnd());

        double padRight = Margin + Band - 2;
        var tag = Run(content, "TAG");
        Assert.Equal(padRight - 10 - 100, tag.X, 1);

        var fill = Assert.Single(Rects(content), r => r.W > 400);
        double padBottom = fill.Y + 2;
        Assert.InRange(tag.Y, padBottom + 5, padBottom + 5 + 6);
        Assert.True(tag.Y < Run(content, "More").Y, "the bottom-anchored block is not below the box's last line");
    }

    /// <summary>A block anchored at the right alone, with no width, is as wide as its text: TAG starts within its text's width of the right anchor, not at the box's left.</summary>
    [Fact]
    public void RightAlone_ShrinksTheBlockToItsContent()
    {
        var box = Card(border: 2, padding: 10);
        var child = Positioned("TAG", new AbsolutePlacement { ContainingBox = box, AnchorsLeft = false, RightPt = 10, TopPt = 0 });
        string content = Render(box, Para("Body"), child, new BoxEnd());

        double padRight = Margin + Band - 2;
        var tag = Run(content, "TAG");
        Assert.InRange(tag.X, padRight - 10 - 40, padRight - 10 - 15);
    }

    /// <summary>With <c>left</c> and <c>right</c> both set and no width, the block spans between them: forty words wrap to more lines when <c>right</c> is larger.</summary>
    [Fact]
    public void LeftAndRight_SizeTheBlockBetweenThem()
    {
        int Lines(double right)
        {
            var box = Card(border: 2, padding: 10);
            var child = new Paragraph { Placement = new AbsolutePlacement { ContainingBox = box, LeftPt = 10, RightPt = right, TopPt = 0 } };
            for (int i = 0; i < 40; i++) child.Runs.Add(new TextRun { Text = "WORD" + i + " " });   // a run a word, so each line's words are drawn runs
            return PdfContentProbe.DrawnTexts(Render(box, Para("Body"), child, new BoxEnd()))
                .Where(t => t.Text.StartsWith("WORD", StringComparison.Ordinal)).Select(t => Math.Round(t.Y, 1)).Distinct().Count();
        }

        int narrow = Lines(300), wide = Lines(20);
        Assert.True(narrow > wide && wide >= 1, $"a block spanning less of the box did not wrap into more lines: {narrow} against {wide}");
    }

    /// <summary>
    /// An anchor further down the box than its first page's fragment is drawn with the fragment —
    /// the page — it falls in: at <c>top: 700</c> in a two-page box TAG is drawn after the second
    /// page's paragraphs; at <c>top: 100</c> with the first page's fragment, before them.
    /// </summary>
    [Fact]
    public void AnAnchorPastTheFirstFragment_IsDrawnOnThatFragmentsPage()
    {
        string Content(double top)
        {
            var box = Card(border: 0, padding: 0);
            var blocks = new List<IDocElement> { box };
            for (int i = 0; i < 60; i++) blocks.Add(Para("P" + i));
            blocks.Add(Positioned("TAG", new AbsolutePlacement { ContainingBox = box, LeftPt = 0, TopPt = top }));
            blocks.Add(new BoxEnd());
            return Render(blocks.ToArray());
        }

        string second = Content(700);
        int tag2 = second.IndexOf("(TAG)", StringComparison.Ordinal), p59 = second.IndexOf("(P59)", StringComparison.Ordinal);
        Assert.True(tag2 > p59 && p59 > 0, "an anchor past the first fragment was not drawn with the last fragment");
        // 700pt down the box is 52pt below the second page's top (the first fragment was the 648pt
        // text block): the baseline an ascent under y = 668. Counted from the box's top alone it
        // would be 700pt down the second page, off its foot.
        Assert.InRange(Run(second, "TAG").Y, 640, 680);

        string first = Content(100);
        int tag1 = first.IndexOf("(TAG)", StringComparison.Ordinal), p45 = first.IndexOf("(P45)", StringComparison.Ordinal);
        Assert.True(tag1 > 0 && tag1 < p45, "an anchor in the first fragment was not drawn with it, before the next page's paragraphs");
    }

    /// <summary>
    /// In a table cell: a child at <c>top: 0; left: 0</c> of a padded box in the cell is drawn at the
    /// box's padding edge — 10pt left of and above the box's first paragraph, which the padding keeps
    /// in — not at the cell's own edge.
    /// </summary>
    [Fact]
    public void InACell_TheBoxIsTheContainingBlock()
    {
        var box = Card(border: 0, padding: 10);
        var doc = NewDoc();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(box);
        cell.Blocks.Add(Para("Body"));
        cell.Blocks.Add(Positioned("TAG", new AbsolutePlacement { ContainingBox = box, LeftPt = 0, TopPt = 0 }));
        cell.Blocks.Add(new BoxEnd());
        row.Cells.Add(cell);
        table.Rows.Add(row);
        doc.Sections[0].Children.Add(table);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());

        var tag = Run(content, "TAG");
        var body = Run(content, "Body");
        Assert.Equal(body.X - 10, tag.X, 1);
        Assert.Equal(body.Y + 10, tag.Y, 1);
        Assert.True(tag.X > Margin + 1, $"TAG at x={tag.X:F1} was measured from the page, not the box in the cell");
    }

    /// <summary>
    /// A page-placed paragraph inside a table cell is drawn at its page point, once, after the flow —
    /// not laid out in the cell's flow, where it used to go; the cell's own text stays where it was.
    /// </summary>
    [Fact]
    public void APagePlacedParagraphInACell_IsDrawnAtItsPagePoint()
    {
        var doc = NewDoc();
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(Para("Body"));
        cell.Blocks.Add(Positioned("TAG", new AbsolutePlacement { LeftPt = 300, TopPt = 100 }));
        cell.Blocks.Add(Para("More"));
        row.Cells.Add(cell);
        table.Rows.Add(row);
        doc.Sections[0].Children.Add(table);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());

        var tag = Run(content, "TAG");   // once
        Assert.Equal(300, tag.X, 1);
        Assert.InRange(tag.Y, PageHeight - 100 - 15, PageHeight - 100);
        Assert.True(content.IndexOf("(TAG)", StringComparison.Ordinal) > content.IndexOf("(More)", StringComparison.Ordinal), "the placed paragraph was drawn in the cell's flow, not after it");
        Assert.True(Run(content, "More").Y > Run(content, "Body").Y - 30, "the cell's text made room for the placed paragraph");
    }

    /// <summary>Two positioned paragraphs of one box paint in z order: the lower z first, under the higher.</summary>
    [Fact]
    public void ZIndex_OrdersTheBoxsPositionedParagraphs()
    {
        var box = Card(border: 0, padding: 10);
        string content = Render(box, Para("Body"),
            Positioned("Above", new AbsolutePlacement { ContainingBox = box, LeftPt = 0, TopPt = 0, ZIndex = 5 }),
            Positioned("Below", new AbsolutePlacement { ContainingBox = box, LeftPt = 0, TopPt = 0, ZIndex = 1 }),
            new BoxEnd());

        int below = content.IndexOf("(Below)", StringComparison.Ordinal), above = content.IndexOf("(Above)", StringComparison.Ordinal);
        Assert.True(below > 0 && above > 0 && below < above, "the lower z-index was not painted first");
        Assert.True(content.IndexOf("(Body)", StringComparison.Ordinal) < below, "the positioned paragraphs were not painted over the box's blocks");
    }

    /// <summary>
    /// A containing box that is not open where the paragraph stands — another box is — leaves the
    /// page as the containing block: the paragraph is drawn once, at its page offsets, not skipped
    /// as the open box's and not measured from it.
    /// </summary>
    [Fact]
    public void ABoxNotOpenHere_IsThePage()
    {
        var elsewhere = Card(border: 2, padding: 10);
        var open = Card(border: 2, padding: 10);
        var child = Positioned("TAG", new AbsolutePlacement { ContainingBox = elsewhere, LeftPt = 300, TopPt = 100 });
        string content = Render(open, Para("Body"), child, new BoxEnd());

        var tag = Run(content, "TAG");
        Assert.Equal(300, tag.X, 1);
        Assert.InRange(tag.Y, PageHeight - 100 - 15, PageHeight - 100);   // the baseline, an ascent below the stated top
    }

    /// <summary>
    /// A box inside the containing box does not take its paragraph: the child at <c>left: 0; top: 0</c>
    /// of the outer box is drawn once, at the outer box's padding corner, not at the inner box's, where
    /// Body sits.
    /// </summary>
    [Fact]
    public void ANestedBox_DoesNotTakeTheOuterBoxsParagraph()
    {
        var outer = Card(border: 0, padding: 10);
        var inner = Card(border: 0, padding: 30);
        var child = Positioned("TAG", new AbsolutePlacement { ContainingBox = outer, LeftPt = 0, TopPt = 0 });
        string content = Render(outer, inner, Para("Body"), child, new BoxEnd(), new BoxEnd());

        var tag = Run(content, "TAG");   // drawn once
        var body = Run(content, "Body");
        Assert.Equal(Margin, tag.X, 1);
        Assert.Equal(body.X - 40, tag.X, 1);
        Assert.Equal(body.Y + 40, tag.Y, 1);
    }

    // ------------------------------------------------------------------

    private static Document NewDoc()
    {
        var doc = new Document();
        doc.Sections.Add(new Section { PageWidthPt = PageWidth, PageHeightPt = PageHeight });
        return doc;
    }

    private static byte[] Export(params IDocElement[] blocks)
    {
        var doc = NewDoc();
        doc.Sections[0].Children.AddRange(blocks);
        return doc.ToPdf();
    }

    private static string Render(params IDocElement[] blocks) => PdfContentProbe.DecodeStreams(Export(blocks));

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static Paragraph Positioned(string text, AbsolutePlacement place)
    {
        var p = Para(text);
        p.Placement = place;
        return p;
    }

    /// <summary>A box with the given margins, border (every side) and padding (every side).</summary>
    private static BoxStart Card(double marginTop = 0, double marginLeft = 0, double border = 1, double padding = 8, string shading = "")
    {
        var b = border > 0 ? new Border(border, "#000000") : null;
        return new BoxStart
        {
            MarginTop = marginTop, MarginLeft = marginLeft,
            BorderTop = b, BorderRight = b, BorderBottom = b, BorderLeft = b,
            PaddingTop = padding, PaddingRight = padding, PaddingBottom = padding, PaddingLeft = padding,
            Shading = shading,
        };
    }

    private static PdfContentProbe.DrawnText Run(string content, string text)
    {
        var runs = PdfContentProbe.DrawnTexts(content).Where(t => t.Text == text).ToList();
        Assert.True(runs.Count == 1, $"'{text}' was drawn {runs.Count} times");
        return runs[0];
    }

    private static List<(double X, double Y, double W, double H)> Rects(string content)
        => Regex.Matches(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
                .Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value))).ToList();

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
