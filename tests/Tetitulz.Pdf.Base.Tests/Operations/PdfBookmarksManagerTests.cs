// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Tests for <see cref="PdfBookmarksManager"/> / <see cref="PdfOperations.GetBookmarks"/>.
/// Covers the read path only; write tests live alongside WriteBookmarks usage.
/// </summary>
public sealed class PdfBookmarksManagerTests
{
    [Fact]
    public void GetBookmarks_ExplicitDestArray_ResolvesPage()
    {
        byte[] pdf = BuildPdfWithBookmarkExplicitDest();
        using var ms = new MemoryStream(pdf);
        var bookmarks = PdfOperations.GetBookmarks(ms);

        var bm = Assert.Single(bookmarks);
        Assert.Equal("Chapter 1", bm.Title);
        Assert.Equal(1, bm.PageNumber);   // points to the second page (0-based index 1)
    }

    /// <summary>
    /// Regression: bookmark /Dest that is a named destination string (PdfString key into
    /// the /Names /Dests name tree) previously returned null page number because
    /// ResolvePageNumber only handled PdfArray, not named-dest strings. Bookmark click
    /// did nothing (tag = -1, guard pageIndex >= 0 failed).
    /// </summary>
    [Fact]
    public void GetBookmarks_NamedDest_ViaNameTree_ResolvesPage()
    {
        byte[] pdf = BuildPdfWithBookmarkNamedDestViaNameTree();
        using var ms = new MemoryStream(pdf);
        var bookmarks = PdfOperations.GetBookmarks(ms);

        var bm = Assert.Single(bookmarks);
        Assert.Equal("Chapter 2", bm.Title);
        Assert.Equal(1, bm.PageNumber);
    }

    /// <summary>
    /// Regression: same as above but the named dest value in the name tree is an INDIRECT
    /// reference to the dest array (N G R) rather than an inline array. Tests both the
    /// PdfBookmarksManager named-dest path AND ResolveDestArray's indirect-reference fix.
    /// </summary>
    [Fact]
    public void GetBookmarks_NamedDest_ViaNameTree_IndirectDestArray_ResolvesPage()
    {
        byte[] pdf = BuildPdfWithBookmarkNamedDestAndIndirectArray();
        using var ms = new MemoryStream(pdf);
        var bookmarks = PdfOperations.GetBookmarks(ms);

        var bm = Assert.Single(bookmarks);
        Assert.Equal("Chapter 3", bm.Title);
        Assert.Equal(1, bm.PageNumber);
    }

    [Fact]
    public void GetBookmarks_NoOutlines_ReturnsEmpty()
    {
        byte[] pdf = BuildBarePdf();
        using var ms = new MemoryStream(pdf);
        var bookmarks = PdfOperations.GetBookmarks(ms);
        Assert.Empty(bookmarks);
    }

    // ----------------------------------------------------------------
    //  Fixtures
    // ----------------------------------------------------------------

    /// <summary>
    /// Two-page PDF with one bookmark whose /Dest is an explicit array [page2Ref /Fit].
    /// </summary>
    private static byte[] BuildPdfWithBookmarkExplicitDest()
    {
        // Objects:
        //  1 = Catalog  → /Pages 2, /Outlines 5
        //  2 = Pages
        //  3 = Page 1
        //  4 = Page 2  (bookmark target)
        //  5 = Outline root
        //  6 = Outline item "Chapter 1"  /Dest [4 0 R /Fit]
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Title (Chapter 1) /Parent 5 0 R /Dest [4 0 R /Fit] >>\nendobj\n");

        WriteXref(sb, new[] { o1, o2, o3, o4, o5, o6 });
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Two-page PDF with one bookmark whose /A GoTo /D is a named destination string
    /// resolved via the /Names /Dests name tree (inline dest array in the leaf).
    /// </summary>
    private static byte[] BuildPdfWithBookmarkNamedDestViaNameTree()
    {
        // Objects:
        //  1 = Catalog  → /Pages 2, /Names 7, /Outlines 5
        //  2 = Pages
        //  3 = Page 1
        //  4 = Page 2  (bookmark target)
        //  5 = Outline root
        //  6 = Outline item "Chapter 2"  /A << /S /GoTo /D (ch2dest) >>
        //  7 = /Names dict  → /Dests << /Names [(ch2dest) [4 0 R /Fit]] >>
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R /Names 7 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Title (Chapter 2) /Parent 5 0 R /A << /S /GoTo /D (ch2dest) >> >>\nendobj\n");
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Dests << /Names [(ch2dest) [4 0 R /Fit]] >> >>\nendobj\n");

        WriteXref(sb, new[] { o1, o2, o3, o4, o5, o6, o7 });
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Two-page PDF with a bookmark that uses a named destination whose dest array is
    /// stored as an INDIRECT reference in the name tree leaf — the double regression case.
    /// </summary>
    private static byte[] BuildPdfWithBookmarkNamedDestAndIndirectArray()
    {
        // Objects:
        //  1 = Catalog  → /Pages 2, /Names 7, /Outlines 5
        //  2 = Pages
        //  3 = Page 1
        //  4 = Page 2  (bookmark target)
        //  5 = Outline root
        //  6 = Outline item "Chapter 3"  /A << /S /GoTo /D (ch3dest) >>
        //  7 = /Names dict  → /Dests 8 0 R
        //  8 = name-tree leaf  /Names [(ch3dest) 9 0 R]
        //  9 = dest array  [4 0 R /Fit]   ← indirect reference
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R /Names 7 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Title (Chapter 3) /Parent 5 0 R /A << /S /GoTo /D (ch3dest) >> >>\nendobj\n");
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Dests 8 0 R >>\nendobj\n");
        int o8 = sb.Length;
        sb.Append("8 0 obj\n<< /Names [(ch3dest) 9 0 R] >>\nendobj\n");
        int o9 = sb.Length;
        sb.Append("9 0 obj\n[4 0 R /Fit]\nendobj\n");

        WriteXref(sb, new[] { o1, o2, o3, o4, o5, o6, o7, o8, o9 });
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

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
        WriteXref(sb, new[] { o1, o2, o3 });
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void WriteXref(StringBuilder sb, int[] offsets)
    {
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Length + 1);
        foreach (int o in offsets)
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ");
        sb.Append(offsets.Length + 1);
        sb.Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
    }
}
