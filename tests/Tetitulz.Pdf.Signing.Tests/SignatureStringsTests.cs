// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>A signer's strings stay strings: a name or reason that tries to close its object adds nothing to the file.</summary>
public sealed class SignatureStringsTests
{
    [Theory]
    [InlineData("a) /OpenAction << /S /JavaScript /JS (alert) >> /X (")]
    [InlineData("a> /OpenAction << /S /JavaScript /JS (alert) >> /X <")]
    [InlineData("a\n>> /OpenAction << /S /JavaScript /JS (alert) >> <<")]
    [InlineData("a\r\nendobj\n99 0 obj\n<< /OpenAction 1 0 R >>\nendobj\n")]
    public void AdversarialSignerStrings_StayStrings(string hostile)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        using var output = new MemoryStream();
        new PdfSigner().Sign(new MemoryStream(SignedPdfFixtures.MinimalPdf()), output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = hostile,
            Reason = hostile,
            Location = hostile,
            ContactInfo = hostile,
        });

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output.ToArray()));

        Assert.Null(doc.Catalog.Dictionary[PdfName.OpenAction]);
        Assert.Null(doc.GetObject(99) as PdfDictionary);

        var result = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = new[] { ca.Cert } }).Single();
        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
        Assert.Equal(hostile, result.FieldName);
    }
}
