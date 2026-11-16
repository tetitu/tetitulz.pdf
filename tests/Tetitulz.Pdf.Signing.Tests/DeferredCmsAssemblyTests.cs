// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="CmsDeferredSignatureBuilder"/> — the half of deferred signing that serves a service
/// returning a <b>raw signature value</b> rather than a finished CMS.
/// </summary>
/// <remarks>
/// <para><b>The signing service is simulated at the level it actually works at.</b> Every test here
/// signs the bytes it was handed with <c>SignerUtilities</c> and nothing else — no CMS builder, no
/// certificate, no knowledge of PDF. That is the whole contract: hand over some bytes, get back a
/// signature value. A test that reached for <see cref="CmsSignatureGenerator"/> to produce the
/// "service's" answer would be proving the library agrees with itself.</para>
///
/// <para><b>The negative control signs the wrong thing.</b> Signing the content digest instead of
/// the attribute DER is the documented failure mode of every one of these integrations, and it
/// produces a perfectly well-formed CMS — so a test that only asserted "assembly succeeds" would
/// pass with the self-check deleted.</para>
/// </remarks>
public sealed class DeferredCmsAssemblyTests
{
    private static readonly DateTime FixedTime = new(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);

    private sealed record Party(SigningTestCrypto.Issued Ca, SigningTestCrypto.Issued Leaf)
    {
        public IList<X509Certificate> Chain => new List<X509Certificate> { Leaf.Cert, Ca.Cert };
        public IList<byte[]> ChainDer =>
            new List<byte[]> { Leaf.Cert.GetEncoded(), Ca.Cert.GetEncoded() };
    }

    private static Party NewParty()
    {
        var ca = SigningTestCrypto.Ca();
        return new Party(ca, SigningTestCrypto.Leaf(ca));
    }

    /// <summary>
    /// What a remote signer does and all it does: sign the bytes it was given. No CMS, no
    /// certificate, no document.
    /// </summary>
    private static byte[] RemoteSign(byte[] toBeSigned, AsymmetricKeyParameter key,
                                     string algorithm = "SHA256withRSA")
    {
        ISigner signer = SignerUtilities.GetSigner(algorithm);
        signer.Init(true, key);
        signer.BlockUpdate(toBeSigned, 0, toBeSigned.Length);
        return signer.GenerateSignature();
    }

    private static byte[] Sha256(byte[] data)
        => DigestUtilities.CalculateDigest("SHA-256", data);

    private static SignerInformation OnlySigner(byte[] cms, byte[] content)
        => new CmsSignedData(new CmsProcessableByteArray(content), cms)
            .GetSignerInfos().GetSigners().Cast<SignerInformation>().Single();

    // ------------------------------------------------------------------ what is handed over

    /// <summary>
    /// The bytes to sign are the RFC 5652 §5.4 <c>SET OF Attribute</c> — carrying the content's
    /// digest, the content type, and the ESS certificate binding — and they are <b>not</b> the
    /// content or its digest.
    /// </summary>
    [Fact]
    public void SignedAttributesCarryTheContentDigestAndTheCertificateBinding()
    {
        var party = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("the bytes a PDF signature covers");

        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256");

        var table = new AttributeTable(Asn1Set.GetInstance(Asn1Object.FromByteArray(toBeSigned)));

        Assert.Equal(CmsObjectIdentifiers.Data,
            DerObjectIdentifier.GetInstance(table[CmsAttributes.ContentType].AttrValues[0]));

        byte[] messageDigest = Asn1OctetString
            .GetInstance(table[CmsAttributes.MessageDigest].AttrValues[0]).GetOctets();
        Assert.Equal(Sha256(content), messageDigest);

        // ESS signing-certificate-v2: without it the signature is not CAdES/PAdES, and a
        // certificate-substitution ambiguity is left open.
        Assert.NotNull(table[PkcsObjectIdentifiers.IdAASigningCertificateV2]);

        // What it is NOT. Handing a service the content or its digest is the failure this whole
        // class exists to prevent, so the difference is asserted rather than assumed.
        Assert.NotEqual(content, toBeSigned);
        Assert.NotEqual(Sha256(content), toBeSigned);
    }

