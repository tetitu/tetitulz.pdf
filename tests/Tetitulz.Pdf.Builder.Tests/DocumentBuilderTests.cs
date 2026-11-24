// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Document;
using System.Text;
using Xunit;
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Tests;

public class DocumentBuilderTests
{
    private Document BuildSampleDoc()
    {
        return new DocumentBuilder()
            .WithTitle("Test Invoice")
            .WithAuthor("Test Author")
            .AddSection(s => s
                .PageSize(595, 842)
                .Margins(72, 72, 72, 72)
                .Header(h => h.AddParagraph(p => p.AddText("Company Name").Bold().FontSize(10)))
                .Footer(f => f.AddParagraph(p => p.AddText("Page 1").Italic().FontSize(8)))
                .AddParagraph(p => p
                    .Alignment(TextAlignment.Center)
                    .AddText("Invoice").Bold().FontSize(24).Color("#0078D4"))
                .AddParagraph(p => p
                    .AddText("Hello World").FontSize(12))
                .AddTable(t => t
                    .AddRow(r => r.AddCell("Item").Bold().AddCell("Price").Bold())
                    .AddRow(r => r.AddCell("Widget").AddCell("$10")))
                .AddListItem("First item")
                .AddListItem("Second item", numbered: true)
            )
            .Build();
    }

    // ---- Fluent API tests ----

    [Fact]
    public void Build_CreatesDocumentWithTitle()
    {
        var doc = new DocumentBuilder().WithTitle("Test").Build();
        Assert.Equal("Test", doc.Title);
    }

