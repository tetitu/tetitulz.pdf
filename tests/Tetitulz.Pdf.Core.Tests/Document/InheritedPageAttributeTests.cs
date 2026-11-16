// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The §7.7.3.4 inheritable attributes — <c>/Resources</c>, <c>/MediaBox</c>, <c>/CropBox</c> and
/// <c>/Rotate</c> — declared on a <c>/Pages</c> node as INDIRECT references.
///
/// <para><see cref="PdfDictionary"/>'s indexer and its typed accessors never follow a
/// <see cref="PdfReference"/>, so <c>node.GetInteger(PdfName.Rotate)</c> yielded nothing whenever a
/// producer wrote <c>/Rotate 4 0 R</c> — indistinguishable from "the key is absent". The two boxes
/// beside it in <c>PdfPageTree.CollectPages</c> each resolved the indirect case explicitly and
/// <c>/Rotate</c> did not, which is what shows producers really do write these values that way: a
/// batch scanner shares one rotation object across the whole tree, and every page in the file
/// rendered, printed and extracted sideways with nothing reported.</para>
///
/// <para>This is the page-TREE half. The sibling defect on a page's own dictionary
/// (<c>PdfPage.Rotate</c> / <c>PdfPage.UserUnit</c>) is covered by
/// <see cref="IndirectPageAttributeTests"/>. <c>/UserUnit</c> has no half here on purpose —
/// §7.7.3.4 does not list it among the inheritable attributes.</para>
/// </summary>
public class InheritedPageAttributeTests
{
    // ================================================================ /Rotate

