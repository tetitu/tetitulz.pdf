// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.TestHelpers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Footnotes: a reference mark where the note belongs in the text, and the note itself wherever
/// the format puts notes.
/// </summary>
/// <remarks>
/// <para><b>One model element, four different answers about where the second half goes.</b> PDF
/// reserves room at the foot of the page the mark landed on; HTML has no page, so the notes go in
/// a section at the end and the mark links to them; ODT and DOCX each have a native note the
/// application lays out itself. The model states the relationship once and each exporter decides —
/// which is why the assertions below are about four different shapes and one shared numbering.
/// </para>
///
/// <para><b>Numbering is asserted separately from placement.</b> A note's number depends on how
/// many precede it in the document, and layout runs more than once per paragraph — a counter
/// advanced during layout produces a document numbered 1, 3, 4, 7, and every "the note appears"
/// assertion passes anyway.</para>
/// </remarks>
public sealed class FootnoteTests
{
    // ==================================================================== numbering

    /// <summary>Notes are numbered in document order, from one.</summary>
    [Fact]
    public void NotesAreNumberedInDocumentOrder()
    {
        Document doc = Doc(
            Para(("First", "note one"), ("Second", "note two")),
            Para(("Third", "note three")));

        Assert.Equal(new[] { "1", "2", "3" }, Marks(doc));
    }

    /// <summary>
    /// Layout running twice does not number a note twice.
    /// </summary>
    /// <remarks>
    /// <b>This is the defect numbering-during-layout has, and only a fixture that forces the
    /// second pass can see it.</b> A table cell is measured before it is drawn, so a note inside
    /// one goes through layout twice — a counter advanced there gives the notes after it the wrong
    /// numbers, and the document still exports cleanly.
    /// </remarks>
    [Fact]
    public void ANoteInsideATableCellIsNumberedOnce()
    {
        var cell = new TableCell();
        cell.Blocks.Add(Para(("In a cell", "cell note")));

        var table = new Table();
        var row = new TableRow();
        row.Cells.Add(cell);
        table.Rows.Add(row);

        Document doc = Doc(table, Para(("After", "later note")));

        Assert.Equal(new[] { "1", "2" }, Marks(doc));
    }

