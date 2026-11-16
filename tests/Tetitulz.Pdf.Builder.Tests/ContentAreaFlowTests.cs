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
/// <see cref="Section.ContentAreas"/>: the flow fills each region of a page in turn, then the next
/// page's first — XFA's content areas (XFA 3.3 chapter 8, "Flowing Between ContentArea Objects").
/// </summary>
/// <remarks>
/// <para>Two columns of the default page, each 250 pt wide and 200 pt tall at the same top, 100 pt down (not the 72 pt margin): the left
/// at x = 36, the right at x = 326. Every line is drawn left-aligned at its region's left edge, so a
/// line's x says which region it is in, and drawing order says which page — within one region the
/// flow only moves down.</para>
/// </remarks>
public sealed class ContentAreaFlowTests
{
    private const double LeftColumn = 36, RightColumn = 326, AreaTop = 100, AreaHeight = 200;

    private static Section TwoColumns()
    {
        var section = new Section();
        section.ContentAreas.Add(new ContentArea { LeftPt = LeftColumn, TopPt = AreaTop, WidthPt = 250, HeightPt = AreaHeight });
        section.ContentAreas.Add(new ContentArea { LeftPt = RightColumn, TopPt = AreaTop, WidthPt = 250, HeightPt = AreaHeight });
        return section;
    }

    private static byte[] Pdf(Section section)
    {
        var doc = new Document();
        doc.Sections.Add(section);
        return doc.ToPdf();
    }

    /// <summary>The x of each of <paramref name="count"/> numbered marks, in drawing order.</summary>
    private static List<PdfContentProbe.DrawnText> Marks(byte[] pdf, string prefix, int count)
    {
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
        var marks = new List<PdfContentProbe.DrawnText>();
        for (int i = 1; i <= count; i++)
            marks.Add(drawn.Single(t => t.Text == prefix + i + "X"));
        return marks;
    }

    /// <summary>The runs of equal x in drawing order: which region each stretch of marks went to.</summary>
    private static List<(double X, int Count, double FirstY)> Runs(List<PdfContentProbe.DrawnText> marks)
    {
        var runs = new List<(double X, int Count, double FirstY)>();
        foreach (var m in marks)
        {
            if (runs.Count > 0 && Math.Abs(runs[^1].X - m.X) < 0.5) runs[^1] = (runs[^1].X, runs[^1].Count + 1, runs[^1].FirstY);
            else runs.Add((m.X, 1, m.Y));
        }
        return runs;
    }

    /// <summary>
    /// A paragraph that continues from a 150 pt area into a 300 pt one is broken again there: its lines
    /// in the wide area hold about twice the words, every word still drawn once, in order, within the
    /// area — where it used to keep the narrow area's width. In a box of no width of its own it is broken
    /// again too (since 2026-10-19 the box takes the wide area's width); in a box of a stated width, or
    /// one the split cannot take (a bordered paragraph), it keeps the width it began at.
    /// </summary>
    [Fact]
    public void AParagraphIntoAWiderArea_IsBrokenAgainThere()
    {
        Section Narrow_Wide()
        {
            var section = new Section();
            section.ContentAreas.Add(new ContentArea { LeftPt = 36, TopPt = AreaTop, WidthPt = 150, HeightPt = 100 });
            section.ContentAreas.Add(new ContentArea { LeftPt = 250, TopPt = AreaTop, WidthPt = 300, HeightPt = 500 });
            return section;
        }
        string words = string.Join(" ", Enumerable.Range(1, 120).Select(i => "w" + i.ToString("000")));
        Paragraph Text() { var p = new Paragraph { LineSpacingPt = 20, SpacingAfter = 0 }; p.Runs.Add(new TextRun { Text = words }); return p; }

        // A line: the words drawn on one baseline in one area (each word is its own Tj), in drawing order.
        List<(double X, string Text)> Lines(params IDocElement[] blocks)
        {
            var section = Narrow_Wide();
            section.Children.AddRange(blocks);
            var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(Pdf(section))).Where(t => t.Text.Contains('w')).ToList();
            var lines = new List<(double X, double Y, List<string> Words)>();
            foreach (var t in drawn)
            {
                bool wideArea = t.X >= 200;
                int at = lines.FindIndex(l => Math.Abs(l.Y - t.Y) < 0.5 && (l.X >= 200) == wideArea);
                if (at < 0) lines.Add((t.X, t.Y, new List<string> { t.Text.Trim() }));
                else lines[at].Words.Add(t.Text.Trim());
            }
            return lines.Select(l => (l.X, string.Join(" ", l.Words))).ToList();
        }
        int WordsIn(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        var lines = Lines(Text());
        var narrow = lines.Where(l => l.X < 200).ToList();
        var wide = lines.Where(l => l.X >= 200).ToList();
        Assert.Equal(5, narrow.Count);                                         // 100 pt of 20 pt lines
        Assert.True(wide.Count > 0, "the paragraph continues in the wide area");
        int narrowWords = narrow.Max(l => WordsIn(l.Text)), wideWords = wide.Take(wide.Count - 1).Min(l => WordsIn(l.Text));
        Assert.True(wideWords >= 2 * narrowWords - 1, $"{narrowWords} words a narrow line, {wideWords} a wide one");
        Assert.Equal(words, string.Join(" ", lines.Select(l => l.Text.Trim())));

        // Every word of the wide area starts inside it: the band is the area's, not grown past it.
        var drawnWide = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(Pdf(WithBlocks(Narrow_Wide(), Text()))))
            .Where(t => t.Text.Contains('w') && t.X >= 200).ToList();
        Assert.All(drawnWide, t => Assert.InRange(t.X, 250, 550 - 20));

