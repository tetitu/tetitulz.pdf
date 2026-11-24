// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Regression test for the weak-algorithm flag that never reached the public API.
///
/// <para>
/// <b>The bug.</b> <c>CmsSignatureVerifier</c> documents that when
/// <see cref="CmsSignatureVerifier.RejectWeakAlgorithms"/> is <c>false</c> (the default) a
/// SHA-1 signature is "merely flagged via <c>CmsVerificationResult.WeakAlgorithm</c>" so that
/// caller policy can refuse it. <c>ReviewFixSigning_WeakAlgorithmTests</c> proved that worked
/// — but only at the <c>CmsSignatureVerifier</c> level. <c>PdfSignatureVerifier</c> copied just
/// <c>IsValid</c>, <c>SignerCommonName</c> and <c>DigestAlgorithm</c> onto
/// <see cref="SignatureVerificationResult"/> and dropped both weak-algorithm members, so a
/// consumer of the actual public verify API got <c>IsValid == true</c> on a SHA-1 signature
/// with no signal at all short of string-matching <c>DigestAlgorithm</c>. The documented safe
/// path did not exist end to end.
/// </para>
/// </summary>
[Collection(WeakAlgorithmPolicyCollection.Name)]
public class WeakAlgorithmReachesPublicResultTests
{
    /// <summary>
    /// The weakness reaches the public result, and the CMS integrity check itself still succeeds —
    /// the bytes do match the signature. What the default policy does with that is the subject
    /// of <see cref="Sha1SignedPdf_IsRejectedByDefault"/>.
    /// </summary>
    [Fact]
    public void Sha1SignedPdf_SurfacesWeakAlgorithmAndKeepsIntegrityValid()
    {
        byte[] signed = SignWith("SHA-1");

        using var stream = new MemoryStream(signed);
        using var doc = PdfDocument.Open(stream);

        var results = new PdfSignatureVerifier().VerifyAll(doc);
        var result = Assert.Single(results);

        // The CMS still verifies against the signed bytes — integrity is a separate question from
        // policy, and conflating the two is what would make a rejected signature look corrupt.
        Assert.True(result.IsIntegrityValid,
            $"Integrity check failed: {string.Join(", ", result.ValidationErrors)}");

        Assert.True(result.WeakAlgorithm,
            "WeakAlgorithm must reach SignatureVerificationResult — this is the whole point of the fix.");
        Assert.False(string.IsNullOrEmpty(result.WeakAlgorithmReason));
        Assert.Contains("SHA-1", result.WeakAlgorithmReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sha256SignedPdf_IsNotFlaggedWeak()
    {
        byte[] signed = SignWith("SHA-256");

        using var stream = new MemoryStream(signed);
        using var doc = PdfDocument.Open(stream);

        var result = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc));

        Assert.True(result.IsIntegrityValid,
            $"Integrity check failed: {string.Join(", ", result.ValidationErrors)}");
        Assert.False(result.WeakAlgorithm);
        Assert.Null(result.WeakAlgorithmReason);
    }

    /// <summary>
    /// A SHA-1 signature is <b>invalid</b> under the default policy.
    /// </summary>
    /// <remarks>
    /// <para><c>SignatureVerificationOptions.RejectWeakAlgorithms</c> and the legacy static both
    /// default to <c>true</c>.</para>
    /// <para>The advisory behaviour is reachable and is pinned by
    /// <see cref="WeakAlgorithmFlag_IsAdvisoryWhenTheCallerOptsOut"/>, so this pair asserts the
    /// policy in both directions rather than only the default.</para>
    /// </remarks>
    [Fact]
    public void Sha1SignedPdf_IsRejectedByDefault()
    {
        byte[] signed = SignWith("SHA-1");

        using var stream = new MemoryStream(signed);
        using var doc = PdfDocument.Open(stream);

        var result = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc));

        Assert.True(result.WeakAlgorithm);
        Assert.False(result.IsValid,
            "A SHA-1 signature must be invalid under the default policy.");
        Assert.Contains(result.ValidationErrors,
            e => e.IndexOf("SHA-1", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// The other direction: a caller that must keep accepting legacy signatures opts out, and the
    /// weakness is still reported. Without this the flip would be indistinguishable from "the
    /// policy is no longer configurable".
    /// </summary>
    [Fact]
    public void WeakAlgorithmFlag_IsAdvisoryWhenTheCallerOptsOut()
    {
        byte[] signed = SignWith("SHA-1");

        using var stream = new MemoryStream(signed);
        using var doc = PdfDocument.Open(stream);

        var options = new SignatureVerificationOptions { RejectWeakAlgorithms = false };
        var result = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc, options));

        Assert.True(result.WeakAlgorithm);
        Assert.True(result.IsValid, "Opting out must restore the advisory behaviour.");
    }

    /// <summary>
    /// A freshly constructed <see cref="SignatureVerificationOptions"/> — every property left at
    /// its default — refuses SHA-1.
    /// </summary>
    /// <remarks>
    /// <para><b>This test exists because a mutation survived without it.</b> Reverting
    /// <c>SignatureVerificationOptions.RejectWeakAlgorithms</c> to <c>false</c> left the whole
    /// signing suite green, which looked like the flip was untested and was worse than that: the
    /// other "by default" tests call <c>VerifyAll</c> with NO options, and since the reachability
    /// fix that path deliberately forwards the caller's null so the CMS layer falls back to the
    /// legacy static. They therefore pin the STATIC's default, not this one. Two defaults, two
    /// tests.</para>
    /// <para>Passing <c>new SignatureVerificationOptions()</c> is the shape a host writes when it
    /// wants trust anchors and takes everything else as given — the case where silently inheriting
    /// "accept SHA-1" would be most surprising.</para>
    /// </remarks>
    [Fact]
    public void DefaultConstructedOptions_RejectSha1()
    {
        byte[] signed = SignWith("SHA-1");

        using var stream = new MemoryStream(signed);
        using var doc = PdfDocument.Open(stream);

        // Explicit options object, nothing set on it.
        var result = Assert.Single(
            new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions()));

        Assert.True(result.WeakAlgorithm);
        Assert.False(result.IsValid, "A default-constructed options object must refuse SHA-1.");
        Assert.True(result.IsIntegrityValid,
            "Refused by policy, not by the cryptography.");
    }

    private static byte[] SignWith(string digestAlgorithm)
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf($"Signed with {digestAlgorithm}");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateTestCertificate();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfxBytes);

        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            DigestAlgorithm = digestAlgorithm,
            SignatureFieldName = "Signature1",
        });

        return output.ToArray();
    }
}