    /// <summary>
    /// The named defect: the value lives on the <c>/Pages</c> node, written as a reference, and
    /// the page declares no rotation of its own.
    /// </summary>
    [Fact]
    public void Rotate_InheritedAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: "/Rotate 4 0 R",
            pageDict: "/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")));

        Assert.Equal(90, doc.Pages[0].Rotate);
    }

    /// <summary>
    /// The honest negative. Without it, inheriting a hard-coded 90 — or resolving the wrong
    /// object — would satisfy the test above.
    /// </summary>
    [Fact]
    public void Rotate_DeclaredNowhereInTheTree_StaysNull()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: string.Empty,
            pageDict: "/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")));

        Assert.Null(doc.Pages[0].Rotate);
    }

    /// <summary>The direct spelling keeps working — the indirect path is a fallback, not a swap.</summary>
    [Fact]
    public void Rotate_InheritedDirectly_IsStillRead()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: "/Rotate 180",
            pageDict: "/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")));

        Assert.Equal(180, doc.Pages[0].Rotate);
    }

    /// <summary>
    /// Propagation order, pinned at DEPTH: two <c>/Pages</c> levels each declare an indirect
    /// <c>/Rotate</c> and the one nearest the page wins.
    ///
    /// <para>The page declares none of its own, which is the whole point. Writing this as
    /// "the page's <c>/Rotate</c> beats the inherited one" instead produced a test that survived
    /// every mutation: the page's own value is ALSO propagated into the inherited slot on its way
    /// through <c>CollectPages</c>, and <c>PdfPage.Rotate</c> resolves the page's own dictionary
    /// besides — so 270 came back under both orderings and with the tree's <c>/Rotate</c> handling
    /// deleted outright. Here nothing but the tree can supply the answer.</para>
    /// </summary>
    [Fact]
    public void Rotate_OnTheNearestPagesNode_OverridesAShallowerInheritedValue()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: "/Rotate 4 0 R",                                   // root: 90
            pageDict: "/Type /Page /Parent 9 0 R /MediaBox [0 0 612 792]",  // page: none
            midExtras: "/Rotate 8 0 R")));                                  // nearer the page: 270

        Assert.Equal(270, doc.Pages[0].Rotate);
    }

    // ================================================ the other three inheritable attributes

    /// <summary>
    /// <c>/MediaBox</c> already resolved the indirect case; this pins it so the audit that added
    /// <c>/Rotate</c> cannot be undone piecemeal.
    /// </summary>
    [Fact]
    public void MediaBox_InheritedAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: "/MediaBox 5 0 R",
            pageDict: "/Type /Page /Parent 2 0 R")));

        var mb = doc.Pages[0].MediaBox;
        Assert.NotNull(mb);
        Assert.Equal(200d, ((PdfNumber)mb[2]).DoubleValue, 6);
        Assert.Equal(400d, ((PdfNumber)mb[3]).DoubleValue, 6);
    }

    [Fact]
    public void CropBox_InheritedAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: "/CropBox 5 0 R",
            pageDict: "/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")));

        var cb = doc.Pages[0].CropBox;
        Assert.NotNull(cb);
        Assert.Equal(200d, ((PdfNumber)cb[2]).DoubleValue, 6);
        Assert.Equal(400d, ((PdfNumber)cb[3]).DoubleValue, 6);
    }

    /// <summary>
    /// Object 6 is the only dictionary in the file carrying a <c>/ProcSet</c>, and its first entry
    /// is read by value — so this pins WHICH object the reference was followed to, rather than
    /// merely that something non-null came back.
    /// </summary>
    [Fact]
    public void Resources_InheritedAsAnIndirectReference_IsResolved()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: "/Resources 6 0 R",
            pageDict: "/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")));

        var res = doc.Pages[0].Resources;
        Assert.NotNull(res);
        var procSet = res.GetArray(new PdfName("ProcSet"));
        Assert.NotNull(procSet);
        Assert.Equal("PDF", (procSet[0] as PdfName)?.Value);
        Assert.Equal("Text", (procSet[1] as PdfName)?.Value);
    }

    // ================================================================ traversal and leaf detection

    /// <summary>
    /// <c>/Kids</c> is not inheritable, but it was read the same non-resolving way: a
    /// <c>/Kids 7 0 R</c> node yielded an empty array, so the ENTIRE subtree contributed zero
    /// pages and the document opened as if it had none.
    /// </summary>
    [Fact]
    public void Kids_WrittenAsAnIndirectReference_StillYieldsItsPages()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: string.Empty,
            pageDict: "/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]",
            kids: "7 0 R")));

        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// The structural leaf test — a node with no <c>/Kids</c> but with <c>/Contents</c> or
    /// <c>/MediaBox</c> is a page — read <c>/MediaBox</c> without resolving it too, so a producer
    /// that omitted <c>/Type</c> AND shared one box object lost the page entirely.
    /// </summary>
    [Fact]
    public void UntypedLeafWhoseMediaBoxIsIndirect_IsStillRecognisedAsAPage()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: string.Empty,
            pageDict: "/Parent 2 0 R /MediaBox 5 0 R")));   // no /Type, no /Contents

        Assert.Single(doc.Pages);
        Assert.Equal(200d, ((PdfNumber)doc.Pages[0].MediaBox[2]).DoubleValue, 6);
    }

    /// <summary>
    /// The counterpart that keeps the leaf test honest: the structural signature is the node's OWN
    /// <c>/MediaBox</c>, never an inherited one. Testing the propagated value instead would make
    /// every childless node under a <c>/Pages</c> node that declares a box into a page.
    /// </summary>
    [Fact]
    public void ChildlessNodeInheritingAMediaBox_IsNotCountedAsAPage()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildTree(
            pagesExtras: "/MediaBox [0 0 612 792]",
            pageDict: "/Parent 2 0 R")));   // no /Type, no /Contents, no /MediaBox of its own

        Assert.Empty(doc.Pages);
    }

    // ================================================================ fixture

    /// <summary>
    /// A one-page file. Object 2 is the <c>/Pages</c> node and carries <paramref name="pagesExtras"/>;
    /// object 3 is its kid, written verbatim from <paramref name="pageDict"/> so a test can omit
    /// <c>/Type</c>. The shared values every test draws from:
    /// <list type="bullet">
    /// <item><description>4 → <c>90</c>, 8 → <c>270</c> (rotations)</description></item>
    /// <item><description>5 → <c>[0 0 200 400]</c> (a box)</description></item>
    /// <item><description>6 → a resource dictionary, the file's only <c>/ProcSet</c> holder</description></item>
    /// <item><description>7 → <c>[3 0 R]</c>, for the indirect <c>/Kids</c> case</description></item>
    /// </list>
    /// <para>Passing <paramref name="midExtras"/> splices object 9 in as a second <c>/Pages</c>
    /// level between the root and the page, so an attribute can be declared at two depths.</para>
    /// </summary>
    private static byte[] BuildTree(string pagesExtras, string pageDict,
        string kids = "[3 0 R]", string? midExtras = null)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        const int LastObject = 9;
        var offsets = new long[LastObject + 1];
        string rootKids = midExtras == null ? kids : "[9 0 R]";

        Write("%PDF-1.6\n");
        offsets[1] = file.Position; Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids " + rootKids + " /Count 1 " + pagesExtras + " >>\nendobj\n");
        offsets[3] = file.Position; Write("3 0 obj\n<< " + pageDict + " >>\nendobj\n");
        offsets[4] = file.Position; Write("4 0 obj\n90\nendobj\n");
        offsets[5] = file.Position; Write("5 0 obj\n[0 0 200 400]\nendobj\n");
        offsets[6] = file.Position; Write("6 0 obj\n<< /ProcSet [/PDF /Text] >>\nendobj\n");
        offsets[7] = file.Position; Write("7 0 obj\n[3 0 R]\nendobj\n");
        offsets[8] = file.Position; Write("8 0 obj\n270\nendobj\n");
        offsets[9] = file.Position;
        Write("9 0 obj\n<< /Type /Pages /Parent 2 0 R /Kids [3 0 R] /Count 1 "
              + (midExtras ?? string.Empty) + " >>\nendobj\n");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 ").Append(LastObject + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= LastObject; i++)
            xs.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size ").Append(LastObject + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }
}
