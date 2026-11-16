// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Core.Tests.Helpers;

namespace Tetitulz.Pdf.Core.Tests.Signing;

public class PdfSignerTests
{
    [Fact]
    public void Sign_MinimalPdf_ProducesSignedOutput()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Test document for signing");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        using var inputStream = new MemoryStream(pdfBytes);
        using var outputStream = new MemoryStream();
        using var certStream = new MemoryStream(pfxBytes);

        var parameters = new SignatureParameters
        {
            CertificateStream = certStream,
            CertificatePassword = password,
            Reason = "Test signing",
            Location = "Unit Test",
            SignatureFieldName = "TestSig",
        };

        var signer = new PdfSigner();
        signer.Sign(inputStream, outputStream, parameters);

        // Verify output is larger than input (has incremental update)
        Assert.True(outputStream.Length > inputStream.Length);

        // Verify output starts with original PDF content
        outputStream.Position = 0;
        byte[] outputBytes = outputStream.ToArray();
        Assert.Equal((byte)'%', outputBytes[0]);
        Assert.Equal((byte)'P', outputBytes[1]);
        Assert.Equal((byte)'D', outputBytes[2]);
        Assert.Equal((byte)'F', outputBytes[3]);
    }

    [Fact]
    public void Sign_ThenVerify_SignatureIsValid()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Test document for signing and verification");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] signedPdfBytes;
        using (var inputStream = new MemoryStream(pdfBytes))
        using (var outputStream = new MemoryStream())
        using (var certStream = new MemoryStream(pfxBytes))
        {
            var parameters = new SignatureParameters
            {
                CertificateStream = certStream,
                CertificatePassword = password,
                Reason = "Test signing",
                Location = "Unit Test",
                SignatureFieldName = "Signature1",
            };

            var signer = new PdfSigner();
            signer.Sign(inputStream, outputStream, parameters);
            signedPdfBytes = outputStream.ToArray();
        }

        // Now verify the signed PDF
        using var signedStream = new MemoryStream(signedPdfBytes);
        using var doc = PdfDocument.Open(signedStream);

        // Document should have AcroForm with signature
        Assert.NotNull(doc.AcroForm);

        var verifier = new PdfSignatureVerifier();
        var results = verifier.VerifyAll(doc);

        Assert.Single(results);
        var result = results[0];
        Assert.True(result.IsIntegrityValid, $"Signature integrity check failed. Errors: {string.Join(", ", result.ValidationErrors)}");
        Assert.Equal("Signature1", result.FieldName);
        Assert.Equal("Test signing", result.Reason);
        Assert.Equal("Unit Test", result.Location);
    }

    [Fact]
    public void Sign_ThenVerify_CoversEntireDocument()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Coverage test");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] signedPdfBytes;
        using (var inputStream = new MemoryStream(pdfBytes))
        using (var outputStream = new MemoryStream())
        using (var certStream = new MemoryStream(pfxBytes))
        {
            var signer = new PdfSigner();
            signer.Sign(inputStream, outputStream, new SignatureParameters
            {
                CertificateStream = certStream,
                CertificatePassword = password,
            });
            signedPdfBytes = outputStream.ToArray();
        }

        using var signedStream = new MemoryStream(signedPdfBytes);
        using var doc = PdfDocument.Open(signedStream);

        var verifier = new PdfSignatureVerifier();
        var results = verifier.VerifyAll(doc);

        Assert.Single(results);
        Assert.True(results[0].CoversEntireDocument);
    }

    [Fact]
    public void Sign_ThenTamper_VerificationFails()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Tamper test document");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] signedPdfBytes;
        using (var inputStream = new MemoryStream(pdfBytes))
        using (var outputStream = new MemoryStream())
        using (var certStream = new MemoryStream(pfxBytes))
        {
            var signer = new PdfSigner();
            signer.Sign(inputStream, outputStream, new SignatureParameters
            {
                CertificateStream = certStream,
                CertificatePassword = password,
            });
            signedPdfBytes = outputStream.ToArray();
        }

        // Tamper with a byte in the original PDF range (before the signature)
        // Find a safe byte to tamper (around offset 50 should be in the original PDF body)
        if (signedPdfBytes.Length > 60)
        {
            signedPdfBytes[50] = (byte)(signedPdfBytes[50] ^ 0xFF);
        }

        using var tamperedStream = new MemoryStream(signedPdfBytes);
        using var doc = PdfDocument.Open(tamperedStream);

        var verifier = new PdfSignatureVerifier();
        var results = verifier.VerifyAll(doc);

        // After tampering, signature should be invalid
        Assert.Single(results);
        Assert.False(results[0].IsIntegrityValid);
    }

    [Fact]
    public void Sign_WithCustomReason_PreservesMetadata()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Metadata test");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate("CN=John Doe");

        byte[] signedPdfBytes;
        using (var inputStream = new MemoryStream(pdfBytes))
        using (var outputStream = new MemoryStream())
        using (var certStream = new MemoryStream(pfxBytes))
        {
            var signer = new PdfSigner();
            signer.Sign(inputStream, outputStream, new SignatureParameters
            {
                CertificateStream = certStream,
                CertificatePassword = password,
                Reason = "Contract approval",
                Location = "New York",
                ContactInfo = "john@example.com",
                SignatureFieldName = "ApprovalSig",
            });
            signedPdfBytes = outputStream.ToArray();
        }

        using var signedStream = new MemoryStream(signedPdfBytes);
        using var doc = PdfDocument.Open(signedStream);

        var verifier = new PdfSignatureVerifier();
        var results = verifier.VerifyAll(doc);

        Assert.Single(results);
        Assert.True(results[0].IsIntegrityValid);
        Assert.Equal("ApprovalSig", results[0].FieldName);
        Assert.Equal("Contract approval", results[0].Reason);
        Assert.Equal("New York", results[0].Location);
        Assert.Contains("John Doe", results[0].SignerCommonName);
    }

    [Fact]
    public void Sign_SignedPdfIsStillValidPdf()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Validity test");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] signedPdfBytes;
        using (var inputStream = new MemoryStream(pdfBytes))
        using (var outputStream = new MemoryStream())
        using (var certStream = new MemoryStream(pfxBytes))
        {
            var signer = new PdfSigner();
            signer.Sign(inputStream, outputStream, new SignatureParameters
            {
                CertificateStream = certStream,
                CertificatePassword = password,
            });
            signedPdfBytes = outputStream.ToArray();
        }

        // Re-open the signed PDF and verify it's still valid
        using var signedStream = new MemoryStream(signedPdfBytes);
        using var doc = PdfDocument.Open(signedStream);

        Assert.NotNull(doc.Catalog);
        Assert.True(doc.Pages.Count >= 1);
        Assert.Equal(1, doc.Version.Major);
    }

    [Fact]
    public void Sign_TwiceWithDifferentCerts_BothSignaturesValid()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Multi-sig test");
        var (pfx1, pwd1) = TestCertificateGenerator.GenerateTestCertificate("CN=Signer One");
        var (pfx2, pwd2) = TestCertificateGenerator.GenerateTestCertificate("CN=Signer Two");

        // First signature
        byte[] signed1;
        using (var input = new MemoryStream(pdfBytes))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx1))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd1,
                SignatureFieldName = "Sig1",
                Reason = "First approval",
            });
            signed1 = output.ToArray();
        }

        // Second signature on already-signed PDF
        byte[] signed2;
        using (var input = new MemoryStream(signed1))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx2))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd2,
                SignatureFieldName = "Sig2",
                Reason = "Second approval",
            });
            signed2 = output.ToArray();
        }

        // Verify both signatures
        using var stream = new MemoryStream(signed2);
        using var doc = PdfDocument.Open(stream);

        var verifier = new PdfSignatureVerifier();
        var results = verifier.VerifyAll(doc);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntegrityValid,
            $"Signature {r.FieldName} integrity failed: {string.Join(", ", r.ValidationErrors)}"));
        Assert.Contains(results, r => r.FieldName == "Sig1");
        Assert.Contains(results, r => r.FieldName == "Sig2");
    }

    [Fact]
    public void Sign_Twice_FirstSignatureCoversRevision()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Revision coverage test");
        var (pfx, pwd) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] signed1;
        using (var input = new MemoryStream(pdfBytes))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd,
                SignatureFieldName = "Sig1",
            });
            signed1 = output.ToArray();
        }

        byte[] signed2;
        using (var input = new MemoryStream(signed1))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd,
                SignatureFieldName = "Sig2",
            });
            signed2 = output.ToArray();
        }

        using var stream = new MemoryStream(signed2);
        using var doc = PdfDocument.Open(stream);

        var results = new PdfSignatureVerifier().VerifyAll(doc);
        var sig1 = results.First(r => r.FieldName == "Sig1");

        // First signature won't cover entire document (second sig was appended)
        Assert.False(sig1.CoversEntireDocument);
        // But it should cover its revision (contiguous ByteRange)
        Assert.True(sig1.CoversRevision);
        Assert.True(sig1.IsIntegrityValid);
    }

    [Fact]
    public void Sign_Twice_SecondSignatureCoversEntireDocument()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Full coverage test");
        var (pfx, pwd) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] signed1;
        using (var input = new MemoryStream(pdfBytes))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd,
                SignatureFieldName = "Sig1",
            });
            signed1 = output.ToArray();
        }

        byte[] signed2;
        using (var input = new MemoryStream(signed1))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd,
                SignatureFieldName = "Sig2",
            });
            signed2 = output.ToArray();
        }

        using var stream = new MemoryStream(signed2);
        using var doc = PdfDocument.Open(stream);

        var results = new PdfSignatureVerifier().VerifyAll(doc);
        var sig2 = results.First(r => r.FieldName == "Sig2");

        Assert.True(sig2.CoversEntireDocument);
        Assert.True(sig2.CoversRevision);
        Assert.True(sig2.IsIntegrityValid);
    }

    [Fact]
    public void Sign_ThreeTimes_AllSignaturesValid()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Triple-sig test");
        var (pfx, pwd) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] current = pdfBytes;
        for (int i = 1; i <= 3; i++)
        {
            using var input = new MemoryStream(current);
            using var output = new MemoryStream();
            using var cert = new MemoryStream(pfx);
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd,
                SignatureFieldName = $"Sig{i}",
            });
            current = output.ToArray();
        }

        using var stream = new MemoryStream(current);
        using var doc = PdfDocument.Open(stream);

        var results = new PdfSignatureVerifier().VerifyAll(doc);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntegrityValid,
            $"Signature {r.FieldName} failed: {string.Join(", ", r.ValidationErrors)}"));
    }

    [Fact]
    public void Sign_Twice_AutoFieldName_NoDuplicate()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Auto-name test");
        var (pfx, pwd) = TestCertificateGenerator.GenerateTestCertificate();

        // First sign with default "Signature1"
        byte[] signed1;
        using (var input = new MemoryStream(pdfBytes))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd,
                // Default: SignatureFieldName = "Signature1"
            });
            signed1 = output.ToArray();
        }

        // Second sign with same default "Signature1" — should auto-rename
        byte[] signed2;
        using (var input = new MemoryStream(signed1))
        using (var output = new MemoryStream())
        using (var cert = new MemoryStream(pfx))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = cert,
                CertificatePassword = pwd,
                // Default: SignatureFieldName = "Signature1" — should become "Signature2"
            });
            signed2 = output.ToArray();
        }

        using var stream = new MemoryStream(signed2);
        using var doc = PdfDocument.Open(stream);

        var results = new PdfSignatureVerifier().VerifyAll(doc);
        Assert.Equal(2, results.Count);
        var fieldNames = results.Select(r => r.FieldName).ToHashSet();
        Assert.Contains("Signature1", fieldNames);
        Assert.Contains("Signature2", fieldNames);
    }
}