    /// <summary>An explicit mark is used verbatim and does not advance the sequence.</summary>
    /// <remarks>
    /// A caller who set a dagger was stepping outside the numbering, not renumbering it — so the
    /// automatic note after it is still 2, not 3.
    /// </remarks>
    [Fact]
    public void AnExplicitMarkIsUsedAndDoesNotAdvanceTheSequence()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "A" });
        paragraph.Runs.Add(Note("auto one"));
        paragraph.Runs.Add(new TextRun { Text = "B" });
        paragraph.Runs.Add(Note("symbol", mark: "†"));
        paragraph.Runs.Add(new TextRun { Text = "C" });
        paragraph.Runs.Add(Note("auto two"));

        Assert.Equal(new[] { "1", "†", "2" }, Marks(Doc(paragraph)));
    }

    /// <summary>One note object used in two places is still one note.</summary>
    /// <remarks>
    /// <b>The model is a public mutable graph, so a caller can share an instance</b> —
    /// deliberately, for a note referenced from two sentences, or by accident when a template is
    /// reused. Numbering it twice would give it two numbers and shift every note after it, and the
    /// document would still export cleanly.
    /// </remarks>
    [Fact]
    public void ANoteInstanceUsedTwiceIsNumberedOnce()
    {
        Footnote shared = Note("shared note");

        var first = new Paragraph();
        first.Runs.Add(new TextRun { Text = "A" });
        first.Runs.Add(shared);

        var second = new Paragraph();
        second.Runs.Add(new TextRun { Text = "B" });
        second.Runs.Add(shared);
        second.Runs.Add(Note("after"));

        // Both references carry the same mark, and the note that follows is 2 rather than 3.
        Assert.Equal(new[] { "1", "1", "2" }, Marks(Doc(first, second)));
    }

    // ==================================================================== PDF

    /// <summary>The PDF draws the mark in the text and the note at the foot of the page.</summary>
    /// <remarks>
    /// <b>Both halves, and the second is what a naive implementation drops.</b> A superscript
    /// number in the sentence with no note anywhere is a document that points at nothing, and it
    /// satisfies every assertion about the mark.
    /// </remarks>
    [Fact]
    public void ThePdfDrawsTheMarkInlineAndTheNoteAtTheFoot()
    {
        List<PdfContentProbe.DrawnText> drawn = Drawn(Doc(Para(("Body text", "the note itself"))));

        // The exporter draws a word per Tj, so the page's words are the unit — asking for
        // "Body text" as one string asserts about the tokenizer rather than about the footnote.
        Assert.Contains(drawn, d => d.Text == "Body");
        Assert.Contains(drawn, d => d.Text == "note");
        Assert.Contains(drawn, d => d.Text == "itself");

        // The mark, superscript, between the body word and the note.
        Assert.Contains(drawn, d => d.Text == "1");
    }

    /// <summary>The note is drawn below the text that references it.</summary>
    /// <remarks>
    /// <b>Order in the content stream is not position on the page, so this measures Y.</b> The note
    /// is drawn when the page closes, so it comes last in the stream whether it is at the foot of
    /// the page or on top of the body — and a note drawn at the wrong Y is invisible to every
    /// assertion about the text the file contains.
    /// </remarks>
    [Fact]
    public void TheNoteIsDrawnBelowTheTextThatReferencesIt()
    {
        List<PdfContentProbe.DrawnText> drawn = Drawn(Doc(Para(("Body text", "the note itself"))));

        double body = YOf(drawn, "Body");
        double note = YOf(drawn, "itself");

        Assert.True(note < body,
            $"the note is at y={note:F1} and the body at y={body:F1}; the note is not below it");
    }

    /// <summary>The mark is drawn beside the text, not down with the note.</summary>
    /// <remarks>
    /// <b>The two halves are drawn by different code at different times</b> — the mark during
    /// the paragraph, the note when the page closes — and a mark that ended up at the foot
    /// with its note would still satisfy "both appear on the page". Its baseline is the body's,
    /// raised by the superscript shift, so comparing it against the note's is what separates them.
    /// </remarks>
    [Fact]
    public void TheMarkIsDrawnBesideTheTextRatherThanWithTheNote()
    {
        List<PdfContentProbe.DrawnText> drawn = Drawn(Doc(Para(("Body text", "the note itself"))));

        double body = YOf(drawn, "Body");
        double mark = YOf(drawn, "1");
        double note = YOf(drawn, "itself");

        Assert.True(mark > note,
            $"the mark is at y={mark:F1}, down with the note at y={note:F1}");
        Assert.True(Math.Abs(mark - body) < 20,
            $"the mark is at y={mark:F1}, nowhere near the text it marks at y={body:F1}");

        // Raised, not on the baseline — and the rise is a TEXT STATE operator, not a
        // different text matrix, so the two draws share a Tm Y and only `Ts` separates them. A
        // mark set flush with the text reads as a stray digit in the middle of the sentence, and
        // every assertion about where it is drawn passes anyway.
        // TWO raised runs, not one. The note draws its own mark in front of its text, so a page
        // carrying one footnote raises twice: once inline and once at the foot. Asserting that
        // SOME run is raised is satisfied by the note's alone — measured, and it is what let a
        // mark drawn flush with the text pass.
        Assert.Equal(2, SuperscriptRuns(Doc(Para(("Body text", "the note itself")))));
    }

    /// <summary>An ordinary document raises nothing.</summary>
    /// <remarks>
    /// The acceptance control for the rises above: without it, a count of two would be satisfied by
    /// an exporter that raised every run it drew.
    /// </remarks>
    [Fact]
    public void ADocumentWithNoNoteRaisesNothing()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "Just text" });

        Assert.Equal(0, SuperscriptRuns(Doc(paragraph)));
    }

    /// <summary>A page carrying a note fits fewer body lines than one that does not.</summary>
    /// <remarks>
    /// <para><b>The reservation is the whole mechanism, and nothing else observes it.</b> Drawing
    /// the notes into the bottom margin instead of into reserved space puts them at the foot of the
    /// page just the same — overprinting the body's last lines. Every assertion about where
    /// the note is drawn passes either way.</para>
    ///
    /// <para><b>Lines on the first page, not pages in the document.</b> Whether the reservation
    /// tips the total page count depends on how close to a boundary the fixture happens to sit;
    /// how much body fits above the notes does not, so that is what is measured.</para>
    /// </remarks>
    [Fact]
    public void APageCarryingANoteFitsFewerBodyLines()
    {
        int withNote = FirstPageBodyLines(ManyParagraphs(withNote: true));
        int without = FirstPageBodyLines(ManyParagraphs(withNote: false));

        Assert.True(withNote < without,
            $"{withNote} body lines on the first page with a note against {without} without: "
            + "the note reserved no room");
    }

    /// <summary>A page with no note of its own gets the whole text block back.</summary>
    /// <remarks>
    /// <b>The reservation belongs to the page, not to the document.</b> A note area left standing
    /// after the page it was reserved for shrinks every page that follows — the document is
    /// still laid out, still readable, and quietly a page or two longer than it should be. Only
    /// comparing a later page against the one that carried the note shows it.
    /// </remarks>
    [Fact]
    public void TheNoteAreaDoesNotSurviveIntoTheNextPage()
    {
        int[] perPage = BodyLinesPerPage(ManyParagraphs(withNote: true));

        Assert.True(perPage.Length >= 2,
            $"the fixture produced {perPage.Length} page(s); it cannot show a carry-over");

        Assert.True(perPage[1] > perPage[0],
            $"page one fits {perPage[0]} body lines and page two {perPage[1]}: the note area "
            + "reserved on page one is still reserved on page two");
    }

    /// <summary>Each DOCX note gets its own id.</summary>
    /// <remarks>
    /// Two references naming one id make both marks point at the same note — a document Word
    /// opens without complaint, showing the first note twice and the second nowhere.
    /// </remarks>
    [Fact]
    public void DocxGivesEachNoteItsOwnId()
    {
        Document doc = Doc(Para(("First", "note one"), ("Second", "note two")));

        string[] referenced = DocxPart(doc, "word/document.xml")
            .Descendants(W + "footnoteReference")
            .Select(r => r.Attribute(W + "id")!.Value)
            .ToArray();

        Assert.Equal(2, referenced.Length);
        Assert.Equal(2, referenced.Distinct().Count());

        // And the part defines both, so neither reference dangles.
        string[] defined = DocxPart(doc, "word/footnotes.xml")
            .Descendants(W + "footnote")
            .Select(f => f.Attribute(W + "id")!.Value)
            .ToArray();

        Assert.All(referenced, id => Assert.Contains(id, defined));
    }

    /// <summary>The note carries its own structure element when the document is tagged.</summary>
    /// <remarks>
    /// §14.8.4.7.4 gives a note the <c>/Note</c> role. A note marked as an artifact — which the
    /// separator rule above it is — would be unreachable from the tag tree, which is the one
    /// failure this exporter's tagging is documented never to cause.
    /// </remarks>
    [Fact]
    public void ATaggedDocumentGivesTheNoteItsOwnStructureElement()
    {
        Document doc = Doc(Para(("Body text", "the note itself")));
        doc.Tagged = true;

        Assert.Contains("/Note", RawPdf(Pdf(doc)), StringComparison.Ordinal);
    }

    // ==================================================================== HTML

    /// <summary>HTML writes a linked superscript and a note section at the end.</summary>
    [Fact]
    public void HtmlWritesALinkedMarkAndANoteSection()
    {
        string html = Html(Doc(Para(("Body text", "the note itself"))));

        Assert.Contains("<sup id=\"fnref1\"><a href=\"#fn1\">1</a></sup>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"fn1\"", html, StringComparison.Ordinal);
        Assert.Contains("the note itself", html, StringComparison.Ordinal);
    }

    /// <summary>A document with no footnote gets no note section.</summary>
    /// <remarks>
    /// An empty list under a rule is furniture a screen reader announces and a reader has to skip
    /// past — the acceptance control for the section above.
    /// </remarks>
    [Fact]
    public void HtmlWritesNoSectionWhenThereAreNoNotes()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "Just text" });

        Assert.DoesNotContain("class=\"footnotes\"", Html(Doc(paragraph)), StringComparison.Ordinal);
    }

    // ==================================================================== ODT

    /// <summary>ODT writes ODF's own note element, with the note inside it.</summary>
    [Fact]
    public void OdtWritesANativeNote()
    {
        string content = Odt(Doc(Para(("Body text", "the note itself"))));

        Assert.Contains("<text:note ", content, StringComparison.Ordinal);
        Assert.Contains("text:note-class=\"footnote\"", content, StringComparison.Ordinal);
        Assert.Contains("<text:note-citation>1</text:note-citation>", content, StringComparison.Ordinal);
        Assert.Contains("the note itself", content, StringComparison.Ordinal);
    }

    // ==================================================================== DOCX

    /// <summary>DOCX writes a footnote reference and a footnotes part holding the note.</summary>
    /// <remarks>
    /// <b>Both, because either alone is a broken document.</b> A reference naming an id no part
    /// defines makes Word report the file as damaged; a part nothing references is a note that
    /// never appears.
    /// </remarks>
    [Fact]
    public void DocxWritesAReferenceAndTheNotesPart()
    {
        Document doc = Doc(Para(("Body text", "the note itself")));

        XDocument document = DocxPart(doc, "word/document.xml");
        XDocument footnotes = DocxPart(doc, "word/footnotes.xml");

        string id = document.Descendants(W + "footnoteReference").Single()
            .Attribute(W + "id")!.Value;

        XElement note = footnotes.Descendants(W + "footnote")
            .Single(f => f.Attribute(W + "id")?.Value == id);

        Assert.Contains("the note itself",
            string.Concat(note.Descendants(W + "t").Select(t => t.Value)), StringComparison.Ordinal);
    }

    /// <summary>The mandatory separator notes are written.</summary>
    /// <remarks>
    /// §17.11.17 reserves ids 0 and 1 for the separator and continuation separator, and Word
    /// repairs a footnotes part that omits them — whether or not anything references them. A real
    /// note therefore starts at 2, which is also asserted: an id of 0 would collide.
    /// </remarks>
    [Fact]
    public void DocxWritesTheMandatorySeparatorNotes()
    {
        XDocument footnotes = DocxPart(Doc(Para(("Body", "note"))), "word/footnotes.xml");

        string[] ids = footnotes.Descendants(W + "footnote")
            .Select(f => f.Attribute(W + "id")!.Value)
            .ToArray();

        Assert.Contains("0", ids);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
    }

    /// <summary>A document with no footnote writes no footnotes part.</summary>
    [Fact]
    public void DocxWritesNoPartWhenThereAreNoNotes()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "Just text" });

        Assert.Null(DocxEntry(Doc(paragraph), "word/footnotes.xml"));
    }

    /// <summary>An explicit mark is written as Word's custom mark, not reported as dropped.</summary>
    /// <remarks>
    /// A custom mark needs <c>w:customMarkFollows</c> (§17.11.14), after which the run's own text is
    /// the mark — not a style this exporter does not write — so all four exports carry it, and a
    /// report saying otherwise would be false. <c>DocxFootnoteMarkTests</c> pins the shape in both
    /// parts.
    /// </remarks>
    [Fact]
    public void DocxWritesAnExplicitMarkRatherThanReportingItDropped()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "A" });
        paragraph.Runs.Add(Note("symbol", mark: "†"));

        Document doc = Doc(paragraph);
        XElement reference = DocxPart(doc, "word/document.xml")
            .Descendants(W + "footnoteReference").Single();

        Assert.NotNull(reference.Attribute(W + "customMarkFollows"));
        Assert.DoesNotContain("explicit mark", BuilderDiagnostics.LastError?.Detail ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>An ordinary note raises no such report.</summary>
    [Fact]
    public void DocxRaisesNoReportForAnOrdinaryNote()
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(Doc(Para(("Body", "note"))), ms);

        Assert.DoesNotContain("explicit mark", BuilderDiagnostics.LastError?.Detail ?? "",
            StringComparison.Ordinal);
    }

    // ==================================================================== helpers

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Footnote Note(string text, string? mark = null)
    {
        var note = new Footnote { Mark = mark };
        note.Runs.Add(new TextRun { Text = text });
        return note;
    }

    /// <summary>A paragraph of (text, note) pairs: each text run followed by its own footnote.</summary>
    private static Paragraph Para(params (string Text, string NoteText)[] parts)
    {
        var paragraph = new Paragraph();
        foreach ((string text, string noteText) in parts)
        {
            paragraph.Runs.Add(new TextRun { Text = text });
            paragraph.Runs.Add(Note(noteText));
        }
        return paragraph;
    }

    private static Document Doc(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (IDocElement child in children) section.Children.Add(child);
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>
    /// Enough paragraphs to fill three pages, optionally with a note on the first.
    /// </summary>
    /// <remarks>
    /// <b>Three, so that page two is a full page rather than the document's tail.</b> With two
    /// pages the second holds whatever was left over — six lines, say — and comparing it
    /// against the first measures where the text ran out, not what the notes reserved.
    /// </remarks>
    private static Document ManyParagraphs(bool withNote)
    {
        var doc = new Document();
        var section = new Section();

        for (int i = 0; i < 120; i++)
        {
            var paragraph = new Paragraph();
            paragraph.Runs.Add(new TextRun { Text = "Line" + i.ToString() });
            if (withNote && i == 0) paragraph.Runs.Add(Note("a note that takes room"));
            section.Children.Add(paragraph);
        }

        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>Every footnote mark the exporters agree on, in document order.</summary>
    /// <remarks>
    /// Read from the PDF export's own numbering rather than from the model: the model holds no
    /// mark for an automatically numbered note, which is the point — the number is the exporter's
    /// to assign.
    /// </remarks>
    private static string[] Marks(Document doc)
    {
        // The exporter's own numbering, reached through InternalsVisibleTo: the model holds no
        // mark for an automatically numbered note, which is the point -- the number is the
        // exporter's to assign, and asserting the model would assert nothing.
        FootnoteNumbering numbering = FootnoteNumbering.For(doc);
        return CollectNotes(doc).Select(numbering.MarkOf).ToArray();
    }

    private static IEnumerable<Footnote> CollectNotes(Document doc)
    {
        foreach (Section section in doc.Sections)
            foreach (IDocElement child in section.Children)
                foreach (Footnote note in NotesOf(child))
                    yield return note;
    }

    private static IEnumerable<Footnote> NotesOf(IDocElement block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                foreach (IInlineElement run in paragraph.Runs)
                    if (run is Footnote note) yield return note;
                break;

            case Table table:
                foreach (TableRow row in table.Rows)
                    foreach (TableCell cell in row.Cells)
                        foreach (IDocElement content in cell.Content())
                            foreach (Footnote note in NotesOf(content))
                                yield return note;
                break;
        }
    }

    private static byte[] Pdf(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsPdf(ms);
        return ms.ToArray();
    }

    private static string RawPdf(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    /// <summary>The decoded text of every content stream in the file.</summary>
    private static string PdfText(Document doc) => DecodeStreams(Pdf(doc));

    private static string Html(Document doc)
    {
        using var ms = new MemoryStream();
        new HtmlExporter().Export(doc, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Odt(Document doc)
    {
        using var ms = new MemoryStream();
        new OdtExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using Stream content = zip.GetEntry("content.xml")!.Open();
        using var reader = new StreamReader(content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static XDocument DocxPart(Document doc, string entryName)
    {
        byte[]? bytes = DocxEntry(doc, entryName);
        Assert.True(bytes != null, $"the package has no {entryName}");
        return XDocument.Parse(Encoding.UTF8.GetString(bytes!));
    }

    private static byte[]? DocxEntry(Document doc, string entryName)
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = zip.GetEntry(entryName);
        if (entry == null) return null;

        using Stream stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>How many body lines each page carries, in page order.</summary>
    /// <remarks>
    /// Pages are separated by the baseline jumping back up: every page starts at the top margin, so
    /// a line higher than the one before it is the first line of the next page.
    /// </remarks>
    private static int[] BodyLinesPerPage(Document doc)
    {
        List<PdfContentProbe.DrawnText> body = Drawn(doc)
            .Where(d => d.Text.StartsWith("Line", StringComparison.Ordinal))
            .ToList();

        Assert.True(body.Count > 1, "the fixture drew no body lines");

        var pages = new List<int>();
        int onThisPage = 1;

        for (int i = 1; i < body.Count; i++)
        {
            if (body[i].Y > body[i - 1].Y) { pages.Add(onThisPage); onThisPage = 1; continue; }
            onThisPage++;
        }
        pages.Add(onThisPage);

        return pages.ToArray();
    }

    /// <summary>How many body lines are drawn before the first page break.</summary>
    /// <remarks>
    /// The break is found by the Y jumping back up: every page starts at the top margin, so the
    /// first line whose baseline is higher than the one before it is the first line of page two.
    /// Only body lines are counted — the note is drawn when the page closes, so it appears
    /// among page one's draws with a low Y and would otherwise be counted as body.
    /// </remarks>
    private static int FirstPageBodyLines(Document doc)
    {
        List<PdfContentProbe.DrawnText> body = Drawn(doc)
            .Where(d => d.Text.StartsWith("Line", StringComparison.Ordinal))
            .ToList();

        Assert.True(body.Count > 1, "the fixture drew no body lines");

        for (int i = 1; i < body.Count; i++)
            if (body[i].Y > body[i - 1].Y) return i;

        return body.Count;
    }

    /// <summary>The Y of the text-positioning operator that precedes a drawn string.</summary>
    /// <remarks>
    /// <b>Position, not order.</b> The note is emitted when the page closes, so it is last in the
    /// stream wherever it sits on the page — only the coordinate says whether it is at the foot.
    /// </remarks>
    private static double YOf(List<PdfContentProbe.DrawnText> drawn, string text)
    {
        // A struct, so FirstOrDefault yields a blank rather than null: the index is what says
        // whether it was found.
        int at = drawn.FindIndex(d => d.Text == text);

        Assert.True(at >= 0,
            $"'{text}' was never drawn; the page holds: "
            + string.Join(", ", drawn.Select(d => d.ToString())));

        return drawn[at].Y;
    }

    /// <summary>How many runs the page draws with a positive text rise.</summary>
    /// <remarks>
    /// <para><b>Superscript is a text-state operator, so it never shows in a text matrix.</b> The
    /// mark and the word beside it are drawn at the same <c>Tm</c> Y; the rise between them is the
    /// whole difference, and reading positions alone cannot see it.</para>
    ///
    /// <para><b>Counted rather than maximised, because a page with one footnote raises twice.</b>
    /// The note draws its own mark in front of its text at the foot of the page, so asking whether
    /// <i>any</i> rise appears is answered by that one whatever the inline mark does.</para>
    /// </remarks>
    private static int SuperscriptRuns(Document doc)
    {
        int count = 0;

        foreach (Match m in Regex.Matches(PdfContentProbe.DecodeStreams(Pdf(doc)),
                                          @"(-?[\d.]+)\s+Ts"))
        {
            double rise = double.Parse(m.Groups[1].Value,
                                       System.Globalization.CultureInfo.InvariantCulture);
            if (rise > 0) count++;
        }

        return count;
    }

    /// <summary>Every positioned text draw on the exported page, with its coordinates.</summary>
    /// <remarks>
    /// <b>Position, not stream order.</b> A note is emitted when the page closes, so it comes last
    /// in the content whether it sits at the foot of the page or on top of the body — only the
    /// coordinate says which.
    /// </remarks>
    private static List<PdfContentProbe.DrawnText> Drawn(Document doc)
        => PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(Pdf(doc)));

    private static string DecodeStreams(byte[] pdf) => PdfContentProbe.DecodeStreams(pdf);
}
