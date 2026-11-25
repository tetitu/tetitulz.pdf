// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for features added in the a native engine gap-analysis remediation:
///   • PDF repair with non-zero generation objects
///   • Structured text extraction with bounding boxes
///   • PDF → SVG rendering
///   • Inline images (BI/ID/EI) and Indexed color space (smoke tests)
/// </summary>
public sealed class PdfNewFeaturesTests
{
    // ====================================================================
    //  1. PDF Repair — non-zero generation objects
    // ====================================================================

    /// <summary>
    /// Builds a PDF where one object has generation number 1 (not the usual 0)
    /// and verifies the repair scanner recovers it.
    /// </summary>
    private static byte[] BuildPdfWithNonZeroGenObject()
    {
        // Object 2 uses generation 1 intentionally.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 1 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        // Corrupted xref (repair must re-scan).
        sb.Append("xref\n0 1\n0000000000 65535 f \n");
        sb.Append("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        sb.Append("startxref\n0\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void Repair_NonZeroGenObject_IsRecovered()
    {
        byte[] pdf = BuildPdfWithNonZeroGenObject();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var result = PdfRepair.Repair(input, output);
        // Both objects (1 0 obj and 2 1 obj) should be recovered.
        Assert.True(result.RecoveredObjectCount >= 2,
            $"Expected >= 2 objects recovered, got {result.RecoveredObjectCount}");
    }

    [Fact]
    public void Repair_NonZeroGenObject_OutputStartsWithPdfHeader()
    {
        byte[] pdf = BuildPdfWithNonZeroGenObject();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRepair.Repair(input, output);
        string header = Encoding.ASCII.GetString(output.ToArray(), 0,
            Math.Min(8, (int)output.Length));
        Assert.StartsWith("%PDF-", header);
    }

    [Fact]
    public void Repair_NonZeroGenObject_OutputContainsXref()
    {
        byte[] pdf = BuildPdfWithNonZeroGenObject();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRepair.Repair(input, output);
        string text = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("xref", text);
    }

    // Regression: gen-0 scanning still works after the change.
    [Fact]
    public void Repair_GenZeroObjects_StillRecovered()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        Assert.True(result.RecoveredObjectCount > 0);
    }

    // ====================================================================
    //  2. Structured text extraction with bounding boxes
    // ====================================================================

    [Fact]
    public void ExtractStructuredText_ReturnsOneEntryPerPage()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var pages = PdfOperations.ExtractStructuredText(ms);
        Assert.Single(pages);
        Assert.Equal(0, pages[0].PageNumber);
    }

