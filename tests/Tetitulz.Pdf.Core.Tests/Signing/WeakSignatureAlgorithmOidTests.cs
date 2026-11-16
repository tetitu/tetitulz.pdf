// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing.Cms;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// <c>WeakAlgorithmPolicy</c> flags a SignerInfo whose <b>signature algorithm</b> names a broken
/// hash, independently of what its <c>digestAlgorithm</c> says.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists: every arm of that switch was unreachable by the suite.</b>
/// Measured, not assumed — neutralising the whole
/// <c>switch (signer.EncryptionAlgOid)</c> in <c>DetectWeakAlgorithm</c> left
/// <c>Tetitulz.Pdf.Core.Tests</c> at 2359/2359 and <c>Tetitulz.Pdf.Signing.Tests</c> at 364/366,
/// both identical to baseline. Six OID arms that help decide <c>IsValid</c>, reached by nothing.
/// The cause is that every existing weak-algorithm fixture sets <c>DigestAlgorithm = SHA-1</c>, so
/// the digest check one line earlier returns first and the switch is never entered.</para>
///
/// <para><b>What it protects.</b> GAPS §20c has to migrate
/// <c>SignerInformation.EncryptionAlgOid</c> to <c>SignatureAlgorithm.Algorithm.Id</c> before
/// BouncyCastle 2.7.0 can be taken. The switch has <b>no default arm</b> and falls through to
/// "not weak", so a migration that returned a different spelling — RFC 5652 permits a SignerInfo's
/// <c>signatureAlgorithm</c> to carry either <c>rsaEncryption</c> or <c>sha256WithRSAEncryption</c>
/// — would turn a rejected SHA-1 signature into an accepted one, silently. No green suite would
/// have announced that.</para>
///
/// <para><b>These assert on <c>WeakAlgorithm</c>, never on <c>IsValid</c>, and that is forced by
/// the fixtures rather than chosen.</b> BouncyCastle cannot verify a SignerInfo whose
/// <c>signatureAlgorithm</c> is a composite SHA-1/MD5 OID while its <c>digestAlgorithm</c> is
/// SHA-256 — it derives the content digest from the hash the composite OID implies, so the
/// message-digest attribute does not match and the signature fails to verify for a reason that has
/// nothing to do with policy. <c>WeakAlgorithm</c> and <c>WeakAlgorithmReason</c> are set
/// unconditionally by <c>CmsSignatureVerifier</c>, before and independently of that, which is
/// exactly why the three answers are kept separate. <b>Do not "fix" these by asserting
/// <c>IsValid</c>.</b></para>
///
/// <para><b>The last two arms are covered now, and the reason they were not is worth keeping.</b>
/// <c>dsaWithSha1</c> and <c>ecdsaWithSha1</c> were recorded here as needing "a signing key of a
/// matching type", which <c>SigningTestCrypto</c> does not issue. That was a claim about the
/// fixture, and reading the callee refutes it: <c>DetectWeakAlgorithm</c> compares
/// <c>signer.SignatureAlgorithm.Algorithm.Id</c> against a table of strings and never touches a
/// key, so the OID substitution this file already performs for the PSS cases reaches them exactly
/// as well. A generated fixture would have needed the key; a <i>substituted</i> one needs
/// nothing.</para>
/// </remarks>
public sealed class WeakSignatureAlgorithmOidTests
{
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("content to be signed");

    /// <summary>sha1WithRSAEncryption (PKCS#1 v1.5).</summary>
    private const string Sha1WithRsa = "1.2.840.113549.1.1.5";
    /// <summary>sha1WithRSASignature, the legacy OIW spelling.</summary>
    private const string Sha1WithRsaOiw = "1.3.14.3.2.29";
    /// <summary>md5WithRSAEncryption.</summary>
    private const string Md5WithRsa = "1.2.840.113549.1.1.4";
    /// <summary>md2WithRSAEncryption.</summary>
    private const string Md2WithRsa = "1.2.840.113549.1.1.2";
    /// <summary>rsaEncryption — names a key algorithm and no digest.</summary>
    private const string RsaEncryption = "1.2.840.113549.1.1.1";
    /// <summary>sha256WithRSAEncryption.</summary>
    private const string Sha256WithRsa = "1.2.840.113549.1.1.11";

