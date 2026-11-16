// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Tests for <see cref="PdfLinkExtractor"/> / <see cref="PdfOperations.ExtractLinks(Stream)"/>:
/// URI links, explicit in-document destinations, and named destinations all resolve
/// with the correct hit rectangle (PDF user space) and navigation target.
/// </summary>
public sealed class PdfLinkExtractorTests
{
    [Fact]
    public void ExtractLinks_ResolvesUri_ExplicitDest_AndNamedDest()
    {
        byte[] pdf = BuildTwoPagePdfWithLinks();

        using var ms = new MemoryStream(pdf);
        var links = PdfOperations.ExtractLinks(ms);

        Assert.Equal(3, links.Count);
        Assert.All(links, l => Assert.Equal(0, l.PageIndex)); // all live on page 0

        // 1) URI link — rect [10 20 110 60] → X=10 Y=20 W=100 H=40.
        var uri = links[0];
        Assert.Equal(PdfLinkKind.Uri, uri.Kind);
        Assert.Equal("https://example.com/", uri.Uri);
        Assert.Equal(-1, uri.TargetPageIndex);
        Assert.Equal(10f, uri.X, 3);
        Assert.Equal(20f, uri.Y, 3);
        Assert.Equal(100f, uri.Width, 3);
        Assert.Equal(40f, uri.Height, 3);

        // 2) Explicit /Dest [page2Ref /XYZ ...] → GoTo page index 1.
        var explicitGoTo = links[1];
        Assert.Equal(PdfLinkKind.GoTo, explicitGoTo.Kind);
        Assert.Null(explicitGoTo.Uri);
        Assert.Equal(1, explicitGoTo.TargetPageIndex);

        // 3) Named /Dest /MyDest resolved via the catalog /Dests dict → GoTo page index 1.
        var namedGoTo = links[2];
        Assert.Equal(PdfLinkKind.GoTo, namedGoTo.Kind);
        Assert.Equal(1, namedGoTo.TargetPageIndex);
    }

    [Fact]
    public void ExtractLinks_PathOverload_Works()
    {
        byte[] pdf = BuildTwoPagePdfWithLinks();
        string path = Path.Combine(Path.GetTempPath(),
            "tetitulz_links_" + System.Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            var links = PdfOperations.ExtractLinks(path);
            Assert.Equal(3, links.Count);
            Assert.Equal("https://example.com/", links.First(l => l.Kind == PdfLinkKind.Uri).Uri);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExtractLinks_NoAnnotations_ReturnsEmpty()
    {
        byte[] pdf = BuildBarePdf();
        using var ms = new MemoryStream(pdf);
        var links = PdfOperations.ExtractLinks(ms);
        Assert.Empty(links);
    }

    /// <summary>
    /// Regression test for bug where named destinations stored in the /Names /Dests
    /// name tree as INDIRECT references to dest arrays (N G R) were silently dropped —
    /// ResolveDestArray only checked obj is PdfArray, missing the resolved-reference case.
    /// </summary>
    [Fact]
    public void ExtractLinks_NamedDest_ViaNameTree_WithIndirectDestArray_ResolvesPage()
    {
        byte[] pdf = BuildPdfWithNameTreeAndIndirectDestArray();
        using var ms = new MemoryStream(pdf);
        var links = PdfOperations.ExtractLinks(ms);

        var goTo = Assert.Single(links.Where(l => l.Kind == PdfLinkKind.GoTo));
        Assert.Equal(1, goTo.TargetPageIndex);
    }

    /// <summary>
    /// Named destination in the /Names /Dests name tree whose value is a direct inline
    /// array — verifies the non-indirect path still works after the fix.
    /// </summary>
    [Fact]
    public void ExtractLinks_NamedDest_ViaNameTree_WithInlineDestArray_ResolvesPage()
    {
        byte[] pdf = BuildPdfWithNameTreeAndInlineDestArray();
        using var ms = new MemoryStream(pdf);
        var links = PdfOperations.ExtractLinks(ms);

        var goTo = Assert.Single(links.Where(l => l.Kind == PdfLinkKind.GoTo));
        Assert.Equal(1, goTo.TargetPageIndex);
    }

    // ----------------------------------------------------------------
    //  Fixtures
    // ----------------------------------------------------------------

    /// <summary>
    /// Two-page PDF whose first page carries three /Link annotations: a /URI action,
    /// an explicit /Dest to page 2, and a named /Dest (/MyDest) resolved through the
    /// catalog's legacy /Dests dictionary (also pointing at page 2).
    /// </summary>
    private static byte[] BuildTwoPagePdfWithLinks()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R" +
                  " /Dests << /MyDest [4 0 R /Fit] >> >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]" +
                  " /Annots [5 0 R 6 0 R 7 0 R] >>\nendobj\n");

        int o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Annot /Subtype /Link /Rect [10 20 110 60]" +
                  " /A << /S /URI /URI (https://example.com/) >> >>\nendobj\n");

        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Annot /Subtype /Link /Rect [10 100 110 140]" +
                  " /Dest [4 0 R /XYZ null null null] >>\nendobj\n");

        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /Annot /Subtype /Link /Rect [10 160 60 190]" +
                  " /Dest /MyDest >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5, o6, o7 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Two-page PDF with a /Names /Dests name tree whose single entry's destination array
    /// is stored as an INDIRECT reference (the "8 0 R" case that was previously silently
    /// dropped by ResolveDestArray).
    /// </summary>
    private static byte[] BuildPdfWithNameTreeAndIndirectDestArray()
    {
        // Object layout:
        //  1 = Catalog (root), references /Names 5 0 R
        //  2 = Pages node
        //  3 = Page 1 (carries the /Link annotation)
        //  4 = Page 2 (the navigation target)
        //  5 = /Names dict  → /Dests 6 0 R
        //  6 = Dests name-tree leaf  → /Names [(dest2) 8 0 R]
        //  7 = Link annotation  → /Dest (dest2)   (string named dest)
        //  8 = dest array  [4 0 R /Fit]            ← the indirect array under test
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names 5 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [7 0 R] >>\nendobj\n");
        int o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Dests 6 0 R >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Names [(dest2) 8 0 R] >>\nendobj\n");
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest (dest2) >>\nendobj\n");
        int o8 = sb.Length;
        sb.Append("8 0 obj\n[4 0 R /Fit]\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5, o6, o7, o8 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Same as above but the destination array is inline in the name tree leaf (not indirect).
    /// </summary>
    private static byte[] BuildPdfWithNameTreeAndInlineDestArray()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names 5 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>\nendobj\n");
        int o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o5 = sb.Length;
        // Inline dest array directly in the /Names array
        sb.Append("5 0 obj\n<< /Dests << /Names [(destInline) [4 0 R /Fit]] >> >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Annot /Subtype /Link /Rect [10 10 50 50] /Dest (destInline) >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5, o6 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Single-page PDF with no annotations at all.</summary>
    private static byte[] BuildBarePdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
