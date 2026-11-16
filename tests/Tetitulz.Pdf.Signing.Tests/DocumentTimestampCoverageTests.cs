// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.Helpers;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A <c>/DocTimeStamp</c> may back-date certificate validity only for the signatures its own
/// <c>/ByteRange</c> actually covers.
///
/// <para><b>The defect.</b> <c>PdfSignatureVerifier</c> resolved the first verifiable, anchored
/// document timestamp in the file and handed its GenTime to <i>every</i> signature as the
/// certificate-validity assessment time. A timestamp proves only that the bytes it signs existed at
/// its GenTime; it says nothing about a signature appended afterwards. So an attacker could take a
/// document legitimately timestamped in the past, append an incremental update carrying a signature
/// made with an already-expired certificate, and have the old timestamp back-date that certificate
/// into its own validity window — the exact failure
/// <c>DeriveTrustedAssessmentTime</c>'s own documentation says it exists to prevent.</para>
///
/// <para>Both directions are pinned here. Dropping document timestamps altogether would also stop
/// the attack, so <see cref="LaterTimestamp_CoversTheSignature_AndBackDatesIt"/> is what keeps the
/// legitimate PAdES B-LT case working.</para>
/// </summary>
public sealed class DocumentTimestampCoverageTests
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddYears(-6);

    [Fact]
    public void EarlierTimestamp_DoesNotCoverALaterSignature_SoExpiredCertIsNotTrusted()
    {
        var root = SigningTestCrypto.Ca("CN=Coverage Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Coverage TSA");

        // A certificate that expired years ago — and years AFTER the timestamp's GenTime, so
        // back-dating to that GenTime is exactly what would rescue it.
        var expired = SigningTestCrypto.Leaf(
            root, "CN=Expired Signer",
            notBefore: DateTime.UtcNow.AddYears(-7),
            notAfter: DateTime.UtcNow.AddYears(-5));

        // 1. A document carrying only the old, anchored document timestamp.
        byte[] timestamped = DocTimeStampAppender.Append(
            MinimalPdfBuilder.CreateMinimalPdf("Coverage fixture"),
            tsa, LongAgo, "DocTimeStamp1", false, root.Cert);

        // 2. The attacker appends a signature made with the expired certificate.
        byte[] attacked = Sign(timestamped, expired, root, "Signature1");

        var sig = VerifyField(attacked, "Signature1", root, tsa);

        Assert.Equal(PdfSignatureChainStatus.Invalid, sig.ChainStatus);
        Assert.False(sig.IsFullyTrusted);

        // The evidence for WHY: the assessment time must have stayed at "now" rather than being
        // pulled back to the timestamp. Without this the status assertion could pass for an
        // unrelated reason.
        Assert.NotNull(sig.AssessmentTime);
        Assert.True(sig.AssessmentTime!.Value > DateTime.UtcNow.AddHours(-1),
            $"Assessment time was pulled back to {sig.AssessmentTime}, i.e. the uncovering " +
            "document timestamp was still honoured for this signature.");
        Assert.False(sig.HasTrustedTimestamp);
        Assert.Contains(sig.Warnings, w => w.Contains("do not cover this signature", StringComparison.Ordinal));
    }

    [Fact]
    public void LaterTimestamp_CoversTheSignature_AndBackDatesIt()
    {
        var root = SigningTestCrypto.Ca("CN=Coverage Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Coverage TSA");

        // Same expired certificate as above; the only difference is the ORDER of the revisions.
        var expired = SigningTestCrypto.Leaf(
            root, "CN=Expired Signer",
            notBefore: DateTime.UtcNow.AddYears(-7),
            notAfter: DateTime.UtcNow.AddYears(-5));

        // 1. Sign first, 2. then archive-timestamp over the signed bytes — the PAdES B-LT order.
        byte[] signed = Sign(MinimalPdfBuilder.CreateMinimalPdf("Coverage fixture"),
                             expired, root, "Signature1");
        byte[] timestamped = DocTimeStampAppender.Append(
            signed, tsa, LongAgo, "DocTimeStamp1", false, root.Cert);

        var sig = VerifyField(timestamped, "Signature1", root, tsa);

        Assert.True(sig.HasTrustedTimestamp,
            "The timestamp covers this signature, so it must supply the assessment time.");
        Assert.NotNull(sig.AssessmentTime);
        Assert.True(Math.Abs((sig.AssessmentTime!.Value - LongAgo).TotalMinutes) < 5,
            $"Expected the assessment time to be the timestamp's GenTime ({LongAgo:u}), got " +
            $"{sig.AssessmentTime:u}.");

        // Back-dated to a time inside its validity window, the expired certificate chains cleanly.
        Assert.Equal(PdfSignatureChainStatus.Trusted, sig.ChainStatus);
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] Sign(
        byte[] pdf, SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued root, string fieldName)
    {
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            SignatureFieldName = fieldName,
        });
        return output.ToArray();
    }

    private static SignatureVerificationResult VerifyField(
        byte[] pdf, string fieldName,
        SigningTestCrypto.Issued root, SigningTestCrypto.Issued tsa)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
            TimestampTrustAnchors = new[] { root.Cert, tsa.Cert },
        });

        var match = results.SingleOrDefault(r => r.FieldName == fieldName);
        Assert.True(match != null,
            $"No result for '{fieldName}'; the fixture never reached the code under test. " +
            $"Fields seen: {string.Join(", ", results.Select(r => r.FieldName ?? "<null>"))}");
        return match!;
    }
}
