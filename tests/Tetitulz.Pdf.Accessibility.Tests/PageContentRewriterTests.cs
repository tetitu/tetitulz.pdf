// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The page-content rewrite and its /StructParents and XObject-repointing helpers, called directly on a
/// small document so each rule is pinned by the object it writes or the number it reports.
/// </summary>
public class PageContentRewriterTests
{
    private const string PageContent = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET";

    [Fact]
    public void RewritePage_WithoutOptions_UsesTheDefaultsAndRewritesThePage()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Document()));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);
        var page = (PdfDictionary)doc.GetObject(3);

        PageRewriteResult result = PageContentRewriter.RewritePage(
            doc, index, page, null, -1, new Dictionary<int, PdfObject>(), new Dictionary<int, PdfObject>());

        Assert.True(result.Changed, "the page should be tagged with default options; skip reason: " + result.SkipReason);
        Assert.Equal(3, result.PageObjectNumber);
        Assert.True(result.NewContentObjectNumber > 0);
    }

    [Fact]
    public void RewritePage_PageWithNoObjectNumber_ReportsMinusOneAndChangesNothing()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Document()));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);
        var modified = new Dictionary<int, PdfObject>();
        var added = new Dictionary<int, PdfObject>();

        PageRewriteResult result = PageContentRewriter.RewritePage(
            doc, index, new PdfDictionary(), null, -1, modified, added);

        Assert.False(result.Changed);
        Assert.Equal(-1, result.PageObjectNumber);
        Assert.NotNull(result.SkipReason);
        Assert.Empty(modified);
        Assert.Empty(added);
    }

    [Fact]
    public void SetStructParents_NonNegativeKey_StampsThePageWithIt()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Document()));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);
        var modified = new Dictionary<int, PdfObject>();

        PageContentRewriter.SetStructParents(doc, index, (PdfDictionary)doc.GetObject(3), 0, modified);

        var page = Assert.IsType<PdfDictionary>(modified[3]);
        Assert.Equal(0, ((PdfNumber)page[PdfName.StructParents]).IntValue);
    }

    [Fact]
    public void SetStructParents_NegativeKey_LeavesThePageAlone()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Document()));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);
        var modified = new Dictionary<int, PdfObject>();

        PageContentRewriter.SetStructParents(doc, index, (PdfDictionary)doc.GetObject(3), -1, modified);

        Assert.Empty(modified);
    }

    [Fact]
    public void TryRepointXObjectNames_PointsTheName_AtGenerationZeroOfTheTarget()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Document()));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);
        var page = (PdfDictionary)doc.GetObject(3);
        var resources = doc.ResolveObject(page[PdfName.Resources]) as PdfDictionary;
        var modified = new Dictionary<int, PdfObject>();

        bool ok = PageContentRewriter.TryRepointXObjectNames(
            doc, index, page, resources, new[] { "Fm1" }, 99, modified, out string reason);

        Assert.True(ok, reason);
        var repointed = Assert.IsType<PdfDictionary>(modified[3]);
        var copy = doc.ResolveObject(repointed[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(copy[PdfName.XObject]) as PdfDictionary;
        var reference = Assert.IsType<PdfReference>(xobjects[new PdfName("Fm1")]);
        Assert.Equal(99, reference.ReferenceObjectNumber);
        Assert.Equal(0, reference.ReferenceGenerationNumber);
    }

    [Fact]
    public void EmitTaggedForm_UncompressedPayload_DoesNotInheritTheTemplatesFilter()
    {
        var template = new PdfStream();
        template.Set(PdfName.Filter, PdfName.FlateDecode);
        template.Set(new PdfName("BBox"), new PdfNumber(5));

        var payload = new PdfStream { RawData = Encoding.ASCII.GetBytes("q Q") };
        payload.Set(PdfName.Length, new PdfNumber(3));
        var tagged = new PageContentRewriter.FormRewriteResult { Changed = true, Payload = payload };

        PdfStream rewritten = PageContentRewriter.EmitTaggedForm(template, tagged, 50, 0, 2);

        // The payload is not Flate-encoded, so the template's /Filter describes bytes that do not exist.
        Assert.Null(rewritten[PdfName.Filter]);
        // Control: an entry the payload does not carry is taken from the template.
        Assert.Equal(5, ((PdfNumber)rewritten[new PdfName("BBox")]).IntValue);
        Assert.Equal(3, ((PdfNumber)rewritten[PdfName.Length]).IntValue);
        Assert.Equal(2, ((PdfNumber)rewritten[PdfName.StructParents]).IntValue);
        Assert.Equal(50, rewritten.ObjectNumber);
    }

    /// <summary>
    /// The compressed form is taken only when it is strictly smaller. This content tags to 67 bytes, and
    /// Flate of those 67 bytes is also 67: a tie stays uncompressed, with no /Filter to describe it.
    /// </summary>
    [Fact]
    public void TagFormContent_CompressedSizeEqualToRawSize_StaysUncompressed()
    {
        string content = "BT /F1 12 Tf 72 700 Td (" + new string('z', 10) + ") Tj ET";

        PageContentRewriter.FormRewriteResult result = PageContentRewriter.TagFormContent(
            new PdfStream(), Encoding.ASCII.GetBytes(content), new PageTagOptions(), compressRewrittenStream: true);

        Assert.True(result.Changed);
        Assert.Null(result.Payload[PdfName.Filter]);
        Assert.Equal(67, result.Payload.RawData.Length);
    }

    /// <summary>
    /// The control for the tie: a run that really compresses is Flate-encoded and carries its /Filter.
    /// </summary>
    [Fact]
    public void TagFormContent_CompressibleContent_IsFlateEncoded()
    {
        string content = "BT /F1 12 Tf 72 700 Td (" + new string('z', 400) + ") Tj ET";

        PageContentRewriter.FormRewriteResult result = PageContentRewriter.TagFormContent(
            new PdfStream(), Encoding.ASCII.GetBytes(content), new PageTagOptions(), compressRewrittenStream: true);

        Assert.True(result.Changed);
        var filter = Assert.IsType<PdfName>(result.Payload[PdfName.Filter]);
        Assert.Equal(PdfName.FlateDecode.Value, filter.Value);
    }

    /// <summary>
    /// A one-page document: the page is object 3, its content object 4, and a form XObject named Fm1 is
    /// object 5 in the page's resources.
    /// </summary>
    private static byte[] Document()
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm1 5 0 R >> >> >>",
            "<< /Length " + PageContent.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + PageContent + "\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n")
              .Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
