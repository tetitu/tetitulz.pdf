// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.Sec;
using X509Name = Org.BouncyCastle.Asn1.X509.X509Name;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing.Cms;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// <see cref="CmsSignatureVerifier"/> at the edges <see cref="CmsHardeningTests"/> does not reach:
/// a signature value that fails to verify without throwing, the reported algorithm of a non-RSA
/// signer, the SHA-1 (v1) ESS signing-certificate attribute, and ESS hashes of the wrong length.
/// </summary>
[Collection(WeakAlgorithmPolicyCollection.Name)]
public class CmsVerifierBoundaryTests
{
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("content to be signed");

    // ---------------------------------------------------------------- the cryptographic verdict

    /// <summary>
    /// A signature value with one bit flipped verifies to <c>false</c> — BouncyCastle returns
    /// rather than throws, because the content digest still matches — and the result is invalid on
    /// both counts. The control is the same CMS unflipped.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignatureValueWithAFlippedBit_IsNotValid(bool flip)
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=Flip Signer");
        byte[] cms = Cms(signer.Private, signer.Cert, Attributes());

        if (flip)
        {
            byte[] signature = FirstSigner(cms).GetSignature();
            int at = IndexOf(cms, signature);
            Assert.True(at >= 0, "the signature value must appear in the encoding");
            cms[at + signature.Length - 1] ^= 0x01;
        }

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.Equal(!flip, result.IsCryptographicallyValid);
        Assert.Equal(!flip, result.IsValid);
    }

    /// <summary>
    /// The reported signature algorithm is the signer's own: <c>ecdsa-with-SHA256</c> for an EC
    /// key, not the RSA OID an RSA-only fixture would also read back.
    /// </summary>
    [Fact]
    public void EcSigner_ReportsItsOwnSignatureAlgorithm()
    {
        var keyGen = new ECKeyPairGenerator();
        keyGen.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, new SecureRandom()));
        var keys = keyGen.GenerateKeyPair();

        var name = new X509Name("CN=EC Signer");
        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ValueOf(4242));
        certGen.SetIssuerDN(name);
        certGen.SetSubjectDN(name);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keys.Public);
        var cert = certGen.Generate(new Asn1SignatureFactory("SHA256WithECDSA", keys.Private));

        byte[] cms = Cms(keys.Private, cert, Attributes());
        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("1.2.840.10045.4.3.2", result.SignatureAlgorithm);
    }

    // ---------------------------------------------------------------- ESS v1 (SHA-1)

    /// <summary>
    /// A v1 signing-certificate attribute names the signer's certificate by its SHA-1 hash
    /// (RFC 2634 §5.4) — the hash is SHA-1 by definition, never the CMS's own digest — and binds.
    /// </summary>
    [Fact]
    public void V1EssBinding_ToTheSigner_Verifies()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=V1 Signer");
        byte[] cms = Cms(signer.Private, signer.Cert, Attributes(V1(signer.Cert)));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.True(result.IsValid, result.Error);
    }

    /// <summary>The control: a v1 attribute naming another certificate is an identity substitution.</summary>
    [Fact]
    public void V1EssBinding_ToAnotherCertificate_IsRejected()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=V1 Signer");
        var other = SigningTestCrypto.SelfSignedLeaf("CN=Someone Else");
        byte[] cms = Cms(signer.Private, signer.Cert, Attributes(V1(other.Cert)));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.False(result.IsValid);
        Assert.Contains("signingCertificate (v1) attribute does not match", result.Error);
    }

    /// <summary>
    /// A v1 attribute that is no <c>SigningCertificate</c> fails closed, and the error carries the
    /// parser's own reason after the colon — not just the prefix.
    /// </summary>
    [Fact]
    public void UnparseableV1Attribute_FailsClosedAndSaysWhy()
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=V1 Signer");
        var garbage = new Org.BouncyCastle.Asn1.Cms.Attribute(
            PkcsObjectIdentifiers.IdAASigningCertificate, new DerSet(new DerUtf8String("not an ESS structure")));
        byte[] cms = Cms(signer.Private, signer.Cert, Attributes(garbage));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        const string prefix = "ESS signingCertificate (v1) attribute is present but unparseable: ";
        Assert.False(result.IsValid);
        Assert.StartsWith(prefix, result.Error);
        Assert.True(result.Error.Length > prefix.Length, "the parser's reason must follow the prefix");
    }

    // ---------------------------------------------------------------- hash length

    /// <summary>
    /// An ESS hash must be the whole digest. A prefix of the signer certificate's real SHA-256 —
    /// sixteen bytes, or none at all — names no certificate: compared only over its own length it
    /// would match the signer, and an empty one would match every certificate there is.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void V2EssHashShorterThanTheDigest_IsRejected(int length)
    {
        var signer = SigningTestCrypto.SelfSignedLeaf("CN=Prefix Signer");
        byte[] full = DigestUtilities.CalculateDigest("SHA-256", signer.Cert.GetEncoded());
        var attribute = new Org.BouncyCastle.Asn1.Cms.Attribute(
            PkcsObjectIdentifiers.IdAASigningCertificateV2,
            new DerSet(new SigningCertificateV2(new[] { new EssCertIDv2(full.Take(length).ToArray()) })));
        byte[] cms = Cms(signer.Private, signer.Cert, Attributes(attribute));

        var result = CmsSignatureVerifier.VerifyDetachedSignature(cms, Data);

        Assert.False(result.IsValid);
        Assert.Contains("signingCertificateV2 attribute does not match", result.Error);
    }

    // ---------------------------------------------------------------- builders

    private static Org.BouncyCastle.Asn1.Cms.Attribute V1(X509Certificate cert)
        => new(PkcsObjectIdentifiers.IdAASigningCertificate,
            new DerSet(new SigningCertificate(
                new EssCertID(DigestUtilities.CalculateDigest("SHA-1", cert.GetEncoded())))));

    private static AttributeTable Attributes(params Org.BouncyCastle.Asn1.Cms.Attribute[] attributes)
    {
        var vector = new Asn1EncodableVector();
        foreach (var a in attributes) vector.Add(a);
        return new AttributeTable(vector);
    }

    private static byte[] Cms(Org.BouncyCastle.Crypto.AsymmetricKeyParameter key, X509Certificate cert,
        AttributeTable signedAttributes)
    {
        var gen = new CmsSignedDataGenerator();
        gen.AddSigner(key, cert, CmsSignedGenerator.DigestSha256, signedAttributes, null);
        gen.AddCertificates(CollectionUtilities.CreateStore(new List<X509Certificate> { cert }));
        return gen.Generate(new CmsProcessableByteArray(Data), false).GetEncoded();
    }

    private static SignerInformation FirstSigner(byte[] cms)
        => new CmsSignedData(new CmsProcessableByteArray(Data), cms).GetSignerInfos().GetSigners().First();

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
