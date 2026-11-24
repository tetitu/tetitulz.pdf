// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A <c>/Pages</c> node whose <c>/Kids</c> is an indirect reference is still a <c>/Pages</c> node.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>PdfDictionary.GetArray</c> does not follow a reference, and the page
/// tree's leaf test reads "no <c>/Kids</c>" as evidence of a page. So a <c>/Pages</c> node written
/// <c>/Kids 7 0 R</c> that also carries its own <c>/MediaBox</c> — both legal, both common, since
/// <c>/MediaBox</c> is one of the four inheritable attributes producers put on the root — was
/// classified as a PAGE. The tree root became page 0 and the real pages were never reached.</para>
///
/// <para><b>The comment that stood there asserted the opposite</b>, and that is the reusable part:
/// it said resolving <c>/Kids</c> below the leaf test "can never reclassify a node the structural
/// test already accepted as a page… widens the traversal and cannot narrow it". True only if the
/// test does not itself depend on <c>/Kids</c> being absent — and it does. Prose asserting a safety
/// property is not the property.</para>
/// </remarks>
public class PageTreeIndirectKidsTests
{
    /// <summary>
    /// Asserts the page COUNT and the page's own content, not merely that opening succeeded: the
    /// defect returns a perfectly valid one-page document whose single page is the tree root.
    /// </summary>
    [Fact]
    public void PagesNodeWithIndirectKidsAndOwnMediaBox_IsNotItselfAPage()
    {
        using var doc = PdfDocument.Open(new MemoryStream(IndirectKidsPdf()));

        Assert.Equal(2, doc.Pages.Count);

        // The root carries /MediaBox but no /Contents; each real page carries /Contents. If the
        // root had been collected, page 0 would have none.
        for (int i = 0; i < doc.Pages.Count; i++)
            Assert.NotNull(doc.Pages[i].Dictionary[Core.Objects.PdfName.Contents]);
    }

    /// <summary>
    /// The floor: the same tree with a DIRECT <c>/Kids</c> array must be unchanged, so the rule
    /// cannot be "stop treating anything as a page".
    /// </summary>
    [Fact]
    public void PagesNodeWithDirectKids_IsUnchanged()
    {
        using var doc = PdfDocument.Open(new MemoryStream(DirectKidsPdf()));

        Assert.Equal(2, doc.Pages.Count);
        for (int i = 0; i < doc.Pages.Count; i++)
            Assert.NotNull(doc.Pages[i].Dictionary[Core.Objects.PdfName.Contents]);
    }

    /// <summary>
    /// An emptied <c>/Pages</c> branch that kept a hoisted <c>/MediaBox</c> is not a page.
    /// </summary>
    /// <remarks>
    /// <c>&lt;&lt; /Type /Pages /Count 0 /MediaBox […] &gt;&gt;</c> is exactly what a page-removal
    /// tool leaves behind when it hoists the box: no <c>/Kids</c>, an own <c>/MediaBox</c>. The
    /// structural clause exists for files that <b>omit</b> <c>/Type</c>, so a node that positively
    /// declares <c>/Type /Pages</c> must be excluded from it — otherwise the empty branch is
    /// collected as a phantom page and <b>every page index after it is off by one</b>, silently.
    /// </remarks>
    [Fact]
    public void EmptiedPagesBranchWithAHoistedMediaBox_IsNotCountedAsAPage()
    {
        using var doc = PdfDocument.Open(new MemoryStream(EmptiedBranchPdf()));

        Assert.Equal(2, doc.Pages.Count);
        for (int i = 0; i < doc.Pages.Count; i++)
            Assert.NotNull(doc.Pages[i].Dictionary[Core.Objects.PdfName.Contents]);
    }

    // ------------------------------------------------------------------ fixtures

    private static byte[] EmptiedBranchPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 7 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
        Stream("q Q\n"),
        Stream("q Q\n"),
        // The emptied branch, between the two real pages so a phantom would also shift the index.
        "<< /Type /Pages /Parent 2 0 R /Count 0 /MediaBox [0 0 612 792] >>");

    private static byte[] IndirectKidsPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids 7 0 R /Count 2 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
        Stream("q Q\n"),
        Stream("q Q\n"),
        "[3 0 R 4 0 R]");

    private static byte[] DirectKidsPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
        Stream("q Q\n"),
        Stream("q Q\n"));

    private static string Stream(string data) =>
        $"<< /Length {data.Length} >>\nstream\n{data}endstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
