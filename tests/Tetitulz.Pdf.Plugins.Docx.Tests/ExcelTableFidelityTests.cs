// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Plugins.Docx;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// A table in the PDF reaches the workbook as <b>cells in their own columns</b>, not as one string
/// per line in column A.
/// </summary>
/// <remarks>
/// <para><b>This is a fidelity gap on a shipped feature, not a missing feature.</b> PDF-to-Excel has
/// always converted; what it produced for a table was a column of run-together sentences, because
/// every line became a single cell and none carried a <c>CellReference</c>.
/// <c>PdfTableExtractor</c> — four strategies, a real geometry pass — existed the whole time and was
/// wired into nothing here.</para>
///
/// <para><b>The assertions are about <i>which cell</i>, and they have to be.</b> "The value appears
/// somewhere in the sheet" was already true before the change: the old output contained every word,
/// in the wrong place. Only a cell reference distinguishes the two, so these read
/// <c>CellReference</c> and assert the column.</para>
/// </remarks>
public sealed class ExcelTableFidelityTests
{
    /// <summary>
    /// A three-column table lands with its second and third columns in B and C.
    /// </summary>
    /// <remarks>
    /// Both a header and a body row are checked, and on the same row index, because a converter
    /// that emitted only the first row of each table would satisfy a single-row assertion.
    /// </remarks>
    [Fact]
    public void ATableReachesTheSheetWithItsColumnsInSeparateCells()
    {
        Dictionary<string, string> cells = ConvertAndReadCells(TablePdf());

        string headerRow = RowOf(cells, "Region");
        Assert.NotNull(headerRow);
        Assert.Equal("Units", cells["B" + headerRow]);
        Assert.Equal("Total", cells["C" + headerRow]);

        string bodyRow = RowOf(cells, "North");
        Assert.NotNull(bodyRow);
        Assert.Equal("12", cells["B" + bodyRow]);
        Assert.Equal("340", cells["C" + bodyRow]);
    }

    /// <summary>
    /// Every cell carries an explicit <c>CellReference</c>.
    /// </summary>
    /// <remarks>
    /// Without it a reader places cells by their order within the row, so one empty cell shifts
    /// every value after it a column to the left. The previous converter wrote none at all, which
    /// is why everything arrived in column A regardless of what the page looked like.
    /// </remarks>
    [Fact]
    public void EveryCellCarriesAnExplicitReference()
    {
        byte[] xlsx = PdfToExcelConverter.Convert(TablePdf());

        using var ms = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(ms, false);

        var all = doc.WorkbookPart!.WorksheetParts.First()
                     .Worksheet.Descendants<Cell>().ToList();

        Assert.NotEmpty(all);
        Assert.All(all, c => Assert.False(string.IsNullOrEmpty(c.CellReference?.Value)));
    }

    /// <summary>
    /// The control: a page with no tabular layout still converts, one line per row in column A.
    /// </summary>
    /// <remarks>
    /// The table path must not become the only path. A prose page has nothing for the extractor to
    /// find, and the line-per-row form is the right answer for it — this is what stops a false
    /// positive in the detector from being the only behaviour anyone tests.
    /// </remarks>
    [Fact]
    public void AProsePageStillConvertsOneLinePerRow()
    {
        Dictionary<string, string> cells = ConvertAndReadCells(ProsePdf());

        Assert.Contains(cells, kv => kv.Value.Contains("single paragraph of ordinary prose"));
        Assert.All(cells.Keys, r => Assert.StartsWith("A", r));
    }

    /// <summary>
    /// The same table reaches DOCX as a real Word table, with its cells in their own columns.
    /// </summary>
    /// <remarks>
    /// The Word path had the same gap as the spreadsheet one: a table arrived as a run of
    /// paragraphs, which is the row structure thrown away — and no reader can get it back. Asserted
    /// through the table element rather than through the body's text, because the body text was
    /// already correct before the change and says nothing about structure.
    /// </remarks>
    [Fact]
    public void ATableReachesTheWordDocumentAsARealTable()
    {
        using var input = new MemoryStream(TablePdf());
        byte[] docx = PdfToDocxConverter.Convert(input);

        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);