    /// <summary>
    /// Each broken signature-algorithm OID is flagged, and flagged with <b>its own</b> reason.
    /// </summary>
    /// <remarks>
    /// The expected substring is per-row on purpose. Asserting only "something was flagged" would
    /// pass if every arm returned the same string, which is the shape a copy-paste error in the
    /// switch produces.
    /// </remarks>
    [Theory]
    [InlineData(Sha1WithRsa, "sha1WithRSAEncryption")]
    [InlineData(Sha1WithRsaOiw, "sha1WithRSASignature (OIW)")]
    [InlineData(Md5WithRsa, "md5WithRSAEncryption")]
    [InlineData(Md2WithRsa, "md2WithRSAEncryption")]
    public void ABrokenSignatureAlgorithmOidIsFlagged_EvenWhenTheDigestIsStrong(
        string signatureAlgorithmOid, string expectedReason)
    {
        var result = VerifyWith(signatureAlgorithmOid);

        Assert.True(result.WeakAlgorithm,
            $"signature algorithm {signatureAlgorithmOid} must be flagged weak");
        Assert.Contains(expectedReason, result.WeakAlgorithmReason);
    }

    /// <summary>
    /// The control: the spelling this library itself emits is <b>not</b> flagged, and verifies.
    /// </summary>
    /// <remarks>
    /// <c>rsaEncryption</c> names a key algorithm and no digest, so falling through the switch to
    /// "not weak" is correct — the digest check above the switch is the only place the hash can be
    /// named for such a SignerInfo. This is also the live path: the library's default
    /// <c>AddSigner</c> emits exactly this OID, so without this row the theory above could be
    /// satisfied by a policy that flagged everything.
    /// </remarks>
    [Theory]
    [InlineData(RsaEncryption)]
    [InlineData(Sha256WithRsa)]
    public void AStrongSignatureAlgorithmOidIsNotFlagged(string signatureAlgorithmOid)
    {
        var result = VerifyWith(signatureAlgorithmOid);

        Assert.False(result.WeakAlgorithm,
            $"signature algorithm {signatureAlgorithmOid} must not be flagged weak");
        Assert.Null(result.WeakAlgorithmReason);
        Assert.True(result.IsValid,
            "a SHA-256 signature under a strong signature algorithm must verify");
    }

