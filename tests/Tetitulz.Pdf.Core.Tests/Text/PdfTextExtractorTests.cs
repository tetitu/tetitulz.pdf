// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests.Text;

public class PdfTextExtractorTests
{
    [Fact]
    public void ExtractText_SinglePage_ReturnsText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.Contains("Hello World", text);
    }

    [Fact]
    public void ExtractText_MultiPage_ReturnsAllText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        string text = PdfTextExtractor.ExtractText(doc);

        Assert.Contains("Page 1", text);
        Assert.Contains("Page 2", text);
        Assert.Contains("Page 3", text);
    }

    [Fact]
    public void ExtractText_EmptyPage_ReturnsEmpty()
    {
        // Create a PDF with a page that has no content stream
        var pdf = new System.Text.StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        int xrefOffset = pdf.Length;
        pdf.Append("xref\n0 4\n");
        pdf.AppendFormat("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3);
        pdf.AppendFormat("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        byte[] pdfBytes = System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);
        Assert.Equal(string.Empty, text);
    }
}
