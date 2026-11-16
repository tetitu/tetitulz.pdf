// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// The two public verification entry points must never disagree about whether a document is
/// trustworthy.
///
/// <para>
/// <b>The bug.</b> The shadow-append detection ("bytes appended after the last signature's
/// ByteRange are unsigned") and DocMDP enforcement lived only in
/// <c>PdfSignatureVerifier.VerifyAll</c>. The by-name <c>Verify(document, fieldName, options)</c>
/// — reachable publicly through <c>PdfSigningOperations.VerifySignature</c> — called
/// <c>VerifyField</c> directly and skipped both. A shadow-attacked PDF therefore reported
/// <c>IsValid == true</c> and <c>IsFullyTrusted == true</c> through one API and <c>false</c>
/// through the other, for byte-identical input.
/// </para>
/// </summary>
public class ByNameVerifyParityTests
{
    /// <summary>
    /// Appends unsigned bytes past the signature's <c>/ByteRange</c>.
    /// <para>
    /// The tail deliberately contains no <c>trailer</c>/<c>startxref</c> keywords: the parser
    /// scans backwards for the last <c>startxref</c>, so an appended trailer would be resolved
    /// instead of the original one and the document would parse with no AcroForm at all —
    /// testing the parser rather than the signature check. What matters here is only that the
    /// file is longer than the region any signature covers, which is precisely the condition a
    /// shadow attack creates.
    /// </para>
    /// </summary>
    private static byte[] AppendUnsignedTail(byte[] signed)
    {
        byte[] tail = Encoding.ASCII.GetBytes(
            "\n% unsigned bytes appended after the signed ByteRange\n" +
            "9 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var combined = new byte[signed.Length + tail.Length];
        Buffer.BlockCopy(signed, 0, combined, 0, signed.Length);
        Buffer.BlockCopy(tail, 0, combined, signed.Length, tail.Length);
        return combined;
    }

    private static byte[] SignMinimal()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("by-name verify parity");
        var (pfx, password) = TestCertificateGenerator.GenerateTestCertificate();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);

        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            SignatureFieldName = "Signature1",
        });
        return output.ToArray();
    }

    [Fact]
    public void ShadowAppendedDocument_ByNameVerify_ReportsInvalidJustLikeVerifyAll()
    {
        byte[] attacked = AppendUnsignedTail(SignMinimal());

        using var ms = new MemoryStream(attacked);
        using var doc = PdfDocument.Open(ms);
        var verifier = new PdfSignatureVerifier();

        var byName = verifier.Verify(doc, "Signature1");
        var all = verifier.VerifyAll(doc);
        var fromAll = Assert.Single(all.Where(r => r.FieldName == "Signature1"));

        // The core assertion: identical verdicts from both APIs on identical bytes.
        Assert.Equal(fromAll.IsValid, byName.IsValid);
        Assert.False(byName.IsValid,
            "A document with unsigned appended bytes must not verify through the by-name path.");
        Assert.Contains(byName.ValidationErrors,
            e => e.Contains("appended after the last signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UntamperedDocument_ByNameVerify_MatchesVerifyAll()
    {
        byte[] signed = SignMinimal();

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);
        var verifier = new PdfSignatureVerifier();

        var byName = verifier.Verify(doc, "Signature1");
        var fromAll = Assert.Single(verifier.VerifyAll(doc).Where(r => r.FieldName == "Signature1"));

        Assert.True(byName.IsIntegrityValid, "The untampered signature must still verify.");
        Assert.Equal(fromAll.IsValid, byName.IsValid);
        Assert.Equal(fromAll.IsIntegrityValid, byName.IsIntegrityValid);
        Assert.Equal(fromAll.ValidationErrors.Count, byName.ValidationErrors.Count);
    }

    [Fact]
    public void ByNameVerify_UnknownField_StillThrowsPdfVerificationException()
    {
        byte[] signed = SignMinimal();

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        Assert.Throws<PdfVerificationException>(
            () => new PdfSignatureVerifier().Verify(doc, "NoSuchField"));
    }
}
