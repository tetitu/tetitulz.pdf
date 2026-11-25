// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Tests.Helpers;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Signing;

/// <summary>End-to-end signature lifecycle: sign → counter-sign →
/// verify. (DSS / DocTimeStamp paths require a real TSA endpoint and are
/// exercised separately; this test covers the chain that runs entirely
/// in-process with BouncyCastle test certs.)</summary>
public sealed class SignatureLifecycleTests
{
    [Fact]
    public void SignThenCounterSignThenVerify_AllStepsSucceed()
    {
        byte[] pdf = BuildMinimalPdf();
        var (pfxA, pwdA) = TestCertificateGenerator.GenerateTestCertificate("CN=Primary Signer");
        var (pfxB, pwdB) = TestCertificateGenerator.GenerateTestCertificate("CN=Counter Signer");

        // Step 1: primary sign
        byte[] signed;
        using (var src = new MemoryStream(pdf))
        using (var dst = new MemoryStream())
        using (var cert = new MemoryStream(pfxA))
        {
            new PdfSigner().Sign(src, dst, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwdA,
                Reason = "Test",
                Location = "CI",
                SignatureFieldName = "Signature1",
            });
            signed = dst.ToArray();
        }
        Assert.NotEmpty(signed);
        // PDF is now bigger and still starts with "%PDF-"
        Assert.True(signed.Length > pdf.Length);
        Assert.Equal((byte)'%', signed[0]);

        // Step 2: verify primary signature integrity
        using (var vs = new MemoryStream(signed))
        using (var vdoc = PdfDocument.Open(vs))
        {
            var verifier = new PdfSignatureVerifier();
            var result = verifier.Verify(vdoc, "Signature1");
            Assert.True(result.IsIntegrityValid,
                $"Primary signature failed integrity: {string.Join("; ", result.ValidationErrors)}");
        }

        // Step 3: add counter-signature (non-empty output check; deep verification
        // of counter-signature integrity is out of scope for this test since
        // PdfSignatureVerifier surfaces the primary signature only).
        byte[] counter;
        using (var src = new MemoryStream(signed))
        using (var dst = new MemoryStream())
        {
            PdfSigningOperations.AddCounterSignature(src, dst, "Signature1", pfxB, pwdB);
            counter = dst.ToArray();
        }
        Assert.True(counter.Length >= signed.Length,
            "Counter-signed PDF should be at least as large as the primary-signed one");

        // Step 4: re-verify primary signature after counter-sign (should still
        // be integrity-valid because counter-sig is added as an unsigned
        // attribute inside the existing SignerInfo, not a new byte-range).
        using (var vs = new MemoryStream(counter))
        using (var vdoc = PdfDocument.Open(vs))
        {
            var verifier = new PdfSignatureVerifier();
            var result = verifier.Verify(vdoc, "Signature1");
            Assert.True(result.IsIntegrityValid,
                $"Primary signature invalidated by counter-sign: {string.Join("; ", result.ValidationErrors)}");
        }
    }

    private static byte[] BuildMinimalPdf()
    {
        var b = new PdfDocumentBuilder();
        var page = b.AddPage(612, 792);
        page.SetFillColorRgb(0, 0, 0);
        page.Rectangle(100, 100, 200, 200);
        page.Fill();
        using var ms = new MemoryStream();
        b.Build(ms);
        return ms.ToArray();
    }
}
