// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// The public converters refuse an encrypted document, as the export adapter does, rather than
/// seeing zero pages and writing an empty .docx or .pptx, or an .xlsx with no sheet, with nothing
/// said. A document with truly no pages converts, and says so.
/// </summary>
public class ConverterEncryptedInputTests
{
    [Fact]
    public void Docx_OfAnEncryptedDocument_IsRefused()
        => Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfToDocxConverter.Convert(new MemoryStream(DocxExportEncryptedInputTests.EncryptedPdf())));

    [Fact]
    public void Xlsx_OfAnEncryptedDocument_IsRefused()
        => Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfToExcelConverter.Convert(DocxExportEncryptedInputTests.EncryptedPdf()));

    [Fact]
    public void Pptx_OfAnEncryptedDocument_IsRefused()
        => Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfToPptxConverter.Convert(DocxExportEncryptedInputTests.EncryptedPdf()));

    /// <summary>The control: a plain document converts to each format, and the DOCX says nothing.</summary>
    [Fact]
    public void APlainDocument_StillConverts()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Body");

        PdfToDocxConverter.Convert(new MemoryStream(pdf));
        Assert.Null(PdfToDocxConverter.LastError);

        using var xlsx = SpreadsheetDocument.Open(new MemoryStream(PdfToExcelConverter.Convert(pdf)), false);
        Assert.Single(xlsx.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>());

        using var pptx = PresentationDocument.Open(new MemoryStream(PdfToPptxConverter.Convert(pdf)), false);
        Assert.Single(pptx.PresentationPart!.SlideParts);
    }

    /// <summary>
    /// A document with no pages: the DOCX says so, and the XLSX holds one empty sheet — CT_Sheets
    /// requires one, and a workbook without it is one Excel calls corrupt.
    /// </summary>
    [Fact]
    public void ADocumentWithNoPages_SaysSo_AndTheWorkbookHasASheet()
    {
        byte[] pdf = NoPagesPdf();

        PdfToDocxConverter.Convert(new MemoryStream(pdf));
        Assert.Contains("no pages", PdfToDocxConverter.LastError, StringComparison.Ordinal);

        using var xlsx = SpreadsheetDocument.Open(new MemoryStream(PdfToExcelConverter.Convert(pdf)), false);
        var sheet = Assert.Single(xlsx.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>());
        var part = (WorksheetPart)xlsx.WorkbookPart.GetPartById(sheet.Id!.Value!);
        Assert.Empty(part.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>());
    }

    private static byte[] NoPagesPdf()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[2];
        offsets[0] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[1] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 3\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
