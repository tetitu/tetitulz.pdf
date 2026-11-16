// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.Plugins.PageEdits;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for Base-layer fixes: rendering, form filling/flattening, operations.
/// </summary>
public class AllFixesBaseTests
{
    #region PdfFormFiller: Unicode field values

    [Fact]
    public void FormFiller_UnicodeValue_DoesNotThrow()
    {
        // Build a minimal PDF with an AcroForm text field
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n");

        int obj2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n");

        int obj3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n");

        int obj4 = pdf.Length;
        pdf.Append("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) ");
        pdf.Append("/Rect [100 700 300 720] /V () >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 5\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj4);
        pdf.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] pdfBytes = Encoding.ASCII.GetBytes(pdf.ToString());

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        // Fill with Unicode text (Hebrew) — should not throw
        var values = new System.Collections.Generic.Dictionary<string, string>
        {
            { "name", "\u05E9\u05DC\u05D5\u05DD" } // שלום
        };

        PdfFormFiller.Fill(input, output, values);
        Assert.True(output.Length > pdfBytes.Length);

        // Verify the output is a valid PDF
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);
    }

    #endregion

    #region PdfDocumentBuilder: compressed content streams

    [Fact]
    public void DocumentBuilder_Compressed_ProducesValidPdf()
    {
        var builder = new PdfDocumentBuilder().WithCompression(true);
        var page = builder.AddPage(612, 792);
        string f = page.AddStandardFont("Courier");
        page.BeginText().SetFont(f, 10).SetTextPosition(72, 700).ShowText("Compressed").EndText();

        using var ms = new MemoryStream();
        builder.Build(ms);
        ms.Position = 0;

        using var doc = PdfDocument.Open(ms);
        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("Compressed", text);
    }

    #endregion

    #region PdfDocumentBuilder: image insertion

    [Fact]
    public void DocumentBuilder_WithImage_ProducesValidPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(612, 792);

        // Create a tiny 2x2 red/green/blue/white raw RGB image
        byte[] imgData = new byte[]
        {
            255, 0, 0,    0, 255, 0,   // row 1: red, green
            0, 0, 255,    255, 255, 255 // row 2: blue, white
        };
        string img = page.AddImage(imgData, 2, 2, colorSpace: "DeviceRGB");
        page.DrawImage(img, 100, 600, 100, 100);

        using var ms = new MemoryStream();
        builder.Build(ms);
        ms.Position = 0;

        using var doc = PdfDocument.Open(ms);
        Assert.Single(doc.Pages);
    }

    #endregion

    #region PdfContentStreamBuilder: all new path operators

    [Fact]
    public void ContentStreamBuilder_AllPathOps_ProduceValidPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 200);
        page.SaveState()
            .SetFillColorRgb(1, 0, 0)
            .MoveTo(10, 10)
            .LineTo(100, 10)
            .CurveTo(100, 50, 50, 90, 10, 90)
            .ClosePath()
            .Fill()
            .RestoreState();

        page.SaveState()
            .Rectangle(20, 20, 50, 50)
            .ClipNonzero()
            .SetFillColorGray(0.5f)
            .Rectangle(0, 0, 200, 200)
            .Fill()
            .RestoreState();

        using var ms = new MemoryStream();
        builder.Build(ms);
        ms.Position = 0;

        using var doc = PdfDocument.Open(ms);
        Assert.Single(doc.Pages);
    }

    #endregion

    #region Text extraction: multi-page accuracy

    [Fact]
    public void TextExtractor_MultiPage_AllPagesExtracted()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("Page 1", text);
        Assert.Contains("Page 2", text);
        Assert.Contains("Page 3", text);
    }

    #endregion

    #region PdfOptimizer: preserves content after optimization

    [Fact]
    public void Optimizer_PreservesText()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("OptimizeMe");
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("OptimizeMe", text);
    }

    #endregion

    #region PdfMerger: merge preserves all pages

    [Fact]
    public void Merger_TwoDocuments_AllPagesPresent()
    {
        byte[] pdf1 = MinimalPdfBuilder.CreateMinimalPdf("Doc1Text");
        byte[] pdf2 = MinimalPdfBuilder.CreateMinimalPdf("Doc2Text");

        using var s1 = new MemoryStream(pdf1);
        using var s2 = new MemoryStream(pdf2);
        using var output = new MemoryStream();

        PdfMerger.Merge(output, s1, s2);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(2, doc.Pages.Count);
    }

    #endregion

    #region PdfSplitter: split preserves content

    [Fact]
    public void Splitter_ExtractSinglePage_HasContent()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfSplitter.SplitRange(input, output, 1, 1); // page 1 (1-based)

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);
    }

    #endregion
}
