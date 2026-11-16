// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Review-fix regression: PdfLayoutAnalyzer.DetectColumns must clamp the X-bucket
/// resolution. A glyph placed at a huge X coordinate makes the page-width span ≈ 1e9,
/// which previously allocated a per-X bool[] grid of ~1e9 columns → OutOfMemory / hang
/// on the DEFAULT extraction path (every Extract() calls DetectColumns via the
/// PdfPageTextLayout constructor).
/// </summary>
public class ReviewFixOps_DetectColumnsDosTests
{
    // Builds a one-page PDF with the supplied content stream.
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

    [Fact]
    public void Extract_GlyphAtHugeX_DoesNotOomOrHang()
    {
        // One normal glyph near the origin and one placed ~500 million points to the
        // right. Pre-fix this forced xBuckets ≈ 5e8 → a multi-gigabyte allocation.
        const string cs =
            "BT /F1 12 Tf 50 700 Td (A) Tj ET\n" +
            "BT /F1 12 Tf 500000000 700 Td (B) Tj ET\n";

        using var ms = new MemoryStream(BuildPdf(cs));

        var sw = Stopwatch.StartNew();
        var pages = PdfStructuredTextExtractor.Extract(ms);   // calls DetectColumns internally
        sw.Stop();

        Assert.NotNull(pages);
        Assert.Single(pages);
        Assert.NotNull(pages[0].Columns);                     // populated, no throw
        // Hang-catcher, not a performance bound — a tight ceiling turns CPU contention during a
        // parallel run into a false regression. See GAPS.md §8a.
        Assert.True(sw.ElapsedMilliseconds < 120_000,
            $"Extraction took {sw.ElapsedMilliseconds} ms — expected the X-bucket clamp to keep it fast.");
    }

    [Fact]
    public void DetectColumns_GlyphAtHugeX_ReturnsBoundedResult()
    {
        const string cs =
            "BT /F1 12 Tf 50 700 Td (A) Tj ET\n" +
            "BT /F1 12 Tf 50 680 Td (C) Tj ET\n" +
            "BT /F1 12 Tf 1000000000 700 Td (B) Tj ET\n";

        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);

        // Must not throw, and the number of detected column zones stays small
        // (it cannot exceed the bucket resolution).
        var cols = PdfLayoutAnalyzer.DetectColumns(pages[0]);
        Assert.NotNull(cols);
        Assert.True(cols.Count < 5000);
    }
}