    /// <summary>
    /// The attributes presented here are byte-identical to the ones the local-key path signs.
    /// </summary>
    /// <remarks>
    /// <para>Both go through <c>CmsSignatureGenerator.BuildSignedAttributes</c>. A second copy would
    /// leave two signatures that both verify while only one of them carried the ESS binding — and
    /// nothing downstream distinguishes them, because a verifier checks the attributes that are
    /// there rather than the ones that should have been.</para>
    ///
    /// <para><b>Compared attribute by attribute, with <c>signingTime</c> excluded and its presence
    /// asserted.</b> The two tables are built moments apart and that attribute is <i>now</i>, so a
    /// byte comparison of the whole set passes or fails on whether the clock ticked between them —
    /// which is exactly how the first version of this file flaked. Excluding it silently would hide
    /// the same defect the exclusion exists for, so it is asserted to be in both.</para>
    /// </remarks>
    [Fact]
    public void DeferredAndLocalPathsPresentTheSameAttributes()
    {
        var party = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("same attributes either way");

        var deferred = new AttributeTable(Asn1Set.GetInstance(Asn1Object.FromByteArray(
            CmsDeferredSignatureBuilder.GetSignedAttributes(content, party.Leaf.Cert, "SHA-256"))));

        byte[] local = CmsSignatureGenerator.GenerateDetachedSignature(
            content, party.Leaf.Private, party.Leaf.Cert, party.Chain, "SHA-256");
        var localTable = OnlySigner(local, content).SignedAttributes;

        Assert.NotNull(deferred[CmsAttributes.SigningTime]);
        Assert.NotNull(localTable[CmsAttributes.SigningTime]);

        var oids = new[]
        {
            CmsAttributes.ContentType,
            CmsAttributes.MessageDigest,
            PkcsObjectIdentifiers.IdAASigningCertificateV2,
        };

        foreach (var oid in oids)
            Assert.Equal(
                localTable[oid].ToAsn1Object().GetEncoded(),
                deferred[oid].ToAsn1Object().GetEncoded());

        // The sets are otherwise the same size, or "every attribute I named matches" would hold for
        // a deferred table carrying extra attributes nobody signed.
        Assert.Equal(localTable.ToAsn1EncodableVector().Count,
                     deferred.ToAsn1EncodableVector().Count);
    }

    // ------------------------------------------------------------------ assembly

    /// <summary>
    /// A signature value produced by something that never saw the document assembles into a CMS that
    /// verifies over it.
    /// </summary>
    [Fact]
    public void AssembledCmsVerifiesOverTheContent()
    {
        var party = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("assemble me");

        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256");
        byte[] cms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
            content, toBeSigned, party.Leaf.Cert, party.Chain, "SHA-256",
            RemoteSign(toBeSigned, party.Leaf.Private));

        var signer = OnlySigner(cms, content);
        Assert.True(signer.Verify(party.Leaf.Cert));
        Assert.Equal(Sha256(content), signer.GetContentDigest());

