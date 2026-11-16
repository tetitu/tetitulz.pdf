// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Standalone (non-render) gap tests:
///   GAP 23 — PDF Actions, JavaScript extraction, Calculation Order
///   GAP 24 — Structure Tree reading
///   GAP 25 — Portfolio detection, Incremental Save Inspector, PDF→HTML
/// </summary>
public sealed class PdfGapStandaloneTests
{
    // ====================================================================
    //  GAP 23 — PDF Actions
    // ====================================================================

    [Fact]
    public void ExtractActions_MinimalPdf_ReturnsEmptyList()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        var actions = PdfOperations.ExtractActions(ms);
        Assert.NotNull(actions);
        // A minimal PDF with no /OpenAction should return no actions.
        Assert.Empty(actions);
    }

    [Fact]
    public void ExtractActions_PdfWithOpenAction_DetectsGoToAction()
    {
        byte[] pdf = BuildPdfWithOpenAction();
        using var ms = new MemoryStream(pdf);
        var actions = PdfOperations.ExtractActions(ms);
        Assert.NotEmpty(actions);
        Assert.Equal(PdfActionType.GoTo, actions[0].ActionType);
        Assert.Equal("OpenDocument", actions[0].Trigger);
    }

    [Fact]
    public void GetCalculationOrder_MinimalPdf_ReturnsEmpty()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        var order = PdfOperations.GetCalculationOrder(ms);
        Assert.NotNull(order);
        Assert.Empty(order);
    }

    // ====================================================================
    //  GAP 24 — Structure Tree
    // ====================================================================

    [Fact]
    public void ReadStructureTree_MinimalPdf_ReturnsRootNode()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        var root = PdfOperations.ReadStructureTree(ms);
        // A minimal PDF has no /StructTreeRoot — still returns a valid node.
        Assert.NotNull(root);
        Assert.Equal("StructTreeRoot", root.ElementType);
    }

    // ====================================================================
    //  GAP 25.2 — Portfolio
    // ====================================================================

    [Fact]
    public void IsPortfolio_MinimalPdf_ReturnsFalse()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        bool result = PdfOperations.IsPortfolio(ms);
        Assert.False(result);
    }

    [Fact]
    public void ListPortfolioItems_MinimalPdf_ReturnsEmpty()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        var items = PdfOperations.ListPortfolioItems(ms);
        Assert.NotNull(items);
        Assert.Empty(items);
    }

    // ====================================================================
    //  GAP 25.3 — Incremental Save Inspector
    // ====================================================================

    [Fact]
    public void InspectIncrementalSave_MinimalPdf_SingleRevision()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        var info = PdfOperations.InspectIncrementalSave(ms);
        Assert.NotNull(info);
        Assert.Equal(1, info.RevisionCount);
        Assert.True(info.SignaturesPreserved);
    }

    [Fact]
    public void InspectIncrementalSave_ReturnsOffsetsList()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        var info = PdfOperations.InspectIncrementalSave(ms);
        Assert.NotNull(info.RevisionOffsets);
        Assert.Equal(info.RevisionCount, info.RevisionOffsets.Count);
        Assert.True(info.RevisionOffsets[0] > 0);
    }

    // ====================================================================
    //  GAP 25.4 — PDF to HTML
    // ====================================================================

    [Fact]
    public void ConvertToHtml_MinimalPdf_ReturnsHtmlDocument()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello GAP25");
        using var ms = new MemoryStream(pdf);
        string html = PdfHtmlConverter.ConvertToHtml(ms, new HtmlConversionOptions
        {
            EmbedImages = true,
            IncludeText = false,
            ImageDpi    = 72,
            Title       = "Test Doc"
        });
        Assert.NotNull(html);
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("<title>Test Doc</title>", html);
        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public void ConvertToHtml_DefaultOptions_ReturnsValidHtml()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        string html = PdfHtmlConverter.ConvertToHtml(ms);
        Assert.Contains("<html", html);
        Assert.Contains("</html>", html);
        Assert.Contains("class=\"page\"", html);
    }

    [Fact]
    public void ConvertToHtmlFiles_WritesIndexAndPngToDirectory()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string pdfPath = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(pdfPath, pdf);
            PdfHtmlConverter.ConvertToHtmlFiles(pdfPath, tempDir, new HtmlConversionOptions { ImageDpi = 72 });
            Assert.True(File.Exists(Path.Combine(tempDir, "index.html")));
            Assert.True(File.Exists(Path.Combine(tempDir, "page_1.png")));
        }
        finally
        {
            if (File.Exists(pdfPath)) File.Delete(pdfPath);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    /// <summary>Builds a minimal PDF with a /OpenAction /GoTo action pointing to page 1.</summary>
    private static byte[] BuildPdfWithOpenAction()
    {
        // We'll produce a hand-crafted PDF with a Catalog /OpenAction entry.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new int[6];

        // obj 1: Catalog with OpenAction referencing obj 6
        offsets[0] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OpenAction 6 0 R >>\nendobj\n");

        // obj 2: Pages
        offsets[1] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // obj 3: Page
        offsets[2] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // obj 4: empty content
        string cs = "q Q";
        offsets[3] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");

        // obj 5: font stub
        offsets[4] = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // obj 6: GoTo action to page 1
        offsets[5] = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Action /S /GoTo /D [3 0 R /Fit] >>\nendobj\n");

        int xrefOffset = sb.Length;
        sb.Append("xref\n");
        sb.Append("0 7\n");
        sb.Append("0000000000 65535 f \n");
        for (int i = 0; i < 6; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
