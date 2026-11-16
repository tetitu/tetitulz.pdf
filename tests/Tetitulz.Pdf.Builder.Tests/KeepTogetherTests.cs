// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.KeepTogether"/>, <see cref="Paragraph.KeepWithNext"/>,
/// <see cref="Table.KeepTogether"/> and which rows <see cref="Table.RepeatHeaderRow"/> repeats.
/// </summary>
/// <remarks>
/// <para><b>Page placement is read from drawing order.</b> The decoded content is every page in
/// order, and within a page the flow only moves down: a word drawn after another and <i>below</i>
/// it is on the same page, one drawn after it and <i>above</i> it started a new one.</para>
/// <para><b>The filler is found, not guessed</b>: each test first finds how many filler paragraphs
/// make the unflagged control split its two words across a page, and fails if none does.</para>
/// </remarks>
public class KeepTogetherTests
{
    private static Document Doc(int fillers, params IDocElement[] tail)
    {
        var section = new Section();
        for (int i = 1; i <= fillers; i++)
            section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "filler" + i } } });
        foreach (var el in tail) section.Children.Add(el);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>True when <paramref name="second"/> is drawn on a later page than <paramref name="first"/>.</summary>
    private static bool Split(Document doc, string first, string second)
    {
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        double a = PdfContentProbe.YOf(content, first), b = PdfContentProbe.YOf(content, second);
        Assert.False(double.IsNaN(a), $"{first} was not drawn");
        Assert.False(double.IsNaN(b), $"{second} was not drawn");
        return b > a;
    }

    private static int FillersThatSplit(Func<int, Document> control, string first, string second)
    {
        for (int n = 1; n <= 80; n++)
            if (Split(control(n), first, second)) return n;
        Assert.Fail($"no filler count split {first} from {second}; the test proves nothing");
        return -1;
    }

    private static Paragraph FourLines(bool keep)
    {
        var p = new Paragraph { KeepTogether = keep };
        p.Runs.Add(new TextRun { Text = "KTOP" });
        p.Runs.Add(new LineBreak()); p.Runs.Add(new TextRun { Text = "mid" });
        p.Runs.Add(new LineBreak()); p.Runs.Add(new TextRun { Text = "mid" });
        p.Runs.Add(new LineBreak()); p.Runs.Add(new TextRun { Text = "KEND" });
        return p;
    }

    [Fact]
    public void AParagraphKeptTogether_StartsOnTheNextPageInsteadOfStraddling()
    {
        int n = FillersThatSplit(f => Doc(f, FourLines(keep: false)), "KTOP", "KEND");

        Assert.False(Split(Doc(n, FourLines(keep: true)), "KTOP", "KEND"),
            "the kept paragraph still straddled the page boundary");
        // And it moved: KTOP is now on a later page than the last filler before it.
        Assert.True(Split(Doc(n, FourLines(keep: true)), "filler" + n, "KTOP"),
            "the kept paragraph did not move to the next page");
    }

    private static Table LongTable(int rows)
    {
        var t = new Table();
        for (int i = 1; i <= rows; i++)
        {
            var row = new TableRow();
            var cell = new TableCell();
            cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "ROW" + i + "X" } } });
            row.Cells.Add(cell);
            t.Rows.Add(row);
        }
        return t;
    }

    /// <summary>
    /// A paragraph kept with a table longer than a page needs only the table's first row beside it:
    /// the heading moves to the page the table begins on.
    /// </summary>
    [Fact]
    public void AParagraphKeptWithALongTable_MovesToThePageTheTableStartsOn()
    {
        Paragraph Heading(bool keep) => new Paragraph { KeepWithNext = keep, Runs = { new TextRun { Text = "HEADING" } } };

        int n = FillersThatSplit(f => Doc(f, Heading(false), LongTable(80)), "HEADING", "ROW1X");

        Assert.False(Split(Doc(n, Heading(true), LongTable(80)), "HEADING", "ROW1X"),
            "the heading was left on the page before its table");
    }

    /// <summary>
    /// A keep chain passes into a box (2026-10-19; it stopped at the box, unkept): a heading kept with the box after
    /// it moves to the page the box's first block starts on — the box's top margin, border and padding counted on
    /// the way. The control, unkept, leaves the heading behind.
    /// </summary>
    [Fact]
    public void AParagraphKeptWithABox_MovesToThePageTheBoxStartsOn()
    {
        Paragraph Heading(bool keep) => new Paragraph { KeepWithNext = keep, Runs = { new TextRun { Text = "HEADING" } } };
        IDocElement[] Tail(bool keep) => new IDocElement[]
        {
            Heading(keep),
            new BoxStart { MarginTop = 30, PaddingTop = 10, BorderTop = new Border(2, "#000000") },
            new Paragraph { Runs = { new TextRun { Text = "BOXEDX" } } },
            new BoxEnd(),
        };

        int n = FillersThatSplit(f => Doc(f, Tail(false)), "HEADING", "BOXEDX");
        Assert.False(Split(Doc(n, Tail(true)), "HEADING", "BOXEDX"), "the heading was left on the page before the box it is kept with");
    }

    /// <summary>
    /// A box kept with the paragraph after it (<see cref="BoxStart.KeepWithNext"/>, 2026-10-19) moves whole to the page
    /// that paragraph starts on; its end continues the chain, so the paragraph after it does not decide again. The
    /// control, unkept, leaves the box behind.
    /// </summary>
    [Fact]
    public void ABoxKeptWithNext_StaysWithTheParagraphAfterIt()
    {
        IDocElement[] Tail(bool keep) => new IDocElement[]
        {
            new BoxStart { KeepWithNext = keep, PaddingTop = 4, PaddingBottom = 4 },
            new Paragraph { Runs = { new TextRun { Text = "BOXTOP" } } },
            new Paragraph { Runs = { new TextRun { Text = "BOXEND" } } },
            new BoxEnd(),
            new Paragraph { Runs = { new TextRun { Text = "AFTERX" } } },
        };

        int n = FillersThatSplit(f => Doc(f, Tail(false)), "BOXEND", "AFTERX");
        Assert.False(Split(Doc(n, Tail(true)), "BOXEND", "AFTERX"), "the box was left on the page before the paragraph it is kept with");
        Assert.False(Split(Doc(n, Tail(true)), "BOXTOP", "BOXEND"), "the kept box split");
    }

    /// <summary>
    /// The block after a box kept with the next block is in the box's chain, decided where the chain starts and not
    /// again: a box taller than a page cannot be kept, so its chain flows as though unkept — and the paragraph after
    /// it, kept with its own next, stays under the box's last line rather than jumping to a page of its own. The
    /// control, the box unkept, lets that paragraph decide, and at this length it jumps.
    /// </summary>
    [Fact]
    public void TheBlockAfterAKeptBox_IsInItsChain_NotDecidedAgain()
    {
        IDocElement[] Tail(int lines, bool keep)
        {
            var list = new System.Collections.Generic.List<IDocElement> { new BoxStart { KeepWithNext = keep } };
            for (int i = 1; i <= lines; i++) list.Add(new Paragraph { Runs = { new TextRun { Text = "line" + i } } });
            list.Add(new Paragraph { Runs = { new TextRun { Text = "BOXEND" } } });
            list.Add(new BoxEnd());
            list.Add(new Paragraph { KeepWithNext = true, Runs = { new TextRun { Text = "AFTERX" } } });
            list.Add(new Paragraph { Runs = { new TextRun { Text = "AFTER2" } } });
            return list.ToArray();
        }

        int n = -1;
        for (int lines = 50; lines <= 160 && n < 0; lines++)
            if (Split(Doc(0, Tail(lines, keep: false)), "BOXEND", "AFTERX")) n = lines;
        Assert.True(n > 0, "no box length let the paragraph after it jump; the test proves nothing");
        Assert.False(Split(Doc(0, Tail(n, keep: true)), "BOXEND", "AFTERX"), "the block after the kept box decided again and left it");
    }

    /// <summary>
    /// A chain passes out of a box too: a paragraph kept with the next block, the last in its box, stays with the
    /// paragraph after the box. The control, unkept, leaves it behind.
    /// </summary>
    [Fact]
    public void AParagraphKeptWithNext_LastInItsBox_StaysWithTheBlockAfterTheBox()
    {
        IDocElement[] Tail(bool keep) => new IDocElement[]
        {
            new BoxStart { PaddingBottom = 8, BorderBottom = new Border(2, "#000000"), MarginBottom = 20 },
            new Paragraph { KeepWithNext = keep, Runs = { new TextRun { Text = "INNERX" } } },
            new BoxEnd(),
            new Paragraph { Runs = { new TextRun { Text = "AFTERX" } } },
        };

        int n = FillersThatSplit(f => Doc(f, Tail(false)), "INNERX", "AFTERX");
        Assert.False(Split(Doc(n, Tail(true)), "INNERX", "AFTERX"), "the box's last paragraph was left on the page before the block it is kept with");
    }

    /// <summary>
    /// A heading kept with a box that is itself kept together keeps the whole box beside it, not only the box's first
    /// block: the two move as one, and the box does not split.
    /// </summary>
    [Fact]
    public void AParagraphKeptWithABoxKeptTogether_MovesWithAllOfIt()
    {
        IDocElement[] Tail(bool keep) => new IDocElement[]
        {
            new Paragraph { KeepWithNext = keep, Runs = { new TextRun { Text = "HEADING" } } },
            new BoxStart { KeepTogether = keep },
            new Paragraph { Runs = { new TextRun { Text = "BOXTOP" } } },
            new Paragraph { Runs = { new TextRun { Text = "mid" } } },
            new Paragraph { Runs = { new TextRun { Text = "BOXEND" } } },
            new BoxEnd(),
        };

        int n = FillersThatSplit(f => Doc(f, Tail(false)), "BOXTOP", "BOXEND");
        Assert.False(Split(Doc(n, Tail(true)), "BOXTOP", "BOXEND"), "the kept box split");
        Assert.False(Split(Doc(n, Tail(true)), "HEADING", "BOXEND"), "the heading was left behind its box");
    }

    /// <summary>
    /// A box kept together with no least height (2026-10-19) moves whole by its content: it was judged by its least
    /// height alone, so a box of blocks with none was never moved. The control splits.
    /// </summary>
    [Fact]
    public void ABoxKeptTogether_MovesWholeByItsContent()
    {
        IDocElement[] Tail(bool keep) => new IDocElement[]
        {
            new BoxStart { KeepTogether = keep },
            new Paragraph { Runs = { new TextRun { Text = "BOXTOP" } } },
            new Paragraph { Runs = { new TextRun { Text = "mid" } } },
            new Paragraph { Runs = { new TextRun { Text = "BOXEND" } } },
            new BoxEnd(),
        };

        int n = FillersThatSplit(f => Doc(f, Tail(false)), "BOXTOP", "BOXEND");
        Assert.False(Split(Doc(n, Tail(true)), "BOXTOP", "BOXEND"), "the kept box still split");
    }

    /// <summary>
    /// A table kept together moves whole to the next page; the control table splits.
    /// </summary>
    [Fact]
    public void ATableKeptTogether_MovesWholeToTheNextPage()
    {
        Table Short(bool keep) { var t = LongTable(4); t.KeepTogether = keep; return t; }

        int n = FillersThatSplit(f => Doc(f, Short(false)), "ROW1X", "ROW4X");

        Assert.False(Split(Doc(n, Short(true)), "ROW1X", "ROW4X"), "the kept table still split");
    }

    /// <summary>
    /// A table kept with the paragraph after it moves to the page that paragraph starts on.
    /// </summary>
    [Fact]
    public void ATableKeptWithNext_StaysWithTheParagraphAfterIt()
    {
        Table Kept(bool keep) { var t = LongTable(3); t.KeepWithNext = keep; return t; }
        Paragraph After() => new Paragraph { Runs = { new TextRun { Text = "AFTERX" } } };

        int n = FillersThatSplit(f => Doc(f, Kept(false), After()), "ROW3X", "AFTERX");

        Assert.False(Split(Doc(n, Kept(true), After()), "ROW3X", "AFTERX"),
            "the table was left on the page before the paragraph it is kept with");
    }

    /// <summary>
    /// Only the header rows a table STARTS with repeat. A row marked a header further down is
    /// drawn once, where it stands — it used to be re-drawn above every later page break.
    /// </summary>
    [Fact]
    public void OnlyTheLeadingHeaderRowsRepeat()
    {
        var t = LongTable(90);
        t.RepeatHeaderRow = true;
        var head = new TableRow { IsHeader = true };
        var hc = new TableCell();
        hc.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "HEADX" } } });
        head.Cells.Add(hc);
        t.Rows.Insert(0, head);
        t.Rows[30].IsHeader = true;          // "ROW30X", mid-table

        byte[] pdf = Doc(0, t).ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);
        int pages = PdfContentProbe.CountPages(pdf);

        Assert.True(pages >= 3, "the table must run past a second page break for this to mean anything");
        Assert.Equal(pages, System.Text.RegularExpressions.Regex.Matches(content, @"\(HEADX\)").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(content, @"\(ROW30X\)"));
    }
}
