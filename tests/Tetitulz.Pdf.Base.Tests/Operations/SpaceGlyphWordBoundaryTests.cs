// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A space glyph drawn between two glyphs ends a word, however narrow the gap it leaves.
/// </summary>
/// <remarks>
/// <para><b>Not the gap alone.</b> With blank glyphs removed before grouping, a word would end
/// only where the gap beat 0.8 of the preceding glyph's advance. A 12 pt Helvetica space is 3.3 pt,
/// and 0.8 of an "o" is 5.3 pt, so ordinary single-spaced text would come back joined — in real
/// files too: <c>InvoiceTotal:</c>, <c>BillingStartDate:</c>, <c>disclaimerofliability,</c>. Table
/// cells, field values, search-driven redaction and the HTML exports all read those words.</para>
///
/// <para><c>WordAssemblyOptions.KeepBlankChars</c> says, for its default, that blank
/// glyphs "are used only for spacing". pdfplumber, which that option mirrors, treats them as the
/// space between words.</para>
/// </remarks>
public sealed class SpaceGlyphWordBoundaryTests
{
    private const string Helvetica =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>
    /// Every code from space to tilde advances 600 units, so at 12 pt glyph <i>i</i> of a run
    /// starting at x = 100 spans 100 + 7.2<i>i</i> to 100 + 7.2(<i>i</i> + 1).
    /// </summary>
    private static readonly string Uniform =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 126 /Widths [" +
        string.Join(" ", Enumerable.Repeat(600, 95)) + "] >>";

    /// <summary>A one-page document drawing <paramref name="contentStream"/> with /F1.</summary>
    /// <param name="font">The /F1 dictionary.</param>
    /// <param name="sixth">An optional object 6, for a font to reference.</param>
    private static byte[] Pdf(string contentStream, string font = Helvetica, string sixth = null)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contentStream)} >>\nstream\n{contentStream}\nendstream",
            font,
        };
        if (sixth != null) bodies.Add(sixth);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PdfPageTextLayout Page(byte[] pdf, WordAssemblyOptions options = null)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfStructuredTextExtractor.Extract(ms, options ?? WordAssemblyOptions.Default)[0];
    }

    private static string[] Words(PdfPageTextLayout page) =>
        page.Lines.SelectMany(l => l.Words).Select(w => w.Text).ToArray();

    [Fact]
    public void ASpaceGlyphNarrowerThanTheGapThreshold_StillEndsTheWord()
    {
        var page = Page(Pdf("BT /F1 12 Tf 100 700 Td (Hello World) Tj ET\n"));

        Assert.Equal(new[] { "Hello", "World" }, Words(page));
        Assert.Equal("Hello World", Assert.Single(page.Lines).Text);
    }

    [Theory]
    [InlineData(680)]
    [InlineData(720)]
    public void ASpaceGlyphOnAnotherLine_DoesNotSplitAWord(int spaceBaseline)
    {
        // "o" is centred at 132.4 and "W" at 139.6; the space, 7.2 pt wide from 133, is centred
        // between them, directly below or above. It separates nothing on a line 20 pt away.
        var page = Page(Pdf(
            "BT /F1 12 Tf 100 700 Td (HelloWorld) Tj ET\n" +
            "BT /F1 12 Tf 133 " + spaceBaseline + " Td ( ) Tj ET\n", Uniform));

        Assert.Equal(new[] { "HelloWorld" }, Words(page));
    }

    [Fact]
    public void ARotatedSpaceGlyph_DoesNotSplitAnUprightWord()
    {
        // The same position as above, on the word's own baseline, but turned a quarter. Rotated
        // glyphs form lines of their own, so their blanks separate nothing in an upright line.
        var page = Page(Pdf(
            "BT /F1 12 Tf 100 700 Td (HelloWorld) Tj ET\n" +
            "BT /F1 12 Tf 0 1 -1 0 133 700 Tm ( ) Tj ET\n", Uniform));

        Assert.Equal(new[] { "HelloWorld" }, Words(page));
    }

    [Fact]
    public void ABlankGlyphAtANonFinitePosition_DoesNotStopOtherSpacesEndingWords()
    {
        // Two moves of 3e38 overflow the text position to infinity, and composing that with the
        // CTM's zero term gives a NaN baseline on a glyph that is still upright. Sorted by Y, a NaN
        // comes first and would end the search for the line's spaces before it reached the real
        // one. (The ".0" matters: written as an integer, a number that large does not parse at all
        // and the glyph lands at the origin.)
        var page = Page(Pdf(
            "BT /F1 12 Tf 300000000000000000000000000000000000000.0 0 Td " +
            "300000000000000000000000000000000000000.0 0 Td ( ) Tj ET\n" +
            "BT /F1 12 Tf 100 700 Td (Hello World) Tj ET\n"));

        Assert.Equal(new[] { "Hello", "World" }, Words(page));
    }

    [Fact]
    public void ASpaceGlyphInARightToLeftLine_EndsTheWord()
    {
        // A /ToUnicode maps A-D to four Hebrew letters, and /Widths makes each letter 600 units
        // and the space 250. The line is right to left, so its glyphs are compared in descending X.
        const string toUnicode =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "/CMapName /Test def /CMapType 2 def\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "5 beginbfchar\n<20> <0020>\n<41> <05D0>\n<42> <05D1>\n<43> <05D2>\n<44> <05D3>\nendbfchar\n" +
            "endcmap end end\n";
        string widths = "250 " + string.Join(" ", Enumerable.Repeat(600, 36));
        string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 68 " +
                      "/Widths [" + widths + "] /ToUnicode 6 0 R >>";
        string cmap = $"<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream";

        var page = Page(Pdf("BT /F1 12 Tf 100 700 Td (AB CD) Tj ET\n", font, cmap));

        var line = Assert.Single(page.Lines);
        Assert.True(line.IsRtl);
        Assert.Equal(2, line.Words.Count);
    }

    [Fact]
    public void WithBidiApplied_TheLineTextKeepsTheSpace()
    {
        var page = Page(Pdf("BT /F1 12 Tf 100 700 Td (Hello World) Tj ET\n"),
            new WordAssemblyOptions { ApplyBidi = true });

        Assert.Equal("Hello World", Assert.Single(page.Lines).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATableCellKeepsTheSpaceBetweenItsWords(bool applyBidi)
    {
        // Two rows, two columns 200 pt apart. The first cell of each row is two words.
        byte[] pdf = Pdf(
            "BT /F1 12 Tf 72 700 Td (Invoice Total) Tj ET\n" +
            "BT /F1 12 Tf 300 700 Td (12.50) Tj ET\n" +
            "BT /F1 12 Tf 72 680 Td (Billing Date) Tj ET\n" +
            "BT /F1 12 Tf 300 680 Td (2026-09-17) Tj ET\n");

        using var ms = new MemoryStream(pdf, writable: false);
        var table = Assert.Single(PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Text, ApplyBidi = applyBidi }));

        var cells = table.Rows.SelectMany(r => r.Cells).Select(c => c.Text).ToArray();
        Assert.Contains("Invoice Total", cells);
        Assert.Contains("Billing Date", cells);
    }

    [Fact]
    public void RedactionFindsAPhraseWrittenWithOneSpace()
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 72 700 Td (Top Secret material) Tj ET\n");

        using var ms = new MemoryStream(pdf, writable: false);
        var match = Assert.Single(PdfTextRedactor.FindMatches(ms, "Top Secret"));

        Assert.Equal("Top Secret", match.Text);
    }

    [Fact]
    public void AFieldValueAfterItsLabelOnTheSameRunIsFound()
    {
        // "Invoice" and "12.50" in one string, one space apart. Joined, the label word swallowed the
        // value, so nothing was left on the line to be the value. (After a colon the gap alone is
        // enough to split, because a colon is narrow.)
        byte[] pdf = Pdf("BT /F1 12 Tf 72 700 Td (Invoice 12.50) Tj ET\n");

        using var ms = new MemoryStream(pdf, writable: false);
        var result = Assert.Single(PdfFieldExtractor.Extract(ms,
            new[] { new FieldQuery { Key = "invoice", Labels = new[] { "Invoice" } } }));

        Assert.Equal("12.50", result.Value);
    }

    [Fact]
    public void AFieldValueOfTwoWordsKeepsItsSpace()
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 72 700 Td (Name: John Smith) Tj ET\n");

        using var ms = new MemoryStream(pdf, writable: false);
        var result = Assert.Single(PdfFieldExtractor.Extract(ms,
            new[] { new FieldQuery { Key = "name", Labels = new[] { "Name" } } }));

        Assert.Equal("John Smith", result.Value);
    }
}
