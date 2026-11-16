// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Both signers find a page through the document's page tree — the one walk every reader in the
/// library uses — rather than by indexing the root node's <c>/Kids</c>.
/// </summary>
/// <remarks>
/// <para><b>ISO 32000-2 §7.7.3.2: a page tree's <c>/Kids</c> holds its immediate children, which may
/// be pages or further page tree nodes.</b> <c>VisualSignatureBuilder</c> took page <i>N</i> to be
/// the root's <c>/Kids[N-1]</c>, and <c>PdfSigner</c> took the first page to be <c>/Kids[0]</c>. In a
/// balanced tree — what most producers write past a few dozen pages — both name an intermediate
/// <c>/Pages</c> node. The visible stamp's widget was then added to that node's <c>/Annots</c>,
/// where no reader looks for annotations, so the stamp never appeared on any page; its <c>/P</c>
/// named the node too. A page past the root's own child count was refused as "out of range" in a
/// document that plainly had it. The invisible signature's widget named the node as its page.</para>
///
/// <para><b>Asserted on which object the widget reached and which object it names</b>, read back
/// through <see cref="PdfDocument.Pages"/>. The signature verifies either way.</para>
/// </remarks>
public sealed class SignerPageTreeTests
{
    private static readonly Lazy<(byte[] Pfx, string Password)> Keys = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Page Tree Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Page Tree Signer");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }));

    /// <summary>The intermediate <c>/Pages</c> node both pages hang from.</summary>
    private const int IntermediateNode = 5;

    /// <summary>
    /// A visible signature lands on the page it names, including a page past the root node's own
    /// child count — and on no other page, and not on the node between them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AVisibleSignatureLandsOnTheNamedPage_InANestedPageTree(int pageNumber)
    {
        byte[] signed = SignVisibly(NestedPdf(), pageNumber);

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        Assert.Equal(2, doc.Pages.Count);
        var page = doc.Pages[pageNumber - 1].Dictionary;
        var otherPage = doc.Pages[2 - pageNumber].Dictionary;

        var widget = SignatureWidgetOn(doc, page);
        Assert.NotNull(widget);
        Assert.IsType<PdfDictionary>(doc.ResolveObject(widget![PdfName.V]));
        Assert.Equal(page.ObjectNumber, (doc.ResolveObject(widget[PdfName.P]) as PdfDictionary)?.ObjectNumber);

        Assert.Null(SignatureWidgetOn(doc, otherPage));
        Assert.Null((doc.GetObject(IntermediateNode) as PdfDictionary)![PdfName.Annots]);
    }

