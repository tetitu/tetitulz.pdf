// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The two questions a caller actually asks a signing library — <b>"were these bytes signed?"</b>
/// and <b>"can I trust the answer?"</b> — and the decisions that compose them.
///
/// <para><b>Found by mutation.</b> Four deliberate mutations of the verdict logic survived the
/// whole <c>Signing.Tests</c> suite: forcing <c>IsIntegrityValid</c> true, forcing
/// <c>ChainStatus</c> to <c>Trusted</c>, and dropping either the chain clause or the revocation
/// clause from <c>IsFullyTrusted</c>. Every one of those makes the library report an untrustworthy
/// document as trustworthy.</para>
///
/// <para><b>Why they survived is worth knowing.</b> The suite was not thin — it has eighteen files
/// touching these properties — but every one of them asserts <c>IsIntegrityValid == <b>true</b></c>
/// for a genuine signature and then drives <c>IsValid</c> false through
/// <c>CoversEntireDocument</c> or <c>ValidationErrors</c>. <b>Nothing tampered with the signed
/// bytes</b>, and nothing separated the three clauses of <c>IsFullyTrusted</c>. The tests covered
/// the paths <i>around</i> the cryptographic verdict without ever asking it to say no.</para>
/// </summary>
public class SignatureVerdictCompositionTests
{
    private const string Field = "Signature1";

    private static SignatureVerificationResult Verify(
        byte[] pdf, Org.BouncyCastle.X509.X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = anchor == null
                ? Array.Empty<Org.BouncyCastle.X509.X509Certificate>()
                : new[] { anchor },
        });

        var match = results.FirstOrDefault(r => r.FieldName == Field);
        Assert.True(match != null, "the fixture produced no signature field to verify");
        return match;
    }

    private static byte[] SignedDocument(out Org.BouncyCastle.X509.X509Certificate anchor)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);
        anchor = ca.Cert;
        return SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);
    }

    // ---- "were these bytes signed?" -----------------------------------------------------

    /// <summary>
    /// The floor, so every negative below can be trusted: an untampered signature verifies.
    /// </summary>
    [Fact]
    public void AGenuineSignatureIsCryptographicallyValid()
    {
        byte[] signed = SignedDocument(out var anchor);

        var r = Verify(signed, anchor);

        Assert.True(r.IsIntegrityValid);
        Assert.True(r.IsValid);
    }

    /// <summary>
    /// <b>The test the suite did not have.</b> Flip one byte of the page content — inside the
    /// signed <c>/ByteRange</c>, so the digest must no longer match — and the cryptographic
    /// verdict has to say no.
    ///
    /// <para>Forcing <c>IsIntegrityValid = true</c> survived the entire suite before this existed,
    /// which means nothing was asking the signature to reject anything: a <b>forged document
    /// reported as valid</b> would have passed CI.</para>
    /// </summary>
    [Fact]
    public void TamperingWithASignedByteMakesTheSignatureCryptographicallyInvalid()
    {
        byte[] signed = SignedDocument(out var anchor);

        // The fixture's page content carries this marker; changing a byte of it changes the
        // digested bytes without disturbing the file's structure or the /Contents hole.
        int at = IndexOf(signed, Encoding.ASCII.GetBytes("signed content"));
        Assert.True(at > 0, "the fixture's page content marker was not found — fixture changed?");

        byte[] tampered = (byte[])signed.Clone();
        tampered[at] = (byte)'f';                       // "signed" -> "figned"

        var r = Verify(tampered, anchor);

        Assert.False(r.IsIntegrityValid);
        Assert.False(r.IsValid);
        Assert.False(r.IsFullyTrusted);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    // ---- "can I trust the answer?" ------------------------------------------------------

    /// <summary>
    /// <c>IsFullyTrusted</c> is the property CLAUDE.md tells callers to ask for the complete
    /// verdict, and its three clauses had no test separating them. These assert the composition
    /// directly, which is the only way to pin a rule whose inputs come from three different
    /// subsystems.
    ///
    /// <para>Each row inverts exactly one clause. Dropping <i>either</i> the chain clause or the
    /// revocation clause from the property is a mutation that used to survive the whole suite.</para>
    /// </summary>
    [Theory]
    // integrity, errors,   chain,                              revocation,                    expected
    [InlineData(true,  false, PdfSignatureChainStatus.Trusted,   PdfRevocationStatus.Good,      true)]
    [InlineData(false, false, PdfSignatureChainStatus.Trusted,   PdfRevocationStatus.Good,      false)]  // not intact
    [InlineData(true,  true,  PdfSignatureChainStatus.Trusted,   PdfRevocationStatus.Good,      false)]  // structural error
    [InlineData(true,  false, PdfSignatureChainStatus.UntrustedRoot, PdfRevocationStatus.Good,      false)]  // no anchor
    [InlineData(true,  false, PdfSignatureChainStatus.Invalid,   PdfRevocationStatus.Good,      false)]  // broken chain
    [InlineData(true,  false, PdfSignatureChainStatus.Trusted,   PdfRevocationStatus.Revoked,   false)]  // revoked
    [InlineData(true,  false, PdfSignatureChainStatus.Trusted,   PdfRevocationStatus.Unknown,   true)]   // unknown is not revoked
    public void IsFullyTrustedRequiresIntegrityAndATrustedChainAndANonRevokedCertificate(
        bool integrity, bool structuralError,
        PdfSignatureChainStatus chain, PdfRevocationStatus revocation, bool expected)
    {
        var r = new SignatureVerificationResult
        {
            IsIntegrityValid = integrity,
            ChainStatus = chain,
            RevocationStatus = revocation,
        };
        if (structuralError) r.ValidationErrors.Add("a structural problem");

        Assert.Equal(expected, r.IsFullyTrusted);
    }

    /// <summary>
    /// And the end-to-end half, so the composition above is not merely a statement about a
    /// hand-built object: with <b>no trust anchor supplied</b>, a perfectly intact signature is
    /// valid but <b>not fully trusted</b>. CLAUDE.md states this exactly — "without anchors
    /// nothing can be <c>Trusted</c>" — and nothing was checking it.
    /// </summary>
    [Fact]
    public void WithNoTrustAnchorAnIntactSignatureIsValidButNotFullyTrusted()
    {
        byte[] signed = SignedDocument(out _);

        var r = Verify(signed, anchor: null);

        Assert.True(r.IsIntegrityValid);
        Assert.True(r.IsValid);
        Assert.NotEqual(PdfSignatureChainStatus.Trusted, r.ChainStatus);
        Assert.False(r.IsFullyTrusted);
    }

    /// <summary>
    /// The control for the test above: the same document <i>with</i> its anchor is fully trusted.
    /// Without this, "not fully trusted" would be satisfied by a property that is never true.
    /// </summary>
    [Fact]
    public void WithItsTrustAnchorTheSameSignatureIsFullyTrusted()
    {
        byte[] signed = SignedDocument(out var anchor);

        var r = Verify(signed, anchor);

        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
        Assert.True(r.IsFullyTrusted);
    }
}
