// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// CMS-level verification hardening: weak-algorithm detection that the OID switch alone could not
/// see (RSASSA-PSS carries its hash in the algorithm parameters), key-size floors, SHA-3 support
/// in the ESS binding, fail-closed handling of a malformed ESS attribute, and rejection of the
/// non-standard multi-signer CMS whose first SignerInfo silently defines the reported identity.
/// </summary>
[Collection(WeakAlgorithmPolicyCollection.Name)]
public class CmsHardeningTests
{
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("content to be signed");

    // ---------------------------------------------------------------- weak algorithms

    /// <summary>
    /// A SHA-1 digest is refused by the default policy, and the refusal does NOT claim the bytes
    /// failed to verify.
    /// </summary>
    /// <remarks>
    /// The <c>IsCryptographicallyValid</c> assertion is the substantive half: policy and integrity
    /// are separate answers, and collapsing them would leave a caller unable to tell a refused
    /// algorithm from a modified document.
    /// </remarks>
    [Fact]
    public void Sha1Digest_IsRefusedByDefault_ButTheBytesStillVerify()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=SHA1 Signer");
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha1);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.False(result.IsValid, "SHA-1 must be refused by the default policy.");
        Assert.True(result.IsCryptographicallyValid,
            "The signature bytes DID verify; only policy refused them.");
        Assert.True(result.WeakAlgorithm);
        Assert.Contains("SHA-1", result.WeakAlgorithmReason);
    }

    [Fact]
    public void Sha1Digest_IsRejectedWhenPolicySaysSo()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=SHA1 Signer");
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha1);

        var options = new SignatureVerificationOptions { RejectWeakAlgorithms = true };
        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data, options);

        Assert.False(result.IsValid);
        Assert.Contains("Weak signature algorithm rejected by policy", result.Error);
    }

    /// <summary>
    /// A PSS signature whose <c>digestAlgorithm</c> is also SHA-1 is flagged — <b>by the digest
    /// check</b>, not by the PSS parameter inspection.
    /// </summary>
    /// <remarks>
    /// <para>The fixture passes <c>DigestSha1</c>, so <c>DetectWeakAlgorithm</c> returns at its digest
    /// check one line above the switch and <c>DetectWeakPssHash</c> is never entered — this test says
    /// nothing about the PSS arm.</para>
    /// <para>That PSS parameters naming a broken hash are detected is
    /// <c>WeakSignatureAlgorithmOidTests</c>'s claim, which needs ASN.1 surgery to build the fixture
    /// because <c>CmsSignedDataGenerator</c> derives the PSS parameters from the digest and so cannot
    /// produce the combination on its own.</para>
    /// </remarks>
    [Fact]
    public void RsaPssWithASha1Digest_IsFlaggedByTheDigestCheck()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=PSS Signer");
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha1, "SHA1WITHRSAANDMGF1");

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.WeakAlgorithm, "PSS over SHA-1 must be flagged.");
        Assert.NotNull(result.WeakAlgorithmReason);
    }

    [Fact]
    public void RsaPssOverSha256_IsNotFlagged()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=PSS Signer");
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256, "SHA256WITHRSAANDMGF1");

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.IsValid, result.Error);
        Assert.False(result.WeakAlgorithm, result.WeakAlgorithmReason);
    }

    [Fact]
    public void Sha256Signature_IsNotFlagged()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf();
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.IsValid, result.Error);
        Assert.False(result.WeakAlgorithm);
    }

    // ---------------------------------------------------------------- key sizes

    [Fact]
    public void UndersizedRsaKey_IsRefusedByDefault_ButTheBytesStillVerify()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=Weak Key", keyBits: 1024);
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.WeakAlgorithm, "A 1024-bit RSA key is below the 2048-bit floor.");
        Assert.Contains("1024", result.WeakAlgorithmReason);
        Assert.False(result.IsValid, "An undersized key is refused by the default policy.");
        Assert.True(result.IsCryptographicallyValid,
            "The signature bytes DID verify; only policy refused the key size.");
    }

    [Fact]
    public void UndersizedRsaKey_IsRejectedUnderStrictPolicy()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=Weak Key", keyBits: 1024);
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(
            cms, Data, new SignatureVerificationOptions { RejectWeakAlgorithms = true });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void KeySizeCheck_CanBeDisabled()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=Weak Key", keyBits: 1024);
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(
            cms, Data, new SignatureVerificationOptions { MinimumRsaKeyBits = 0 });

        Assert.False(result.WeakAlgorithm);
    }

    // ---------------------------------------------------------------- ESS binding

    [Fact]
    public void ValidEssBinding_Verifies()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf();
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256, essHash: "SHA-256");

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.IsValid, result.Error);
    }

    /// <summary>
    /// The generator can emit SHA-3 digests, but the verifier's OID→name map stopped at SHA-512.
    /// An ESS attribute hashed with SHA3-256 therefore fell back to SHA-256, mismatched, and the
    /// signature was rejected with a bogus "identity substitution" error.
    /// </summary>
    [Fact]
    public void Sha3EssBinding_IsNotMisreadAsIdentitySubstitution()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf();
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256, essHash: "SHA3-256");

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.IsValid,
            "A SHA-3 ESS hash must verify, not be reported as substitution: " + result.Error);
    }

    [Fact]
    public void MismatchedEssBinding_IsRejected()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf();
        var other = SigningTestCrypto.SelfSignedLeaf("CN=Someone Else");
        // ESS names a DIFFERENT certificate than the one that signed.
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256, essCertOverride: other.Cert);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.False(result.IsValid);
        Assert.Contains("does not match the signer certificate", result.Error);
    }

    /// <summary>
    /// Fail-closed: the old code caught every parse exception and returned "binding OK", so an
    /// attacker could defeat the whole substitution check by making the attribute malformed rather
    /// than merely wrong.
    /// </summary>
    [Fact]
    public void UnparseableEssAttribute_FailsClosed()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf();
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256, garbageEss: true);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.False(result.IsValid, "A malformed ESS attribute must not be treated as absent.");
        Assert.Contains("ESS", result.Error);
    }

    [Fact]
    public void NoEssAttribute_IsStillAccepted()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf();
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha256, noEss: true);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.IsValid, "ESS is optional; its absence must not fail a good signature.");
    }

    // ---------------------------------------------------------------- multi-signer

    /// <summary>
    /// PDF /Contents is single-signer per ISO 32000 §12.8. With two SignerInfos only the first is
    /// examined, so an attacker who prepends a decoy silently swaps the identity shown to the user.
    /// </summary>
    [Fact]
    public void MultiSignerCms_IsCountedSoCallersCanRefuseIt()
    {
        var a = SigningTestCrypto.SelfSignedLeaf("CN=Signer A");
        var b = SigningTestCrypto.SelfSignedLeaf("CN=Signer B");
        byte[] cms = TwoSignerCms(a, b);

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);
        Assert.True(result.SignerCount > 1, "The extra SignerInfo must be counted.");
    }

    // ---------------------------------------------------------------- policy isolation

    /// <summary>
    /// The legacy static is process-wide and races across concurrent verifications. An explicit
    /// options object must win outright so one caller cannot be silently governed by another's
    /// policy.
    /// </summary>
    [Fact]
    public void ExplicitOptions_OverrideTheLegacyStatic()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=SHA1 Signer");
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha1);

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            // Static says "reject"; the per-call options say "flag only" and must win.
            CmsSignatureVerifier.RejectWeakAlgorithms = true;
            var lenient = CmsSignatureVerifier.VerifyDetachedSignature(
                cms, Data, new SignatureVerificationOptions { RejectWeakAlgorithms = false });
            Assert.True(lenient.IsValid, "Per-call options must override the process-wide static.");

            // Static says "allow"; the per-call options say "reject" and must win.
            CmsSignatureVerifier.RejectWeakAlgorithms = false;
            var strict = CmsSignatureVerifier.VerifyDetachedSignature(
                cms, Data, new SignatureVerificationOptions { RejectWeakAlgorithms = true });
            Assert.False(strict.IsValid);
        }
        finally { CmsSignatureVerifier.RejectWeakAlgorithms = saved; }
    }

    [Fact]
    public void LegacyStatic_StillAppliesWhenNoOptionsPassed()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=SHA1 Signer");
        byte[] cms = Cms(signer, CmsSignedGenerator.DigestSha1);

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = true;
            Assert.False(CmsSignatureVerifier.VerifyDetachedSignature(cms, Data).IsValid);
        }
        finally { CmsSignatureVerifier.RejectWeakAlgorithms = saved; }
    }

    // ---------------------------------------------------------------- nested signature fields

    /// <summary>
    /// AcroForm fields form a tree. Iterating only the top level of /Fields, the validator would
    /// report "No signature field found" for a signature nested under a parent field node on a
    /// perfectly valid document; it shares the verifier's recursive lookup.
    /// </summary>
    [Fact]
    public void SignatureNestedUnderAParentField_IsFound()
    {
        byte[] pdf = PdfWithNestedSignatureField();

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var results = new PdfSignatureVerifier().VerifyAll(doc);

        Assert.NotEmpty(results);

        using var ms2 = new MemoryStream(pdf);
        var validated = PdfSignatureValidator.Validate(ms2);
        Assert.True(validated.HasSignature,
            "A nested signature field must be discovered by the validator too.");
    }

    // ---------------------------------------------------------------- CMS builders

    private static byte[] Cms(
        SigningTestCrypto.Issued signer,
        string digestOid,
        string signatureAlgorithm = null,
        string essHash = "SHA-256",
        X509Certificate essCertOverride = null,
        bool garbageEss = false,
        bool noEss = false)
    {
        var gen = new CmsSignedDataGenerator();
        AttributeTable attrs = noEss ? null : EssAttributes(
            essCertOverride ?? signer.Cert, essHash, garbageEss);

        if (signatureAlgorithm == null)
            gen.AddSigner(signer.Private, signer.Cert, digestOid, attrs, null);
        else
            gen.AddSigner(signer.Private, signer.Cert,
                SignatureAlgorithmOid(signatureAlgorithm), digestOid, attrs, null);

        gen.AddCertificates(CollectionUtilities.CreateStore(new List<X509Certificate> { signer.Cert }));
        return gen.Generate(new CmsProcessableByteArray(Data), false).GetEncoded();
    }

    private static string SignatureAlgorithmOid(string name) => name switch
    {
        "SHA1WITHRSAANDMGF1"   => "1.2.840.113549.1.1.10", // RSASSA-PSS
        "SHA256WITHRSAANDMGF1" => "1.2.840.113549.1.1.10",
        _ => throw new ArgumentException("Unmapped algorithm " + name),
    };

    private static AttributeTable EssAttributes(X509Certificate cert, string hashName, bool garbage)
    {
        if (garbage)
        {
            // A signingCertificateV2 attribute whose value is not a SigningCertificateV2 at all.
            return new AttributeTable(new Asn1EncodableVector
            {
                new Org.BouncyCastle.Asn1.Cms.Attribute(
                    PkcsObjectIdentifiers.IdAASigningCertificateV2,
                    new DerSet(new DerUtf8String("not an ESS structure"))),
            });
        }

        byte[] hash = DigestUtilities.CalculateDigest(hashName, cert.GetEncoded());
        var essId = hashName == "SHA-256"
            ? new EssCertIDv2(hash)
            : new EssCertIDv2(new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(
                new DerObjectIdentifier(DigestOid(hashName))), hash);

        return new AttributeTable(new Asn1EncodableVector
        {
            new Org.BouncyCastle.Asn1.Cms.Attribute(
                PkcsObjectIdentifiers.IdAASigningCertificateV2,
                new DerSet(new SigningCertificateV2(new[] { essId }))),
        });
    }

    private static string DigestOid(string name) => name switch
    {
        "SHA-256"  => "2.16.840.1.101.3.4.2.1",
        "SHA-384"  => "2.16.840.1.101.3.4.2.2",
        "SHA-512"  => "2.16.840.1.101.3.4.2.3",
        "SHA3-256" => "2.16.840.1.101.3.4.2.8",
        "SHA3-384" => "2.16.840.1.101.3.4.2.9",
        "SHA3-512" => "2.16.840.1.101.3.4.2.10",
        _ => throw new ArgumentException("Unmapped digest " + name),
    };

    /// <summary>A CMS blob carrying two SignerInfos — non-standard for a PDF signature.</summary>
    private static byte[] TwoSignerCms(SigningTestCrypto.Issued a, SigningTestCrypto.Issued b)
    {
        var gen = new CmsSignedDataGenerator();
        gen.AddSigner(a.Private, a.Cert, CmsSignedGenerator.DigestSha256, EssAttributes(a.Cert, "SHA-256", false), null);
        gen.AddSigner(b.Private, b.Cert, CmsSignedGenerator.DigestSha256, EssAttributes(b.Cert, "SHA-256", false), null);
        gen.AddCertificates(CollectionUtilities.CreateStore(new List<X509Certificate> { a.Cert, b.Cert }));
        return gen.Generate(new CmsProcessableByteArray(Data), false).GetEncoded();
    }

    // ---------------------------------------------------------------- PDF builder

    /// <summary>
    /// A PDF whose AcroForm /Fields holds a parent node, with the actual /FT /Sig field as its
    /// /Kids entry — the shape a flat top-level scan misses.
    /// </summary>
    private static byte[] PdfWithNestedSignatureField()
    {
        var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Header(int n) { offsets[n] = ms.Length; Ascii($"{n} 0 obj\n"); }

        Ascii("%PDF-1.7\n");
        Header(1); Ascii("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>\nendobj\n");
        Header(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Header(3); Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Header(4); Ascii("<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 10] /Contents <00> >>\nendobj\n");
        Header(5); Ascii("<< /T (Parent) /Kids [6 0 R] >>\nendobj\n");
        Header(6); Ascii("<< /FT /Sig /T (NestedSig) /Parent 5 0 R /V 4 0 R >>\nendobj\n");

        long xref = ms.Length;
        Ascii("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i < 7; i++) Ascii($"{offsets[i]:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
