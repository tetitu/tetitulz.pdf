// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A counter-signature is only worth adding if it can be verified, and verifying one means
/// resolving the counter-signer's <c>SignerID</c> against the CMS certificate bag.
/// </summary>
/// <remarks>
/// <para>
/// <c>InjectCounterSignature</c> built the merged certificate list under the comment "Also add the
/// counter-signer's certificate to the CertStore" and then never used it:
/// <c>CmsSignedData.ReplaceSigners</c> carries the ORIGINAL certificates field across unchanged.
/// The emitted CMS therefore held the <c>id-countersignature</c> unsigned attribute but not the
/// certificate needed to check it, so Adobe, an independent implementation, BouncyCastle and this library's own
/// <c>CmsSignatureVerifier</c> ("No matching certificate found for signer.") all failed on it. The
/// feature produced output that could not be validated — by construction, for every input.
/// </para>
/// <para>
/// The assertions resolve the certificate exactly the way a verifier does —
/// <c>certStore.EnumerateMatches(signer.SignerID)</c> — and check that what comes back is the
/// right identity. Asserting only that the output parses would pass with the certificate still
/// missing.
/// </para>
/// <para>
/// <b>The second defect, now closed and asserted below.</b> BouncyCastle used to refuse to
/// <c>Verify</c> the resulting counter-signature at all — "[For counter signatures,] the
/// signedAttributes field MUST NOT contain a content-type attribute" (RFC 5652 §11.4) — because
/// <c>InjectCounterSignature</c> built it with an ordinary <c>CmsSignedDataGenerator</c>, which
/// always adds one. It now uses <c>GenerateCounterSigners</c>/<c>AddCounterSigners</c>, which omit
/// it, so <see cref="CounterSignedDocument_CounterSignatureVerifiesAgainstItsOwnCertificate"/>
/// checks the whole promise rather than just the certificate's presence.
/// </para>
/// </remarks>
public sealed class CounterSignatureCertificateTests
{
    [Fact]
    public void CounterSignedDocument_CarriesTheCounterSignersCertificate()
    {
        var (cms, counterSigners) = CounterSign();

        var certs = cms.GetCertificates();

        Assert.NotEmpty(counterSigners);
        foreach (SignerInformation cs in counterSigners)
        {
            var matches = certs.EnumerateMatches(cs.SignerID).ToList();
            Assert.True(matches.Count > 0,
                "The counter-signer's certificate is absent from the CMS certificate bag, so the " +
                "counter-signature cannot be verified by anything that resolves a SignerID.");

            // The certificate resolved must be the counter-signer's, not (say) the original
            // signer's re-found under a coincidentally matching issuer.
            Assert.Contains("Counter Signer", matches[0].SubjectDN.ToString());
        }
    }

    /// <summary>
    /// Merging must ADD, not replace: the original signer's own certificate has to survive, or
    /// counter-signing would break the signature it attests to.
    /// </summary>
    [Fact]
    public void CounterSigning_KeepsTheOriginalSignersCertificate()
    {
        var (cms, _) = CounterSign();

        var certs = cms.GetCertificates();
        foreach (SignerInformation signer in cms.GetSignerInfos().GetSigners())
        {
            var matches = certs.EnumerateMatches(signer.SignerID).ToList();
            Assert.True(matches.Count > 0,
                "The original signer's certificate was dropped from the certificate bag.");
            Assert.Contains("Original Signer", matches[0].SubjectDN.ToString());
        }
    }

    /// <summary>
    /// The counter-signature must actually <c>Verify</c> against the certificate resolved out of
    /// the bag — RFC 5652 §11.4 forbids a content-type attribute in a counter-signature's
    /// signedAttributes, and BouncyCastle enforces it.
    /// </summary>
    /// <remarks>
    /// Resolving the certificate (the test above) and verifying with it are two different claims:
    /// the certificate shipped correctly for a while whilst every counter-signature it identified
    /// was still rejected, because the generator added the forbidden attribute. Asserting presence
    /// alone would pass with that defect fully present.
    /// </remarks>
    [Fact]
    public void CounterSignedDocument_CounterSignatureVerifiesAgainstItsOwnCertificate()
    {
        var (cms, counterSigners) = CounterSign();

        var certs = cms.GetCertificates();

        Assert.NotEmpty(counterSigners);
        foreach (SignerInformation cs in counterSigners)
        {
            var resolved = certs.EnumerateMatches(cs.SignerID).ToList();
            Assert.True(resolved.Count > 0, "The counter-signer's certificate is absent.");
            Assert.True(cs.Verify(resolved[0]),
                "The counter-signature does not verify against its own certificate — a " +
                "content-type attribute in its signedAttributes violates RFC 5652 §11.4.");
        }
    }

    // CRL preservation — that counter-signing keeps the original SignedData's CRLs rather than
    // stripping a PAdES B-LT signature's embedded revocation data — is covered by
    // CounterSignatureCrlPreservationTests, which builds the CRL-bearing /Contents this suite's
    // round trip cannot produce.

    /// <summary>
    /// Signs a document, counter-signs it with a second identity, and hands back the parsed CMS
    /// out of the output's <c>/Contents</c> together with the counter-signature SignerInfos.
    /// </summary>
    private static (CmsSignedData cms, IList<SignerInformation> counterSigners) CounterSign()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Original Signer");
        var counter = SigningTestCrypto.Leaf(root, "CN=Counter Signer");

        var (signerPfx, signerPw) = SigningTestCrypto.Pfx(signer, root.Cert);
        var (counterPfx, counterPw) = SigningTestCrypto.Pfx(counter, root.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), signerPfx, signerPw);

        using var input = new MemoryStream(signed);
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(
            input, output, "Signature1", counterPfx, counterPw);

        byte[] contents = ContentsOf(output.ToArray(), "Signature1");
        var cms = new CmsSignedData(contents);

        var counterSigners = new List<SignerInformation>();
        foreach (SignerInformation s in cms.GetSignerInfos().GetSigners())
            foreach (SignerInformation cs in s.GetCounterSignatures().GetSigners())
                counterSigners.Add(cs);

        return (cms, counterSigners);
    }

    private static byte[] ContentsOf(byte[] pdf, string fieldName)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var field = doc.AcroForm!.FindSignatureField(fieldName);
        Assert.NotNull(field);
        var contents = field!.SignatureValue!.GetString(PdfName.Contents);
        Assert.NotNull(contents);
        // Trimmed of the /Contents reserve padding, exactly as the production readers do
        // (SigningDefaults.TrimToAsn1Object). A stand-in that parses the hole rather than
        // the object is not standing in for what ships: BouncyCastle 2.6.2 tolerates the
        // tail and 2.7.0 refuses it, so without this the fixture pins the wrong contract.
        return SigningDefaults.TrimToAsn1Object(contents!.RawBytes);
    }
}
