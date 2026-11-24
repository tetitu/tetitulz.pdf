// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Plugins.Docx;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// The PDF-to-Office converters at their edges: the smallest grid that is laid out as a table, the
/// lines a table already carries, blank lines and blank pages, spreadsheet column names past Z, and
/// the slide geometry the PowerPoint path declares.
/// </summary>
public class OfficeConversionBoundaryTests
{
    // ------------------------------------------------------------------ tables

    /// <summary>
    /// The smallest grid that is a table: two columns, two rows each carrying two values. It
    /// reaches Word as a real table, cell for cell.
    /// </summary>
    [Fact]
    public void TwoByTwoGrid_IsLaidOutAsATable()
    {
        byte[] pdf = OnePagePdf(Grid(new[] { new[] { "Name", "Qty" }, new[] { "Apple", "12" } }));

        Table table = Assert.Single(WordBody(pdf).Elements<Table>());
        var rows = table.Elements<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();
        Assert.Equal(new[] { "Name", "Qty" }, rows[0]);
        Assert.Equal(new[] { "Apple", "12" }, rows[1]);
    }

    /// <summary>
    /// A line a table already carries is not repeated as a paragraph, and nothing is dropped: the
    /// sentence below the grid appears exactly once in the document, and no row of the grid
    /// reappears as a paragraph.
    /// </summary>
    [Fact]
    public void LinesATableCarries_AreNotRepeated_AndProseBelowItIsAParagraph()
    {
        string content = Grid(new[]
        {
            new[] { "Region", "Units", "Total" },
            new[] { "North", "12", "340" },
            new[] { "South", "45", "678" },
        }) + "BT /F1 11 Tf 1 0 0 1 60 500 Tm (A closing sentence of prose.) Tj ET\n";

        Body body = WordBody(OnePagePdf(content));
        var paragraphs = body.Elements<Paragraph>().Select(p => p.InnerText).ToList();

        Table table = Assert.Single(body.Elements<Table>());
        Assert.Equal(3, table.Elements<TableRow>().Count());
        // The sentence is a paragraph of its own, whole — not a fourth row of the grid split at a
        // column boundary.
        Assert.Contains(paragraphs, p => p.Contains("A closing sentence of prose."));
        string squeezed = string.Concat(body.InnerText.Where(c => !char.IsWhiteSpace(c)));
        Assert.Equal(1, CountOf(squeezed, "Aclosingsentenceofprose."));
        Assert.Equal(1, CountOf(squeezed, "North"));
        Assert.DoesNotContain(paragraphs, p => p.Contains("North") || p.Contains("Region"));
    }

    // ------------------------------------------------------------------ blank lines and pages

    /// <summary>
    /// A blank line in the page's text is a paragraph of its own in Word — the vertical gap the
    /// reader sees is kept rather than closed up.
    /// </summary>
    [Fact]
    public void BlankLine_IsKeptAsAnEmptyParagraph()
    {
        byte[] pdf = OnePagePdf(TwoLinesWithAGap());
        string text = ExtractedText(pdf);
        int blankLines = text.Replace("\r\n", "\n").Split('\n').Count(l => l.Length == 0);
        Assert.True(blankLines > 0, "fixture: the extracted text must carry a blank line, got: " + text);

        var paragraphs = WordBody(pdf).Elements<Paragraph>().Select(p => p.InnerText).ToList();

        int first = paragraphs.FindIndex(p => p.Contains("First line"));
        int second = paragraphs.FindIndex(p => p.Contains("Second line"));
        Assert.True(second > first + 1, "the blank line between them must be its own paragraph");
        Assert.All(paragraphs.Skip(first + 1).Take(second - first - 1), p => Assert.Equal("", p));
    }

    /// <summary>
    /// The same blank line on a slide is a paragraph with no run: DrawingML's empty paragraph, not a
    /// run of empty text.
    /// </summary>
    [Fact]
    public void BlankLineOnASlide_IsAParagraphWithoutARun()
    {
        byte[] pptx = PdfToPptxConverter.Convert(OnePagePdf(TwoLinesWithAGap()));

        using var doc = PresentationDocument.Open(new MemoryStream(pptx), false);
        var paragraphs = doc.PresentationPart!.SlideParts.Single().Slide.Descendants<A.Paragraph>().ToList();

        Assert.Contains(paragraphs, p => p.InnerText.Contains("First line"));
        Assert.Contains(paragraphs, p => !p.Elements<A.Run>().Any());
        Assert.DoesNotContain(paragraphs, p => p.Elements<A.Run>().Any(r => r.Text?.Text == ""));
    }

