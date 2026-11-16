// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A footnote that does not fit at the foot of its mark's page splits: as many of its lines as fit stay
/// there, keeping its orphans and widows, and the rest continues at the foot of the next page, under a
/// full-width rule, ahead of that page's own notes. It used to move the mark's line to the next page
/// with it — and a note taller than a page ran below the margin.
/// </summary>
public sealed class FootnoteSplitTests
{
    private const int NoteWords = 220;

    private static string NoteWord(int i) => "N" + i.ToString("D4");

    private static Footnote Note(int words, string prefix = "")
    {
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = string.Join(" ", Enumerable.Range(1, words).Select(i => prefix + NoteWord(i))) });
        return note;
    }

    /// <summary><paramref name="fillers"/> one-line paragraphs, then MARKLINE carrying the long note, then <paramref name="after"/>.</summary>
    private static Document Doc(int fillers, int noteWords = NoteWords, params Paragraph[] after)
    {
        var section = new Section();
        for (int i = 0; i < fillers; i++)
        {
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = "Body" + i.ToString("D3") });
            section.Children.Add(p);
        }
        var marked = new Paragraph();
        marked.Runs.Add(new TextRun { Text = "MARKLINE" });
        marked.Runs.Add(Note(noteWords));
        section.Children.Add(marked);
        foreach (var p in after) section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>Each page's words, in page order.</summary>
    private static List<string[]> PageWords(byte[] pdf)
    {
        var pages = new List<string[]>();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfTextExtractor.ExtractText(doc, (i, text) =>
        {
            while (pages.Count <= i) pages.Add(Array.Empty<string>());
            pages[i] = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        });
        return pages;
    }

    private static int PageOf(List<string[]> pages, string word)
    {
        for (int i = 0; i < pages.Count; i++) if (pages[i].Contains(word)) return i;
        return -1;
    }

    /// <summary>The page of a marked word: the superscript mark extracts glued to it (<c>MARKLINE1</c>).</summary>
    private static int PageOfMark(List<string[]> pages, string word)
    {
        for (int i = 0; i < pages.Count; i++) if (pages[i].Any(w => w.StartsWith(word, StringComparison.Ordinal))) return i;
        return -1;
    }

    /// <summary>The note's words, every page's in order: each once, in sequence.</summary>
    private static void AssertTheNoteIsWholeAndInOrder(List<string[]> pages, int words, string prefix = "")
    {
        var seen = pages.SelectMany(p => p).Where(w => w.StartsWith(prefix + "N", StringComparison.Ordinal) && w.Length == prefix.Length + 5).ToList();
        Assert.Equal(Enumerable.Range(1, words).Select(i => prefix + NoteWord(i)), seen);
    }

    /// <summary>
    /// Across filler counts that push the mark's line down the first page: the note always begins on its
    /// mark's page and is drawn whole and in order; and at some count — the line fitting, the note not —
    /// it splits, its head with the mark and the rest on the next page.
    /// </summary>
    [Fact]
    public void ANoteThatDoesNotFitSplits_ItsHeadWithTheMark_TheRestOnTheNextPage()
    {
        int splits = 0;
        for (int fillers = 20; fillers <= 60; fillers += 2)
        {
            var pages = PageWords(Doc(fillers).ToPdf());
            int mark = PageOfMark(pages, "MARKLINE");
            Assert.Equal(mark, PageOf(pages, NoteWord(1)));                // the note starts where its mark is
            AssertTheNoteIsWholeAndInOrder(pages, NoteWords);
            if (PageOf(pages, NoteWord(NoteWords)) > mark)
            {
                splits++;
                Assert.Equal(mark + 1, PageOf(pages, NoteWord(NoteWords)));
            }
        }
        Assert.True(splits > 0, "no filler count split the note");
    }

    /// <summary>
    /// The mark's line stays on its page when only the note is short of room: at the first filler count
    /// where the old rule moved the line, the line and the note's head are on page one.
    /// </summary>
    [Fact]
    public void TheMarksLineStaysWhereItFits()
    {
        bool sawLineStay = false;
        for (int fillers = 20; fillers <= 60; fillers++)
        {
            var pages = PageWords(Doc(fillers).ToPdf());
            // Body lines after the mark would have followed it; here the mark is page one's last body line.
            if (PageOfMark(pages, "MARKLINE") == 0 && PageOf(pages, NoteWord(NoteWords)) == 1) { sawLineStay = true; break; }
        }
        Assert.True(sawLineStay);
    }

    /// <summary>
    /// A note split across pages is one <c>/Note</c> in the structure tree, its content on every page it
    /// reaches — the 2 400-word note's three or more — where it used to be one element per page; two
    /// notes, the control, are two.
    /// </summary>
    [Fact]
    public void ASplitNote_IsOneNoteElement_OnEveryPageItReaches()
    {
        var doc = Doc(5, 2400);
        doc.Tagged = true;
        byte[] pdf = doc.ToPdf();
        string raw = System.Text.Encoding.Latin1.GetString(pdf);
        int pages = PageWords(pdf).Count;
        Assert.True(pages >= 3, $"{pages} pages");

        var notes = System.Text.RegularExpressions.Regex.Matches(raw, @"\d+ 0 obj\s*<<(?:(?!endobj).)*?/S /Note(?:(?!endobj).)*endobj",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Single(notes);
        // The note's kids are the parts' paragraph elements; each names the page it is on.
        string kids = System.Text.RegularExpressions.Regex.Match(notes[0].Value, @"/K \[([^\]]*)\]").Groups[1].Value;
        var pagesReached = System.Text.RegularExpressions.Regex.Matches(kids, @"(\d+) 0 R")
            .Select(k => System.Text.RegularExpressions.Regex.Match(raw, @"(?<!\d)" + k.Groups[1].Value + @" 0 obj\s*<<[^>]*?/Pg (\d+) 0 R").Groups[1].Value)
            .Where(p => p.Length > 0).Distinct().Count();
        Assert.True(pagesReached >= 3, $"the note's content is on {pagesReached} pages");

        var two = Doc(5, 20);
        ((Paragraph)two.Sections[0].Children[0]).Runs.Add(Note(10, "S"));
        two.Tagged = true;
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(two.ToPdf()), "/S /Note").Count);
    }

    /// <summary>A note taller than a page runs onto as many pages as it needs, never below the bottom margin.</summary>
    [Fact]
    public void ANoteTallerThanAPage_SpansPages_InsideTheMargins()
    {
        const int words = 2400;
        byte[] pdf = Doc(5, words).ToPdf();
        var pages = PageWords(pdf);
        Assert.True(pages.Count >= 3, $"{pages.Count} pages");
        AssertTheNoteIsWholeAndInOrder(pages, words);

        var section = new Section();
        double bottom = section.MarginBottom, top = section.PageHeightPt - section.MarginTop;
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
        Assert.All(drawn, d => Assert.True(d.Y >= bottom - 0.5, $"'{d.Text}' drawn at y={d.Y:F1}, below the margin at {bottom}"));
        // A continuation takes at most half the text block, so it never climbs past the top margin.
        Assert.All(drawn, d => Assert.True(d.Y <= top + 0.5, $"'{d.Text}' drawn at y={d.Y:F1}, above the margin at {top}"));
    }

    /// <summary>
    /// A note of lines ended by line breaks — taller than a page — is cut between its lines too, and
    /// spans pages inside the margins. It used to be refused the cut and drawn whole, its strip running
    /// up past the page's top edge.
    /// </summary>
    [Fact]
    public void ANoteOfLineBreaks_TallerThanAPage_SpansPages_InsideTheMargins()
    {
        const int lines = 120;
        var note = new Footnote();
        for (int i = 1; i <= lines; i++)
        {
            if (i > 1) note.Runs.Add(new LineBreak());
            note.Runs.Add(new TextRun { Text = NoteWord(i) });
        }
        var section = new Section();
        var marked = new Paragraph();
        marked.Runs.Add(new TextRun { Text = "MARKLINE" });
        marked.Runs.Add(note);
        section.Children.Add(marked);
        var doc = new Document();
        doc.Sections.Add(section);
        byte[] pdf = doc.ToPdf();

        var pages = PageWords(pdf);
        Assert.True(pages.Count >= 2, $"{pages.Count} pages");
        AssertTheNoteIsWholeAndInOrder(pages, lines);
        double bottom = section.MarginBottom, top = section.PageHeightPt - section.MarginTop;
        var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
        Assert.All(drawn, d => Assert.True(d.Y >= bottom - 0.5 && d.Y <= top + 0.5, $"'{d.Text}' drawn at y={d.Y:F1}, outside the margins {bottom}..{top}"));
        // Each line on a baseline of its own: no break lost, none drawn as an extra empty line.
        var baselines = Enumerable.Range(1, lines).Select(i => drawn.Single(d => d.Text == NoteWord(i)).Y).ToList();
        for (int i = 1; i < lines; i++)
            Assert.True(baselines[i] < baselines[i - 1] || baselines[i] > baselines[i - 1] + 100, $"{NoteWord(i + 1)} is not on the line after {NoteWord(i)}");
    }

    /// <summary>
    /// A later mark's note waits for the continuation: it is drawn after the continued note, never between
    /// its parts — the line carrying it moves to the page the first note ends on.
    /// </summary>
    [Fact]
    public void ALaterNoteFollowsTheContinuation()
    {
        var second = new Paragraph();
        second.Runs.Add(new TextRun { Text = "SECONDMARK" });
        second.Runs.Add(Note(6, "S"));
        for (int fillers = 20; fillers <= 60; fillers += 2)
        {
            var pages = PageWords(Doc(fillers, NoteWords, second).ToPdf());
            if (PageOf(pages, NoteWord(NoteWords)) <= PageOfMark(pages, "MARKLINE")) continue;   // no split here
            var all = pages.SelectMany(p => p).ToList();
            Assert.True(all.IndexOf(NoteWord(NoteWords)) < all.IndexOf("S" + NoteWord(1)), "the second note was drawn inside the first");
            Assert.True(PageOfMark(pages, "SECONDMARK") >= PageOf(pages, NoteWord(NoteWords)));
            return;
        }
        Assert.Fail("no filler count split the note");
    }

    /// <summary>
    /// Two notes on one line, the first split: the second follows the first's rest on the next page, never
    /// drawn under the first's head.
    /// </summary>
    [Fact]
    public void TheSecondNoteOnTheLineFollowsTheFirstsRest()
    {
        int splits = 0;
        for (int fillers = 20; fillers <= 60; fillers += 2)
        {
            var section = new Section();
            for (int i = 0; i < fillers; i++)
            {
                var f = new Paragraph();
                f.Runs.Add(new TextRun { Text = "Body" + i.ToString("D3") });
                section.Children.Add(f);
            }
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = "MARKLINE" });
            p.Runs.Add(Note(NoteWords));
            p.Runs.Add(new TextRun { Text = " and" });
            p.Runs.Add(Note(4, "S"));
            section.Children.Add(p);
            var doc = new Document();
            doc.Sections.Add(section);

            var pages = PageWords(doc.ToPdf());
            if (PageOf(pages, NoteWord(NoteWords)) <= PageOfMark(pages, "MARKLINE")) continue;
            splits++;
            var all = pages.SelectMany(w => w).ToList();
            Assert.True(all.IndexOf(NoteWord(NoteWords)) < all.IndexOf("S" + NoteWord(1)), $"fillers {fillers}: the second note was drawn inside the first");
        }
        Assert.True(splits > 0, "no filler count split the note");
    }

    /// <summary>
    /// A later mark's note never starts on the page a note continues from, even when its line and its note
    /// would fit in the room the split left: a tiny line and a tiny note after a split one. Without the
    /// rule it was drawn under the first note's head, between that note's parts.
    /// </summary>
    [Fact]
    public void ALaterNoteNeverStartsBeforeTheContinuationEnds()
    {
        var tiny = new Paragraph();
        tiny.Runs.Add(new TextRun { Text = "TINYMARK", FontSize = 2 });
        var tinyNote = new Footnote();
        tinyNote.Runs.Add(new TextRun { Text = "T0001", FontSize = 2 });
        tiny.Runs.Add(tinyNote);
        int splits = 0;
        for (int fillers = 20; fillers <= 60; fillers++)
        {
            var pages = PageWords(Doc(fillers, 70, tiny).ToPdf());
            int mark = PageOfMark(pages, "MARKLINE");
            if (PageOf(pages, NoteWord(70)) == mark) continue;
            splits++;
            var all = pages.SelectMany(p => p).ToList();
            Assert.True(all.IndexOf(NoteWord(70)) < all.IndexOf("T0001"), $"fillers {fillers}: the tiny note was drawn inside the first");
        }
        Assert.True(splits > 0, "no filler count split the note");
    }

    /// <summary>
    /// The page planner knows a continuing note holds the page: in a paragraph whose orphans are two, the
    /// line with a note that would split never ends its page alone — the paragraph moves on and its note
    /// fits there. The control is the same paragraph with no orphans to keep, whose note does split, its
    /// next line pushed after the continuation.
    /// </summary>
    [Fact]
    public void AContinuationKeepsTheParagraphsOrphans()
    {
        Document Doc(int fillers, int orphans)
        {
            var p = new Paragraph { Orphans = orphans };
            p.Runs.Add(new TextRun { Text = "MARKLINE" });
            p.Runs.Add(Note(70));
            p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "TINYLINE", FontSize = 2 });
            var tinyNote = new Footnote();
            tinyNote.Runs.Add(new TextRun { Text = "T0001", FontSize = 2 });
            p.Runs.Add(tinyNote);
            p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "THIRDLINE" });
            var section = new Section();
            for (int i = 0; i < fillers; i++)
            {
                var f = new Paragraph();
                f.Runs.Add(new TextRun { Text = "Body" + i.ToString("D3") });
                section.Children.Add(f);
            }
            section.Children.Add(p);
            var doc = new Document();
            doc.Sections.Add(section);
            return doc;
        }

        int controlSplits = 0;
        for (int fillers = 20; fillers <= 60; fillers++)
        {
            var kept = PageWords(Doc(fillers, 2).ToPdf());
            Assert.True(PageOfMark(kept, "TINYLINE") == PageOfMark(kept, "MARKLINE"), $"fillers {fillers}: MARKLINE ended its page alone");

            var control = PageWords(Doc(fillers, 1).ToPdf());
            int mark = PageOfMark(control, "MARKLINE");
            if (PageOf(control, NoteWord(70)) > mark && PageOfMark(control, "TINYLINE") > mark) controlSplits++;
        }
        Assert.True(controlSplits > 0, "the control never split the note and moved the next line");
    }

    /// <summary>
    /// A strip that opens with a continued note is ruled the full content width; one of new notes alone
    /// keeps the short rule.
    /// </summary>
    [Fact]
    public void AContinuationIsRuledTheFullWidth()
    {
        var section = new Section();
        double fullWidth = section.PageWidthPt - section.MarginLeft - section.MarginRight;
        bool Full(byte[] pdf) => PdfContentProbe.StrokedSegments(PdfContentProbe.DecodeStreams(pdf))
            .Any(s => s.IsHorizontal && Math.Abs(Math.Abs(s.X1 - s.X0) - fullWidth) < 0.5);

        Assert.True(Full(Doc(5, 2400).ToPdf()));
        Assert.False(Full(Doc(5, 10).ToPdf()));
    }

    /// <summary>
    /// A short note at a page's foot that would leave one line behind moves with its line, as before: its
    /// head would be shorter than its orphans.
    /// </summary>
    [Fact]
    public void ASplitNoteKeepsItsOrphansAndWidows()
    {
        const int words = 70;   // four lines at note size, so a split keeps two on each side
        int splits = 0;
        for (int fillers = 20; fillers <= 60; fillers++)
        {
            byte[] pdf = Doc(fillers, words).ToPdf();
            var pages = PageWords(pdf);
            int mark = PageOfMark(pages, "MARKLINE");
            AssertTheNoteIsWholeAndInOrder(pages, words);
            if (PageOf(pages, NoteWord(words)) == mark) continue;
            splits++;
            // Lines are baselines: the head's on the mark's page, the rest's on the next.
            var drawn = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
            int Lines(IEnumerable<string> ws) => ws.Select(w => drawn.First(d => d.Text == w).Y).Distinct().Count();
            bool IsNote(string w) => w.Length == 5 && w[0] == 'N' && char.IsDigit(w[1]);
            Assert.True(Lines(pages[mark].Where(IsNote)) >= 2, "the head left fewer lines than the orphans");
            Assert.True(Lines(pages[mark + 1].Where(IsNote)) >= 2, $"the rest carried fewer lines than the widows (fillers {fillers}): head {string.Join(" ", pages[mark].Where(IsNote))} | rest {string.Join(" ", pages[mark + 1].Where(IsNote))}");
        }
        Assert.True(splits > 0, "no filler count split the note");
    }
}
