// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// DocMDP (certification) enforcement is now wired into the signature-verification path:
/// <see cref="PdfSignatureVerifier.VerifyAll"/> surfaces the certification permission on the
/// certifying signature and, when a post-certification incremental update exceeds that level,
/// records a validation error so <see cref="SignatureVerificationResult.IsValid"/> becomes false.
/// Previously this was only available via the standalone <c>PdfOperations.VerifyMdp</c> API.
/// </summary>
public class DocMdpEnforcementTests
{
    private static byte[] Sign(byte[] pdf, string field, DocMdpPermission perm = DocMdpPermission.None,
                               bool allowCertificationInvalidation = false)
    {
        var (pfx, pwd) = TestCertificateGenerator.GenerateTestCertificate();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = pwd,
            SignatureFieldName = field,
            DocMdpPermission = perm,
            AllowCertificationInvalidation = allowCertificationInvalidation,
        });
        return output.ToArray();
    }

    private static IReadOnlyList<SignatureVerificationResult> Verify(byte[] pdf)
    {
        using var s = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(s);
        return new PdfSignatureVerifier().VerifyAll(doc);
    }

    [Fact]
    public void CertifiedUnmodified_IsMarkedCertification_AndStaysValid()
    {
        byte[] certified = Sign(MinimalPdfBuilder.CreateMinimalPdf("Certified P1"),
            "Cert", DocMdpPermission.NoChanges);

        var cert = Verify(certified).Single();

        Assert.True(cert.IsCertification);
        Assert.Equal(1, cert.CertificationPermission);
        Assert.Empty(cert.MdpViolations);
        Assert.True(cert.IsValid, $"Errors: {string.Join(", ", cert.ValidationErrors)}");
    }

    [Fact]
    public void CertifiedP1_ThenModified_FlagsCertificationSignatureInvalid()
    {
        byte[] certified = Sign(MinimalPdfBuilder.CreateMinimalPdf("Certified then changed"),
            "Cert", DocMdpPermission.NoChanges);
        // A second (approval) signature is a post-certification incremental update — forbidden under
        // P=1, so the signer refuses it unless told to break the certification on purpose.
        byte[] modified = Sign(certified, "Approval", allowCertificationInvalidation: true);

        var cert = Verify(modified).First(r => r.IsCertification);

        Assert.Equal(1, cert.CertificationPermission);
        Assert.NotEmpty(cert.MdpViolations);
        Assert.False(cert.IsValid); // certified-but-modified must not report valid
        Assert.Contains(cert.ValidationErrors, e => e.Contains("DocMDP"));
    }

    [Fact]
    public void NonCertifiedSignature_IsNotMarkedCertification_AndUnaffected()
    {
        byte[] signed = Sign(MinimalPdfBuilder.CreateMinimalPdf("Plain approval"), "Approval");

        var result = Verify(signed).Single();

        Assert.False(result.IsCertification);
        Assert.Equal(0, result.CertificationPermission);
        Assert.Empty(result.MdpViolations);
        Assert.True(result.IsValid, $"Errors: {string.Join(", ", result.ValidationErrors)}");
    }
}
