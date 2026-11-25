// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A <c>/DocTimeStamp</c> may move a signature's certificate-validity assessment time only when it
/// actually covers that signature.
/// </summary>
/// <remarks>
/// <para>
/// Assessing validity at a cryptographically verified, trust-anchored timestamp rather than at the
/// unauthenticated <c>SigningTime</c> is deliberate. What was missing is the second half of the
/// same rule: <c>ResolveDocumentTimestamp</c> returned the FIRST anchored <c>/DocTimeStamp</c> in
/// the document and <c>DeriveTrustedAssessmentTime</c> handed it to <b>every</b> signature, with no
/// check that its <c>/ByteRange</c> covered the one being assessed. A timestamp proves only that
/// the bytes it hashed existed at its GenTime; it says nothing about a signature appended to the
/// file afterwards.
/// </para>
/// <para>
/// <b>Why the fixture uses a certificate issued AFTER the timestamp rather than an expired one.</b>
/// The finding's scenario is an expired certificate reporting <c>Trusted</c> at a back-dated
/// instant. BouncyCastle's <c>SignerInformation.Verify</c> refuses an expired signer certificate
/// outright, so that case never reaches <c>IsValid</c> anyway — the reachable half of the same
/// defect is the assessment time itself, which the attacker's certificate here makes visible in
/// the verdict: valid now, not yet issued at the old timestamp's GenTime. Under the defect it is
/// assessed at 2020 and reported untrusted <i>with</i> <c>HasTrustedTimestamp</c> true; under the
/// fix it is assessed now, and the timestamp is reported as not covering it.
/// </para>
/// </remarks>
public sealed class DocumentTimestampScopeTests
{
    private static readonly DateTime TokenTime = new(2020, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Timestamp first, signature appended afterwards: the timestamp hashed none of the
    /// signature's bytes, so it must not become that signature's trusted time.
    /// </summary>
    [Fact]
    public void ATimestampThatPredatesTheSignature_DoesNotSupplyItsAssessmentTime()
    {
        var root = SigningTestCrypto.Ca();
        var tsa = SigningTestCrypto.TsaCert(root);

        // Issued after the timestamp, valid today: the attacker's own certificate.
        var later = SigningTestCrypto.Leaf(root, "CN=Later Signer",
            notBefore: TokenTime.AddYears(1), notAfter: DateTime.UtcNow.AddYears(5));
        var (pfx, pw) = SigningTestCrypto.Pfx(later, root.Cert);

        // Revision 2: the anchored document timestamp. Revision 3: the appended signature.
        byte[] timestamped = SignedPdfFixtures.AppendDocumentTimestamp(
            SignedPdfFixtures.MinimalPdf(), tsa, TokenTime, new[] { root.Cert },
            sigDictObjNum: 6, sigFieldObjNum: 7);
        byte[] attacked = SignedPdfFixtures.Sign(timestamped, pfx, pw);

        SignatureVerificationResult result = VerifyApproval(attacked, root.Cert);

        Assert.True(result.IsIntegrityValid,
            "The fixture's appended signature does not verify cryptographically, so the " +
            "assertions below would pass for the wrong reason.");
        Assert.False(result.HasTrustedTimestamp,
            "An uncovering document timestamp was accepted as this signature's trusted time.");
        Assert.NotNull(result.AssessmentTime);
        Assert.True(result.AssessmentTime!.Value > TokenTime.AddYears(1),
            $"Certificate validity was assessed at {result.AssessmentTime:O} — back-dated to an " +
            "unrelated timestamp instead of the current time.");
        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
        Assert.Contains(result.Warnings, w => w.Contains("does not cover this signature"));
    }

    /// <summary>
    /// The control, and the half that stops the rule from degenerating into "ignore every
    /// timestamp": signature first, timestamp appended over it. The certificate here has
    /// expired, so only a timestamp that really covers the signature can produce a
    /// <c>Trusted</c> chain.
    /// </summary>
    [Fact]
    public void ATimestampThatCoversTheSignature_StillMovesTheAssessmentTime()
    {
        var root = SigningTestCrypto.Ca();
        var tsa = SigningTestCrypto.TsaCert(root);

        // Valid at the token's GenTime, expired long before "now".
        var expired = SigningTestCrypto.Leaf(root, "CN=Expired Signer",
            notBefore: TokenTime.AddYears(-1), notAfter: TokenTime.AddMonths(6));
        var (pfx, pw) = SigningTestCrypto.Pfx(expired, root.Cert);

        // Revision 2: the signature (objects 6/7). Revision 3: a timestamp over the whole file.
        byte[] signed = SignedPdfFixtures.Sign(
            SignedPdfFixtures.MinimalPdf(), pfx, pw, signingTime: TokenTime);
        byte[] timestamped = SignedPdfFixtures.AppendDocumentTimestamp(
            signed, tsa, TokenTime, new[] { root.Cert },
            sigDictObjNum: 8, sigFieldObjNum: 9, existingFieldRefs: "7 0 R ");

        SignatureVerificationResult result = VerifyApproval(timestamped, root.Cert);

        // The CMS claims TokenTime, inside the certificate's period. Claiming the moment of signing
        // whatever SigningTime said, BouncyCastle would refuse the expired certificate at it, and
        // every assertion below would pass on a signature that did not verify.
        Assert.True(result.IsIntegrityValid,
            "The fixture's signature does not verify cryptographically, so the assertions below " +
            "would pass for the wrong reason.");
        Assert.True(result.HasTrustedTimestamp,
            "A document timestamp that covers this signature must supply its assessment time.");
        Assert.NotNull(result.AssessmentTime);
        Assert.True(Math.Abs((result.AssessmentTime!.Value - TokenTime).TotalMinutes) < 1,
            $"Assessment time {result.AssessmentTime:O} is not the covering token's time {TokenTime:O}.");
        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("does not cover this signature"));
    }

    /// <summary>Verifies the fixture and returns the approval signature's result (never the timestamp's).</summary>
    private static SignatureVerificationResult VerifyApproval(byte[] pdf, X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { anchor },
        });

        var approval = results.SingleOrDefault(r => r.FieldName == "Signature1");
        Assert.NotNull(approval);
        return approval!;
    }
}
