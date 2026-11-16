// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// End-to-end sign+verify round trips for TS 32002 (ISO 32000-2 extension: additional elliptic
/// curve families for digital signatures) — NIST P-curves, a Brainpool curve, and EdDSA
/// (Ed25519/Ed448). <see cref="SignatureAlgorithm"/> and <see cref="SignatureAlgorithmOids"/>
/// already name all of these; what had not been verified is that a real key on each curve
/// actually signs and verifies through the full <see cref="PdfSigner"/>/
/// <see cref="PdfSignatureVerifier"/> pipeline, not merely that the OID tables recognise the
/// algorithm name.
/// </summary>
public partial class EcCurveSignatureRoundTripTests
{
    [Theory]
    [InlineData("P-256", "SHA256withECDSA", SignatureAlgorithm.EcdsaSha256)]
    [InlineData("P-384", "SHA384withECDSA", SignatureAlgorithm.EcdsaSha384)]
    [InlineData("P-521", "SHA512withECDSA", SignatureAlgorithm.EcdsaSha512)]
    [InlineData("brainpoolP256r1", "SHA256withECDSA", SignatureAlgorithm.EcdsaSha256)]
    [InlineData("brainpoolP384r1", "SHA384withECDSA", SignatureAlgorithm.EcdsaSha384)]
    public void Sign_ThenVerify_EcdsaOnCurve_IsIntegrityValid(
        string curveName, string certSignatureDigest, SignatureAlgorithm algorithm)
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf($"ECDSA on {curveName}");
        var (pfxBytes, password) =
            TestCertificateGenerator.GenerateEcTestCertificate(curveName, certSignatureDigest);