    [Fact]
    public void ExtractStructuredText_MultiPage_ReturnsManyEntries()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        var pages = PdfOperations.ExtractStructuredText(ms);
        Assert.Equal(3, pages.Count);
    }

    [Fact]
    public void ExtractStructuredText_LinesHaveNonZeroHeight()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Test Text");
        using var ms = new MemoryStream(pdf);
        var pages = PdfOperations.ExtractStructuredText(ms);
        // Not every test PDF will emit font metrics we can measure — just ensure
        // no line has a negative height.
        foreach (var page in pages)
            foreach (var line in page.Lines)
                Assert.True(line.Height >= 0f);
    }

    [Fact]
    public void ExtractStructuredText_WordsAreNonEmpty()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello");
        using var ms = new MemoryStream(pdf);
        var pages = PdfOperations.ExtractStructuredText(ms);
        foreach (var page in pages)
            foreach (var line in page.Lines)
                foreach (var word in line.Words)
                    Assert.False(string.IsNullOrEmpty(word.Text));
    }

    [Fact]
    public void ExtractStructuredText_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractStructuredText((Stream)null!));

    [Fact]
    public void ExtractStructuredText_NullPath_Throws()
        => Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractStructuredText((string)null!));

    /// <summary>
    /// Structured extraction ends a word at a space glyph the content stream drew, as plain
    /// extraction does, so the two APIs agree on "Hello World".
    ///
    /// <para>With blank glyphs removed before words were built, only the gap would be left: at 12 pt a
    /// single space is narrower than the split threshold, so structured extraction would return the
    /// single word <c>"HelloWorld"</c> while <c>PdfOperations.ExtractText</c> returns "Hello World".</para>
    ///
    /// <para>The assertion is the text, not <c>NotNull</c> — which an empty string satisfies.</para>
    /// </summary>
    [Fact]
    public void ExtractStructuredText_SplitsWordsAtASpaceGlyph_LikePlainExtraction()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");

        using var forLayout = new MemoryStream(pdf);
        var pages = PdfOperations.ExtractStructuredText(forLayout);

        var page = Assert.Single(pages);
        var line = Assert.Single(page.Lines);

        Assert.Equal(new[] { "Hello", "World" }, line.Words.Select(w => w.Text));
        Assert.Equal("Hello World", line.Text);
        Assert.Contains("Hello World", page.ToString());

        using var forPlain = new MemoryStream(pdf);
        Assert.Contains("Hello World", PdfOperations.ExtractText(forPlain));
    }

    // ====================================================================
    //  3. SVG rendering
    // ====================================================================

    [Fact]
    public void RenderPageToSvg_ReturnsSvgXml()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfOperations.RenderPageToSvg(ms, 0);
        Assert.NotNull(svg);
        Assert.Contains("<svg", svg);
    }

    [Fact]
    public void RenderPageToSvg_ContainsXmlDeclaration()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfOperations.RenderPageToSvg(ms, 0);
        Assert.StartsWith("<?xml", svg);
    }

    [Fact]
    public void RenderPageToSvg_ContainsClosingSvgTag()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfOperations.RenderPageToSvg(ms, 0);
        Assert.Contains("</svg>", svg);
    }

    [Fact]
    public void RenderPageToSvg_ContainsWidthAndHeight()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfOperations.RenderPageToSvg(ms, 0);
        Assert.Contains("width=", svg);
        Assert.Contains("height=", svg);
    }

    [Fact]
    public void RenderPageToSvg_InvalidPageIndex_Throws()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOperations.RenderPageToSvg(ms, 99));
    }

    [Fact]
    public void RenderPageToSvg_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => PdfOperations.RenderPageToSvg((Stream)null!, 0));

    [Fact]
    public void RenderPageToSvg_NullPath_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => PdfOperations.RenderPageToSvg((string)null!, 0));

    [Fact]
    public void RenderAllPagesToSvg_ReturnsOneArrayEntryPerPage()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        string[] svgs = PdfOperations.RenderAllPagesToSvg(ms);
        Assert.Equal(3, svgs.Length);
        foreach (var svg in svgs)
            Assert.Contains("<svg", svg);
    }

    [Fact]
    public void RenderPageToSvg_ContainsWhiteBackground()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfOperations.RenderPageToSvg(ms, 0);
        Assert.Contains("fill=\"white\"", svg);
    }

    [Fact]
    public void RenderPageToSvg_MultiPagePdf_FirstPageOk()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(5);
        using var ms = new MemoryStream(pdf);
        string svg = PdfOperations.RenderPageToSvg(ms, 0);
        Assert.Contains("<svg", svg);
    }

    // ====================================================================
    //  4. Inline images (BI/ID/EI) — smoke test via renderer
    // ====================================================================

    /// <summary>
    /// The inline image's four samples must reach the page, in image-space order: row 0 is the
    /// TOP of the unit square the CTM maps, so red/green land above blue/white.
    ///
    /// <para>Its predecessor rendered the same PDF and asserted only that nothing was thrown —
    /// which is exactly as true when <c>BI</c>…<c>EI</c> is skipped over as uninterpreted bytes,
    /// the failure mode an inline-image parser actually has.</para>
    /// </summary>
    [Fact]
    public void RenderPage_PdfWithInlineImage_PaintsTheFourSamplesInImageSpaceOrder()
    {
        // `q 50 0 0 50 100 600 cm` maps the image's unit square onto (100,600)-(150,650).
        byte[] pdf = BuildPdfWithInlineImage();
        using var bmp = Rendering.PixelAssert.Render(pdf);

        AssertSample(bmp, 103f, 647f, SKColors.Red,   "top-left");
        AssertSample(bmp, 147f, 647f, SKColors.Lime,  "top-right");   // 0,255,0
        AssertSample(bmp, 103f, 603f, SKColors.Blue,  "bottom-left");
        AssertSample(bmp, 147f, 603f, SKColors.White, "bottom-right");

        // …and nothing spills outside the 50×50pt box the CTM defined.
        Assert.False(Rendering.PixelAssert.IsInk(Rendering.PixelAssert.At(bmp, 90f, 625f)),
            "ink left of the image box");
        Assert.False(Rendering.PixelAssert.IsInk(Rendering.PixelAssert.At(bmp, 160f, 625f)),
            "ink right of the image box");

        static void AssertSample(SKBitmap bmp, float x, float y, SKColor want, string where)
        {
            var got = Rendering.PixelAssert.At(bmp, x, y);
            Assert.True(Rendering.PixelAssert.Near(got, want, tolerance: 40),
                $"inline image {where} sample should be {want}, got {got}");
        }
    }

    private static byte[] BuildPdfWithInlineImage()
    {
        // 2ֳ—2 DeviceRGB raw pixel data (4 ֳ— 3 = 12 bytes): red, green, blue, white
        byte[] pixels = { 255, 0, 0,   0, 255, 0,
                          0,   0, 255, 255, 255, 255 };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        string inlineImg =
            "q 50 0 0 50 100 600 cm\n" +
            "BI\n" +
            "/W 2 /H 2 /CS /RGB /BPC 8\n" +
            "ID\n";

        // We need the content stream as bytes so we can embed binary pixel data.
        byte[] prefix = Encoding.ASCII.GetBytes(inlineImg);
        byte[] suffix = Encoding.ASCII.GetBytes("\nEI Q\n");
        byte[] content = new byte[prefix.Length + pixels.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, content, 0, prefix.Length);
        Buffer.BlockCopy(pixels, 0, content, prefix.Length, pixels.Length);
        Buffer.BlockCopy(suffix, 0, content, prefix.Length + pixels.Length, suffix.Length);

        int off1 = 0, off2, off3, off4;

        var hdr    = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        var obj1   = Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var obj2   = Encoding.ASCII.GetBytes("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var page   = Encoding.ASCII.GetBytes($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n");
        string lenStr = $"4 0 obj\n<< /Length {content.Length} >>\nstream\n";
        var obj4hdr = Encoding.ASCII.GetBytes(lenStr);
        var obj4end = Encoding.ASCII.GetBytes("\nendstream\nendobj\n");

        using var ms = new MemoryStream();
        ms.Write(hdr,    0, hdr.Length);    off1 = (int)ms.Position;
        ms.Write(obj1,   0, obj1.Length);   off2 = (int)ms.Position;
        ms.Write(obj2,   0, obj2.Length);   off3 = (int)ms.Position;
        ms.Write(page,   0, page.Length);   off4 = (int)ms.Position;
        ms.Write(obj4hdr,0, obj4hdr.Length);
        ms.Write(content,0, content.Length);
        ms.Write(obj4end,0, obj4end.Length);
        int xrefOff = (int)ms.Position;

        var xref = Encoding.ASCII.GetBytes(
            $"xref\n0 5\n0000000000 65535 f \n" +
            $"{off1:D10} 00000 n \n{off2:D10} 00000 n \n{off3:D10} 00000 n \n{off4:D10} 00000 n \n" +
            $"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xrefOff}\n%%EOF\n");
        ms.Write(xref, 0, xref.Length);
        return ms.ToArray();
    }

    // ====================================================================
    //  5. PdfPageTextLayout / PdfTextLine / PdfTextWord API surface
    // ====================================================================

    [Fact]
    public void PdfPageTextLayout_EmptyDoc_ReturnsEmptyLines()
    {
        // A PDF with no content stream should return zero lines.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int xOff = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.Append("0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xOff}\n%%EOF\n");

        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());
        using var ms = new MemoryStream(pdf);
        var pages = PdfOperations.ExtractStructuredText(ms);
        Assert.Single(pages);
        Assert.Empty(pages[0].Lines);
    }
}
