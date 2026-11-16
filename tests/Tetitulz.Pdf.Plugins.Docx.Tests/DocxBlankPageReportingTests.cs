// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Plugins.Docx;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// What <see cref="PdfToDocxConverter.Convert(System.IO.Stream)"/> does with a page that yields no
/// text, and how it says so.
///
/// <para>Two defects lived here together. A blank page was <c>continue</c>d, and because the page
/// break was emitted <i>after</i> the page's content the <c>continue</c> took the break with it —
/// so the pages on either side of a full-page image ran together in one Word page. And nothing
/// anywhere recorded that a page produced nothing, so a scanned PDF converted to a valid, empty
/// .docx that is indistinguishable from a successful conversion.</para>
///
/// <para>The break count is asserted on the parsed <c>word/document.xml</c>, not on a substring:
/// <c>w:br</c> also spells line and column breaks, so counting the element name alone would pass
/// for the wrong break kind.</para>
/// </summary>
public class DocxBlankPageReportingTests
{
    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Text-drawing content for a page, and content that draws no text at all.</summary>
    private const string Text = "BT /F1 12 Tf 100 700 Td (Marker) Tj ET";
    private const string NoText = "0 0 0 rg 100 100 200 200 re f";

    /// <summary>
    /// A document whose pages carry exactly the given content streams. Local rather than in
    /// <c>MinimalPdfBuilder</c> because these tests need per-page control over whether the page
    /// draws text, which no shared builder offers.
    /// </summary>
    private static byte[] BuildPdf(params string[] pageContents)
    {
        int n = pageContents.Length;
        const int firstPageObj = 4;
        int firstContentObj = firstPageObj + n;
        int totalObjects = firstContentObj + n;

        var pdf = new StringBuilder();
        var offsets = new int[totalObjects];
        pdf.Append("%PDF-1.4\n");

        offsets[1] = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [");
        for (int i = 0; i < n; i++)
        {
            if (i > 0) pdf.Append(' ');
            pdf.Append(firstPageObj + i).Append(" 0 R");
        }
        pdf.Append("] /Count ").Append(n).Append(" >>\nendobj\n");

        offsets[3] = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            offsets[firstPageObj + i] = pdf.Length;
            pdf.Append(firstPageObj + i).Append(" 0 obj\n")
               .Append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents ")
               .Append(firstContentObj + i)
               .Append(" 0 R /Resources << /Font << /F1 3 0 R >> >> >>\nendobj\n");
        }

        for (int i = 0; i < n; i++)
        {
            offsets[firstContentObj + i] = pdf.Length;
            pdf.Append(firstContentObj + i).Append(" 0 obj\n")
               .Append("<< /Length ").Append(pageContents[i].Length).Append(" >>\nstream\n")
               .Append(pageContents[i]).Append("\nendstream\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(totalObjects).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        for (int i = 1; i < totalObjects; i++)
            pdf.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(totalObjects).Append(" /Root 1 0 R >>\n");
        pdf.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static XDocument MainPart(byte[] docx)
    {
        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var stream = archive.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(stream);
    }

    private static int PageBreakCount(byte[] docx) =>
        MainPart(docx).Descendants(W + "br")
            .Count(e => (string?)e.Attribute(W + "type") == "page");

    /// <summary>
    /// Three pages, the middle one drawing a rectangle and no text. The document must still be
    /// paginated into three Word pages, which is two page breaks. With the break emitted after the
    /// content, page 2's <c>continue</c> skipped its own break and only one was written — pages 1
    /// and 3 ran together.
    /// </summary>
    [Fact]
    public void A_page_with_no_text_still_contributes_its_page_break()
    {
        byte[] docx = PdfToDocxConverter.Convert(BuildPdf(Text, NoText, Text));

        Assert.Equal(2, PageBreakCount(docx));
    }

    /// <summary>
    /// The unchanged case: N pages that all yield text are separated by N-1 breaks, and no break
    /// is written before the first page or after the last.
    /// </summary>
    [Fact]
    public void Pages_that_all_yield_text_are_separated_by_one_break_each()
    {
        byte[] docx = PdfToDocxConverter.Convert(BuildPdf(Text, Text, Text, Text));

        Assert.Equal(3, PageBreakCount(docx));
    }

    /// <summary>
    /// The honest-failure half. A document whose every page is image-only converts to a valid,
    /// empty .docx; without this channel the caller cannot tell that from a document that really
    /// had no words in it, and never learns that OCR is the missing step.
    /// </summary>
    [Fact]
    public void An_image_only_document_reports_how_many_pages_yielded_nothing()
    {
        byte[] docx = PdfToDocxConverter.Convert(BuildPdf(NoText, NoText, NoText));

        // The conversion still succeeds — the report is the point, not a refusal.
        Assert.NotEmpty(docx);
        string? reported = PdfToDocxConverter.LastError;
        Assert.False(string.IsNullOrEmpty(reported),
            "a document where every page yielded no text reported nothing at all");
        Assert.Contains("3 of 3", reported!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A partial failure names the real counts rather than rounding to "some pages".
    /// </summary>
    [Fact]
    public void A_mixed_document_reports_only_the_pages_that_yielded_nothing()
    {
        PdfToDocxConverter.Convert(BuildPdf(Text, NoText, Text, NoText));

        Assert.Contains("2 of 4", PdfToDocxConverter.LastError!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The slot is reset at the start of every conversion. A per-thread slot that is written but
    /// never cleared hands the previous document's verdict to the next caller, which is worse than
    /// having no channel: the caller has no way to know the answer is stale.
    /// </summary>
    [Fact]
    public void A_conversion_that_extracts_text_clears_a_previous_documents_report()
    {
        PdfToDocxConverter.Convert(BuildPdf(NoText));
        Assert.NotNull(PdfToDocxConverter.LastError);

        PdfToDocxConverter.Convert(BuildPdf(Text));

        Assert.Null(PdfToDocxConverter.LastError);
    }
}