    /// <summary>
    /// The control: a page the document does not have is still refused, and for that reason.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void APageTheNestedTreeDoesNotHave_IsRefusedAsOutOfRange(int pageNumber)
    {
        var ex = Assert.Throws<PdfException>(() => SignVisibly(NestedPdf(), pageNumber));

        Assert.Contains($"Page number {pageNumber} is out of range", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page written inline in <c>/Kids</c> has no object number for the revision to supersede, so it
    /// is refused rather than re-declared as object 0 — the head of the free list.
    /// </summary>
    [Fact]
    public void AnInlinePageDictionary_IsRefusedRatherThanSupersededAsObjectZero()
    {
        byte[] pdf = SignedPdfFixtures.Assemble(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>] /Count 1 >>");

        var ex = Assert.Throws<PdfException>(() => SignVisibly(pdf, 1));

        Assert.Contains("Inline page dictionaries are not supported", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The invisible signature's widget names the first page, not the node above it.</summary>
    [Fact]
    public void AnInvisibleSignaturesWidgetNamesTheFirstPage_InANestedPageTree()
    {
        byte[] signed = SignInvisibly(NestedPdf(), new MemoryStream());

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var acroForm = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var fields = doc.ResolveObject(acroForm![PdfName.Fields]) as PdfArray;
        var field = Assert.Single(fields!);
        var widget = (PdfDictionary)doc.ResolveObject(field);

        Assert.Equal(doc.Pages[0].Dictionary.ObjectNumber,
            (doc.ResolveObject(widget[PdfName.P]) as PdfDictionary)?.ObjectNumber);
    }

    /// <summary>
    /// A document whose <c>/Root</c> names no catalog is refused before the output is touched.
    /// </summary>
    /// <remarks>
    /// Reading the document through <see cref="PdfDocument"/> is what moved the refusal: the signer
    /// used to copy the input to the output and write the signature and field objects first, and
    /// only then find it had no catalog to supersede — leaving a caller's output file half-written.
    /// </remarks>
    [Fact]
    public void ADocumentWithoutACatalog_IsRefusedBeforeAnyByteIsWritten()
    {
        using var output = new MemoryStream();

        Assert.ThrowsAny<PdfException>(() => SignInvisibly(NoCatalogPdf(), output));

        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The visible signer says the document has no catalog, rather than blaming the page number the
    /// caller passed.
    /// </summary>
    [Fact]
    public void AVisibleSignatureOnADocumentWithoutACatalog_IsRefusedForThatReason()
    {
        var ex = Assert.ThrowsAny<PdfException>(() => SignVisibly(NoCatalogPdf(), 1));

        Assert.Contains("catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("out of range", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Two pages under one intermediate node: the root's <c>/Kids</c> is <c>[5 0 R]</c>, and object
    /// 5's is <c>[3 0 R 4 0 R]</c>.
    /// </summary>
    private static byte[] NestedPdf() => SignedPdfFixtures.Assemble(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 5 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>",
        "<< /Type /Page /Parent 5 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>",
        "<< /Type /Pages /Parent 2 0 R /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Length 0 >>\nstream\n\nendstream");

    /// <summary>A trailer whose <c>/Root</c> resolves to <c>null</c>.</summary>
    private static byte[] NoCatalogPdf() => SignedPdfFixtures.Assemble(
        "null",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

    /// <summary>The first annotation on <paramref name="page"/> that is a signature widget, or null.</summary>
    private static PdfDictionary? SignatureWidgetOn(PdfDocument doc, PdfDictionary page)
    {
        if (doc.ResolveObject(page[PdfName.Annots]) is not PdfArray annots) return null;
        for (int i = 0; i < annots.Count; i++)
        {
            if (doc.ResolveObject(annots[i]) is not PdfDictionary a) continue;
            if ((doc.ResolveObject(a[PdfName.Subtype]) as PdfName)?.Value == "Widget"
                && (doc.ResolveObject(a[PdfName.FT]) as PdfName)?.Value == "Sig")
                return a;
        }
        return null;
    }

    private static byte[] SignVisibly(byte[] pdf, int pageNumber)
    {
        string pfxPath = Path.Combine(Path.GetTempPath(), $"pagetree-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(pfxPath, Keys.Value.Pfx);
        try
        {
            using var input = new MemoryStream(pdf);
            using var output = new MemoryStream();
            VisualSignatureBuilder.Sign(input, output, pfxPath, Keys.Value.Password, Jpeg(),
                pageNumber: pageNumber, x: 50, y: 50, width: 120, height: 60);
            return output.ToArray();
        }
        finally
        {
            try { File.Delete(pfxPath); } catch (IOException) { /* best effort */ }
        }
    }

    private static byte[] SignInvisibly(byte[] pdf, MemoryStream output)
    {
        using var input = new MemoryStream(pdf);
        using var cert = new MemoryStream(Keys.Value.Pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = Keys.Value.Password,
        });
        return output.ToArray();
    }

    /// <summary>
    /// A JPEG carrying only what the embedder reads: SOI, a baseline SOF0 frame header for a 16x16
    /// RGB image, and EOI. Nothing in this path decodes the image.
    /// </summary>
    private static byte[] Jpeg()
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0, 16, 0, 16, 3 };
        for (int i = 0; i < 3; i++) { b.Add((byte)(i + 1)); b.Add(0x11); b.Add(0); }
        b.Add(0xFF); b.Add(0xD9);
        return b.ToArray();
    }
}
