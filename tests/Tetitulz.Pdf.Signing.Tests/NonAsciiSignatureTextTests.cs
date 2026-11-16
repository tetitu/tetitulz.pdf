// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The signature's <c>/Reason</c>, <c>/Location</c> and a new field's <c>/T</c> are text strings
/// (§7.9.2.2): written through the raw writer's ASCII encoding, every other character came back a
/// question mark. ASCII text is still written as a literal (the control).
/// </summary>
public sealed class NonAsciiSignatureTextTests
{
    private static byte[] Sign(string reason, string location, string field)
    {
        var root = SigningTestCrypto.Ca("CN=Text Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Text Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            Reason = reason,
            Location = location,
            SignatureFieldName = field,
        });
        return output.ToArray();
    }

    private static SignatureVerificationResult Read(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc).Single();
    }

    [Fact]
    public void NonAsciiReasonLocationAndFieldName_ComeBackAsWritten()
    {
        var r = Read(Sign("אישור", "Zürich", "חתימה"));

        Assert.Equal("אישור", r.Reason);
        Assert.Equal("Zürich", r.Location);
        Assert.Equal("חתימה", r.FieldName);
        Assert.True(r.IsIntegrityValid);
    }

    [Fact]
    public void AsciiText_IsStillALiteral()
    {
        byte[] pdf = Sign("Approved (final)", "Tel Aviv", "Sig1");
        Assert.Contains("/Reason (Approved \\(final\\))", Encoding.Latin1.GetString(pdf), System.StringComparison.Ordinal);
        Assert.Equal("Approved (final)", Read(pdf).Reason);
    }
}