        var tables = doc.MainDocumentPart!.Document.Body!
                        .Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().ToList();
        var table = Assert.Single(tables);

        var rows = table.Elements<TableRow>().ToList();
        Assert.True(rows.Count >= 2, "the table reached Word with fewer than two rows");

        var header = rows[0].Elements<TableCell>().Select(c => c.InnerText).ToList();
        Assert.Equal("Region", header[0]);
        Assert.Equal("Units", header[1]);
        Assert.Equal("Total", header[2]);
    }

    /// <summary>
    /// The control: a prose page produces paragraphs and no table at all.
    /// </summary>
    /// <remarks>
    /// This is the assertion the first version of the change broke. The extractor used to report
    /// <c>ColumnCount = 2</c> for two words stacked at the same x — with every cell in column 1 and
    /// column 0 empty — so a column count alone turned a page of prose into a table and the
    /// paragraphs disappeared. The phantom column has since been removed and such a page answers
    /// <c>ColumnCount = 1</c>, but this control keeps its full strength: it asserts the *outcome*
    /// (paragraphs, no table), which must hold however the count is computed.
    /// </remarks>
    [Fact]
    public void AProsePageProducesParagraphsAndNoTable()
    {
        using var input = new MemoryStream(ProsePdf());
        byte[] docx = PdfToDocxConverter.Convert(input);

        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart!.Document.Body!;

        Assert.Empty(body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>());
        Assert.Contains(body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>(),
                        p => p.InnerText.Contains("single paragraph of ordinary prose"));
    }

    // ------------------------------------------------------------------

    /// <summary>Converts and returns every non-empty cell, keyed by its reference ("B3").</summary>
    private static Dictionary<string, string> ConvertAndReadCells(byte[] pdf)
    {
        byte[] xlsx = PdfToExcelConverter.Convert(pdf);

        using var ms = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(ms, false);

        var result = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (Cell cell in doc.WorkbookPart!.WorksheetParts.First()
                                 .Worksheet.Descendants<Cell>())
        {
            string reference = cell.CellReference?.Value ?? string.Empty;
            string value = cell.CellValue?.Text ?? string.Empty;
            if (reference.Length > 0 && value.Length > 0) result[reference] = value;
        }
        return result;
    }

    /// <summary>Row number of the column-A cell whose text is <paramref name="columnAText"/>.</summary>
    private static string RowOf(Dictionary<string, string> cells, string columnAText)
    {
        foreach (var kv in cells)
        {
            if (!kv.Key.StartsWith("A", System.StringComparison.Ordinal)) continue;
            if (kv.Value != columnAText) continue;
            return kv.Key.Substring(1);
        }
        return null;
    }

    /// <summary>
    /// One page carrying a three-column, three-row grid, positioned so the columns are separated by
    /// clear whitespace and the rows share a baseline.
    /// </summary>
    private static byte[] TablePdf()
    {
        var text = new StringBuilder();
        text.Append("BT /F1 11 Tf\n");
        string[][] rows =
        {
            new[] { "Region", "Units", "Total" },
            new[] { "North",  "12",    "340"   },
            new[] { "South",  "45",    "678"   },
        };
        float[] columnX = { 60f, 220f, 380f };
        float y = 700f;
        foreach (string[] row in rows)
        {
            for (int c = 0; c < row.Length; c++)
                text.AppendFormat(CultureInfo.InvariantCulture,
                    "1 0 0 1 {0} {1} Tm ({2}) Tj\n", columnX[c], y, row[c]);
            y -= 30f;
        }
        text.Append("ET\n");
        return OnePagePdf(text.ToString());
    }

    /// <summary>One page of ordinary prose, with nothing a table detector should find.</summary>
    private static byte[] ProsePdf()
        => OnePagePdf("BT /F1 11 Tf 1 0 0 1 60 700 Tm "
                      + "(This is a single paragraph of ordinary prose with no columns.) Tj ET\n");

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
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}endstream\nendobj\n",
            contentStream.Length, contentStream));
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        int size = offsets.Count + 1;
        sb.Append("xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture)
                  + " /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