    [Fact]
    public void Build_CreatesDocumentWithSection()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s.PageSize(100, 200))
            .Build();
        Assert.Single(doc.Sections);
        Assert.Equal(100, doc.Sections[0].PageWidthPt);
    }

    [Fact]
    public void Build_ParagraphWithBoldText()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddText("Bold").Bold()))
            .Build();
        var para = doc.Sections[0].Children[0] as Paragraph;
        var run = para!.Runs[0] as TextRun;
        Assert.True(run!.IsBold);
    }

    [Fact]
    public void Build_TableWithRows()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s.AddTable(t => t
                .AddRow(r => r.AddCell("A").AddCell("B"))
                .AddRow(r => r.AddCell("C").AddCell("D"))))
            .Build();
        var table = doc.Sections[0].Children[0] as Table;
        Assert.Equal(2, table!.Rows.Count);
        Assert.Equal(2, table.Rows[0].Cells.Count);
    }

    [Fact]
    public void Build_HeaderAndFooter()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s
                .Header(h => h.AddParagraph(p => p.AddText("Header")))
                .Footer(f => f.AddParagraph(p => p.AddText("Footer"))))
            .Build();
        Assert.NotNull(doc.Sections[0].Header);
        Assert.NotNull(doc.Sections[0].Footer);
        Assert.Single(doc.Sections[0].Header!.Paragraphs);
    }

    // ---- PDF export ----

    [Fact]
    public void ExportToPdf_ProducesValidPdfBytes()
    {
        var doc = BuildSampleDoc();
        byte[] pdf = doc.ToPdf();
        Assert.True(pdf.Length > 100);
        Assert.Equal((byte)'%', pdf[0]); // %PDF-
        Assert.Equal((byte)'P', pdf[1]);
    }

    [Fact]
    public void ExportToPdf_Stream_Works()
    {
        var doc = BuildSampleDoc();
        using var ms = new MemoryStream();
        doc.SaveAsPdf(ms);
        Assert.True(ms.Length > 100);
    }

    // ---- DOCX export ----

    [Fact]
    public void ExportToDocx_ProducesValidDocxBytes()
    {
        var doc = BuildSampleDoc();
        byte[] docx = doc.ToDocx();
        Assert.True(docx.Length > 100);
        // DOCX is a ZIP file — starts with PK
        Assert.Equal((byte)'P', docx[0]);
        Assert.Equal((byte)'K', docx[1]);
    }

    // ---- HTML export ----

    [Fact]
    public void ExportToHtml_ContainsHtmlTags()
    {
        var doc = BuildSampleDoc();
        byte[] html = doc.ToHtml();
        string content = Encoding.UTF8.GetString(html);
        Assert.Contains("<!DOCTYPE html>", content);
        Assert.Contains("Invoice", content);
        Assert.Contains("<table>", content);
        Assert.Contains("Hello World", content);
    }

    [Fact]
    public void ExportToHtml_BoldTextHasStyle()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddText("Bold").Bold()))
            .Build();
        string html = Encoding.UTF8.GetString(doc.ToHtml());
        Assert.Contains("font-weight:bold", html);
    }

    // ---- ODT export ----

    [Fact]
    public void ExportToOdt_ProducesValidOdtZip()
    {
        var doc = BuildSampleDoc();
        byte[] odt = doc.ToOdt();
        Assert.True(odt.Length > 100);
        // ODT is a ZIP file — starts with PK
        Assert.Equal((byte)'P', odt[0]);
        Assert.Equal((byte)'K', odt[1]);
    }

    /// <summary>
    /// An ODT package must carry the document's text in <c>content.xml</c>, not merely have an
    /// entry with that name. The predecessor stopped at "the entry exists", which an empty
    /// placeholder satisfies — and an ODT whose content.xml is empty opens as a blank document.
    /// </summary>
    [Fact]
    public void ExportToOdt_ContentXmlCarriesTheDocumentText()
    {
        var doc = BuildSampleDoc();
        using var ms = new MemoryStream(doc.ToOdt());
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

        var entry = zip.GetEntry("content.xml");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open());
        string content = reader.ReadToEnd();

        Assert.Contains("Invoice", content);
        Assert.Contains("Hello World", content);
        Assert.Contains("Widget", content);        // the table cell
    }

    // ---- Full round-trip: all 4 formats ----

    /// <summary>
    /// Every exporter must produce a document of its own format that carries the model's text —
    /// the point of one model with four back-ends. The predecessor checked four lengths, which
    /// four exporters emitting only their file headers would also satisfy.
    /// </summary>
    [Fact]
    public void ExportAllFormats_EachCarriesTheDocumentTextInItsOwnContainer()
    {
        var doc = BuildSampleDoc();

        // PDF: a real document whose extracted text contains the body.
        //
        // Compared with the spaces removed: the PDF exporter positions each run with its own text
        // matrix and emits no space *character* between them, so plain extraction returns
        // "HelloWorld". That is legal PDF — inter-word gaps are geometry, which is why the
        // structured extractor exists — but it means an exact-match assertion here would be
        // asserting the exporter's spacing strategy rather than that the content survived.
        using (var pdfDoc = PdfDocument.Open(new MemoryStream(doc.ToPdf())))
        {
            Assert.True(pdfDoc.Pages.Count >= 1);

            string pdfText = PdfTextExtractor.ExtractText(pdfDoc.Pages[0]).Replace(" ", "");
            Assert.Contains("HelloWorld", pdfText);
            Assert.Contains("Invoice", pdfText);
            Assert.Contains("Widget", pdfText);      // the table cell
        }

        // HTML: plain text, so assert directly.
        string html = Encoding.UTF8.GetString(doc.ToHtml());
        Assert.Contains("Hello World", html);
        Assert.Contains("Invoice", html);

        // DOCX and ODT are ZIP packages — the text lives in their main part.
        Assert.Contains("Hello World", ZipEntryText(doc.ToDocx(), "word/document.xml"));
        Assert.Contains("Hello World", ZipEntryText(doc.ToOdt(), "content.xml"));
    }

    /// <summary>Reads one entry of a ZIP-based document package as text.</summary>
    private static string ZipEntryText(byte[] package, string entryName)
    {
        using var ms = new MemoryStream(package);
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);
        Assert.True(entry != null, $"the package has no {entryName}");
        using var reader = new StreamReader(entry!.Open());
        return reader.ReadToEnd();
    }
}
