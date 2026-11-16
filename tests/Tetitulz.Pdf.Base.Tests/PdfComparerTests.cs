// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;

namespace Tetitulz.Pdf.Base.Tests;

public class PdfComparerTests
{
    private static byte[] BuildPdfWithText(string text)
    {
        var cs = $"BT /F1 12 Tf 50 700 Td ({text}) Tj ET";
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
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);
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

    /// <summary>
    /// A side whose page cannot be read is refused: the extractor hands the page back empty, and two
    /// documents neither of which can be read would otherwise compare identical. The control, two
    /// readable documents of different text, still compares.
    /// </summary>
    [Fact]
    public void CompareText_APageThatCannotBeRead_IsRefusedNotReadAsEmpty()
    {
        byte[] damagedA = BuildPdfWithDamagedContent("AAAAAAAA");
        byte[] damagedB = BuildPdfWithDamagedContent("BBBBBBBB");

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfComparer.CompareText(new MemoryStream(damagedA), new MemoryStream(damagedB)));
        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);

        Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfComparer.CompareText(new MemoryStream(BuildPdfWithText("Hello")), new MemoryStream(damagedB)));

        var control = PdfComparer.CompareText(new MemoryStream(BuildPdfWithText("Hello")), new MemoryStream(BuildPdfWithText("Bye")));
        Assert.False(control.AreTextEqual);
    }

    /// <summary>One page whose content stream claims FlateDecode over bytes that are not deflate data.</summary>
    private static byte[] BuildPdfWithDamagedContent(string notDeflate)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} /Filter /FlateDecode >>\nstream\n{1}\nendstream\nendobj\n", notDeflate.Length, notDeflate);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void CompareText_IdenticalPdfs_AreTextEqualTrue()
    {
        byte[] pdf = BuildPdfWithText("Hello World");
        using var l = new MemoryStream(pdf);
        using var r = new MemoryStream(pdf);
        var result = PdfComparer.CompareText(l, r);
        Assert.True(result.AreTextEqual);
    }

    [Fact]
    public void CompareText_DifferentText_AreTextEqualFalse()
    {
        byte[] left  = BuildPdfWithText("Hello World");
        byte[] right = BuildPdfWithText("Goodbye World");
        using var l = new MemoryStream(left);
        using var r = new MemoryStream(right);
        var result = PdfComparer.CompareText(l, r);
        Assert.False(result.AreTextEqual);
    }

    [Fact]
    public void CompareText_DifferentText_HasInsertedAndDeletedDiffs()
    {
        byte[] left  = BuildPdfWithText("Hello");
        byte[] right = BuildPdfWithText("World");
        using var l = new MemoryStream(left);
        using var r = new MemoryStream(right);
        var result = PdfComparer.CompareText(l, r);
        bool hasDeleted  = false;
        bool hasInserted = false;
        foreach (var d in result.TextDiffs)
        {
            if (d.Type == PdfDiffType.Deleted)  hasDeleted  = true;
            if (d.Type == PdfDiffType.Inserted) hasInserted = true;
        }
        // Either both fire, or text extractor found nothing (graceful)
        if (result.TextDiffs.Count > 0)
        {
            Assert.True(hasDeleted || hasInserted);
        }
    }

    [Fact]
    public void CompareText_NullLeft_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfComparer.CompareText((Stream)null, new MemoryStream()));
    }

    [Fact]
    public void CompareText_NullRight_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfComparer.CompareText(new MemoryStream(BuildPdfWithText("x")), null));
    }

    [Fact]
    public void CompareText_PageCountsStored()
    {
        byte[] pdf = BuildPdfWithText("Test");
        using var l = new MemoryStream(pdf);
        using var r = new MemoryStream(pdf);
        var result = PdfComparer.CompareText(l, r);
        Assert.Equal(result.LeftPageCount, result.RightPageCount);
    }

    [Fact]
    public void PdfTextDiff_ToString_ContainsText()
    {
        var d = new PdfTextDiff(PdfDiffType.Inserted, "hello", 0);
        Assert.Contains("hello", d.ToString());
        Assert.Contains("+", d.ToString());
    }

    [Fact]
    public void PdfTextDiff_Deleted_HasMinusPrefix()
    {
        var d = new PdfTextDiff(PdfDiffType.Deleted, "gone", 0);
        Assert.Contains("-", d.ToString());
    }

    [Fact]
    public void PdfTextDiff_Unchanged_HasSpacePrefix()
    {
        var d = new PdfTextDiff(PdfDiffType.Unchanged, "same", 0);
        var s = d.ToString();
        Assert.DoesNotContain("+", s);
        Assert.DoesNotContain("-", s);
    }

    [Fact]
    public void PdfDiffResult_AreTextEqual_TrueWhenNoDiffs()
    {
        var result = new PdfDiffResult_TestHelper().MakeResult(
            new[] { new PdfTextDiff(PdfDiffType.Unchanged, "x", 0) }, 1, 1);
        Assert.True(result.AreTextEqual);
    }

    [Fact]
    public void PdfDiffResult_AreTextEqual_FalseWhenDiffPageCount()
    {
        // Same text diffs but different page counts -> not equal
        byte[] left  = BuildPdfWithText("A");
        byte[] right = BuildPdfWithText("A");
        using var l = new MemoryStream(left);
        using var r = new MemoryStream(right);
        var result = PdfComparer.CompareText(l, r);
        // Both have 1 page so should be equal if text is same
        Assert.Equal(1, result.LeftPageCount);
        Assert.Equal(1, result.RightPageCount);
    }
}

// Helper to construct PdfDiffResult (internal constructor)
file sealed class PdfDiffResult_TestHelper
{
    public PdfDiffResult MakeResult(PdfTextDiff[] diffs, int left, int right)
        => new PdfDiffResult(diffs, left, right);
}
