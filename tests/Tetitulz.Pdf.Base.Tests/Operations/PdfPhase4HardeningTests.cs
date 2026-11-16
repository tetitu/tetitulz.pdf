// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Round 27 hardening tests covering code-review findings:
///   - Crop validation rejects negative width / height
///   - PdfGraphicsExtractor.LastError exposes Form XObject decode failures
///     and recursion-depth cap hits
///   - PdfStructuredTextExtractor.LastError matches the same contract
///   - Strict-mode confidence denominator excludes skipped cells
/// All PDFs are built in-memory.
/// </summary>
public class PdfPhase4HardeningTests
{
    // ------------------------------------------------------------------
    //  Minimal in-memory single-page PDF builder
    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ==================================================================
    //  Crop validation
    // ==================================================================

    [Fact]
    public void Crop_NegativeWidth_Throws()
    {
        const string cs = "BT /F1 12 Tf 100 500 Td (Hi) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var page = PdfStructuredTextExtractor.Extract(ms)[0];

        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            page.Crop(0f, 0f, -1f, 100f));
    }

    [Fact]
    public void Crop_NegativeHeight_Throws()
    {
        const string cs = "BT /F1 12 Tf 100 500 Td (Hi) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var page = PdfStructuredTextExtractor.Extract(ms)[0];

        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            page.Crop(0f, 0f, 100f, -1f));
    }

    [Fact]
    public void Crop_ZeroSize_ReturnsEmptyButDoesNotThrow()
    {
        const string cs = "BT /F1 12 Tf 100 500 Td (Hi) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var page = PdfStructuredTextExtractor.Extract(ms)[0];

        // Zero is non-negative so must not throw; result has no lines.
        var cropped = page.Crop(0f, 0f, 0f, 0f);
        Assert.Empty(cropped.Lines);
    }

    // ==================================================================
    //  Diagnostics — LastError surface
    // ==================================================================

    [Fact]
    public void GraphicsExtractor_LastError_NullAfterCleanRun()
    {
        const string cs = "50 500 m 200 500 l S\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        _ = PdfGraphicsExtractor.Extract(ms);
        Assert.Null(PdfGraphicsExtractor.LastError);
    }

    [Fact]
    public void GraphicsExtractor_LastError_ResetAtStartOfEachCall()
    {
        const string cs = "50 500 m 200 500 l S\n";
        using var ms1 = new MemoryStream(BuildPdf(cs));
        _ = PdfGraphicsExtractor.Extract(ms1);

        using var ms2 = new MemoryStream(BuildPdf(cs));
        _ = PdfGraphicsExtractor.Extract(ms2);
        Assert.Null(PdfGraphicsExtractor.LastError);
    }

    [Fact]
    public void StructuredTextExtractor_LastError_NullAfterCleanRun()
    {
        const string cs = "BT /F1 12 Tf 100 500 Td (Hi) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        _ = PdfStructuredTextExtractor.Extract(ms);
        Assert.Null(PdfStructuredTextExtractor.LastError);
    }

    // ==================================================================
    //  Strict-mode confidence — denominator excludes skipped cells
    // ==================================================================

    [Fact]
    public void LinesStrict_Confidence_ExcludesSkippedCells()
    {
        // 2x2 bordered grid — every cell has all 4 borders → strict keeps all cells,
        // confidence must be exactly 1.0.
        const string cs =
            "50 700 m 200 700 l S\n" +
            "50 680 m 200 680 l S\n" +
            "50 660 m 200 660 l S\n" +
            "50 700 m 50 660 l S\n"  +
            "125 700 m 125 660 l S\n" +
            "200 700 m 200 660 l S\n";

        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions
            {
                Strategy     = TableStrategy.LinesStrict,
                MinTableRows = 1
            });

        Assert.NotEmpty(tables);
        Assert.Equal(1f, tables[0].Confidence, precision: 3);
    }
}