        AssertSignAndVerify(pdfBytes, pfxBytes, password, algorithm);
    }

    [Fact]
    public void Sign_ThenVerify_Ed25519_IsIntegrityValid()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Ed25519 test");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateEdDsaTestCertificate(ed448: false);

        AssertSignAndVerify(pdfBytes, pfxBytes, password, SignatureAlgorithm.Ed25519);
    }

    /// <summary>
    /// Ed448 signs and verifies through the full pipeline. It was pinned as failing until
    /// 2026-10-18 — BouncyCastle 2.7.0's generator cannot resolve the SHAKE256 digest its own Ed448
    /// factory names — and is now signed and verified by RFC 8419 §3.1 written out
    /// (<c>CmsSignatureGenerator.Ed448SignedData</c>, <c>CmsSignatureVerifier.VerifyEd448</c>).
    /// </summary>
    [Fact]
    public void Sign_ThenVerify_Ed448_IsIntegrityValid()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Ed448 test");
        var (pfxBytes, password) = TestCertificateGenerator.GenerateEdDsaTestCertificate(ed448: true);

        AssertSignAndVerify(pdfBytes, pfxBytes, password, SignatureAlgorithm.Ed448);
    }

    /// <summary>
    /// The Ed448 CMS is a PDF's: the digest algorithm the fixed-length id-shake256 with no parameters
    /// (ISO/TS 32002 Table 4, ISO/TS 32001 — not RFC 8419 §3.1's id-shake256-len), the signature
    /// algorithm id-Ed448 with no parameters, and a message digest of 64 bytes equal to SHAKE256 of
    /// the signed byte ranges.
    /// </summary>
    [Fact]
    public void Ed448_Cms_IsIsoTs32002s()
    {
        var (cms, content, _) = SignEd448("Ed448 structure");
        var signer = System.Linq.Enumerable.Single(
            new Org.BouncyCastle.Cms.CmsSignedData(cms).GetSignerInfos().GetSigners());

        Assert.Equal("2.16.840.1.101.3.4.2.12", signer.DigestAlgorithmID.Algorithm.Id);
        Assert.Null(signer.DigestAlgorithmID.Parameters);
        Assert.Equal("1.3.101.113", signer.SignatureAlgorithm.Algorithm.Id);
        Assert.Null(signer.SignatureAlgorithm.Parameters);

        var shake = new Org.BouncyCastle.Crypto.Digests.ShakeDigest(256);
        shake.BlockUpdate(content, 0, content.Length);
        var expected = new byte[64];
        shake.OutputFinal(expected, 0, 64);
        var attribute = signer.SignedAttributes[Org.BouncyCastle.Asn1.Cms.CmsAttributes.MessageDigest];
        Assert.Equal(expected, ((Org.BouncyCastle.Asn1.Asn1OctetString)attribute.AttrValues[0]).GetOctets());
    }

    /// <summary>
    /// The Ed448 CMS is written by hand rather than by BouncyCastle's generator, and claims its time
    /// by the same rule: no signing-time attribute under PAdES (ETSI EN 319 142-1 §6.3, Table 1), the
    /// instant <c>/M</c> claims under <c>adbe.pkcs7.detached</c>.
    /// </summary>
    [Fact]
    public void Ed448_SigningTimeAttribute_IsAbsentUnderPades_AndTheClaimedInstantOtherwise()
    {
        var claimed = new System.DateTime(2025, 11, 2, 9, 30, 15, System.DateTimeKind.Utc);
        var signingTime = Org.BouncyCastle.Asn1.Cms.CmsAttributes.SigningTime;

        var (padesCms, _, padesSigned) = SignEd448("Ed448 PAdES time", pades: true, signingTime: claimed);
        var pades = System.Linq.Enumerable.Single(new Org.BouncyCastle.Cms.CmsSignedData(padesCms).GetSignerInfos().GetSigners());
        Assert.NotNull(pades.SignedAttributes[Org.BouncyCastle.Asn1.Cms.CmsAttributes.MessageDigest]);
        Assert.Null(pades.SignedAttributes[signingTime]);
        using (var doc = PdfDocument.Open(new MemoryStream(padesSigned)))
            Assert.True(Assert.Single(new PdfSignatureVerifier().VerifyAll(doc)).IsIntegrityValid);

        var (pkcs7Cms, _, _) = SignEd448("Ed448 PKCS#7 time", pades: false, signingTime: claimed);
        var pkcs7 = System.Linq.Enumerable.Single(new Org.BouncyCastle.Cms.CmsSignedData(pkcs7Cms).GetSignerInfos().GetSigners());
        Assert.Equal(claimed, Org.BouncyCastle.Asn1.Cms.Time.GetInstance(pkcs7.SignedAttributes[signingTime].AttrValues[0]).ToDateTime());
    }

    /// <summary>
    /// A signature naming any SHAKE256 identifier but id-shake256 is refused (ISO/TS 32001 forbids the
    /// others): the digest algorithm patched to RFC 8419's id-shake256-len in <c>/Contents</c> — it is
    /// outside the signed attributes, so the Ed448 signature itself still holds.
    /// </summary>
    [Fact]
    public void Ed448_ADigestOtherThanIdShake256_IsRefused()
    {
        var (_, _, signed) = SignEd448("Ed448 digest identifier");
        string latin = System.Text.Encoding.Latin1.GetString(signed);
        const string Shake256 = "060960864801650304020C", Shake256Len = "06096086480165030402" + "12";
        int found = 0;
        for (int at = latin.IndexOf(Shake256, System.StringComparison.OrdinalIgnoreCase); at >= 0;
             at = latin.IndexOf(Shake256, at + 1, System.StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < Shake256Len.Length; i++) signed[at + i] = (byte)Shake256Len[i];
            found++;
        }
        Assert.Equal(2, found);   // SignedData.digestAlgorithms and the SignerInfo's

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        Assert.False(Assert.Single(new PdfSignatureVerifier().VerifyAll(doc)).IsIntegrityValid);
    }

    /// <summary>
    /// An Ed448 SignerInfo whose signed attributes carry no content type (RFC 5652 §11.1 requires one
    /// beside the message digest) is refused — though validly signed — and the same SignerInfo with it
    /// verifies: the attribute is checked, not only the signature.
    /// </summary>
    [Fact]
    public void Ed448_SignedAttributesWithoutAContentType_AreRefused()
    {
        var (pfxBytes, password) = TestCertificateGenerator.GenerateEdDsaTestCertificate(ed448: true);
        var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        store.Load(new MemoryStream(pfxBytes), password.ToCharArray());
        string alias = System.Linq.Enumerable.First(store.Aliases, store.IsKeyEntry);
        var key = store.GetKey(alias).Key;
        var certificate = store.GetCertificate(alias).Certificate;
        byte[] content = System.Text.Encoding.ASCII.GetBytes("signed bytes");

        Org.BouncyCastle.Cms.SignerInformation Build(bool withContentType)
        {
            var shake = new Org.BouncyCastle.Crypto.Digests.ShakeDigest(256);
            shake.BlockUpdate(content, 0, content.Length);
            var digest = new byte[64];
            shake.OutputFinal(digest, 0, 64);
            var attributes = new Org.BouncyCastle.Asn1.Asn1EncodableVector
            {
                new Org.BouncyCastle.Asn1.Cms.Attribute(Org.BouncyCastle.Asn1.Cms.CmsAttributes.MessageDigest,
                    new Org.BouncyCastle.Asn1.DerSet(new Org.BouncyCastle.Asn1.DerOctetString(digest))),
            };
            if (withContentType)
                attributes.Add(new Org.BouncyCastle.Asn1.Cms.Attribute(Org.BouncyCastle.Asn1.Cms.CmsAttributes.ContentType,
                    new Org.BouncyCastle.Asn1.DerSet(Org.BouncyCastle.Asn1.Cms.CmsObjectIdentifiers.Data)));
            var signedAttributes = new Org.BouncyCastle.Asn1.DerSet(attributes);
            byte[] toSign = signedAttributes.GetEncoded(Org.BouncyCastle.Asn1.Asn1Encodable.Der);
            var signer = new Org.BouncyCastle.Crypto.Signers.Ed448Signer(System.Array.Empty<byte>());
            signer.Init(true, key);
            signer.BlockUpdate(toSign, 0, toSign.Length);
            var digestAlgorithm = new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(new Org.BouncyCastle.Asn1.DerObjectIdentifier("2.16.840.1.101.3.4.2.12"));
            var info = new Org.BouncyCastle.Asn1.Cms.SignerInfo(
                new Org.BouncyCastle.Asn1.Cms.SignerIdentifier(new Org.BouncyCastle.Asn1.Cms.IssuerAndSerialNumber(certificate.IssuerDN, certificate.SerialNumber)),
                digestAlgorithm, signedAttributes,
                new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(new Org.BouncyCastle.Asn1.DerObjectIdentifier("1.3.101.113")),
                new Org.BouncyCastle.Asn1.DerOctetString(signer.GenerateSignature()), null);
            var signedData = new Org.BouncyCastle.Asn1.Cms.SignedData(new Org.BouncyCastle.Asn1.DerSet(digestAlgorithm),
                new Org.BouncyCastle.Asn1.Cms.ContentInfo(Org.BouncyCastle.Asn1.Cms.CmsObjectIdentifiers.Data, null),
                new Org.BouncyCastle.Asn1.DerSet(certificate.CertificateStructure), null, new Org.BouncyCastle.Asn1.DerSet(info));
            var cms = new Org.BouncyCastle.Cms.CmsSignedData(new Org.BouncyCastle.Asn1.Cms.ContentInfo(Org.BouncyCastle.Asn1.Cms.CmsObjectIdentifiers.SignedData, signedData));
            return System.Linq.Enumerable.Single(cms.GetSignerInfos().GetSigners());
        }

        Assert.True(Tetitulz.Pdf.Signing.Cms.CmsSignatureVerifier.VerifyEd448(Build(withContentType: true), certificate, content));
        var ex = Assert.Throws<Org.BouncyCastle.Cms.CmsException>(
            () => Tetitulz.Pdf.Signing.Cms.CmsSignatureVerifier.VerifyEd448(Build(withContentType: false), certificate, content));
        Assert.Contains("content type", ex.Message, System.StringComparison.Ordinal);
    }

    /// <summary>A byte changed inside the signed ranges fails the Ed448 signature — the control that the check reads the document.</summary>
    [Fact]
    public void Ed448_ATamperedDocument_FailsIntegrity()
    {
        var (_, _, signed) = SignEd448("Ed448 tamper");
        string text = System.Text.Encoding.Latin1.GetString(signed);
        int at = text.IndexOf("Ed448 tamper", System.StringComparison.Ordinal);
        Assert.True(at > 0, "the fixture's text is not in the file uncompressed");
        signed[at] = (byte)'X';

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var result = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc));
        Assert.False(result.IsIntegrityValid);
    }

    /// <summary>Signs a minimal PDF with Ed448 and returns the CMS, the signed byte ranges' bytes and the signed file.</summary>
    private static (byte[] Cms, byte[] Content, byte[] Signed) SignEd448(
        string text, bool pades = false, System.DateTime? signingTime = null)
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf(text);
        var (pfxBytes, password) = TestCertificateGenerator.GenerateEdDsaTestCertificate(ed448: true);
        byte[] signed;
        using (var input = new MemoryStream(pdfBytes))
        using (var output = new MemoryStream())
        using (var certStream = new MemoryStream(pfxBytes))
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = certStream,
                CertificatePassword = password,
                SignatureFieldName = "Signature1",
                UseEtsiPadesSubFilter = pades,
                SigningTime = signingTime,
            }.UseAlgorithm(SignatureAlgorithm.Ed448));
            signed = output.ToArray();
        }

        string latin = System.Text.Encoding.Latin1.GetString(signed);
        var range = System.Text.RegularExpressions.Regex.Match(latin, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(range.Success, "no /ByteRange");
        int a = int.Parse(range.Groups[1].Value), b = int.Parse(range.Groups[2].Value);
        int c = int.Parse(range.Groups[3].Value), d = int.Parse(range.Groups[4].Value);
        var content = new byte[b + d];
        System.Buffer.BlockCopy(signed, a, content, 0, b);
        System.Buffer.BlockCopy(signed, c, content, b, d);

        string hex = latin.Substring(a + b, c - (a + b)).Trim('<', '>', ' ');
        byte[] padded = System.Convert.FromHexString(hex);
        using var asn1 = new Org.BouncyCastle.Asn1.Asn1InputStream(padded);   // reads to the DER's end; the zero padding is left
        var parsed = asn1.ReadObject();
        return (parsed.GetDerEncoded(), content, signed);
    }

    private static void AssertSignAndVerify(
        byte[] pdfBytes, byte[] pfxBytes, string password, SignatureAlgorithm algorithm)
    {
        byte[] signedPdfBytes;
        using (var inputStream = new MemoryStream(pdfBytes))
        using (var outputStream = new MemoryStream())
        using (var certStream = new MemoryStream(pfxBytes))
        {
            var parameters = new SignatureParameters
            {
                CertificateStream = certStream,
                CertificatePassword = password,
                Reason = "EC curve round trip",
                SignatureFieldName = "Signature1",
            }.UseAlgorithm(algorithm);

            new PdfSigner().Sign(inputStream, outputStream, parameters);
            signedPdfBytes = outputStream.ToArray();
        }

        using var signedStream = new MemoryStream(signedPdfBytes);
        using var doc = PdfDocument.Open(signedStream);

        var verifier = new PdfSignatureVerifier();
        var results = verifier.VerifyAll(doc);

        var result = Assert.Single(results);
        Assert.True(result.IsIntegrityValid,
            $"Signature integrity check failed for {algorithm}. " +
            $"Errors: {string.Join(", ", result.ValidationErrors)}");
    }
}
