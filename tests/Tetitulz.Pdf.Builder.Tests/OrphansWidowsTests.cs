// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.Orphans"/> and <see cref="Paragraph.Widows"/>: the fewest lines of a
/// paragraph a page may end with, and begin with, where the paragraph breaks across a page.
/// </summary>
/// <remarks>
/// <para><b>Page placement is read from drawing order</b>, as in <see cref="KeepTogetherTests"/>:
/// within a page the flow only moves down, so a line drawn after another and <i>above</i> it
/// started a new page.</para>
/// <para><b>The push is found, not guessed</b>: a paragraph's space before it moves the
/// paragraph down the page, and each test first finds the push that makes the unconstrained
/// control break after the line it is about — or fails, proving nothing. Found by bisection: the
/// lines left before the break only fall as the push grows.</para>
/// </remarks>
public class OrphansWidowsTests
{
    /// <summary>No push paragraph: the paragraph starts the page.</summary>
    private const double AtTheTop = -1;

    private static Paragraph Lines(int count, int orphans = 1, int widows = 1)
    {
        var p = new Paragraph { Orphans = orphans, Widows = widows };
        for (int i = 1; i <= count; i++)
        {
            if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "LINE" + i + "X" });
        }
        return p;
    }

    private static Document Doc(double push, params IDocElement[] tail)
    {
        var section = new Section();
        if (push >= 0)
        {
            section.Children.Add(new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = "TOPX" } } });
            section.Children.Add(new Paragraph
            {
                SpacingBefore = push, SpacingAfter = 0, Runs = { new TextRun { Text = "PUSHX" } },
            });
        }
        foreach (var el in tail) section.Children.Add(el);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>
    /// How many of the paragraph's lines are on the page the push is on — 0 when even its first
    /// line is on a later page, and -1 when the push itself went over to the next page, which is
    /// too far to say anything.
    /// </summary>
    private static int LinesBeforeTheBreak(Document doc, int lines)
    {
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        if (content.Contains("(PUSHX)", System.StringComparison.Ordinal))
        {
            // At or above the first line: the top of the next page, where the first line's y recurs.
            if (Y(content, "PUSHX") >= Y(content, "TOPX")) return -1;
            if (Y(content, "LINE1X") > Y(content, "PUSHX")) return 0;
        }
        return LinesOnItsFirstPage(content, lines);
    }

    /// <summary>How many of the paragraph's lines share a page with its first line.</summary>
    private static int LinesOnItsFirstPage(Document doc, int lines) =>
        LinesOnItsFirstPage(PdfContentProbe.DecodeStreams(doc.ToPdf()), lines);

    private static int LinesOnItsFirstPage(string content, int lines)
    {
        for (int i = 1; i < lines; i++)
            if (Y(content, "LINE" + (i + 1) + "X") > Y(content, "LINE" + i + "X")) return i;
        return lines;
    }

    private static double Y(string content, string text)
    {
        double y = PdfContentProbe.YOf(content, text);
        Assert.False(double.IsNaN(y), $"{text} was not drawn");
        return y;
    }

    /// <summary>
    /// The push that makes the unconstrained paragraph keep exactly <paramref name="kept"/> lines
    /// on the first page. A push too far to say (-1) counts as too much.
    /// </summary>
    private static double PushLeaving(int kept, int lines, System.Func<Paragraph>? make = null)
    {
        var page = new Section();
        double lo = 0, hi = page.PageHeightPt - page.MarginTop - page.MarginBottom;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            int k = LinesBeforeTheBreak(Doc(mid, make?.Invoke() ?? Lines(lines)), lines);
            if (k == kept) return mid;
            if (k > kept) lo = mid; else hi = mid;
        }
        Assert.Fail($"no push left {kept} of {lines} lines before the break; the test proves nothing");
        return -1;
    }

    [Fact]
    public void TooFewLinesBeforeTheBreak_MoveTheParagraphToTheNextPage()
    {
        double push = PushLeaving(kept: 1, lines: 5);

        Assert.Equal(0, LinesBeforeTheBreak(Doc(push, Lines(5, orphans: 2)), 5));
        // The control, at the same place: one line is allowed to stay.
        Assert.Equal(1, LinesBeforeTheBreak(Doc(push, Lines(5, orphans: 1)), 5));
    }

    /// <summary>Enough lines before the break: orphans changes nothing.</summary>
    [Fact]
    public void EnoughLinesBeforeTheBreak_StayWhereTheyAre()
    {
        double push = PushLeaving(kept: 3, lines: 6);

        Assert.Equal(3, LinesBeforeTheBreak(Doc(push, Lines(6, orphans: 3)), 6));
    }

    [Fact]
    public void TooFewLinesAfterTheBreak_PullLinesOverFromThisPage()
    {
        double push = PushLeaving(kept: 4, lines: 5);

        Assert.Equal(3, LinesBeforeTheBreak(Doc(push, Lines(5, widows: 2)), 5));
        Assert.Equal(2, LinesBeforeTheBreak(Doc(push, Lines(5, widows: 3)), 5));
    }

    /// <summary>
    /// When pulling lines over for the widows would leave too few for the orphans, the whole
    /// paragraph moves: three lines cannot break two-and-two.
    /// </summary>
    [Fact]
    public void AParagraphTooShortToSplitBothWays_MovesWhole()
    {
        double push = PushLeaving(kept: 2, lines: 3);

        Assert.Equal(0, LinesBeforeTheBreak(Doc(push, Lines(3, orphans: 2, widows: 2)), 3));
        // Each alone is satisfied by a break: orphans where it is, widows one line earlier.
        Assert.Equal(2, LinesBeforeTheBreak(Doc(push, Lines(3, orphans: 2)), 3));
        Assert.Equal(1, LinesBeforeTheBreak(Doc(push, Lines(3, widows: 2)), 3));
    }

    /// <summary>
    /// A paragraph already at the top of a page gains nothing from a new one: it breaks where the
    /// lines run out, however many orphans or widows it asks for — and the layout ends.
    /// </summary>
    /// <remarks>
    /// Hostile input: without the test for the top of a page, a count no page can satisfy moves the
    /// paragraph to a new page, finds it still unsatisfied, and moves it again, without end.
    /// </remarks>
    [Fact]
    public void AnUnsatisfiableCount_OnAParagraphLongerThanAPage_BreaksAsUnconstrained()
    {
        static int Pages(Paragraph p) => PdfContentProbe.CountPages(Doc(AtTheTop, p).ToPdf());

        int unconstrained = LinesOnItsFirstPage(Doc(AtTheTop, Lines(200)), 200);
        Assert.True(unconstrained < 200, "200 lines fit one page; the test proves nothing");

        var hostile = Lines(200, orphans: int.MaxValue, widows: int.MaxValue);
        Assert.Equal(Pages(Lines(200)), Pages(hostile));
        Assert.Equal(unconstrained, LinesOnItsFirstPage(Doc(AtTheTop, hostile), 200));
    }

    /// <summary>
    /// Further down a page, an unsatisfiable count moves the paragraph once — to the top of the
    /// next page, where it can do no better — and then breaks it as unconstrained.
    /// </summary>
    [Fact]
    public void AnUnsatisfiableCount_MovesTheParagraphOnceAtMost()
    {
        double push = PushLeaving(kept: 3, lines: 200);
        var hostile = Lines(200, orphans: 150, widows: 150);

        Assert.Equal(0, LinesBeforeTheBreak(Doc(push, hostile), 200));
        // On the next page it breaks where an unconstrained paragraph at the top of a page does.
        Assert.Equal(LinesOnItsFirstPage(Doc(AtTheTop, Lines(200)), 200),
                     LinesOnItsFirstPage(Doc(push, hostile), 200));
    }

    /// <summary>
    /// A page break inside the paragraph is forced, and neither count applies to it: the lines
    /// before it stay, and those after it — which would have fitted here too — are not counted
    /// as the lines this page ends with.
    /// </summary>
    [Fact]
    public void AForcedBreakIsNotConstrained()
    {
        double push = PushLeaving(kept: 3, lines: 5);
        var p = new Paragraph { Widows = 4 };
        for (int i = 1; i <= 5; i++)
        {
            if (i == 3) p.Runs.Add(new PageBreak());
            else if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "LINE" + i + "X" });
        }

        Assert.Equal(2, LinesBeforeTheBreak(Doc(push, p), 5));
    }

    /// <summary>
    /// A line's footnote takes room on its page, and the plan counts it: where only the lines up to
    /// the note's and the note itself fit, one more orphan than that moves the paragraph — the
    /// lines after it would have fitted had the note not been there. On the part's first line, and
    /// on a later one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AFootnoteTakesItsRoomInThePlan(int noteOn)
    {
        Paragraph Noted(int orphans)
        {
            var p = new Paragraph { Orphans = orphans };
            for (int i = 1; i <= 5; i++)
            {
                if (i > 1) p.Runs.Add(new LineBreak());
                p.Runs.Add(new TextRun { Text = "LINE" + i + "X" });
                if (i != noteOn) continue;
                var note = new Footnote();
                for (int n = 1; n <= 8; n++)
                {
                    if (n > 1) note.Runs.Add(new LineBreak());
                    note.Runs.Add(new TextRun { Text = "NOTE" + n });
                }
                p.Runs.Add(note);
            }
            return p;
        }

        double push = PushLeaving(kept: noteOn, lines: 5, make: () => Noted(1));

        Assert.Equal(0, LinesBeforeTheBreak(Doc(push, Noted(noteOn + 1)), 5));
        Assert.Equal(noteOn, LinesBeforeTheBreak(Doc(push, Noted(noteOn)), 5));
    }

    /// <summary>
    /// A float kept with the paragraph after it goes where that paragraph starts: it takes no
    /// height of its own, so the keep is decided by the paragraph's orphans — and when they move
    /// the paragraph, the float goes too rather than staying on the page before.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFloatKeptWithAParagraph_GoesWhereItStarts(bool keep)
    {
        double push = PushLeaving(kept: 1, lines: 5);
        var floated = new Paragraph
        {
            Float = FloatSide.Right, FloatWidth = 60, KeepWithNext = keep, SpacingAfter = 0,
            Runs = { new TextRun { Text = "FLOATX" } },
        };

        string content = PdfContentProbe.DecodeStreams(Doc(push, floated, Lines(5, orphans: 2)).ToPdf());

        Assert.True(Y(content, "LINE1X") > Y(content, "PUSHX"), "the paragraph did not move; the test proves nothing");
        // Kept, it is beside the paragraph's first line; the control stays behind.
        Assert.Equal(keep, Y(content, "FLOATX") > Y(content, "PUSHX"));
        if (keep) Assert.Equal(Y(content, "LINE1X"), Y(content, "FLOATX"), 1);
    }

    /// <summary>
    /// A paragraph spread over several pages is planned again on each: its widows hold on its
    /// last page, not only across its first break.
    /// </summary>
    [Fact]
    public void TheWidowsHoldOnTheLastPageOfALongParagraph()
    {
        static int LinesOnTheLastPage(Paragraph p, int lines)
        {
            string content = PdfContentProbe.DecodeStreams(Doc(AtTheTop, p).ToPdf());
            for (int i = lines - 1; i >= 1; i--)
                if (Y(content, "LINE" + (i + 1) + "X") > Y(content, "LINE" + i + "X")) return lines - i;
            return lines;
        }

        int last = LinesOnTheLastPage(Lines(200), 200);
        Assert.True(last < 200 && last > 1, $"{last} lines on the last page; the test proves nothing");

        Assert.Equal(last + 1, LinesOnTheLastPage(Lines(200, widows: last + 1), 200));
    }

    /// <summary>
    /// A page in the middle of a paragraph begins with its widows too (CSS 2.1 §13.3.3: the lines
    /// after a break, which a middle page's first fragment is, as well as before one): on pages of
    /// four lines, nine lines with <c>widows: 2</c> go four, three and two — the second page's break
    /// leaves the third its two; with <c>widows: 3</c> the five lines left after the first page cannot
    /// give both later pages three, so the second page keeps the four it has room for and the last
    /// line runs out onto the third, as an unconstrained paragraph's would — not two lines on the
    /// second page, which the orphans alone allowed.
    /// </summary>
    [Fact]
    public void AMiddlePage_BeginsWithItsWidowsToo()
    {
        double height = PageHeightHolding(4, lines: 9, new Section());

        Assert.Equal(new[] { 4, 4, 1 }, LinesPerPage(DocOnPages(height, Lines(9)), 9));
        Assert.Equal(new[] { 4, 3, 2 }, LinesPerPage(DocOnPages(height, Lines(9, orphans: 1, widows: 2)), 9));
        Assert.Equal(new[] { 4, 4, 1 }, LinesPerPage(DocOnPages(height, Lines(9, orphans: 1, widows: 3)), 9));
    }

    /// <summary>
    /// A fragment after a forced break is not held to the widows at its top — neither count applies
    /// to a forced break: nine lines with a page break after the fourth and <c>widows: 3</c>, on pages
    /// of four lines, go four, two and three — the five lines after the break leave the third page its
    /// three — where a fragment after the page's own break would have kept all four it had room for.
    /// </summary>
    [Fact]
    public void AFragmentAfterAForcedBreak_BeginsUnconstrained()
    {
        double height = PageHeightHolding(4, lines: 9, new Section());
        var p = new Paragraph { Orphans = 1, Widows = 3 };
        for (int i = 1; i <= 9; i++)
        {
            if (i == 5) p.Runs.Add(new PageBreak());
            else if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "LINE" + i + "X" });
        }

        Assert.Equal(new[] { 4, 2, 3 }, LinesPerPage(DocOnPages(height, p), 9));
    }

    /// <summary>A document of one section <paramref name="pageHeight"/> tall holding <paramref name="tail"/>, the paragraph at the top of its first page.</summary>
    private static Document DocOnPages(double pageHeight, params IDocElement[] tail)
    {
        var doc = Doc(AtTheTop, tail);
        doc.Sections[0].PageHeightPt = pageHeight;
        return doc;
    }

    /// <summary>The lines of a paragraph on each page it reaches, in order: a line drawn above the one before it began a page.</summary>
    private static int[] LinesPerPage(Document doc, int lines)
    {
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        var pages = new System.Collections.Generic.List<int> { 1 };
        for (int i = 1; i < lines; i++)
        {
            if (Y(content, "LINE" + (i + 1) + "X") > Y(content, "LINE" + i + "X")) pages.Add(0);
            pages[pages.Count - 1]++;
        }
        return pages.ToArray();
    }

    /// <summary>The page height at which exactly <paramref name="kept"/> of an unconstrained paragraph's <paramref name="lines"/> fit on the first page, found by bisection over a page's body.</summary>
    private static double PageHeightHolding(int kept, int lines, Section page)
    {
        double lo = page.MarginTop + page.MarginBottom, hi = page.PageHeightPt;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            int k = LinesOnItsFirstPage(DocOnPages(mid, Lines(lines)), lines);
            if (k == kept) return mid;
            if (k > kept) hi = mid; else lo = mid;
        }
        Assert.Fail($"no page height held {kept} of {lines} lines; the test proves nothing");
        return -1;
    }

    /// <summary>
    /// A heading kept with a paragraph too short to break — three lines, two orphans, two widows —
    /// needs all of it beside it: where the heading and two lines fit but not three, the heading
    /// moves with the paragraph.
    /// </summary>
    [Fact]
    public void AHeadingKeptWithAParagraphTooShortToBreak_MovesWithAllOfIt()
    {
        static Paragraph Heading() => new() { KeepWithNext = true, Runs = { new TextRun { Text = "HEADX" } } };
        var page = new Section();
        double body = page.PageHeightPt - page.MarginTop - page.MarginBottom;

        // The push that leaves the heading and two lines of the unconstrained paragraph on the page.
        double push = -1;
        for (double s = body - 100; s < body && push < 0; s += 1)
        {
            string c = PdfContentProbe.DecodeStreams(Doc(s, Heading(), Lines(3)).ToPdf());
            double pushed = Y(c, "PUSHX"), head = Y(c, "HEADX"), l2 = Y(c, "LINE2X"), l3 = Y(c, "LINE3X");
            if (head < pushed && l2 < head && l3 > l2) push = s;
        }
        Assert.True(push >= 0, "no push left the heading and two lines on the page; the test proves nothing");

        string content = PdfContentProbe.DecodeStreams(Doc(push, Heading(), Lines(3, orphans: 2, widows: 2)).ToPdf());
        Assert.True(Y(content, "HEADX") > Y(content, "PUSHX"), "the heading stayed behind while its paragraph moved");
        Assert.True(Y(content, "LINE3X") < Y(content, "LINE1X"), "the paragraph was broken");
    }

    /// <summary>
    /// A heading kept with its paragraph needs the paragraph's orphans on its page, not just its
    /// first line: where only one line fits, the heading moves with the paragraph.
    /// </summary>
    [Fact]
    public void AHeadingKeptWithAParagraph_NeedsItsOrphansBesideIt()
    {
        static Paragraph Heading() => new() { KeepWithNext = true, Runs = { new TextRun { Text = "HEADX" } } };
        var page = new Section();
        double body = page.PageHeightPt - page.MarginTop - page.MarginBottom;

        // The push that leaves the heading and one line of the paragraph on the page.
        double push = -1;
        for (double s = body - 100; s < body && push < 0; s += 1)
        {
            string c = PdfContentProbe.DecodeStreams(Doc(s, Heading(), Lines(5)).ToPdf());
            double pushed = Y(c, "PUSHX"), head = Y(c, "HEADX"), l1 = Y(c, "LINE1X"), l2 = Y(c, "LINE2X");
            if (head < pushed && l1 < head && l2 > l1) push = s;
        }
        Assert.True(push >= 0, "no push left the heading and one line on the page; the test proves nothing");

        string content = PdfContentProbe.DecodeStreams(Doc(push, Heading(), Lines(5, orphans: 2)).ToPdf());
        Assert.True(Y(content, "HEADX") > Y(content, "PUSHX"), "the heading stayed behind while its paragraph moved");
        Assert.True(Y(content, "LINE1X") < Y(content, "HEADX"), "the paragraph is not below its heading");
    }
}
