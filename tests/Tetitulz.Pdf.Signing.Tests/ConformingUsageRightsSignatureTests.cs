// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// ISO 32000-2 §12.8.1's conforming usage-rights shape (GAPS.md §1, closed): the signature
/// dictionary is in no field, referenced only from an indirect <c>/Perms /UR3</c>, and refused
/// when the document would end up carrying any other signature.
/// </summary>
/// <remarks>
/// <see cref="UsageRightsReferenceTests"/> already covers the granted-rights content
/// (<c>/TransformParams</c>'s <c>/Form</c> array); this file covers the shape around it — where
/// the signature dictionary lives, what refuses, and that an unsigned document's ordinary case
/// still succeeds.
/// </remarks>
public sealed class ConformingUsageRightsSignatureTests
{
    [Fact]
    public void AUsageRightsSignature_AddsNoAcroFormField()
    {
        byte[] signed = SignUsageRights(UsageRights.FormFillIn);
        using var doc = PdfDocument.Open(new MemoryStream(signed));

        // MinimalPdf starts with no /AcroForm at all; a usage-rights signature must not add one,
        // since it is not in a field for that form to name (§12.8.1: "not from a signature field").
        Assert.Null(doc.Catalog.Dictionary[PdfName.AcroForm]);
    }

    /// <summary>
    /// The revision itself must not write a signature-field widget at all — not merely leave it
    /// unreferenced. <see cref="AUsageRightsSignature_AddsNoAcroFormField"/> alone cannot tell an
    /// omitted widget from an orphaned one <c>BuildUsageRightsSignedCatalog</c> simply never
    /// pointed the (still nonexistent) <c>/AcroForm</c> at, since neither leaves a trace in the
    /// catalog; this reads the appended revision's raw bytes for the one string a signature widget
    /// always carries.
    /// </summary>
    [Fact]
    public void AUsageRightsSignature_WritesNoWidgetAnnotationAtAll()
    {
        byte[] original = SignedPdfFixtures.MinimalPdf();
        byte[] signed = SignUsageRights(UsageRights.FormFillIn);

        string appended = System.Text.Encoding.Latin1.GetString(signed, original.Length,
            signed.Length - original.Length);

        Assert.DoesNotContain("/FT /Sig", appended);
        Assert.DoesNotContain("/Subtype /Widget", appended);
    }

    [Fact]
    public void AUsageRightsSignature_IsReferencedOnlyFromPermsUR3()
    {
        byte[] signed = SignUsageRights(UsageRights.Annotation);
        using var doc = PdfDocument.Open(new MemoryStream(signed));

        var perms = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Perms]));
        var signature = Assert.IsType<PdfDictionary>(doc.ResolveObject(perms[PdfName.UR3]));

        Assert.Equal("Sig", (doc.ResolveObject(signature[PdfName.Type]) as PdfName)?.Value);
        Assert.NotNull(doc.ResolveObject(signature[PdfName.Contents]));
        Assert.NotNull(doc.ResolveObject(signature[PdfName.ByteRange]));
    }

    /// <summary>
    /// The acceptance control every refusal test below needs: the ordinary case still works. Not
    /// merely "did not throw" (an unfalsifiable assertion that would also pass with signing
    /// deleted, per CLAUDE.md's banned-pattern rule) — asserting the granted rights actually
    /// reached the signature dictionary is what makes a genuine break in this path visible here.
    /// </summary>
    [Fact]
    public void AUsageRightsSignature_OnAnUnsignedDocument_GrantsExactlyTheRequestedRights()
    {
        byte[] signed = SignUsageRights(UsageRights.FormFillIn | UsageRights.Annotation);
        using var doc = PdfDocument.Open(new MemoryStream(signed));

        var perms = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Perms]));
        var signature = Assert.IsType<PdfDictionary>(doc.ResolveObject(perms[PdfName.UR3]));
        var references = Assert.IsType<PdfArray>(doc.ResolveObject(signature[PdfName.Reference]));

        PdfDictionary transformParams = null!;
        foreach (var item in references)
        {
            if (doc.ResolveObject(item) is PdfDictionary sigRef
                && (doc.ResolveObject(sigRef[PdfName.TransformMethod]) as PdfName)?.Value == "UR3")
                transformParams = Assert.IsType<PdfDictionary>(doc.ResolveObject(sigRef[PdfName.TransformParams]));
        }

        Assert.NotNull(transformParams);
        var form = Assert.IsType<PdfArray>(doc.ResolveObject(transformParams[PdfName.Form]));
        Assert.Equal(new[] { "FillIn" }, form.Select(n => (doc.ResolveObject(n) as PdfName)?.Value));
        Assert.NotNull(doc.ResolveObject(transformParams[PdfName.Annots]));
    }

    [Fact]
    public void UsageRightsCannotBeCombinedWithDocMdpPermission()
    {
        var ex = Assert.Throws<PdfSigningException>(() => SignUsageRights(
            UsageRights.FormFillIn, docMdp: DocMdpPermission.NoChanges));

        Assert.Contains("UsageRights", ex.Message);
        Assert.Contains("DocMdpPermission", ex.Message);
    }

    [Fact]
    public void UsageRightsRefusesADocumentThatAlreadyCarriesAnApprovalSignature()
    {
        var ca = SigningTestCrypto.Ca("CN=UR Conflict Root");
        var leaf = SigningTestCrypto.Leaf(ca, "CN=UR Conflict Signer");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        // An ordinary approval signature first (no DocMdp, no UsageRights) — the document now
        // carries "another type of signature", which §12.8.1 forbids a usage-rights signature
        // from joining.
        using var approvalOut = new MemoryStream();
        new PdfSigner().Sign(
            new MemoryStream(SignedPdfFixtures.MinimalPdf()), approvalOut,
            new SignatureParameters { CertificateStream = new MemoryStream(pfx), CertificatePassword = pw });

        var ex = Assert.Throws<PdfSigningException>(() =>
        {
            using var output = new MemoryStream();
            new PdfSigner().Sign(new MemoryStream(approvalOut.ToArray()), output, new SignatureParameters
            {
                CertificateStream = new MemoryStream(pfx),
                CertificatePassword = pw,
                UsageRights = UsageRights.FormFillIn,
            });
        });

        Assert.Contains("already carries a signature", ex.Message);
    }

    [Fact]
    public void UsageRightsRefusesADocumentThatAlreadyCarriesUsageRights()
    {
        byte[] firstUr = SignUsageRights(UsageRights.FormFillIn);

        var ca = SigningTestCrypto.Ca("CN=UR Repeat Root");
        var leaf = SigningTestCrypto.Leaf(ca, "CN=UR Repeat Signer");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        var ex = Assert.Throws<PdfSigningException>(() =>
        {
            using var output = new MemoryStream();
            new PdfSigner().Sign(new MemoryStream(firstUr), output, new SignatureParameters
            {
                CertificateStream = new MemoryStream(pfx),
                CertificatePassword = pw,
                UsageRights = UsageRights.Annotation,
            });
        });

        Assert.Contains("already carries a usage-rights signature", ex.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] SignUsageRights(UsageRights rights, DocMdpPermission docMdp = DocMdpPermission.None)
    {
        var ca = SigningTestCrypto.Ca("CN=UR Shape Root");
        var leaf = SigningTestCrypto.Leaf(ca, "CN=UR Shape Signer");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            UsageRights = rights,
            DocMdpPermission = docMdp,
        });
        return output.ToArray();
    }
}