    /// <summary>
    /// RSASSA-PSS whose <b>parameters</b> name SHA-1 is flagged, even though its
    /// <c>digestAlgorithm</c> says SHA-256.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the first test that reaches <c>DetectWeakPssHash</c> at all.</b> Measured:
    /// replacing the whole PSS arm with <c>return null</c> left <c>CmsHardeningTests</c> at 17/17.
    /// Its <c>RsaPssOverSha1_IsFlaggedAsWeak</c> passes <c>DigestSha1</c>, so the digest check above
    /// the switch answers first and the PSS branch is never entered — the test was satisfied by a
    /// claim it does not make, and has been renamed rather than having its assertion edited.</para>
    ///
    /// <para><b>The fixture needs ASN.1 surgery, and that is a finding rather than a
    /// preference.</b> <c>CmsSignedDataGenerator</c> derives the PSS parameters from the digest it
    /// is given, so it will not produce the one combination that matters — a strong
    /// <c>digestAlgorithm</c> over parameters naming a broken hash. Handing it the PSS OID with
    /// SHA-256 yields parameters naming SHA-256, which is correctly *not* weak. So the
    /// <c>signatureAlgorithm</c> is substituted after generation.</para>
    ///
    /// <para><b>Why it matters for the BouncyCastle migration.</b> GAPS §20c site #3 replaces
    /// <c>SignerInformation.EncryptionAlgorithmID</c> with <c>SignatureAlgorithm</c>, and
    /// <c>DetectWeakPssHash</c> needs the algorithm <b>parameters</b>, not just an OID. A migration
    /// that supplied only the OID would report every PSS signature as having absent parameters —
    /// i.e. as SHA-1 — and this is the only test that would notice.</para>
    /// </remarks>
    [Fact]
    public void RsaPssWhoseParametersNameSha1IsFlagged_EvenWhenTheDigestIsStrong()
    {
        byte[] cms = CmsWithSubstitutedSignatureAlgorithm(PssAlgorithmIdentifierNaming(Sha1Oid));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.WeakAlgorithm, "PSS over SHA-1 must be flagged");
        Assert.Contains("RSASSA-PSS", result.WeakAlgorithmReason);
    }

    /// <summary>
    /// The control for the test above: the same surgery with parameters naming SHA-256 is
    /// <b>not</b> flagged.
    /// </summary>
    /// <remarks>
    /// Without this, the test above would pass equally well against a PSS arm that flagged every
    /// PSS signature — which is exactly what a migration losing the parameters would produce.
    /// </remarks>
    [Fact]
    public void RsaPssWhoseParametersNameSha256IsNotFlagged()
    {
        byte[] cms = CmsWithSubstitutedSignatureAlgorithm(PssAlgorithmIdentifierNaming(Sha256Oid));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.False(result.WeakAlgorithm, "PSS over SHA-256 must not be flagged");
        Assert.Null(result.WeakAlgorithmReason);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The two arms no <i>generated</i> fixture could reach: the signature algorithm names a key
    /// type this project cannot issue.
    /// </summary>
    /// <remarks>
    /// <para><b>They are reached by substitution, not by signing.</b> The OID is written into a
    /// finished SignerInfo, so no DSA or EC key is needed and the signature bytes stay the RSA ones
    /// — which is fine, because the policy half and the integrity half are computed
    /// independently and only the policy half is asserted here.</para>
    ///
    /// <para><b>Parameters are absent rather than <c>NULL</c>.</b> RFC 3279 §2.2.2/§2.2.3
    /// omit the parameters field for DSA and ECDSA, where RFC 8017 requires an explicit <c>NULL</c>
    /// for the RSA family — so the one-argument constructor is the correct spelling and a
    /// <c>DerNull</c> here would be a fixture that does not resemble a real signature.</para>
    /// </remarks>
    [Theory]
    [InlineData(DsaWithSha1, "dsaWithSha1")]
    [InlineData(EcdsaWithSha1, "ecdsaWithSha1")]
    public void ASignatureAlgorithmNamingSha1WithANonRsaKeyTypeIsWeak(string oid, string expected)
    {
        byte[] cms = CmsWithSubstitutedSignatureAlgorithm(
            new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(new DerObjectIdentifier(oid)));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.WeakAlgorithm);

        // Its OWN reason, so a wrong-arm match fails rather than passing on a neighbour's string.
        Assert.Contains(expected, result.WeakAlgorithmReason);
    }

    /// <summary>The same substitution with a strong non-RSA algorithm is not weak.</summary>
    /// <remarks>
    /// The acceptance control the two refusals need. Without it, "an ecdsaWithSha1 SignerInfo is
    /// flagged" is equally satisfied by a policy that flags every substituted SignerInfo — and
    /// substitution is exactly what makes these fixtures unusual, so that is the confusion most
    /// available to them.
    /// </remarks>
    [Fact]
    public void EcdsaWithSha256IsNotWeak()
    {
        byte[] cms = CmsWithSubstitutedSignatureAlgorithm(
            new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(
                new DerObjectIdentifier(EcdsaWithSha256)));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.False(result.WeakAlgorithm, "ecdsa-with-SHA256 must not be flagged");
        Assert.Null(result.WeakAlgorithmReason);
    }

    /// <summary>dsaWithSha1 (RFC 3279 §2.2.2).</summary>
    private const string DsaWithSha1 = "1.2.840.10040.4.3";

    /// <summary>ecdsaWithSha1 (RFC 3279 §2.2.3).</summary>
    private const string EcdsaWithSha1 = "1.2.840.10045.4.1";

    /// <summary>ecdsa-with-SHA256 (RFC 5758) — the acceptance control.</summary>
    private const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";

    /// <summary>id-RSASSA-PSS. Its digest lives in the parameters, not in the OID.</summary>
    private const string RsaPss = "1.2.840.113549.1.1.10";
    private const string Sha1Oid = "1.3.14.3.2.26";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string Mgf1Oid = "1.2.840.113549.1.1.8";

    /// <summary>RFC 4055 <c>RSASSA-PSS-params</c> naming <paramref name="hashOid"/>.</summary>
    private static Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier PssAlgorithmIdentifierNaming(string hashOid)
    {
        var hash = new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(
            new DerObjectIdentifier(hashOid), DerNull.Instance);
        var mgf = new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(
            new DerObjectIdentifier(Mgf1Oid), hash);
        var pss = new Org.BouncyCastle.Asn1.Pkcs.RsassaPssParameters(
            hash, mgf, new DerInteger(32), new DerInteger(1));
        return new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(
            new DerObjectIdentifier(RsaPss), pss);
    }

    /// <summary>
    /// Generates a normal SHA-256 CMS and rewrites its single SignerInfo's
    /// <c>digestEncryptionAlgorithm</c> to <paramref name="signatureAlgorithm"/>.
    /// </summary>
    /// <remarks>
    /// The result does not <i>verify</i> — the signature bytes were produced under the original
    /// algorithm — and that is fine here: <c>CmsSignatureVerifier</c> sets <c>WeakAlgorithm</c> and
    /// <c>WeakAlgorithmReason</c> unconditionally, before and independently of the integrity
    /// answer. These tests assert only the policy half, which is why they must not assert
    /// <c>IsValid</c>.
    /// </remarks>
    private static byte[] CmsWithSubstitutedSignatureAlgorithm(
        Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier signatureAlgorithm)
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=PSS Parameter Signer");

        var gen = new CmsSignedDataGenerator();
        gen.AddSigner(signer.Private, signer.Cert,
                      CmsSignedGenerator.EncryptionRsa, CmsSignedGenerator.DigestSha256,
                      (AttributeTable)null, (AttributeTable)null);
        gen.AddCertificates(
            CollectionUtilities.CreateStore(new List<X509Certificate> { signer.Cert }));

        byte[] original = gen.Generate(new CmsProcessableByteArray(Data), false).GetEncoded();

        var contentInfo = ContentInfo.GetInstance(Asn1Object.FromByteArray(original));
        var signedData = SignedData.GetInstance(contentInfo.Content);

        var infos = new Asn1EncodableVector();
        foreach (Asn1Encodable entry in signedData.SignerInfos)
        {
            var info = SignerInfo.GetInstance(entry);
            infos.Add(new SignerInfo(
                info.SignerID, info.DigestAlgorithm, info.AuthenticatedAttributes,
                signatureAlgorithm, info.EncryptedDigest, info.UnauthenticatedAttributes));
        }

        var rebuilt = new SignedData(
            signedData.DigestAlgorithms, signedData.EncapContentInfo,
            signedData.Certificates, signedData.CRLs, new DerSet(infos));

        return new ContentInfo(contentInfo.ContentType, rebuilt).GetEncoded();
    }

    /// <summary>
    /// Builds a detached CMS over <see cref="Data"/> with a SHA-256 <c>digestAlgorithm</c> and the
    /// given <c>signatureAlgorithm</c> OID, and verifies it.
    /// </summary>
    /// <remarks>
    /// The digest is SHA-256 throughout so the digest branch of <c>DetectWeakAlgorithm</c> cannot
    /// answer first — that branch returning early is precisely why the switch had no coverage.
    /// </remarks>
    private static CmsVerificationResult VerifyWith(string signatureAlgorithmOid)
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=Weak Signature Algorithm Signer");

        var gen = new CmsSignedDataGenerator();
        // The casts pick the AttributeTable overload: AddSigner has a CmsAttributeTableGenerator
        // twin with the same shape, so a bare `null, null` is ambiguous.
        gen.AddSigner(signer.Private, signer.Cert,
                      signatureAlgorithmOid, CmsSignedGenerator.DigestSha256,
                      (AttributeTable)null, (AttributeTable)null);
        gen.AddCertificates(
            CollectionUtilities.CreateStore(new List<X509Certificate> { signer.Cert }));

        byte[] cms = gen.Generate(new CmsProcessableByteArray(Data), false).GetEncoded();

        return CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);
    }
}