        // In a box of no width it is broken again; in a box of a stated width it keeps its width; and so
        // does a paragraph the split cannot take (a bordered one).
        var boxed = Lines(new BoxStart(), Text(), new BoxEnd()).Where(l => l.X >= 200).ToList();
        Assert.True(boxed.Take(boxed.Count - 1).Min(l => WordsIn(l.Text)) >= 2 * narrowWords - 1, "the box's paragraph was not broken again");
        var stated = Lines(new BoxStart { WidthPt = 150 }, Text(), new BoxEnd()).Where(l => l.X >= 200).ToList();
        Assert.Equal(narrowWords, stated.Max(l => WordsIn(l.Text)));
        var bordered = Text();
        bordered.BorderLeft = new Border(1, "#000000");
        var kept = Lines(bordered).Where(l => l.X >= 200).ToList();
        Assert.True(kept.Max(l => WordsIn(l.Text)) <= narrowWords, "a bordered paragraph keeps its width");
    }

    private static Section WithBlocks(Section section, params IDocElement[] blocks)
    {
        section.Children.AddRange(blocks);
        return section;
    }

    /// <summary>A 150 pt area at x = 36, 100 pt tall, then a 300 pt one at x = 250.</summary>
    private static Section NarrowThenWide()
    {
        var section = new Section();
        section.ContentAreas.Add(new ContentArea { LeftPt = 36, TopPt = AreaTop, WidthPt = 150, HeightPt = 100 });
        section.ContentAreas.Add(new ContentArea { LeftPt = 250, TopPt = AreaTop, WidthPt = 300, HeightPt = 500 });
        return section;
    }

    /// <summary>Ten 20 pt paragraphs: half in the narrow area, half in the wide one.</summary>
    private static IEnumerable<IDocElement> TenLines()
        => Enumerable.Range(1, 10).Select(i => (IDocElement)new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "BOXED" + i + "X", FontSize = 10 } } });

    /// <summary>The x of every vertical stroke drawn.</summary>
    private static List<double> VerticalStrokes(byte[] pdf)
        => PdfContentProbe.StrokedSegments(PdfContentProbe.DecodeStreams(pdf)).Where(s => !s.IsHorizontal).Select(s => s.X0).ToList();

    /// <summary>
    /// A bordered box of no width of its own, open across a break from the 150 pt area into the 300 pt
    /// one, takes the wide area's width there: its right border at the wide area's right edge, where in
    /// the narrow area it is at the narrow one's. A box of a stated 120 pt, the control, keeps its width in both.
    /// </summary>
    [Fact]
    public void ABoxOfNoWidth_TakesTheWidthOfTheAreaItEnters()
    {
        var edge = new Border(1, "#000000");
        byte[] Boxed(double width)
        {
            var section = NarrowThenWide();
            section.Children.Add(new BoxStart { WidthPt = width, BorderLeft = edge, BorderRight = edge, BorderTop = edge, BorderBottom = edge });
            section.Children.AddRange(TenLines());
            section.Children.Add(new BoxEnd());
            return Pdf(section);
        }

        var free = VerticalStrokes(Boxed(0));
        Assert.Contains(free, x => Math.Abs(x - (36 + 150 - 0.5)) < 0.6);
        Assert.Contains(free, x => Math.Abs(x - (250 + 300 - 0.5)) < 0.6);
        Assert.Contains(free, x => Math.Abs(x - (250 + 0.5)) < 0.6);

        var stated = VerticalStrokes(Boxed(120));
        Assert.Contains(stated, x => Math.Abs(x - (36 + 120 - 0.5)) < 0.6);
        Assert.Contains(stated, x => Math.Abs(x - (250 + 120 - 0.5)) < 0.6);
        Assert.DoesNotContain(stated, x => Math.Abs(x - (250 + 300 - 0.5)) < 0.6);
    }

    /// <summary>A box inside a padded box takes its parent's new band: its right border 20 pt in from the wide area's edge.</summary>
    [Fact]
    public void ANestedBox_TakesItsParentsNewBand()
    {
        var edge = new Border(1, "#000000");
        var section = NarrowThenWide();
        section.Children.Add(new BoxStart { PaddingRight = 20 });
        section.Children.Add(new BoxStart { BorderRight = edge });
        section.Children.AddRange(TenLines());
        section.Children.Add(new BoxEnd());
        section.Children.Add(new BoxEnd());

        var strokes = VerticalStrokes(Pdf(section));
        Assert.Contains(strokes, x => Math.Abs(x - (36 + 150 - 20 - 0.5)) < 0.6);
        Assert.Contains(strokes, x => Math.Abs(x - (250 + 300 - 20 - 0.5)) < 0.6);
    }

    /// <summary>
    /// A table of no width of its own whose rows continue from the 150 pt area into the 300 pt one has
    /// its columns resolved again there: a row's two shaded cells span the wide area, where in the narrow
    /// one they span it. Its rows, whose text wraps in the narrow area's columns, are measured again at
    /// the wide ones — the repeated header row too — so the wide area's rows are one line high and
    /// follow one another with no gap. A centred table of a stated 120 pt, the control, keeps its width,
    /// centred in each area.
    /// </summary>
    [Fact]
    public void ATableOfNoWidth_TakesTheWidthOfTheAreaItEnters()
    {
        byte[] Rows(double? width)
        {
            var table = new Table { WidthPt = width, Alignment = width == null ? null : TextAlignment.Center, BorderWidth = 0, SpacingAfter = 0, RepeatHeaderRow = true, MinRowHeightPt = 0 };
            for (int i = 0; i <= 10; i++)
            {
                var row = new TableRow { HeightPt = 20, IsHeader = i == 0 };
                foreach (string side in new[] { "A", "B" })
                {
                    var cell = new TableCell { BackgroundColor = side == "A" ? "#FF0000" : "#00FF00" };
                    cell.Paragraphs.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = (i == 0 ? "HEAD" : "ROW" + i) + side + " has words to wrap", FontSize = 8 } } });
                    row.Cells.Add(cell);
                }
                table.Rows.Add(row);
            }
            var section = NarrowThenWide();
            section.Children.Add(table);
            return Pdf(section);
        }
        // Each green (second-column) fill: its right edge, its bottom and its height.
        List<(double Right, double Bottom, double Height)> Green(byte[] pdf) => System.Text.RegularExpressions.Regex
            .Matches(PdfContentProbe.DecodeStreams(pdf), @"0 1 0 rg\s+(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re")
            .Select(m =>
            {
                double N(int g) => double.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
                return (N(1) + N(3), N(2), N(4));
            })
            .ToList();

        var free = Green(Rows(null));
        var narrow = free.Where(f => Math.Abs(f.Right - (36 + 150)) < 1.5).ToList();
        var wide = free.Where(f => Math.Abs(f.Right - (250 + 300)) < 1.5).OrderByDescending(f => f.Bottom).ToList();
        Assert.Equal(free.Count, narrow.Count + wide.Count);
        Assert.True(narrow.Count > 0 && narrow.All(f => f.Height > 21), "the narrow area's rows are not two lines high");
        Assert.True(wide.Count >= 3, $"{wide.Count} rows reached the wide area");
        Assert.All(wide, f => Assert.Equal(20, f.Height, 1));                      // the repeated header among them
        for (int k = 1; k < wide.Count && wide[k].Bottom > wide[0].Bottom - 400; k++)
            Assert.Equal(20, wide[k - 1].Bottom - wide[k].Bottom, 1);              // no gap where the table moved on

        var stated = Green(Rows(120));
        double narrowLeft = 36 + (150 - 120) / 2.0, wideLeft = 250 + (300 - 120) / 2.0;
        Assert.All(stated, f => Assert.True(Math.Abs(f.Right - (narrowLeft + 120)) < 1.5 || Math.Abs(f.Right - (wideLeft + 120)) < 1.5, $"a cell ends at {f.Right:F1}"));
        Assert.Contains(stated, f => Math.Abs(f.Right - (wideLeft + 120)) < 1.5);
    }

    /// <summary>
    /// A paragraph of 25 lines 20 pt apart: ten fill the left column, ten the right — the lines that
    /// continue there drawn at its left edge, from its top — and the last five the next page's left.
    /// </summary>
    [Fact]
    public void AParagraphPoursIntoTheNextArea_ThenTheNextPage()
    {
        var section = TwoColumns();
        var p = new Paragraph { LineSpacingPt = 20, SpacingAfter = 0 };
        for (int i = 1; i <= 25; i++)
        {
            if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "LINE" + i + "X", FontSize = 10 });
        }
        section.Children.Add(p);
        byte[] pdf = Pdf(section);

        var runs = Runs(Marks(pdf, "LINE", 25));
        Assert.Equal(new[] { LeftColumn, RightColumn, LeftColumn }, runs.Select(r => Math.Round(r.X)).ToArray());
        Assert.Equal(new[] { 10, 10, 5 }, runs.Select(r => r.Count).ToArray());
        // Each region's first line at the same height: the right column starts at its own top.
        Assert.Equal(runs[0].FirstY, runs[1].FirstY, 1);
        Assert.Equal(runs[0].FirstY, runs[2].FirstY, 1);
        Assert.InRange(runs[0].FirstY, section.PageHeightPt - AreaTop - 20, section.PageHeightPt - AreaTop);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>The control: with no content areas the same paragraph keeps to the margins, one column.</summary>
    [Fact]
    public void NoContentAreas_TheMarginsAreTheOneRegion()
    {
        var section = new Section();
        var p = new Paragraph { LineSpacingPt = 20, SpacingAfter = 0 };
        for (int i = 1; i <= 25; i++)
        {
            if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "LINE" + i + "X", FontSize = 10 });
        }
        section.Children.Add(p);
        byte[] pdf = Pdf(section);

        var runs = Runs(Marks(pdf, "LINE", 25));
        Assert.Single(runs);
        Assert.Equal(section.MarginLeft, runs[0].X, 1);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>Paragraphs one per line, each a block of its own: the break between blocks moves to the next area too.</summary>
    [Fact]
    public void BlocksBetweenBreaks_StartInTheAreaTheFlowIsIn()
    {
        var section = TwoColumns();
        for (int i = 1; i <= 25; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "PARA" + i + "X", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        var runs = Runs(Marks(pdf, "PARA", 25));
        Assert.Equal(new[] { LeftColumn, RightColumn, LeftColumn }, runs.Select(r => Math.Round(r.X)).ToArray());
        Assert.Equal(new[] { 10, 10, 5 }, runs.Select(r => r.Count).ToArray());
    }

    /// <summary>A table's rows go on in the next area at its left edge, as a paragraph's lines do.</summary>
    [Fact]
    public void ATablesRowsContinueInTheNextArea()
    {
        var section = TwoColumns();
        var table = new Table { BorderWidth = 0, SpacingAfter = 0 };
        for (int i = 1; i <= 25; i++)
        {
            var row = new TableRow { HeightPt = 20 };
            var cell = new TableCell();
            cell.Paragraphs.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = "ROW" + i + "X", FontSize = 10 } } });
            row.Cells.Add(cell);
            table.Rows.Add(row);
        }
        section.Children.Add(table);
        byte[] pdf = Pdf(section);

        var marks = Marks(pdf, "ROW", 25);
        var runs = Runs(marks);
        Assert.Equal(3, runs.Count);
        // The cell's text is inset from the table's edge by the same amount in each region.
        Assert.Equal(RightColumn - LeftColumn, runs[1].X - runs[0].X, 1);
        Assert.Equal(runs[0].X, runs[2].X, 1);
        Assert.Equal(runs[0].FirstY, runs[1].FirstY, 1);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// A box open across the break moves with the flow: its blocks in the right column are at the
    /// right column's edge plus the box's own padding, and its border is drawn there too.
    /// </summary>
    [Fact]
    public void AnOpenBoxMovesWithTheFlowIntoTheNextArea()
    {
        var section = TwoColumns();
        var edge = new Border(1, "#000000");
        section.Children.Add(new BoxStart { PaddingLeft = 10, BorderLeft = edge, BorderRight = edge, BorderTop = edge, BorderBottom = edge });
        for (int i = 1; i <= 15; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "INBOX" + i + "X", FontSize = 10 } } });
        section.Children.Add(new BoxEnd());
        byte[] pdf = Pdf(section);

        var runs = Runs(Marks(pdf, "INBOX", 15));
        Assert.Equal(2, runs.Count);
        Assert.Equal(LeftColumn + 11, runs[0].X, 1);    // the 1 pt border and the 10 pt padding
        Assert.Equal(RightColumn + 11, runs[1].X, 1);

        // The fragment in the right column has its left edge there: a vertical stroke at x = 326.5.
        string content = PdfContentProbe.DecodeStreams(pdf);
        var verticals = PdfContentProbe.StrokedSegments(content).Where(s => !s.IsHorizontal).ToList();
        Assert.Contains(verticals, s => Math.Abs(s.X0 - (RightColumn + 0.5)) < 0.6);
        Assert.Contains(verticals, s => Math.Abs(s.X0 - (LeftColumn + 0.5)) < 0.6);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// A footnote whose mark is in the left column is drawn at that column's foot, at its left edge —
    /// not at the page's bottom margin, 72 pt up, where a page without areas draws it.
    /// </summary>
    [Fact]
    public void AFootnoteIsDrawnAtTheFootOfItsArea()
    {
        var section = TwoColumns();
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = "NOTEBODYX", FontSize = 8 });
        section.Children.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = "MARKEDX", FontSize = 10 }, note } });
        byte[] pdf = Pdf(section);

        var body = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)).Single(t => t.Text.Contains("NOTEBODYX", StringComparison.Ordinal));
        double areaBottom = section.PageHeightPt - AreaTop - AreaHeight;
        Assert.InRange(body.Y, areaBottom, areaBottom + 30);
        Assert.InRange(body.X, LeftColumn, LeftColumn + 20);
    }

    /// <summary>
    /// The flow goes on into the right column after the footnote's mark: the note stays at the
    /// left column's foot and edge, drawn as that column filled, not with the right column's at the page's end.
    /// </summary>
    [Fact]
    public void AFootnoteStaysWithItsAreaWhenTheFlowMovesOn()
    {
        var section = TwoColumns();
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = "NOTEBODYX", FontSize = 8 });
        section.Children.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = "MARKEDX", FontSize = 10 }, note } });
        for (int i = 1; i <= 12; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "PARA" + i + "X", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        Assert.Equal(RightColumn, Drawn(pdf, "PARA12X").X, 1);   // the flow did move on
        var body = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)).Single(t => t.Text.Contains("NOTEBODYX", StringComparison.Ordinal));
        double areaBottom = section.PageHeightPt - AreaTop - AreaHeight;
        Assert.InRange(body.Y, areaBottom, areaBottom + 30);
        Assert.InRange(body.X, LeftColumn, LeftColumn + 20);
    }

    /// <summary>
    /// A note too long for the left column's foot is cut there, and its rest continues at the right
    /// column's foot — in at most half that column, as on a new page — not on the next page.
    /// </summary>
    [Fact]
    public void AFootnoteCutAtAnAreasFoot_ContinuesAtTheNextAreasFoot()
    {
        var section = TwoColumns();
        for (int i = 1; i <= 6; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "FILL" + i + "X", FontSize = 10 } } });
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = string.Join(" ", Enumerable.Range(1, 400).Select(i => "N" + i + "X")), FontSize = 8 });
        section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "MARKEDX", FontSize = 10 }, note } });
        for (int i = 1; i <= 20; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "AFTER" + i + "X", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
        Assert.Equal(LeftColumn, drawn.Single(t => t.Text == "MARKEDX").X, 1);
        // The first word the right column continues: the left column's foot held the words before it.
        var words = drawn.Where(t => t.Text.Length > 2 && t.Text[0] == 'N' && t.Text.EndsWith("X", StringComparison.Ordinal)).ToList();
        int firstRight = words.FindIndex(t => Math.Abs(t.X - RightColumn) < 0.5);
        Assert.True(firstRight > 0, "the note does not continue in the right column");
        Assert.All(words.Take(firstRight), t => Assert.InRange(t.X, LeftColumn, LeftColumn + 250));
        // On the first page: drawn before the flow's first line on the second (drawing order is page order).
        Assert.True(drawn.IndexOf(words[firstRight]) < drawn.FindIndex(t => t.Text == "AFTER6X"), "the note continues on the next page, not in the right column");
        Assert.Equal(RightColumn, drawn.Single(t => t.Text == "AFTER5X").X, 1);
        // Its first line no higher than half the column above the column's foot.
        double areaBottom = section.PageHeightPt - AreaTop - AreaHeight;
        Assert.InRange(words[firstRight].Y, areaBottom, areaBottom + AreaHeight / 2);
    }

    // ---------------------------------------------------------------- every site a block breaks at

    /// <summary>Nine 20 pt paragraphs: 180 of the left column's 200 pt, so the next block of more than 20 pt does not fit.</summary>
    private static void NineLines(Section section)
    {
        for (int i = 1; i <= 9; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "FILL" + i + "X", FontSize = 10 } } });
    }

    private static PdfContentProbe.DrawnText Drawn(byte[] pdf, string text)
        => PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)).Single(t => t.Text == text);

    /// <summary>
    /// A box open across two breaks: into the right column, and then the next page's left — where it
    /// comes back, its blocks at the left column's edge again.
    /// </summary>
    [Fact]
    public void AnOpenBoxComesBackToTheLeftOnTheNextPage()
    {
        var section = TwoColumns();
        section.Children.Add(new BoxStart { PaddingLeft = 10 });
        for (int i = 1; i <= 25; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "INBOX" + i + "X", FontSize = 10 } } });
        section.Children.Add(new BoxEnd());
        byte[] pdf = Pdf(section);

        var runs = Runs(Marks(pdf, "INBOX", 25));
        Assert.Equal(new[] { LeftColumn + 10, RightColumn + 10, LeftColumn + 10 }, runs.Select(r => Math.Round(r.X, 1)).ToArray());
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>A page break the model asks for ends the page: the paragraph after it starts the next page's left column, not the right.</summary>
    [Fact]
    public void APageBreakBefore_EndsThePage_NotJustTheArea()
    {
        var section = TwoColumns();
        section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "BEFOREX", FontSize = 10 } } });
        section.Children.Add(new Paragraph { PageBreakBefore = true, Runs = { new TextRun { Text = "AFTERX", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        Assert.Equal(LeftColumn, Drawn(pdf, "AFTERX").X, 1);
        Assert.Equal(Drawn(pdf, "BEFOREX").Y, Drawn(pdf, "AFTERX").Y, 1);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>An inline page break likewise: the line after it on the next page, in its first area.</summary>
    [Fact]
    public void AnInlinePageBreak_EndsThePage_NotJustTheArea()
    {
        var section = TwoColumns();
        section.Children.Add(new Paragraph
        {
            Runs = { new TextRun { Text = "BEFOREX", FontSize = 10 }, new PageBreak(), new TextRun { Text = "AFTERX", FontSize = 10 } },
        });
        byte[] pdf = Pdf(section);

        Assert.Equal(LeftColumn, Drawn(pdf, "AFTERX").X, 1);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// A page break asked for at the top of the right column — where a block kept together moved it —
    /// still ends the page: the top of a later area is not the top of a page.
    /// </summary>
    [Fact]
    public void APageBreakAtTheTopOfALaterArea_StillEndsThePage()
    {
        var section = TwoColumns();
        NineLines(section);
        var kept = new Paragraph { KeepTogether = true, PageBreakBefore = true, LineSpacingPt = 20, SpacingAfter = 0 };
        kept.Runs.Add(new TextRun { Text = "KEPT1X", FontSize = 10 });
        kept.Runs.Add(new LineBreak());
        kept.Runs.Add(new TextRun { Text = "KEPT2X", FontSize = 10 });
        section.Children.Add(kept);
        byte[] pdf = Pdf(section);

        Assert.Equal(LeftColumn, Drawn(pdf, "KEPT1X").X, 1);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>A shaded paragraph that does not fit the rest of the left column is drawn whole in the right one, its text there too.</summary>
    [Fact]
    public void AShadedParagraphMovedToTheNextArea_DrawsItsTextThere()
    {
        var section = TwoColumns();
        NineLines(section);
        var boxed = new Paragraph { Shading = "#DDDDDD", LineSpacingPt = 20, SpacingAfter = 0, BorderRight = new Border(1, "#000000") };
        boxed.Runs.Add(new TextRun { Text = "SHADED1X", FontSize = 10 });
        boxed.Runs.Add(new LineBreak());
        boxed.Runs.Add(new TextRun { Text = "SHADED2X", FontSize = 10 });
        section.Children.Add(boxed);
        byte[] pdf = Pdf(section);

        Assert.Equal(RightColumn, Drawn(pdf, "SHADED1X").X, 1);
        Assert.Equal(RightColumn, Drawn(pdf, "SHADED2X").X, 1);
        // Its right border at the right column's edge plus the 2 pt inset, not the left column's.
        var verticals = PdfContentProbe.StrokedSegments(PdfContentProbe.DecodeStreams(pdf)).Where(s => !s.IsHorizontal).ToList();
        Assert.Contains(verticals, s => Math.Abs(s.X0 - (RightColumn + 250 + Paragraph.DefaultBoxInsetPt)) < 1.5);
        Assert.DoesNotContain(verticals, s => Math.Abs(s.X0 - (LeftColumn + 250 + Paragraph.DefaultBoxInsetPt)) < 1.5);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>An image that does not fit the rest of the left column is drawn at the right column's edge.</summary>
    [Fact]
    public void AnImageMovedToTheNextArea_IsDrawnThere()
    {
        var section = TwoColumns();
        NineLines(section);
        var withImage = new Paragraph { SpacingAfter = 0 };
        withImage.Runs.Add(new ImageRun { Data = ExifJpeg.Build(8, 8, 1), WidthPt = 100, HeightPt = 50 });
        section.Children.Add(withImage);
        byte[] pdf = Pdf(section);

        var placed = System.Text.RegularExpressions.Regex.Match(PdfContentProbe.DecodeStreams(pdf), @"100 0 0 50 ([\d.]+) ([\d.]+) cm");
        Assert.True(placed.Success, "the image's matrix was not found");
        Assert.Equal(RightColumn, double.Parse(placed.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// The widows rule is planned again in each area: of 21 lines, ten fill the left column, and the
    /// right column leaves two for the next page instead of one.
    /// </summary>
    [Fact]
    public void WidowsArePlannedAgainInEachArea()
    {
        var section = TwoColumns();
        var p = new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Widows = 2 };
        for (int i = 1; i <= 21; i++)
        {
            if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "LINE" + i + "X", FontSize = 10 });
        }
        section.Children.Add(p);
        byte[] pdf = Pdf(section);

        var runs = Runs(Marks(pdf, "LINE", 21));
        Assert.Equal(new[] { 10, 9, 2 }, runs.Select(r => r.Count).ToArray());
    }

    /// <summary>A floated box that does not fit the rest of the left column floats at the right column's edge.</summary>
    [Fact]
    public void AFloatedBoxMovedToTheNextArea_FloatsThere()
    {
        var section = TwoColumns();
        NineLines(section);
        section.Children.Add(new BoxStart { Float = FloatSide.Left, FloatWidth = 100 });
        for (int i = 1; i <= 3; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "FLOAT" + i + "X", FontSize = 10 } } });
        section.Children.Add(new BoxEnd());
        byte[] pdf = Pdf(section);

        Assert.Equal(RightColumn, Drawn(pdf, "FLOAT1X").X, 1);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// A box opened where its top padding no longer fits moves to the right column before it opens —
    /// its blocks at the right column's edge plus its insets.
    /// </summary>
    [Fact]
    public void ABoxOpenedAtTheEndOfAnArea_OpensInTheNextOne()
    {
        var section = TwoColumns();
        for (int i = 1; i <= 10; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "FILL" + i + "X", FontSize = 10 } } });
        section.Children.Add(new BoxStart { PaddingTop = 10, PaddingLeft = 10 });
        section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "OPENEDX", FontSize = 10 } } });
        section.Children.Add(new BoxEnd());
        byte[] pdf = Pdf(section);

        Assert.Equal(LeftColumn, Drawn(pdf, "FILL10X").X, 1);
        Assert.Equal(RightColumn + 10, Drawn(pdf, "OPENEDX").X, 1);
        Assert.Equal(1, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>The header and footer stay between the margins, whatever area the flow is in.</summary>
    [Fact]
    public void TheHeaderAndFooterKeepToTheMargins()
    {
        var section = TwoColumns();
        section.Header = new Header();
        section.Header.Paragraphs.Add(new Paragraph { Runs = { new TextRun { Text = "HEADERX", FontSize = 10 } } });
        section.Footer = new Footer();
        section.Footer.Paragraphs.Add(new Paragraph { Runs = { new TextRun { Text = "FOOTERX", FontSize = 10 } } });
        for (int i = 1; i <= 15; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "PARA" + i + "X", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        Assert.Equal(section.MarginLeft, Drawn(pdf, "HEADERX").X, 1);
        Assert.Equal(section.MarginLeft, Drawn(pdf, "FOOTERX").X, 1);
        Assert.True(Drawn(pdf, "FOOTERX").Y < 150, "the footer is not at the foot of the page");
        Assert.Equal(RightColumn, Drawn(pdf, "PARA15X").X, 1);   // the flow did reach the right column
    }

    /// <summary>
    /// A block placed on the page with no width reaches the right margin — the page's, not the right
    /// edge of the area the flow ended in (576 pt here, past the margin at the page's width less 72).
    /// </summary>
    [Fact]
    public void APlacedBlockWithNoWidth_ReachesTheRightMargin()
    {
        var section = TwoColumns();
        for (int i = 1; i <= 15; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "PARA" + i + "X", FontSize = 10 } } });
        section.Children.Add(new Paragraph
        {
            Alignment = TextAlignment.Right,
            Placement = new AbsolutePlacement { LeftPt = 100, TopPt = 500 },
            Runs = { new TextRun { Text = "RIGHTX", FontSize = 10 } },
        });
        byte[] pdf = Pdf(section);

        double margin = section.PageWidthPt - section.MarginRight;
        Assert.InRange(Drawn(pdf, "RIGHTX").X, margin - 60, margin - 20);
    }

    /// <summary>A right-aligned line ends at its area's right edge, 286 pt in the left column, not at the page's right margin.</summary>
    [Fact]
    public void ARightAlignedLine_EndsAtItsAreasRightEdge()
    {
        var section = TwoColumns();
        section.Children.Add(new Paragraph { Alignment = TextAlignment.Right, Runs = { new TextRun { Text = "ENDX", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        Assert.InRange(Drawn(pdf, "ENDX").X, LeftColumn + 250 - 40, LeftColumn + 250 - 15);
    }

    /// <summary>A page break taken in the right column brings the paragraph back to the next page's left column.</summary>
    [Fact]
    public void APageBreakFromALaterArea_ComesBackToTheFirst()
    {
        var section = TwoColumns();
        for (int i = 1; i <= 15; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "PARA" + i + "X", FontSize = 10 } } });
        section.Children.Add(new Paragraph { PageBreakBefore = true, Runs = { new TextRun { Text = "AFTERX", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        Assert.Equal(RightColumn, Drawn(pdf, "PARA15X").X, 1);
        Assert.Equal(LeftColumn, Drawn(pdf, "AFTERX").X, 1);
        Assert.Equal(2, PdfContentProbe.CountPages(pdf));
    }

    /// <summary>
    /// An image after a paragraph's lines that went on into the right column is drawn where they
    /// went: each piece of a paragraph starts in the area the pieces before it reached.
    /// </summary>
    [Fact]
    public void AnImageAfterLinesThatMovedOn_IsDrawnInTheirArea()
    {
        var section = TwoColumns();
        var p = new Paragraph { LineSpacingPt = 20, SpacingAfter = 0 };
        for (int i = 1; i <= 12; i++)
        {
            if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "LINE" + i + "X", FontSize = 10 });
        }
        p.Runs.Add(new ImageRun { Data = ExifJpeg.Build(8, 8, 1), WidthPt = 100, HeightPt = 50 });
        section.Children.Add(p);
        byte[] pdf = Pdf(section);

        Assert.Equal(RightColumn, Drawn(pdf, "LINE12X").X, 1);
        var placed = System.Text.RegularExpressions.Regex.Match(PdfContentProbe.DecodeStreams(pdf), @"100 0 0 50 ([\d.]+) ([\d.]+) cm");
        Assert.True(placed.Success, "the image's matrix was not found");
        Assert.Equal(RightColumn, double.Parse(placed.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
    }

    /// <summary>A content area's measurements are validated with the rest of the model, the area named in the refusal.</summary>
    [Theory]
    [InlineData("LeftPt")]
    [InlineData("TopPt")]
    [InlineData("WidthPt")]
    [InlineData("HeightPt")]
    public void AContentAreasMeasurement_IsRefusedWhenNotFinite(string property)
    {
        var section = TwoColumns();
        ContentArea area = section.ContentAreas[1];
        switch (property)
        {
            case "LeftPt": area.LeftPt = double.NaN; break;
            case "TopPt": area.TopPt = double.PositiveInfinity; break;
            case "WidthPt": area.WidthPt = double.NaN; break;
            default: area.HeightPt = double.NegativeInfinity; break;
        }
        section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "TEXTX" } } });
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Pdf(section));
        Assert.Contains("Sections[0].ContentAreas[1]." + property, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A size is refused below zero; a position may be negative — the control beside it.</summary>
    [Fact]
    public void AContentAreasSize_IsRefusedBelowZero_ItsPositionIsNot()
    {
        var section = TwoColumns();
        section.ContentAreas[0].WidthPt = -1;
        section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "TEXTX" } } });
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Pdf(section));
        Assert.Contains("Sections[0].ContentAreas[0].WidthPt", ex.Message, StringComparison.Ordinal);

        var accepted = TwoColumns();
        accepted.ContentAreas[0].LeftPt = -5;
        accepted.Children.Add(new Paragraph { Runs = { new TextRun { Text = "TEXTX" } } });
        Assert.Equal(-5, Drawn(Pdf(accepted), "TEXTX").X, 1);
    }

    /// <summary>A null entry among the content areas is passed over: the flow uses the others.</summary>
    [Fact]
    public void ANullContentArea_IsPassedOver()
    {
        var section = TwoColumns();
        section.ContentAreas.Insert(0, null!);
        for (int i = 1; i <= 15; i++)
            section.Children.Add(new Paragraph { LineSpacingPt = 20, SpacingAfter = 0, Runs = { new TextRun { Text = "PARA" + i + "X", FontSize = 10 } } });
        byte[] pdf = Pdf(section);

        Assert.Equal(LeftColumn, Drawn(pdf, "PARA1X").X, 1);
        Assert.Equal(RightColumn, Drawn(pdf, "PARA15X").X, 1);
    }
}