    /// <summary>
    /// The coverage rule with no page lines to place: a line is covered when it is a whole emitted
    /// row, whitespace aside, and not when it is merely part of one — a part of a row is covered
    /// only by standing inside it (<see cref="TableCoverageTests"/>). Until Wave 22 (1:5) any part
    /// was, wherever it stood.
    /// </summary>
    [Theory]
    [InlineData("North  12 340", true)]     // a whole row, spaced differently
    [InlineData("  ", true)]                // nothing to emit
    [InlineData("North 12", false)]         // part of a row, placed nowhere
    [InlineData("Closing prose.", false)]   // in no row
    public void IsCovered_IsAWholeRow_WhenNoLineIsPlaced(string line, bool covered)
    {
        var table = TableCoverageTests.Table(
            new[] { "Region", "Units", "Total" },
            new[] { "North", "12", "340" });

        Assert.Equal(covered, ConverterTableSupport.IsCovered(line, ConverterTableSupport.CoveredLines(new[] { table }, null)));
    }

    // ------------------------------------------------------------------ spreadsheet columns

    /// <summary>
    /// Zero-based column indices to spreadsheet letters, past the single letters: the most
    /// significant letter comes first, so 27 is AB, not BA.
    /// </summary>
    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    [InlineData(-3, "A")]
    public void ColumnName_PutsTheMostSignificantLetterFirst(int index, string expected)
    {
        Assert.Equal(expected, PdfToExcelConverter.ColumnName(index));
    }

    // ------------------------------------------------------------------ slide geometry

    /// <summary>
    /// The slide is the standard 4:3 sheet, 10 in × 7.5 in (914 400 EMU to the inch), the notes
    /// page is the same sheet turned portrait, and the text box sits half an inch in from every
    /// edge.
    /// </summary>
    [Fact]
    public void Slide_IsTenBySevenAndAHalfInches_WithTheTextHalfAnInchIn()
    {
        const long inch = 914400;
        byte[] pptx = PdfToPptxConverter.Convert(OnePagePdf("BT /F1 11 Tf 1 0 0 1 60 700 Tm (Hello) Tj ET\n"));

        using var doc = PresentationDocument.Open(new MemoryStream(pptx), false);
        var presentation = doc.PresentationPart!.Presentation;

        P.SlideSize size = presentation.SlideSize!;
        Assert.Equal(10 * inch, (long)size.Cx!.Value);
        Assert.Equal(15 * inch / 2, (long)size.Cy!.Value);
        Assert.Equal(15 * inch / 2, presentation.NotesSize!.Cx!.Value);
        Assert.Equal(10 * inch, presentation.NotesSize!.Cy!.Value);

        A.Transform2D xfrm = doc.PresentationPart.SlideParts.Single().Slide.Descendants<A.Transform2D>().Single();
        Assert.Equal(inch / 2, xfrm.Offset!.X!.Value);
        Assert.Equal(inch / 2, xfrm.Offset!.Y!.Value);
        Assert.Equal(10 * inch - inch, xfrm.Extents!.Cx!.Value);
        Assert.Equal(15 * inch / 2 - inch, xfrm.Extents!.Cy!.Value);
    }

    /// <summary>The one layout every slide uses carries no placeholders, and says so: it is Blank.</summary>
    [Fact]
    public void TheSlideLayout_IsBlank()
    {
        byte[] pptx = PdfToPptxConverter.Convert(OnePagePdf("BT /F1 11 Tf 1 0 0 1 60 700 Tm (Hello) Tj ET\n"));

        using var doc = PresentationDocument.Open(new MemoryStream(pptx), false);
        P.SlideLayout layout = doc.PresentationPart!.SlideParts.Single().SlideLayoutPart!.SlideLayout;

        Assert.Equal(P.SlideLayoutValues.Blank, layout.Type!.Value);
        Assert.Empty(layout.Descendants<P.PlaceholderShape>());
    }

    // ------------------------------------------------------------------ fixtures

    private static Body WordBody(byte[] pdf)
    {
        byte[] docx = PdfToDocxConverter.Convert(new MemoryStream(pdf));
        var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return doc.MainDocumentPart!.Document.Body!;
    }

    private static string ExtractedText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return PdfTextExtractor.ExtractText(doc.Pages[0]);
    }

    /// <summary>
    /// Two lines with an empty one between: two line advances (<c>T*</c>) with nothing shown in
    /// between. A plain vertical gap is not enough — the extractor closes it up.
    /// </summary>
    private static string TwoLinesWithAGap()
        => "BT /F1 11 Tf 14 TL 1 0 0 1 60 700 Tm (First line) Tj T* T* (Second line) Tj ET\n";

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, System.StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + 1, System.StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>A grid with clear gaps between columns and rows sharing a baseline.</summary>
    private static string Grid(string[][] rows)
    {
        var text = new StringBuilder("BT /F1 11 Tf\n");
        float[] columnX = { 60f, 220f, 380f };
        float y = 700f;
        foreach (string[] row in rows)
        {
            for (int c = 0; c < row.Length; c++)
                text.AppendFormat(CultureInfo.InvariantCulture, "1 0 0 1 {0} {1} Tm ({2}) Tj\n", columnX[c], y, row[c]);
            y -= 30f;
        }
        return text.Append("ET\n").ToString();
    }

    private static byte[] OnePagePdf(string contentStream)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj(string.Format(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}endstream\nendobj\n", contentStream.Length, contentStream));
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        int size = offsets.Count + 1;
        sb.Append("xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture) + " /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
