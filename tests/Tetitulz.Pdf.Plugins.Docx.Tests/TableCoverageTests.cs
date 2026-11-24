// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// Which of a page's lines an emitted table already carries: a line is covered when it is a whole
/// row or stands inside one. Counted as covered whenever its text is contained in some row's,
/// wherever it stands, a page number "1" under a row holding "12" would vanish from the DOCX and
/// the XLSX, and every line would be searched against every row. And a table wider than Word lays
/// out is written as lines.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class TableCoverageTests
{
    // Rows 30 pt apart from y = 700, columns at x = 60, 220, 380; each cell 11 pt tall.
    internal static PdfTable Table(params string[][] rows)
    {
        float[] columnX = { 60f, 220f, 380f };
        var tableRows = new List<PdfTableRow>();
        float y = 700f;
        foreach (string[] row in rows)
        {
            var cells = row.Select((t, c) => new PdfTableCell(t, columnX[c], y, 100f, 11f, c)).ToList();
            tableRows.Add(new PdfTableRow(cells, y));
            y -= 30f;
        }
        return new PdfTable(0, tableRows, rows.Max(r => r.Length));
    }

    private static PdfTextLine Line(string text, float x, float y) =>
        new PdfTextLine(text, x, y, 40f, 11f, Array.Empty<PdfTextWord>());

    /// <summary>
    /// A line standing inside a row's band and its table's extent is covered; the same text below
    /// the table, or beside it, is not.
    /// </summary>
    [Fact]
    public void ALineInsideARow_IsCovered_AndTheSameTextElsewhereIsNot()
    {
        var table = Table(new[] { "Item", "Qty" }, new[] { "Pens", "12" }, new[] { "Paper", "30" });

        var inside = ConverterTableSupport.CoveredLines(new[] { table }, new[] { Line("Pens", 60f, 670f) });
        Assert.True(ConverterTableSupport.IsCovered("Pens", inside));

        var below = ConverterTableSupport.CoveredLines(new[] { table }, new[] { Line("Pens", 60f, 200f) });
        Assert.False(ConverterTableSupport.IsCovered("Pens", below));

        var beside = ConverterTableSupport.CoveredLines(new[] { table }, new[] { Line("Pens", 520f, 670f) });
        Assert.False(ConverterTableSupport.IsCovered("Pens", beside));
    }

    /// <summary>
    /// A line costs its page's tables, not its page's rows: one table whose single row spans the page beside
    /// another of 2 000 rows, and 4 000 lines between the two, inside neither. The bands searched back from each
    /// line while the tall one kept the reach up, some 4x10^6 steps here (F8 of 07's DoS hunt); now each line
    /// asks two tables. The lines stay uncovered, and a line inside a row of the long table is covered.
    /// </summary>
    [Fact]
    public void ALineCostsItsPagesTables_NotItsRows()
    {
        var tall = new PdfTable(0, new[]
        {
            new PdfTableRow(new[] { new PdfTableCell("tall", 0f, 0f, 50f, 800f, 0), new PdfTableCell("x", 50f, 0f, 50f, 800f, 1) }, 0f),
        }, 2);
        var longRows = Enumerable.Range(0, 2000).Select(i => new PdfTableRow(new[]
        {
            new PdfTableCell("r" + i, 300f, 800f - i * 0.4f, 50f, 0.3f, 0),
            new PdfTableCell("v", 350f, 800f - i * 0.4f, 50f, 0.3f, 1),
        }, 800f - i * 0.4f)).ToList();
        var longTable = new PdfTable(0, longRows, 2);
        var lines = Enumerable.Range(0, 4000).Select(i => Line("between" + i, 180f, i * 0.2f)).ToList();
        lines.Add(Line("inside", 330f, 800f - 10 * 0.4f + 0.15f - 5.5f));   // its centre in row 10's band

        long before = ConverterTableSupport.CoverageSteps;
        var covered = ConverterTableSupport.CoveredLines(new[] { tall, longTable }, lines);
        long steps = ConverterTableSupport.CoverageSteps - before;

        Assert.InRange(steps, 1, 2L * lines.Count);
        Assert.False(ConverterTableSupport.IsCovered("between10", covered));
        Assert.True(ConverterTableSupport.IsCovered("inside", covered));
    }

    /// <summary>
    /// A page's tables are asked by position up to <see cref="ConverterTableSupport.MaxPlacedTablesPerPage"/>: a
    /// hundred small tables and a thousand lines cost at most 64 questions a line; a line inside a row of the first
    /// table is still covered, and the rows of every table still count as whole rows.
    /// </summary>
    [Fact]
    public void APagesTablesAreAskedByPosition_UpToTheCap()
    {
        var tables = Enumerable.Range(0, 100).Select(t => new PdfTable(0, new[]
        {
            new PdfTableRow(new[] { new PdfTableCell("a" + t, t * 6f, 100f, 3f, 5f, 0), new PdfTableCell("b", t * 6f + 3f, 100f, 3f, 5f, 1) }, 100f),
        }, 2)).ToList();
        var lines = Enumerable.Range(0, 1000).Select(i => Line("l" + i, 0f, 300f + i)).ToList();
        lines.Add(new PdfTextLine("first", 0f, 100f, 3f, 5f, Array.Empty<PdfTextWord>()));

        long before = ConverterTableSupport.CoverageSteps;
        var covered = ConverterTableSupport.CoveredLines(tables, lines);
        long steps = ConverterTableSupport.CoverageSteps - before;

        Assert.InRange(steps, 1, (long)ConverterTableSupport.MaxPlacedTablesPerPage * lines.Count);
        Assert.True(ConverterTableSupport.IsCovered("first", covered));
        Assert.True(ConverterTableSupport.IsCovered("a99b", covered));
    }

    /// <summary>
    /// The finding's page through the converters: a footer "1" and a stand-alone "30" below the
    /// grid reach the DOCX and the XLSX, and the grid's own lines are not repeated.
    /// </summary>
    [Fact]
    public void AShortLineBelowATable_IsKept_InTheDocxAndTheXlsx()
    {
        string content = Grid(new[] { new[] { "Item", "Qty" }, new[] { "Pens", "12" }, new[] { "Paper", "30" } })
            + "BT /F1 11 Tf 1 0 0 1 220 300 Tm (30) Tj 1 0 0 1 300 60 Tm (1) Tj ET\n";
        byte[] pdf = OnePagePdf(content);

        using var word = WordprocessingDocument.Open(new MemoryStream(PdfToDocxConverter.Convert(new MemoryStream(pdf))), false);
        Body body = word.MainDocumentPart!.Document.Body!;
        Table table = Assert.Single(body.Elements<Table>());
        Assert.Equal(3, table.Elements<TableRow>().Count());   // the footer and the total are not rows
        var paragraphs = body.Elements<Paragraph>().Select(p => p.InnerText.Trim()).ToList();
        Assert.Contains("1", paragraphs);
        Assert.Contains("30", paragraphs);
        Assert.DoesNotContain(paragraphs, p => p.Contains("Pens", StringComparison.Ordinal));

        using var xlsx = SpreadsheetDocument.Open(new MemoryStream(PdfToExcelConverter.Convert(pdf)), false);
        var cells = xlsx.WorkbookPart!.WorksheetParts.Single().Worksheet
            .Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>()
            .Select(c => c.CellValue?.Text).ToList();
        Assert.Contains("1", cells);
        Assert.Equal(2, cells.Count(c => c == "30"));            // the cell, and the total below
        Assert.Equal(1, cells.Count(c => c == "Pens"));
    }

    /// <summary>
    /// A table wider than <see cref="PdfLimits.MaxDocxTableColumns"/> is written as its lines, and
    /// the DOCX says so; the control at the limit is a Word table.
    /// </summary>
    [Fact]
    public void ATableWiderThanWordLaysOut_IsWrittenAsLines()
    {
        byte[] pdf = OnePagePdf(Grid(new[] { new[] { "Region", "Units", "Total" }, new[] { "North", "12", "340" } }));
        try
        {
            PdfLimits.MaxDocxTableColumns = 2;
            using var wide = WordprocessingDocument.Open(new MemoryStream(PdfToDocxConverter.Convert(new MemoryStream(pdf))), false);
            Body body = wide.MainDocumentPart!.Document.Body!;
            Assert.Empty(body.Elements<Table>());
            Assert.Contains(body.Elements<Paragraph>(), p => p.InnerText.Contains("North", StringComparison.Ordinal));
            Assert.Contains("columns", PdfToDocxConverter.LastError, StringComparison.Ordinal);

            PdfLimits.MaxDocxTableColumns = 3;
            using var atLimit = WordprocessingDocument.Open(new MemoryStream(PdfToDocxConverter.Convert(new MemoryStream(pdf))), false);
            Assert.Single(atLimit.MainDocumentPart!.Document.Body!.Elements<Table>());
            Assert.Null(PdfToDocxConverter.LastError);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A table wider than a worksheet (<see cref="PdfLimits.MaxSpreadsheetColumns"/>) is written to the XLSX
    /// as its lines in column A, and the converter says so; the control at the limit is the grid, with a
    /// cell in column C. A cell past column XFD is one a spreadsheet application repairs or refuses the
    /// workbook over.
    /// </summary>
    [Fact]
    public void ATableWiderThanAWorksheet_IsWrittenAsLines_InTheXlsx()
    {
        byte[] pdf = OnePagePdf(Grid(new[] { new[] { "Region", "Units", "Total" }, new[] { "North", "12", "340" } }));
        try
        {
            PdfLimits.MaxSpreadsheetColumns = 2;
            var wide = Cells(PdfToExcelConverter.Convert(pdf));
            Assert.DoesNotContain(wide, c => c.Reference.StartsWith("C", StringComparison.Ordinal));
            Assert.Contains(wide, c => c.Text.Contains("North", StringComparison.Ordinal));
            Assert.Contains("columns", PdfToExcelConverter.LastError, StringComparison.Ordinal);

            PdfLimits.MaxSpreadsheetColumns = 3;
            var atLimit = Cells(PdfToExcelConverter.Convert(pdf));
            Assert.Contains(atLimit, c => c.Reference.StartsWith("C", StringComparison.Ordinal) && c.Text == "340");
            Assert.Null(PdfToExcelConverter.LastError);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A page's rows past a worksheet's last (<see cref="PdfLimits.MaxSpreadsheetRows"/>) are not written,
    /// and the converter says so; the control, the same page under the default, writes every line.
    /// </summary>
    [Fact]
    public void RowsPastAWorksheetsLast_AreNotWritten_AndSaid()
    {
        byte[] pdf = OnePagePdf("BT /F1 11 Tf 1 0 0 1 60 700 Tm (one) Tj 1 0 0 1 60 670 Tm (two) Tj "
                                + "1 0 0 1 60 640 Tm (three) Tj 1 0 0 1 60 610 Tm (four) Tj ET\n");

        var all = Cells(PdfToExcelConverter.Convert(pdf));
        Assert.Equal(new[] { "one", "two", "three", "four" }, all.Select(c => c.Text));
        Assert.Null(PdfToExcelConverter.LastError);

        try
        {
            PdfLimits.MaxSpreadsheetRows = 2;
            var cut = Cells(PdfToExcelConverter.Convert(pdf));
            Assert.Equal(new[] { "one", "two" }, cut.Select(c => c.Text));
            Assert.Contains("rows", PdfToExcelConverter.LastError, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The one worksheet's cells, each with its reference, in document order.</summary>
    private static List<(string Reference, string Text)> Cells(byte[] xlsx)
    {
        using var book = SpreadsheetDocument.Open(new MemoryStream(xlsx), false);
        return book.WorkbookPart!.WorksheetParts.Single().Worksheet
            .Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>()
            .Select(c => (c.CellReference!.Value!, c.CellValue?.Text ?? ""))
            .ToList();
    }

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
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

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
