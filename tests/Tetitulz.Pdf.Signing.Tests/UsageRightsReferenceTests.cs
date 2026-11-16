// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
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
/// The <c>/UR3</c> signature reference names every usage right the caller granted — ISO 32000-2
/// Table 258, where <c>/Form</c> is <b>one</b> array of names.
/// </summary>
/// <remarks>
/// <para>Each form right used to write a <c>/Form</c> array of its own, so a caller granting fill-in and
/// import/export got a <c>/TransformParams</c> dictionary with the key twice. §7.3.7 leaves which one a
/// reader keeps undefined, and whichever it keeps, the other grant is gone.
/// <c>SignatureDictionaryKeyUniquenessTests</c> reads the raw bytes for the duplicate; this reads the
/// parsed document for what a reader is left with.</para>
/// </remarks>
public sealed class UsageRightsReferenceTests
{
    [Theory]
    [InlineData(UsageRights.FormFillIn, new[] { "FillIn" })]
    [InlineData(UsageRights.FormFillIn | UsageRights.FormImportExport, new[] { "FillIn", "Import", "Export" })]
    [InlineData(UsageRights.FormImportExport | UsageRights.FormSave, new[] { "Import", "Export", "SubmitStandalone" })]
    [InlineData(UsageRights.FormFillIn | UsageRights.FormImportExport | UsageRights.FormSave,
                new[] { "FillIn", "Import", "Export", "SubmitStandalone" })]
    public void TheFormArrayNamesEveryGrantedFormRight(UsageRights rights, string[] expected)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Sign(rights)));

        var form = doc.ResolveObject(TransformParams(doc)[PdfName.Form]) as PdfArray;

        Assert.NotNull(form);
        Assert.Equal(expected, form!.Select(n => (doc.ResolveObject(n) as PdfName)?.Value));
    }

    /// <summary>The control: with no form right granted, no <c>/Form</c> array is written at all.</summary>
    [Fact]
    public void NoFormArrayIsWrittenWhenNoFormRightIsGranted()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Sign(UsageRights.Annotation)));

        var transformParams = TransformParams(doc);

        Assert.Null(transformParams[PdfName.Form]);
        Assert.IsType<PdfArray>(doc.ResolveObject(transformParams[PdfName.Annots]));
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Sign(UsageRights rights)
    {
        var ca = SigningTestCrypto.Ca("CN=Usage Rights Root");
        var leaf = SigningTestCrypto.Leaf(ca, "CN=Usage Rights Signer");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            UsageRights = rights,
        });
        return output.ToArray();
    }

    /// <summary>
    /// The <c>/TransformParams</c> of the usage-rights signature's <c>/UR3</c> reference, reached
    /// the conforming way (ISO 32000-2 §12.8.1): <c>/Perms /UR3</c>, not a signature field — there
    /// is no field at all for a usage-rights-only signature (GAPS.md §1, closed).
    /// </summary>
    private static PdfDictionary TransformParams(PdfDocument doc)
    {
        var perms = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Perms]);
        var signature = (PdfDictionary)doc.ResolveObject(perms[PdfName.UR3]);
        var references = (PdfArray)doc.ResolveObject(signature[PdfName.Reference]);

        var ur3 = new List<PdfDictionary>();
        foreach (var item in references)
        {
            if (doc.ResolveObject(item) is PdfDictionary sigRef
                && (doc.ResolveObject(sigRef[PdfName.TransformMethod]) as PdfName)?.Value == "UR3")
                ur3.Add(sigRef);
        }

        return (PdfDictionary)doc.ResolveObject(Assert.Single(ur3)[PdfName.TransformParams]);
    }
}