        // The chain travels, or a verifier has nothing to build a path from.
        var embedded = new CmsSignedData(new CmsProcessableByteArray(content), cms)
            .GetCertificates().EnumerateMatches(null).Cast<X509Certificate>().ToList();
        Assert.Equal(2, embedded.Count);
        Assert.Contains(embedded, c => c.SubjectDN.Equivalent(party.Ca.Cert.SubjectDN));
    }

    /// <summary>
    /// Signing the content digest instead of the attribute DER is refused, by name, rather than
    /// producing a CMS that is well-formed and does not verify.
    /// </summary>
    /// <remarks>
    /// This is the mistake every raw-signing integration makes once. Left undetected it surfaces
    /// downstream as "the PDF's signature is broken", which points at the document rather than at
    /// the service.
    /// </remarks>
    [Fact]
    public void AssemblyRefusesASignatureOverTheWrongBytes()
    {
        var party = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("wrong bytes");

        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256");
        byte[] wrong = RemoteSign(Sha256(content), party.Leaf.Private);

        var ex = Assert.Throws<PdfSigningException>(() =>
            CmsDeferredSignatureBuilder.AssembleDetachedSignature(
                content, toBeSigned, party.Leaf.Cert, party.Chain, "SHA-256", wrong));

        Assert.Contains("does not verify", ex.Message);
    }

    /// <summary>
    /// A signature made by a different key is refused too — the check is over the value, not merely
    /// over its length.
    /// </summary>
    [Fact]
    public void AssemblyRefusesASignatureFromAnotherKey()
    {
        var party = NewParty();
        var stranger = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("not your signature");

        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256");

        var ex = Assert.Throws<PdfSigningException>(() =>
            CmsDeferredSignatureBuilder.AssembleDetachedSignature(
                content, toBeSigned, party.Leaf.Cert, party.Chain, "SHA-256",
                RemoteSign(toBeSigned, stranger.Leaf.Private)));

        Assert.Contains("does not verify", ex.Message);
    }

    /// <summary>An empty signature value is refused before anything is assembled.</summary>
    [Fact]
    public void AssemblyRefusesAnEmptySignatureValue()
    {
        var party = NewParty();
        byte[] attributes = CmsDeferredSignatureBuilder.GetSignedAttributes(
            new byte[] { 1 }, party.Leaf.Cert, "SHA-256");

        var ex = Assert.Throws<PdfSigningException>(() =>
            CmsDeferredSignatureBuilder.AssembleDetachedSignature(
                new byte[] { 1 }, attributes, party.Leaf.Cert, party.Chain, "SHA-256",
                Array.Empty<byte>()));
        Assert.Contains("empty", ex.Message);
    }

    /// <summary>
    /// An RFC 3161 token supplied by the caller is embedded as the <c>signatureTimeStampToken</c>
    /// unsigned attribute — the same attachment the local-key path uses.
    /// </summary>
    /// <remarks>
    /// This is where a deferred signature gets its timestamp. <c>PdfDeferredSigner.Prepare</c>
    /// refuses <c>TimestampAuthorityUrl</c> precisely because the token belongs to whoever builds
    /// the CMS, so if this did nothing the refusal would be sending callers to a dead end.
    /// </remarks>
    [Fact]
    public void ACallerSuppliedTimestampTokenIsEmbedded()
    {
        var party = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("timestamp me");

        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256");
        byte[] signatureValue = RemoteSign(toBeSigned, party.Leaf.Private);

        var tsa = SigningTestCrypto.TsaCert(party.Ca);
        byte[] token = SigningTestCrypto.TimestampToken(
            signatureValue, tsa, FixedTime, party.Ca.Cert);

        byte[] cms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
            content, toBeSigned, party.Leaf.Cert, party.Chain, "SHA-256", signatureValue,
            signatureAlgorithmName: null, timestampToken: token);

        var signer = OnlySigner(cms, content);
        var attribute = signer.UnsignedAttributes?[
            new DerObjectIdentifier(Tetitulz.Pdf.Core.Constants.PdfEkuOids.SignatureTimeStampToken)];

        Assert.NotNull(attribute);
        Assert.Equal(token, attribute!.AttrValues[0].ToAsn1Object().GetEncoded());

        // The signature itself still verifies: an unsigned attribute is outside its scope, and a
        // path that rebuilt the SignerInfo to attach one could easily break it.
        Assert.True(signer.Verify(party.Leaf.Cert));
    }

    /// <summary>
    /// The attributes embedded in the CMS are the exact bytes that were handed over to be signed.
    /// </summary>
    /// <remarks>
    /// <b>This is the case a flaky test taught.</b> An earlier version rebuilt the attribute set at
    /// assembly time on the reasoning that it was deterministic. It is not: BouncyCastle's default
    /// attribute generator adds a <c>signingTime</c> of <i>now</i>, so two calls either side of a
    /// tick produced a signature over a set that no longer existed — and the self-check caught it
    /// about one run in four, which reads as an intermittent test rather than as a defect. The
    /// contract is now that the bytes travel, and this asserts it directly rather than relying on
    /// the verification passing, which it did for the other three runs in four.
    /// </remarks>
    [Fact]
    public void TheEmbeddedAttributesAreTheBytesThatWereSigned()
    {
        var party = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("the bytes travel");

        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256");

        // The far side is slow, and the attributes carry a timestamp. A second later, the naive
        // rebuild would produce a different set.
        System.Threading.Thread.Sleep(1100);

        byte[] cms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
            content, toBeSigned, party.Leaf.Cert, party.Chain, "SHA-256",
            RemoteSign(toBeSigned, party.Leaf.Private));

        Assert.Equal(toBeSigned, OnlySigner(cms, content).GetEncodedSignedAttributes());
        Assert.True(OnlySigner(cms, content).Verify(party.Leaf.Cert));
    }

    /// <summary>
    /// Attribute bytes that are not a DER attribute set are refused, naming what they should be.
    /// </summary>
    [Fact]
    public void MalformedAttributeBytesAreRefused()
    {
        var party = NewParty();

        var ex = Assert.Throws<PdfSigningException>(() =>
            CmsDeferredSignatureBuilder.AssembleDetachedSignature(
                new byte[] { 1 }, new byte[] { 9, 9, 9 }, party.Leaf.Cert, party.Chain,
                "SHA-256", new byte[] { 1 }));

        Assert.Contains("GetSignedAttributes", ex.Message);
    }

    // ------------------------------------------------------------------ the algorithm name

    /// <summary>
    /// Naming RSA-PSS carries its <b>parameters</b> into the SignerInfo, which an OID alone cannot
    /// do — and the same certificate defaults to PKCS#1, because nothing about the key says which.
    /// </summary>
    /// <remarks>
    /// This is why the parameter is a name rather than the OID
    /// <c>SignatureParameters.SignatureAlgorithmOid</c> takes: there BouncyCastle holds the private
    /// key and fills the PSS parameters in from it, here there is no key to ask.
    /// </remarks>
    [Fact]
    public void NamingRsaPssCarriesItsParametersAndTheDefaultDoesNot()
    {
        var party = NewParty();
        byte[] content = System.Text.Encoding.ASCII.GetBytes("pss or pkcs1");
        const string Pss = "SHA256withRSAandMGF1";

        byte[] pssAttrs = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256", Pss);
        byte[] pssCms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
            content, pssAttrs, party.Leaf.Cert, party.Chain, "SHA-256",
            RemoteSign(pssAttrs, party.Leaf.Private, Pss), Pss);

        var pss = OnlySigner(pssCms, content);
        Assert.Equal(PkcsObjectIdentifiers.IdRsassaPss.Id, pss.SignatureAlgorithm.Algorithm.Id);
        Assert.NotNull(pss.SignatureAlgorithm.Parameters);
        Assert.True(pss.Verify(party.Leaf.Cert));

        // Same certificate, no name given: PKCS#1, with no parameters to carry.
        byte[] plainAttrs = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, party.Leaf.Cert, "SHA-256");
        byte[] plainCms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
            content, plainAttrs, party.Leaf.Cert, party.Chain, "SHA-256",
            RemoteSign(plainAttrs, party.Leaf.Private));

        Assert.NotEqual(PkcsObjectIdentifiers.IdRsassaPss.Id,
            OnlySigner(plainCms, content).SignatureAlgorithm.Algorithm.Id);
    }

    /// <summary>
    /// The default algorithm name is derived for the digest and the key that are actually in play —
    /// a SHA-3 digest and an EC key included, neither of which is spelled the way SHA-2-with-RSA is.
    /// </summary>
    /// <remarks>
    /// <para><b>Both cases exist because a mutation survived.</b> Removing the digest-name
    /// normalisation left ten tests green: they were all SHA-256 with RSA, the one combination
    /// where the raw spelling happens to resolve. The normalisation was not merely untested — it
    /// was <b>wrong</b>, stripping every hyphen and so turning <c>SHA3-256</c> into a name
    /// BouncyCastle does not know, which would have refused every SHA-3 deferred signature with a
    /// message blaming the caller.</para>
    ///
    /// <para>The two families genuinely differ: the finder knows <c>SHA256withECDSA</c> but not
    /// <c>SHA-256withECDSA</c>, and <c>SHA3-256withRSA</c> but not <c>SHA3256withRSA</c>. So the
    /// test has to carry one of each, or half the rule is unobserved.</para>
    /// </remarks>
    [Theory]
    [InlineData("SHA3-256", "SHA3-256withRSA", false)]
    [InlineData("SHA3_512", "SHA3-512withRSA", false)]
    [InlineData("SHA-384", "SHA384withRSA", false)]
    [InlineData("SHA-256", "SHA256withECDSA", true)]
    [InlineData("SHA-512", "SHA512withECDSA", true)]
    public void DefaultAlgorithmNameCoversTheDigestAndKeyActuallyInPlay(
        string digest, string remoteAlgorithm, bool ellipticCurve)
    {
        var party = NewParty();
        var signer = ellipticCurve ? SigningTestCrypto.EcLeaf(party.Ca) : party.Leaf;
        var chain = new List<X509Certificate> { signer.Cert, party.Ca.Cert };
        byte[] content = System.Text.Encoding.ASCII.GetBytes("derive my algorithm");

        // No signatureAlgorithmName: the whole point is what the default derives.
        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            content, signer.Cert, digest);

        byte[] cms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
            content, toBeSigned, signer.Cert, chain, digest,
            RemoteSign(toBeSigned, signer.Private, remoteAlgorithm));

        // AssembleDetachedSignature verifies before returning, so reaching here already proves the
        // derived algorithm matched what the "service" used. Asserted again on the parsed CMS so
        // the claim does not rest on the implementation's own self-check.
        Assert.True(OnlySigner(cms, content).Verify(signer.Cert));
    }

    /// <summary>An algorithm name nothing can resolve is refused, naming the remedy.</summary>
    [Fact]
    public void AnUnresolvableAlgorithmNameIsRefused()
    {
        var party = NewParty();
        var ex = Assert.Throws<PdfSigningException>(() =>
            CmsDeferredSignatureBuilder.GetSignedAttributes(
                new byte[] { 1 }, party.Leaf.Cert, "SHA-256", "SHA256withUnicorn"));
        Assert.Contains("signatureAlgorithmName", ex.Message);
    }

    // ------------------------------------------------------------------ end to end

    /// <summary>
    /// The whole boundary-crossing flow: prepare a PDF, hand the attribute bytes to a signer that
    /// holds only a key, assemble the CMS from what comes back, inject it, and verify the file.
    /// </summary>
    /// <remarks>
    /// <para>This is the case neither half closes alone, and the one the feature exists for. The
    /// "service" here receives a few hundred bytes and returns a signature value; it is handed no
    /// document, no certificate and no CMS builder.</para>
    ///
    /// <para>The unsigned document is verified beside it as a control — a verifier that reported
    /// everything valid would pass the positive case on its own.</para>
    /// </remarks>
    [Fact]
    public void PdfSignedAcrossTheBoundaryVerifies()
    {
        var party = NewParty();
        byte[] pdf = SignedPdfFixtures.MinimalPdf();

        PreparedSignature descriptor;
        byte[] prepared;
        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            descriptor = PdfDeferredSigner.Prepare(input, output, party.ChainDer,
                new SignatureParameters
                {
                    EstimatedSignatureSize = 12_000,
                    SigningTime = FixedTime,
                    Reason = "signed elsewhere",
                });
            prepared = output.ToArray();
        }

        // ---- the boundary. Only these bytes cross it. ----
        byte[] toBeSigned = CmsDeferredSignatureBuilder.GetSignedAttributes(
            descriptor.SignedBytes, party.Leaf.Cert, descriptor.DigestAlgorithm);
        Assert.True(toBeSigned.Length < 1024,
            "the point of this shape is that a small, fixed-size blob crosses the wire rather " +
            "than the document.");

        byte[] signatureValue = RemoteSign(toBeSigned, party.Leaf.Private);
        // ---- and back. ----

        byte[] cms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
            descriptor.SignedBytes, toBeSigned, party.Leaf.Cert, party.Chain,
            descriptor.DigestAlgorithm, signatureValue);

        byte[] completed;
        using (var source = new MemoryStream(prepared))
        using (var output = new MemoryStream())
        {
            PdfDeferredSigner.Complete(source, descriptor, cms, output);
            completed = output.ToArray();
        }

        Assert.False(Verify(prepared, descriptor.FieldName).IsIntegrityValid);

        var result = Verify(completed, descriptor.FieldName);
        Assert.True(result.IsIntegrityValid);
        Assert.True(result.CoversEntireDocument);
        Assert.Equal("CN=Test Signer", result.SignerSubject);
    }

    private static SignatureVerificationResult Verify(byte[] pdf, string fieldName)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return new PdfSignatureVerifier().Verify(doc, fieldName);
    }
}
