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
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Endnotes (<see cref="NotePlacement.DocumentEnd"/>): a reference mark where the note belongs in
/// the text, the note itself after the document's last content — in the PDF, flowed under a rule
/// after the last section's content and paginating as body text; in DOCX the endnotes part and a
/// <c>w:endnoteReference</c>; in ODT a <c>text:note</c> of class <c>endnote</c>; in HTML a second
/// section after the footnotes. Footnotes and endnotes are numbered in two sequences.
/// </summary>
/// <remarks>
/// Every placement claim has the footnote's placement as its control, in the same fixture: the
/// footnote's content stays on the page of its mark, the endnote's leaves it.
/// </remarks>
public sealed class EndnoteTests
{
    // ==================================================================== numbering

    /// <summary>Footnotes and endnotes count in two sequences: footnote 1, endnote 1, footnote 2, endnote 2.</summary>
    [Fact]
    public void FootnotesAndEndnotes_AreNumberedInTwoSequences()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "A" });
        paragraph.Runs.Add(Note("f one", NotePlacement.PageFoot));
        paragraph.Runs.Add(new TextRun { Text = "B" });
        paragraph.Runs.Add(Note("e one", NotePlacement.DocumentEnd));
        paragraph.Runs.Add(new TextRun { Text = "C" });
        paragraph.Runs.Add(Note("f two", NotePlacement.PageFoot));
        paragraph.Runs.Add(new TextRun { Text = "D" });
        paragraph.Runs.Add(Note("e two", NotePlacement.DocumentEnd));

        FootnoteNumbering numbering = FootnoteNumbering.For(Doc(paragraph));
        string[] marks = paragraph.Runs.OfType<Footnote>().Select(numbering.MarkOf).ToArray();

        Assert.Equal(new[] { "1", "1", "2", "2" }, marks);
        Assert.Equal(new[] { "e one", "e two" }, numbering.Endnotes.Select(n => ((TextRun)n.Runs[0]).Text));
    }

    /// <summary>The fluent chain makes an endnote with the placement set, beside the current run.</summary>
    [Fact]
    public void AddEndnote_PlacesTheNoteAtTheDocumentEnd()
    {
        Document doc = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddText("Body").AddEndnote("the end note").AddFootnote("the foot note")))
            .Build();

        Footnote[] notes = doc.Sections[0].Children.OfType<Paragraph>().Single().Runs.OfType<Footnote>().ToArray();
        Assert.Equal(NotePlacement.DocumentEnd, notes[0].Placement);
        Assert.Equal(NotePlacement.PageFoot, notes[1].Placement);
    }

    // ==================================================================== PDF

    /// <summary>
    /// A document of three pages with a footnote and an endnote on its first line: the footnote's
    /// content is drawn on page one, the endnote's after the last body line, on the last page —
    /// and page one keeps as many body lines as a page with no note at all, since nothing is
    /// reserved for an endnote.
    /// </summary>
    [Fact]
    public void ThePdfDrawsAnEndnoteAfterTheLastContent_AndReservesNothingForItOnTheMarksPage()
    {
        byte[] pdf = Pdf(ManyParagraphs(footnote: "FOOTNOTETEXT", endnote: "ENDNOTETEXT"));
        List<PdfContentProbe.DrawnText>[] pages = DrawnPerPage(pdf);
        Assert.True(pages.Length >= 3, $"the fixture produced {pages.Length} page(s); the end is not past the mark's page");

        // The footnote stays with its mark; the endnote is nowhere on that page.
        Assert.Contains(pages[0], d => d.Text == "FOOTNOTETEXT");
        Assert.DoesNotContain(pages[0], d => d.Text == "ENDNOTETEXT");

        // The endnote is on the last page, below the last body line.
        List<PdfContentProbe.DrawnText> last = pages[pages.Length - 1];
        PdfContentProbe.DrawnText end = Assert.Single(last, d => d.Text == "ENDNOTETEXT");
        double lastBody = last.Where(d => d.Text.StartsWith("Line", StringComparison.Ordinal)).Min(d => d.Y);
        Assert.True(end.Y < lastBody, $"the endnote is at y={end.Y:F1}, the last body line at y={lastBody:F1}");

        // Both marks sit beside the first line: the footnote's "1" and the endnote's "1", two sequences.
        Assert.Equal(2, pages[0].Count(d => d.Text == "1" && Math.Abs(d.Y - pages[0].First(b => b.Text == "Line0").Y) < 6));

        // Nothing reserved: page one of a document with only the endnote holds as many body lines
        // as one with no note; the footnote's document holds fewer.
        int[] none = BodyLinesPerPage(Pdf(ManyParagraphs(null, null)));
        int[] endOnly = BodyLinesPerPage(Pdf(ManyParagraphs(null, "ENDNOTETEXT")));
        int[] footOnly = BodyLinesPerPage(Pdf(ManyParagraphs("FOOTNOTETEXT", null)));
        Assert.Equal(none[0], endOnly[0]);
        Assert.True(footOnly[0] < none[0], "the footnote control reserved nothing either, so the comparison proves nothing");
    }

    /// <summary>
    /// Endnotes that do not fit the last page continue on a new one — flowed as body text, where a
    /// footnote may never break the page.
    /// </summary>
    [Fact]
    public void EndnotesThatOverflowTheLastPage_ContinueOnANewPage()
    {
        // Sixty long endnotes on the first line: far more than a page.
        var doc = new Document();
        var section = new Section();
        var first = new Paragraph();
        first.Runs.Add(new TextRun { Text = "Line0" });
        for (int i = 0; i < 60; i++)
            first.Runs.Add(Note("ENDNOTE" + i + " " + string.Join(" ", Enumerable.Repeat("words", 20)), NotePlacement.DocumentEnd));
        section.Children.Add(first);
        doc.Sections.Add(section);

        List<PdfContentProbe.DrawnText>[] pages = DrawnPerPage(Pdf(doc));
        Assert.True(pages.Length >= 2, "sixty endnotes fitted one page; the fixture proves nothing about overflow");
        Assert.Contains(pages[0], d => d.Text == "ENDNOTE0");
        Assert.Contains(pages[pages.Length - 1], d => d.Text == "ENDNOTE59");
        Assert.Equal(60, pages.Sum(p => p.Count(d => d.Text.StartsWith("ENDNOTE", StringComparison.Ordinal))));
    }

    /// <summary>A tagged document gives an endnote its own <c>/Note</c> element, as it gives a footnote.</summary>
    [Fact]
    public void ATaggedDocumentGivesTheEndnoteItsOwnStructureElement()
    {
        Document doc = ManyParagraphs(null, "ENDNOTETEXT");
        doc.Tagged = true;
        string raw = Encoding.Latin1.GetString(Pdf(doc));

        Assert.Contains("/S /Note", raw, StringComparison.Ordinal);
    }

    // ==================================================================== DOCX

    /// <summary>DOCX writes a <c>w:endnoteReference</c> and the endnotes part, and no footnotes part for a document with endnotes only.</summary>
    [Fact]
    public void DocxWritesAnEndnoteReferenceAndTheEndnotesPart()
    {
        Document doc = Doc(Para("Body", "the end note", NotePlacement.DocumentEnd));

        XDocument document = DocxPart(doc, "word/document.xml");
        XElement reference = Assert.Single(document.Descendants(W + "endnoteReference"));
        Assert.Empty(document.Descendants(W + "footnoteReference"));
        string id = reference.Attribute(W + "id")!.Value;

        XDocument endnotes = DocxPart(doc, "word/endnotes.xml");
        XElement note = Assert.Single(endnotes.Descendants(W + "endnote"), e => e.Attribute(W + "id")?.Value == id);
        Assert.Contains("the end note", note.Value, StringComparison.Ordinal);
        Assert.Single(endnotes.Descendants(W + "endnoteRef"));

        // The two separators the part must define, and no footnotes part at all.
        Assert.Equal(new[] { "separator", "continuationSeparator" },
            endnotes.Descendants(W + "endnote").Select(e => e.Attribute(W + "type")?.Value).Where(t => t != null).ToArray());
        Assert.Null(DocxEntry(doc, "word/footnotes.xml"));
    }

    /// <summary>A footnote and an endnote in one document go to their own parts.</summary>
    [Fact]
    public void DocxKeepsFootnotesAndEndnotesInTheirOwnParts()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "Body" });
        paragraph.Runs.Add(Note("the foot note", NotePlacement.PageFoot));
        paragraph.Runs.Add(Note("the end note", NotePlacement.DocumentEnd));
        Document doc = Doc(paragraph);

        Assert.Contains("the foot note", DocxPart(doc, "word/footnotes.xml").Root!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("the end note", DocxPart(doc, "word/footnotes.xml").Root!.Value, StringComparison.Ordinal);
        Assert.Contains("the end note", DocxPart(doc, "word/endnotes.xml").Root!.Value, StringComparison.Ordinal);
    }

    // ==================================================================== HTML and ODT

    /// <summary>HTML writes the endnotes in their own section after the footnotes, with their own anchors.</summary>
    [Fact]
    public void HtmlWritesTheEndnotesInTheirOwnSectionAfterTheFootnotes()
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = "Body" });
        paragraph.Runs.Add(Note("the foot note", NotePlacement.PageFoot));
        paragraph.Runs.Add(Note("the end note", NotePlacement.DocumentEnd));
        string html = Html(Doc(paragraph));

        int footnotes = html.IndexOf("<section class=\"footnotes\">", StringComparison.Ordinal);
        int endnotes = html.IndexOf("<section class=\"endnotes\">", StringComparison.Ordinal);
        Assert.True(footnotes >= 0 && endnotes > footnotes, "the endnotes section is missing or precedes the footnotes");
        Assert.Contains("<sup id=\"enref1\"><a href=\"#en1\">1</a></sup>", html, StringComparison.Ordinal);
        Assert.Contains("<li id=\"en1\"><p>the end note", html, StringComparison.Ordinal);
        Assert.Contains("<li id=\"fn1\"><p>the foot note", html, StringComparison.Ordinal);
    }

    /// <summary>ODT writes an endnote as a <c>text:note</c> of class <c>endnote</c>.</summary>
    [Fact]
    public void OdtWritesANativeEndnote()
    {
        string content = Odt(Doc(Para("Body", "the end note", NotePlacement.DocumentEnd)));

        Assert.Contains("<text:note text:note-class=\"endnote\" text:id=\"edn1\">", content, StringComparison.Ordinal);
        Assert.Contains("the end note", content, StringComparison.Ordinal);
        Assert.DoesNotContain("text:note-class=\"footnote\"", content, StringComparison.Ordinal);
    }

    // ==================================================================== helpers

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static Footnote Note(string text, NotePlacement placement)
    {
        var note = new Footnote { Placement = placement };
        note.Runs.Add(new TextRun { Text = text });
        return note;
    }

    private static Paragraph Para(string text, string noteText, NotePlacement placement)
    {
        var paragraph = new Paragraph();
        paragraph.Runs.Add(new TextRun { Text = text });
        paragraph.Runs.Add(Note(noteText, placement));
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

    /// <summary>Enough paragraphs to fill three pages, with the given notes on the first line.</summary>
    private static Document ManyParagraphs(string? footnote, string? endnote)
    {
        var doc = new Document();
        var section = new Section();
        for (int i = 0; i < 120; i++)
        {
            var paragraph = new Paragraph();
            paragraph.Runs.Add(new TextRun { Text = "Line" + i });
            if (i == 0 && footnote != null) paragraph.Runs.Add(Note(footnote, NotePlacement.PageFoot));
            if (i == 0 && endnote != null) paragraph.Runs.Add(Note(endnote, NotePlacement.DocumentEnd));
            section.Children.Add(paragraph);
        }
        doc.Sections.Add(section);
        return doc;
    }

    private static byte[] Pdf(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsPdf(ms);
        return ms.ToArray();
    }

    /// <summary>The drawn strings of each page, in drawing order, one list per page's content stream.</summary>
    private static List<PdfContentProbe.DrawnText>[] DrawnPerPage(byte[] pdf)
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        var pages = new List<List<PdfContentProbe.DrawnText>>();
        foreach (Tetitulz.Pdf.Core.Document.PdfPage page in doc.Pages)
        {
            byte[] content = page.GetContentStreamData();
            pages.Add(PdfContentProbe.DrawnTexts(Encoding.Latin1.GetString(content)));
        }
        return pages.ToArray();
    }

    /// <summary>Body lines ("Line…") per page.</summary>
    private static int[] BodyLinesPerPage(byte[] pdf)
        => DrawnPerPage(pdf).Select(p => p.Count(d => d.Text.StartsWith("Line", StringComparison.Ordinal))).ToArray();

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
}
