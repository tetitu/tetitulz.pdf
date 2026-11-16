// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// <see cref="PdfSplitter.ExtractPages(Stream, Stream, IReadOnlyList{int})"/> — the arbitrary,
/// possibly non-contiguous page selection that <c>SplitRange</c> cannot express.
/// </summary>
/// <remarks>
/// <para><b>Every assertion here names WHICH pages came out, not how many.</b> A page count is
/// satisfied by copying the wrong pages, by copying one page twice, and by copying them in the
/// wrong order — so it passes with the page-indexing arithmetic mutated. The text of each output
/// page is read back instead, which also proves each page's <c>/Font</c> resource survived: a
/// clone that emits <c>/Font &lt;&lt; /F1 null &gt;&gt;</c> extracts as the empty string while
/// still producing a valid, non-empty PDF.</para>
/// <para>The contiguous path is deliberately not re-tested here. <c>SplitRange</c> now builds a
/// list and calls the same core, and the existing split suites are what cover that core — asserting
/// that the two agree after the merge would only restate the merge.</para>
/// </remarks>
public class PdfSplitterArbitraryPagesTests
{
    // ------------------------------------------------------------------
    //  Selection
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractPages_NonContiguousSelection_KeepsExactlyThosePagesInOrder()
    {
        byte[] output = Extract(FourPagePdf(), new[] { 1, 3 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(2, doc.Pages.Count);

        Assert.Contains("ALPHA", TextOfPage(output, 1), StringComparison.Ordinal);
        Assert.Contains("CHARLIE", TextOfPage(output, 2), StringComparison.Ordinal);

        // The pages between and after the selection are gone, not merely unreferenced.
        Assert.DoesNotContain("BRAVO", TextOfPage(output, 1) + TextOfPage(output, 2), StringComparison.Ordinal);
        Assert.DoesNotContain("DELTA", TextOfPage(output, 1) + TextOfPage(output, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPages_ReorderedSelection_WritesPagesInTheOrderGiven()
    {
        // The list's order is the output's order — the property that makes this a reorder as well
        // as a subset. Ascending-only fixtures cannot tell the two apart.
        byte[] output = Extract(FourPagePdf(), new[] { 3, 1 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(2, doc.Pages.Count);

        Assert.Contains("CHARLIE", TextOfPage(output, 1), StringComparison.Ordinal);
        Assert.Contains("ALPHA", TextOfPage(output, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPages_SingleInteriorPage_KeepsThatPageAndNoNeighbour()
    {
        byte[] output = Extract(FourPagePdf(), new[] { 2 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Single(doc.Pages);
        Assert.Contains("BRAVO", TextOfPage(output, 1), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  A destination into a page the caller did NOT select
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractPages_LinkIntoAnUnselectedPage_DoesNotCarryThatPageIntoTheOutput()
    {
        // A non-contiguous selection is where this bites: page 2 sits BETWEEN two kept pages and is
        // named by a link annotation's /Dest on page 1. A /Dest is an ordinary indirect reference,
        // so an object walk that follows it drags page 2 — content stream included — into the file
        // as an orphan, recoverable with any text extractor. That is a leak, not an untidiness.
        byte[] output = Extract(LinkFromPage1ToPage2(), new[] { 1, 3 });

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(2, doc.Pages.Count);

        // Counts /Type /Page OBJECTS in the file, not the entries the page tree lists — an orphan
        // page is by definition not in /Kids.
        Assert.Equal(2, CountPageObjects(doc));

        string raw = Encoding.ASCII.GetString(output);
        // The acceptance control comes first and is what makes the next line mean anything: the
        // kept page's text IS in the bytes verbatim, so these streams are not compressed and an
        // absent word is genuinely absent rather than merely unreadable.
        Assert.Contains("ALPHA", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("BRAVO", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Refusals
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractPages_PageNumberPastTheEnd_IsRefusedAndNamesTheValue()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Extract(FourPagePdf(), new[] { 1, 9 }));

        Assert.Equal("pageNumbers", ex.ParamName);
        Assert.Contains("9", ex.Message, StringComparison.Ordinal);
        Assert.Contains("4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPages_PageNumberBelowOne_IsRefused()
    {
        // 1-based: 0 is not "the first page", it is a caller who thought this was 0-based.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Extract(FourPagePdf(), new[] { 0 }));
        Assert.Equal("pageNumbers", ex.ParamName);
    }

    [Fact]
    public void ExtractPages_EmptySelection_IsRefusedRatherThanWritingAPagelessPdf()
    {
        var ex = Assert.Throws<ArgumentException>(() => Extract(FourPagePdf(), new int[0]));
        Assert.Equal("pageNumbers", ex.ParamName);
    }

    [Fact]
    public void ExtractPages_RepeatedPageNumber_IsRefusedAndNamesTheValue()
    {
        var ex = Assert.Throws<ArgumentException>(() => Extract(FourPagePdf(), new[] { 2, 3, 2 }));

        Assert.Equal("pageNumbers", ex.ParamName);
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPages_NullSelection_IsRefused()
    {
        using var input = new MemoryStream(FourPagePdf());
        using var output = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => PdfSplitter.ExtractPages(input, output, null));
    }

    /// <summary>
    /// The encrypted document must be refused <b>for being encrypted</b>.
    /// </summary>
    /// <remarks>
    /// <para>This asserted <c>ThrowsAny&lt;Exception&gt;</c> and was worthless: with no decryptor
    /// registered an encrypted document exposes <b>zero</b> pages, so deleting
    /// <c>RefuseEncrypted</c> entirely still threw — an
    /// <see cref="ArgumentOutOfRangeException"/> from the range check, which
    /// <c>ThrowsAny</c> happily accepted. Verified by mutation: 12/12 passed with the guard
    /// removed.</para>
    /// <para>Worse, that <c>ArgumentOutOfRangeException</c> is the exact defect
    /// <c>RefuseEncrypted</c>'s own doc comment says it exists to eliminate — "blaming the caller's
    /// range for the document's encryption". The old assertion would have passed on the
    /// reintroduced bug it was written to prevent.</para>
    /// <para>Asserting the concrete type is what makes only the named guard able to satisfy it,
    /// matching the four sibling refusals above.</para>
    /// </remarks>
    [Fact]
    public void ExtractPages_EncryptedDocument_IsRefusedForBeingEncrypted()
    {
        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfSplitter.ExtractPages(input, output, new[] { 1 }));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    //  Diagnostics
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractPages_RejectedSelection_ClearsAnyWarningLeftByTheCallBefore()
    {
        // The reset runs BEFORE the argument checks. Without that, a caller reading a Last*Warning
        // after a rejected call gets the previous extraction's message on this thread and attributes
        // it to the wrong document.
        Extract(PdfWithPageLabels(), new[] { 1 });
        Assert.NotNull(PdfSplitter.LastCatalogWarning);

        Assert.ThrowsAny<ArgumentException>(() => Extract(FourPagePdf(), new[] { 99 }));

        Assert.Null(PdfSplitter.LastCatalogWarning);
    }

    [Fact]
    public void ExtractPages_DroppedCatalogEntry_IsReportedRatherThanSilentlyLost()
    {
        // /PageLabels is a number tree keyed by page INDEX, so it describes a pagination the
        // extract no longer has. Dropping it is right; dropping it silently is not.
        Extract(PdfWithPageLabels(), new[] { 1 });

        Assert.NotNull(PdfSplitter.LastCatalogWarning);
        Assert.Contains("PageLabels", PdfSplitter.LastCatalogWarning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] Extract(byte[] source, IReadOnlyList<int> pageNumbers)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(input, output, pageNumbers);
        return output.ToArray();
    }

    private static string TextOfPage(byte[] pdf, int pageNumber)
    {
        using var ms = new MemoryStream(pdf, false);
        return PdfOperations.ExtractText(ms, pageNumber) ?? string.Empty;
    }

    private static int CountPageObjects(PdfDocument doc)
    {
        int count = 0;
        foreach (var kvp in doc.Parser.EnumerateAllObjects())
            if (kvp.Value is PdfDictionary dict && (dict[PdfName.Type] as PdfName)?.Value == "Page")
                count++;
        return count;
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static string ContentFor(string word) => $"BT /F1 12 Tf 72 700 Td ({word}) Tj ET";

    /// <summary>Four pages, each drawing a different word, sharing one font object.</summary>
    private static byte[] FourPagePdf() => PagesPdf(new[] { "ALPHA", "BRAVO", "CHARLIE", "DELTA" });

    private static byte[] PagesPdf(string[] words, string catalogExtras = "")
    {
        // 1 catalog, 2 /Pages, then (page, contents) pairs from object 3, font last.
        int fontObj = 3 + words.Length * 2;

        var kids = new List<string>();
        for (int i = 0; i < words.Length; i++) kids.Add($"{3 + i * 2} 0 R");

        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtras}>>",
            $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {words.Length} >>",
        };

        for (int i = 0; i < words.Length; i++)
        {
            string content = ContentFor(words[i]);
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {4 + i * 2} 0 R " +
                       $"/Resources << /Font << /F1 {fontObj} 0 R >> >> >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        return Assemble(bodies);
    }

    /// <summary>Three pages; page 1 carries a link annotation whose /Dest names page 2.</summary>
    private static byte[] LinkFromPage1ToPage2()
    {
        const int FontObj = 10;
        string a = ContentFor("ALPHA"), b = ContentFor("BRAVO"), c = ContentFor("CHARLIE");
        string resources = $"/Resources << /Font << /F1 {FontObj} 0 R >> >>";

        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R {resources} /Annots [9 0 R] >>",
            $"<< /Length {a.Length} >>\nstream\n{a}\nendstream",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R {resources} >>",
            $"<< /Length {b.Length} >>\nstream\n{b}\nendstream",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R {resources} >>",
            $"<< /Length {c.Length} >>\nstream\n{c}\nendstream",
            "<< /Type /Annot /Subtype /Link /Rect [72 700 200 720] /Dest [5 0 R /Fit] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        });
    }

    /// <summary>Two pages plus a catalog <c>/PageLabels</c>, which an extract must drop.</summary>
    private static byte[] PdfWithPageLabels() =>
        PagesPdf(new[] { "ALPHA", "BRAVO" },
                 "/PageLabels << /Nums [0 << /S /D >> 1 << /S /r >>] >> ");

    private static byte[] EncryptedPdf()
    {
        string content = ContentFor("ALPHA");
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Filter /Standard /V 1 /R 2 /O <0102030405060708090A0B0C0D0E0F10" +
            "1112131415161718191A1B1C1D1E1F20> /U <0102030405060708090A0B0C0D0E0F10" +
            "1112131415161718191A1B1C1D1E1F20> /P -1 >>",
        };
        return Assemble(bodies, trailerExtras: "/Encrypt 5 0 R ");
    }

    private static byte[] Assemble(List<string> bodies, string trailerExtras = "")
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R {trailerExtras}>>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
