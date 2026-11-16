// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Regression tests for a policy switch that no public caller could reach.
///
/// <para>
/// <b>The bug.</b> <see cref="CmsSignatureVerifier.RejectWeakAlgorithms"/> documents itself as the
/// legacy process-wide opt-in to hard-rejecting SHA-1, "consulted only when no options object is
/// passed" — and GAPS.md told readers to set it for hard rejection. But
/// <see cref="PdfSignatureVerifier.VerifyAll(PdfDocument, SignatureVerificationOptions)"/> resolved
/// <see cref="SignatureVerificationOptions.Default"/> up front and handed that never-null instance
/// to the CMS layer, so every call arrived carrying an explicit policy and the static governed
/// nothing reachable from the public API. Setting it changed no verdict anywhere.
/// <see cref="CmsHardeningTests.LegacyStatic_StillAppliesWhenNoOptionsPassed"/> passed throughout,
/// because it calls the CMS entry point directly — one layer below the hole.
/// </para>
/// <para>
/// <b>The trap in fixing it.</b> The obvious repair is to make the static a floor
/// (<c>(options?.RejectWeakAlgorithms ?? false) || RejectWeakAlgorithms</c>). That would break the
/// documented precedence in the relaxing direction: per-call options must win <i>outright</i>, so a
/// caller who explicitly says "flag only" is not overruled by whatever a neighbouring request left
/// in the static. Hence <see cref="ExplicitOptions_StillOverrideTheLegacyStatic_ThroughVerifyAll"/>,
/// which pins both directions through the document-level API rather than the CMS one.
/// </para>
/// </summary>
[Collection(WeakAlgorithmPolicyCollection.Name)]
public class WeakAlgorithmPolicyReachabilityTests
{
    /// <summary>
    /// The reachability defect itself: no options passed ⇒ the legacy static decides, and setting
    /// it must actually turn a SHA-1 signature invalid through the public verify API.
    /// </summary>
    [Fact]
    public void LegacyStatic_HardRejectsSha1_ThroughVerifyAllWithNoOptions()
    {
        byte[] signed = SignSha1Pdf();

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = true;

            using var stream = new MemoryStream(signed);
            using var doc = PdfDocument.Open(stream);
            var result = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc));

            Assert.True(result.WeakAlgorithm, "SHA-1 must still be detected as weak.");
            Assert.False(result.IsValid,
                "The legacy static must reach the CMS layer when the caller passed no options.");
            // Integrity stays TRUE: the bytes verified and only policy refused them. This assertion
            // used to read Assert.False(IsIntegrityValid) — correct while a policy refusal was
            // mapped onto the integrity flag, and wrong since they were separated, because
            // conflating them makes a refused algorithm indistinguishable from a modified document.
            Assert.True(result.IsIntegrityValid,
                "A policy refusal must not be reported as an integrity failure.");
            Assert.Contains(result.ValidationErrors,
                e => e.Contains("Weak signature algorithm rejected by policy", StringComparison.Ordinal));
        }
        finally { CmsSignatureVerifier.RejectWeakAlgorithms = saved; }
    }

    /// <summary>
    /// The control for the test above: with the static left at its default the same bytes verify.
    /// Without this, a fix that rejected SHA-1 unconditionally would look correct.
    /// </summary>
    [Fact]
    public void LegacyStaticUnset_LeavesSha1AdvisoryOnly_ThroughVerifyAllWithNoOptions()
    {
        byte[] signed = SignSha1Pdf();

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = false;

            using var stream = new MemoryStream(signed);
            using var doc = PdfDocument.Open(stream);
            var result = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc));

            Assert.True(result.WeakAlgorithm);
            Assert.True(result.IsIntegrityValid,
                "Flagging stays advisory unless the policy asks for rejection: "
                + string.Join("; ", result.ValidationErrors));
            Assert.True(result.IsValid);
        }
        finally { CmsSignatureVerifier.RejectWeakAlgorithms = saved; }
    }

    /// <summary>
    /// Precedence, pinned at the document level: an explicit options object wins over the static in
    /// <b>both</b> directions. A "floor" implementation passes the tightening half and fails here.
    /// </summary>
    [Fact]
    public void ExplicitOptions_StillOverrideTheLegacyStatic_ThroughVerifyAll()
    {
        byte[] signed = SignSha1Pdf();

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            // Static says "reject"; the per-call options say "flag only" and must win.
            CmsSignatureVerifier.RejectWeakAlgorithms = true;
            using (var stream = new MemoryStream(signed))
            using (var doc = PdfDocument.Open(stream))
            {
                var lenient = Assert.Single(new PdfSignatureVerifier().VerifyAll(
                    doc, new SignatureVerificationOptions { RejectWeakAlgorithms = false }));
                Assert.True(lenient.WeakAlgorithm);
                Assert.True(lenient.IsIntegrityValid,
                    "Per-call options must override the process-wide static, including in the "
                    + "relaxing direction: " + string.Join("; ", lenient.ValidationErrors));
            }

            // Static says "allow"; the per-call options say "reject" and must win.
            CmsSignatureVerifier.RejectWeakAlgorithms = false;
            using (var stream = new MemoryStream(signed))
            using (var doc = PdfDocument.Open(stream))
            {
                var strict = Assert.Single(new PdfSignatureVerifier().VerifyAll(
                    doc, new SignatureVerificationOptions { RejectWeakAlgorithms = true }));
                Assert.False(strict.IsValid);
                Assert.True(strict.IsIntegrityValid,
                    "Refused by policy, not by the cryptography.");
                Assert.Contains(strict.ValidationErrors,
                    e => e.Contains("Weak signature algorithm rejected by policy", StringComparison.Ordinal));
            }
        }
        finally { CmsSignatureVerifier.RejectWeakAlgorithms = saved; }
    }

    /// <summary>
    /// The second half of the same defect, on the facade: naming a trust anchor built an options
    /// object out of thin air, and a non-null options object silences the static. So a host that had
    /// set hard rejection lost it the moment it also supplied anchors — tightening trust silently
    /// relaxed algorithm policy. The two dimensions are now independent.
    /// </summary>
    [Fact]
    public void LegacyStatic_SurvivesTheTrustAnchorOverload_OfTheValidatorFacade()
    {
        var (pfxBytes, password, anchor) = TestCertificateGenerator.GenerateCustom("CN=Anchored SHA1 Signer");
        byte[] signed = SignSha1Pdf(pfxBytes, password);
        var anchors = new List<X509Certificate> { anchor };

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = true;
            using (var stream = new MemoryStream(signed))
            {
                var rejected = PdfSignatureValidator.Validate(stream, anchors);
                Assert.False(rejected.IsValid,
                    "Supplying trust anchors must not switch off the weak-algorithm policy.");
                Assert.Contains("Weak signature algorithm rejected by policy", rejected.ErrorMessage,
                    StringComparison.Ordinal);
            }

            // Control: the same anchors with the policy unset still validate, so the assertion
            // above cannot be passing merely because the anchor set broke something.
            CmsSignatureVerifier.RejectWeakAlgorithms = false;
            using (var stream = new MemoryStream(signed))
            {
                var accepted = PdfSignatureValidator.Validate(stream, anchors);
                Assert.True(accepted.IsValid, "Control failed: " + accepted.ErrorMessage);
                Assert.Equal(PdfSignatureChainStatus.Trusted, accepted.ChainStatus);
            }
        }
        finally { CmsSignatureVerifier.RejectWeakAlgorithms = saved; }
    }

    // ---------------------------------------------------------------- fixture

    private static byte[] SignSha1Pdf(byte[] pfxBytes = null, string password = null)
    {
        if (pfxBytes == null)
            (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Signed with SHA-1");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfxBytes);

        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            DigestAlgorithm = "SHA-1",
            SignatureFieldName = "Signature1",
        });

        return output.ToArray();
    }
}
