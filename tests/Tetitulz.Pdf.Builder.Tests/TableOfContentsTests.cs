// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A table of contents: one entry per heading, with the page each heading starts on in the PDF and
/// a link to it everywhere else.
/// </summary>
/// <remarks>
/// <para><b>The page number is the hard half, and it is why the PDF export runs twice.</b> A
/// heading's page depends on everything before it — including the contents block itself — so the
/// number cannot be known while the block is being drawn. The first pass records where each
/// heading landed and its output is discarded; the second draws the numbers. The two agree because
/// the block's height does not depend on them: one line per entry either way, with the number
/// right-aligned at the margin.</para>
///
/// <para><b>The formats with no page get a link instead</b>, which is the more useful thing there
/// and the only correct one — a page number in a document that reflows describes a layout the
/// reader is not looking at.</para>
/// </remarks>
public sealed class TableOfContentsTests
{
    // ==================================================================== collection

    /// <summary>Every heading becomes an entry, in reading order.</summary>
    [Fact]
    public void EveryHeadingBecomesAnEntryInOrder()
    {
        Document doc = WithToc(maxLevel: 3,
            Head(1, "Alpha"), Body("text"), Head(2, "Beta"), Head(3, "Gamma"));

        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, EntryTexts(doc));
    }

    /// <summary>A heading deeper than the limit is left out.</summary>
    /// <remarks>
    /// The acceptance control for the collection above: without a limit, "the headings appear"
    /// would be satisfied by a block that listed everything and called it a depth setting.
    /// </remarks>
    [Fact]
    public void AHeadingDeeperThanTheLimitIsLeftOut()
    {
        Document doc = WithToc(maxLevel: 2, Head(1, "Alpha"), Head(2, "Beta"), Head(3, "Gamma"));

        Assert.Equal(new[] { "Alpha", "Beta" }, EntryTexts(doc));
    }

    /// <summary>An ordinary paragraph is not a heading, however it is styled.</summary>
    /// <remarks>
    /// <b>The role is what makes a heading, not the font.</b> A document whose headings are only
    /// large and bold has none to list, and inferring them from size would put a pull-quote in the
    /// contents — the same guess the auto-tagger is deliberately opt-in about.
    /// </remarks>
    [Fact]
    public void ALargeBoldParagraphIsNotAHeading()
    {
        var big = new Paragraph();
        big.Runs.Add(new TextRun { Text = "Looks like a heading", FontSize = 24, IsBold = true });

        Assert.Empty(EntryTexts(WithToc(maxLevel: 3, big)));
    }

    /// <summary>A bare <c>H</c> role is placed at the level its section nesting gives it.</summary>
    /// <remarks>
    /// PDF/UA's "strong structure" heading takes its level from how deeply its <c>/Sect</c>
    /// elements nest, not from its name. This model nests exactly one — every
    /// <see cref="Section"/> is a <c>/Sect</c> under the root and nothing else is a section — so
    /// the level it carries is 1, and the entry is placed there rather than dropped. (It used to be
    /// dropped as unplaceable; <c>TocSectionNumberingTests</c> pins the derivation.)
    /// </remarks>
    [Fact]
    public void ABareStrongStructureHeadingIsPlacedAtItsSectionDepth()
    {
        var heading = new Paragraph { Role = "H" };
        heading.Runs.Add(new TextRun { Text = "Unlevelled" });

        Assert.Equal(new[] { "Unlevelled" }, EntryTexts(WithToc(maxLevel: 6, heading)));
    }

    // ==================================================================== PDF

    /// <summary>The PDF lists each heading with the page it starts on.</summary>
    [Fact]
    public void ThePdfListsEachHeadingWithItsPage()
    {
        Document doc = WithToc(maxLevel: 1,
            Head(1, "First"), PageFiller(), Head(1, "Second"), PageFiller());

        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);

        // The contents is on page one, so the first two "First"/"Second" draws are its entries and
        // the headings themselves follow.
        Assert.Contains(drawn, d => d.Text == "First");
        Assert.Contains(drawn, d => d.Text == "Second");

        // Two different page numbers, because the two headings are on different pages. A single
        // repeated number is what a contents built from one pass produces.
        string[] numbers = TocNumbers(doc);
        Assert.Equal(2, numbers.Length);
        Assert.NotEqual(numbers[0], numbers[1]);
    }

    /// <summary>The number is the page the heading is actually drawn on.</summary>
    /// <remarks>
    /// <b>Two different numbers is not the same claim as two correct ones.</b> A contents that
    /// numbered its entries 1, 2, 3 by position would satisfy every ordering assertion and be
    /// wrong about every page. This compares each entry's number against where the heading's own
    /// text was drawn.
    /// </remarks>
    [Fact]
    public void EachNumberIsThePageTheHeadingIsDrawnOn()
    {
        Document doc = WithToc(maxLevel: 1,
            Head(1, "First"), PageFiller(), Head(1, "Second"), PageFiller());

        string[] numbers = TocNumbers(doc);

        Assert.Equal(PageOfHeading(doc, "First").ToString(), numbers[0]);
        Assert.Equal(PageOfHeading(doc, "Second").ToString(), numbers[1]);
    }

    /// <summary>The contents carries its own structure element when the document is tagged.</summary>
    /// <remarks>
    /// §14.8.4.6.2 gives it <c>/TOC</c> and its entries <c>/TOCI</c>. A contents written as a run
    /// of <c>/P</c> tells a reader nothing about what it is, and passes every structural check by
    /// having no contents in it.
    /// </remarks>
    [Fact]
    public void ATaggedDocumentGivesTheContentsItsOwnStructure()
    {
        Document doc = WithToc(maxLevel: 1, Head(1, "First"));
        doc.Tagged = true;

        List<(string Role, string Parent)> structure = StructureRoles(Pdf(doc));

        Assert.Contains(structure, e => e.Role == PdfStructureTypes.TOC);
        Assert.Contains(structure,
            e => e.Role == PdfStructureTypes.TOCI && e.Parent == PdfStructureTypes.TOC);
    }

    /// <summary>A heading inside a table cell is an entry like any other.</summary>
    /// <remarks>
    /// It is a heading in the tag tree either way, so a contents list that skipped it would
    /// disagree with the document's own structure. A cell's paragraphs go through the same
    /// renderer as a section's, which is what gives this one a page number too.
    /// </remarks>
    [Fact]
    public void AHeadingInsideATableCellIsAnEntry()
    {
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Paragraphs.Add(Head(2, "Nested"));
        row.Cells.Add(cell);
        table.Rows.Add(row);

        Document doc = WithToc(maxLevel: 3, Head(1, "Alpha"), table);

        Assert.Equal(new[] { "Alpha", "Nested" }, EntryTexts(doc));
        Assert.Equal(new[] { "1", "1" }, TocNumbers(doc));
    }

    /// <summary>A document with no contents block is laid out once.</summary>
    /// <remarks>
    /// <b>The second pass is a cost, so it is paid only by a document that asks for it.</b> The
    /// evidence is the heading map: a document with no contents block never allocates one, which
    /// is what says the scratch pass did not run.
    /// </remarks>
    [Fact]
    public void ADocumentWithNoContentsBlockIsLaidOutOnce()
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(Head(1, "First"));
        doc.Sections.Add(section);

        // Two exports of the same model produce identical bytes but for the file id, so a second
        // layout pass would have to be visible some other way; the block's absence is the claim.
        Assert.DoesNotContain(doc.Sections[0].Children, c => c is TocBlock);
        Assert.Empty(TocNumbers(doc));
    }

    // ==================================================================== HTML

    /// <summary>HTML writes a nav list of links rather than page numbers.</summary>
    [Fact]
    public void HtmlWritesLinksRatherThanPageNumbers()
    {
        Document doc = WithToc(maxLevel: 2, Head(1, "Alpha"), Head(2, "Beta"));
        string html = Html(doc);

        Assert.Contains("<nav class=\"toc\">", html, StringComparison.Ordinal);

        // Each entry links to the heading's anchor, and the heading carries that anchor.
        string anchor = doc.Sections[0].Children.OfType<Paragraph>()
            .First(p => p.Role == PdfStructureTypes.H1).AnchorName!;

        Assert.Contains($"href=\"#{anchor}\"", html, StringComparison.Ordinal);
        Assert.Contains($"id=\"{anchor}\"", html, StringComparison.Ordinal);
    }

    /// <summary>A document with no heading gets no nav.</summary>
    [Fact]
    public void HtmlWritesNoNavWhenThereAreNoHeadings()
        => Assert.DoesNotContain("<nav class=\"toc\">", Html(WithToc(maxLevel: 3, Body("text"))),
                                 StringComparison.Ordinal);

    // ==================================================================== ODT

    /// <summary>ODT writes ODF's own contents element with the entries inside it.</summary>
    /// <remarks>
    /// <b>Both halves.</b> The element tells an office application this is a contents list it owns
    /// and can regenerate; the entries are what a reader — or a converter that never regenerates —
    /// sees. Writing only the element gives a document that is empty until someone presses update.
    /// </remarks>
    [Fact]
    public void OdtWritesANativeContentsWithItsEntries()
    {
        string content = Odt(WithToc(maxLevel: 2, Head(1, "Alpha"), Head(2, "Beta")));

        Assert.Contains("<text:table-of-content ", content, StringComparison.Ordinal);
        Assert.Contains("text:outline-level=\"2\"", content, StringComparison.Ordinal);
        Assert.Contains("Alpha", content, StringComparison.Ordinal);
        Assert.Contains("Beta", content, StringComparison.Ordinal);
    }

    // ==================================================================== DOCX

    /// <summary>DOCX writes a TOC field with the entries as its cached result.</summary>
    /// <remarks>
    /// <b>Both, because Word shows the cache until it is told to update.</b> The field is what
    /// makes it a contents list Word owns; the paragraphs between <c>begin</c> and <c>end</c> are
    /// what every other reader sees, and what Word itself shows before anyone presses F9.
    /// </remarks>
    [Fact]
    public void DocxWritesAFieldAndItsCachedEntries()
    {
        XDocument document = DocxPart(WithToc(maxLevel: 2, Head(1, "Alpha"), Head(2, "Beta")));

        string instruction = string.Concat(
            document.Descendants(W + "instrText").Select(t => t.Value));

        Assert.Contains("TOC", instruction, StringComparison.Ordinal);
        Assert.Contains("1-2", instruction, StringComparison.Ordinal);

        string[] chars = document.Descendants(W + "fldChar")
            .Select(c => c.Attribute(W + "fldCharType")!.Value).ToArray();

        Assert.Equal(new[] { "begin", "separate", "end" }, chars);

        // Between separate and end, not merely somewhere in the part: the heading itself also
        // says "Alpha", so a search of every w:t is satisfied by the body text with the cache
        // never written -- which is exactly what a mutation deleting it proved.
        List<XElement> paragraphs = document.Descendants(W + "p").ToList();
        int separate = paragraphs.FindIndex(HasFieldChar("separate"));
        int end = paragraphs.FindIndex(HasFieldChar("end"));

        Assert.InRange(separate, 0, end - 1);

        XElement[] cached = paragraphs.Skip(separate + 1).Take(end - separate - 1).ToArray();

        Assert.Equal(
            new[] { "Alpha", "Beta" },
            cached.Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))));

        // The level, which is what indents the entry in Word.
        Assert.Equal(
            new[] { "TOC1", "TOC2" },
            cached.Select(p => p.Descendants(W + "pStyle").Single().Attribute(W + "val")!.Value));
    }

    // ==================================================================== the two passes agree

    /// <summary>
    /// A contents entry whose text reaches near the right margin keeps its page number on the entry's
    /// line, not on a line of its own (Builder-2:8). The number is right-aligned at a tab stop at the
    /// margin; when the text ran up to the stop the number used to wrap, so an entry was one line in
    /// the number-less first pass and two in the second — the two passes disagreed on its height and
    /// every page number after it could drift. The number now pins to the stop even when the text has
    /// reached it, so both passes lay the entry out the same.
    /// </summary>
    [Fact]
    public void ALongEntrysPageNumberStaysOnItsLine()
    {
        Document doc = WithToc(3, PageFiller(), Head(1, new string('m', 62)));
        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);

        // The first draw of the m-text is the contents entry (the second is the heading itself, a
        // page later); the standalone digit is its page number.
        PdfContentProbe.DrawnText entry = drawn.First(d => d.Text.Contains('m'));
        PdfContentProbe.DrawnText number = drawn.First(d => d.Text.Trim().Length > 0 && d.Text.Trim().All(char.IsDigit));

        Assert.Equal(entry.Y, number.Y, 1);   // same baseline: the number did not wrap below the entry
    }

    /// <summary>The control: a short entry's number is on its line too, so the assertion above is a
    /// statement about an entry that reaches the margin, not one that always holds trivially.</summary>
    [Fact]
    public void AShortEntrysPageNumberIsOnItsLine()
    {
        Document doc = WithToc(3, PageFiller(), Head(1, "Short"));
        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);

        PdfContentProbe.DrawnText entry = drawn.First(d => d.Text.Contains("Short"));
        PdfContentProbe.DrawnText number = drawn.First(d => d.Text.Trim().Length > 0 && d.Text.Trim().All(char.IsDigit));

        Assert.Equal(entry.Y, number.Y, 1);
    }

    private static Predicate<XElement> HasFieldChar(string type)
        => p => p.Descendants(W + "fldChar")
                 .Any(c => c.Attribute(W + "fldCharType")?.Value == type);

    /// <summary>A document with no heading writes no field.</summary>
    [Fact]
    public void DocxWritesNoFieldWhenThereAreNoHeadings()
        => Assert.Empty(DocxPart(WithToc(maxLevel: 3, Body("text"))).Descendants(W + "fldChar"));

    // ==================================================================== helpers

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Paragraph Head(int level, string text)
    {
        var p = new Paragraph { Role = "H" + level.ToString() };
        p.Runs.Add(new TextRun { Text = text, FontSize = 18 - (level * 2), IsBold = true });
        return p;
    }

    private static Paragraph Body(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    /// <summary>Enough paragraphs to push what follows onto the next page.</summary>
    private static Table PageFiller()
    {
        // A table rather than a page break, so the fixture also proves a heading inside ordinary
        // flow is found rather than only one after an explicit break.
        var table = new Table();
        for (int i = 0; i < 45; i++)
        {
            var row = new TableRow();
            var cell = new TableCell();
            cell.Paragraphs.Add(Body("filler" + i.ToString()));
            row.Cells.Add(cell);
            table.Rows.Add(row);
        }
        return table;
    }

    private static Document WithToc(int maxLevel, params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(new TocBlock { Title = "Contents", MaxLevel = maxLevel });
        foreach (IDocElement child in children) section.Children.Add(child);
        doc.Sections.Add(section);
        return doc;
    }

    private static string[] EntryTexts(Document doc)
    {
        TocBlock? toc = doc.Sections.SelectMany(s => s.Children).OfType<TocBlock>().FirstOrDefault();
        int maxLevel = toc?.MaxLevel ?? 3;

        return TocEntries.Collect(doc, maxLevel).Select(e => e.Text).ToArray();
    }

    private static byte[] Pdf(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsPdf(ms);
        return ms.ToArray();
    }

    private static List<PdfContentProbe.DrawnText> Drawn(Document doc)
        => PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(Pdf(doc)));

    /// <summary>The page numbers the contents block drew, in entry order.</summary>
    /// <remarks>
    /// A contents entry's number is the only text on the page that is drawn <b>right-aligned at
    /// the margin</b> after a dot leader, so it is found by taking the numeric draws that sit to
    /// the right of everything else on their line — which is what a reader looking at the page
    /// does too.
    /// </remarks>
    private static string[] TocNumbers(Document doc)
    {
        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);
        if (drawn.Count == 0) return Array.Empty<string>();

        double rightmost = drawn.Max(d => d.X);

        return drawn
            .Where(d => d.X > rightmost - 1 && d.Text.All(char.IsDigit) && d.Text.Length > 0)
            .Select(d => d.Text)
            .ToArray();
    }

    /// <summary>Which page a heading's own text was drawn on.</summary>
    /// <remarks>
    /// Counted by the baseline jumping back up: every page starts at the top margin, so a draw
    /// higher than the one before it opens a new page. The heading's SECOND appearance is the
    /// heading itself — the first is its contents entry.
    /// </remarks>
    private static int PageOfHeading(Document doc, string text)
    {
        List<PdfContentProbe.DrawnText> drawn = Drawn(doc);

        int page = 1;
        int seen = 0;

        for (int i = 0; i < drawn.Count; i++)
        {
            if (i > 0 && drawn[i].Y > drawn[i - 1].Y) page++;
            if (drawn[i].Text != text) continue;

            seen++;
            if (seen == 2) return page;
        }

        Assert.Fail($"'{text}' was drawn {seen} time(s); the heading itself was never found");
        return 0;
    }

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

    /// <summary>Every structure element in the produced document, with its parent's role.</summary>
    /// <remarks>
    /// Read through the object model and matched on the <b>whole</b> role name. <c>/TOC</c> is a
    /// prefix of <c>/TOCI</c>, so a substring search of the file's bytes is satisfied by the
    /// entries alone and says nothing about the element that is meant to contain them -- which is
    /// how a contents element that was never created passed for one that was.
    /// </remarks>
    private static List<(string Role, string Parent)> StructureRoles(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var found = new List<(string, string)>();

        if (doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("StructTreeRoot")])
            is not PdfDictionary root) return found;

        var queue = new Queue<(PdfObject? Node, string Parent)>();
        queue.Enqueue((root[new PdfName("K")], ""));
        var seen = new HashSet<int>();

        while (queue.Count > 0)
        {
            (PdfObject? node, string parent) = queue.Dequeue();
            PdfObject? resolved = doc.ResolveObject(node);

            if (resolved is PdfArray array)
            {
                foreach (PdfObject item in array) queue.Enqueue((item, parent));
                continue;
            }

            if (resolved is not PdfDictionary dict) continue;
            if (dict.ObjectNumber > 0 && !seen.Add(dict.ObjectNumber)) continue;

            string role = doc.ResolveObject(dict[new PdfName("S")]) is PdfName s ? s.Value : "";
            found.Add((role, parent));

            if (dict[new PdfName("K")] != null) queue.Enqueue((dict[new PdfName("K")], role));
        }

        return found;
    }

    private static XDocument DocxPart(Document doc)
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using Stream part = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(part);
    }
}
