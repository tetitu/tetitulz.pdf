// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tetitulz.Pdf.Plugins.CertStoreSign;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Plugins.CertStoreSign.Tests;

/// <summary>
/// <see cref="CertStoreExternalSigner"/> — the in-box <see cref="IExternalSigner"/>.
/// </summary>
/// <remarks>
/// <para><b>Tested with a generated certificate, not a smart card.</b> That is why the signer takes
/// an <see cref="X509Certificate2"/> rather than reading the store itself: the CMS-building half —
/// all of the logic — is exercisable on any machine, while the part that touches ambient machine
/// state is isolated in <c>CertStoreLookup</c>. A signer that only accepted a thumbprint could not
/// be tested at all here.</para>
///
/// <para><b>The assertions decode the CMS.</b> `SignData` returns bytes, and bytes of the right
/// rough size are not evidence: a detached CMS over the wrong content, with the wrong digest, or
/// carrying no certificate is the same length as a correct one.</para>
/// </remarks>
public class CertStoreExternalSignerTests
{
    [Fact]
    public void SignDataProducesADetachedCmsThatVerifiesOverTheContent()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert) { Silent = true };

        byte[] content = Encoding.ASCII.GetBytes("the PDF byte range");
        byte[] cmsBytes = signer.SignData(content, "SHA-256");

        // Decoded and re-verified against the SAME content: this is what "detached" means, and it
        // is the only assertion that separates a correct signature from a well-formed one.
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(cmsBytes);
        cms.CheckSignature(verifySignatureOnly: true);

        Assert.Equal(1, cms.SignerInfos.Count);
        Assert.Equal(cert.Thumbprint, cms.SignerInfos[0].Certificate!.Thumbprint);
    }

    /// <summary>
    /// PAdES (EN 319 142-1 §6.3) requires the ESS signing-certificate-v2 attribute of a CMS written
    /// under <c>/ETSI.CAdES.detached</c>, and CMS requires content-type and message-digest beside any
    /// signed attribute. The certificate's SHA-256 hash is the last thing in the attribute's DER:
    /// <c>SEQUENCE { SEQUENCE { SEQUENCE { OCTET STRING hash } } }</c>, the DEFAULT algorithm omitted.
    /// </summary>
    [Fact]
    public void TheCmsCarriesTheEssSigningCertificateV2_AndTheCmsAttributesItNeeds()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert) { Silent = true };

        byte[] content = Encoding.ASCII.GetBytes("the PDF byte range");
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(signer.SignData(content, "SHA-256"));
        cms.CheckSignature(verifySignatureOnly: true);

        var attributes = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>().ToList();
        var oids = attributes.Select(a => a.Oid.Value).ToList();
        Assert.Contains("1.2.840.113549.1.9.16.2.47", oids);   // signing-certificate-v2
        Assert.Contains("1.2.840.113549.1.9.3", oids);         // content-type
        Assert.Contains("1.2.840.113549.1.9.4", oids);         // message-digest

        byte[] ess = attributes.Single(a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47").Values[0].RawData;
        byte[] hash = SHA256.HashData(cert.RawData);
        Assert.Equal(0x04, ess[ess.Length - hash.Length - 2]);
        Assert.Equal(hash.Length, ess[ess.Length - hash.Length - 1]);
        Assert.Equal(hash, ess.Skip(ess.Length - hash.Length).ToArray());
        Assert.DoesNotContain(new byte[] { 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01 }, Windows(ess, 9));
    }

    /// <summary>A digest other than the DEFAULT names its algorithm in the ESSCertIDv2, and hashes the certificate with it.</summary>
    [Fact]
    public void TheEssCertId_NamesADigestOtherThanSha256()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert) { Silent = true };

        byte[] content = Encoding.ASCII.GetBytes("the PDF byte range");
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(signer.SignData(content, "SHA-384"));

        byte[] ess = cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>()
            .Single(a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47").Values[0].RawData;
        byte[] hash = SHA384.HashData(cert.RawData);
        Assert.Equal(hash, ess.Skip(ess.Length - hash.Length).ToArray());
        Assert.Contains(new byte[] { 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x02 }, Windows(ess, 11));
    }

    private static IEnumerable<byte[]> Windows(byte[] bytes, int length)
    {
        for (int i = 0; i + length <= bytes.Length; i++) yield return bytes.Skip(i).Take(length).ToArray();
    }

    /// <summary>
    /// The control: the same CMS must NOT verify over different content. Without this, a signature
    /// computed over the wrong bytes would pass the test above.
    /// </summary>
    [Fact]
    public void TheSignatureDoesNotVerifyOverDifferentContent()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert) { Silent = true };

        byte[] cmsBytes = signer.SignData(Encoding.ASCII.GetBytes("original"), "SHA-256");

        var cms = new SignedCms(new ContentInfo(Encoding.ASCII.GetBytes("tampered")), detached: true);
        cms.Decode(cmsBytes);
        Assert.ThrowsAny<CryptographicException>(() => cms.CheckSignature(verifySignatureOnly: true));
    }

    /// <summary>The requested digest reaches the CMS, rather than the default being used silently.</summary>
    [Theory]
    [InlineData("SHA-256", "2.16.840.1.101.3.4.2.1")]
    [InlineData("SHA-384", "2.16.840.1.101.3.4.2.2")]
    [InlineData("SHA-512", "2.16.840.1.101.3.4.2.3")]
    public void TheRequestedDigestAlgorithmIsUsed(string name, string expectedOid)
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert) { Silent = true };

        byte[] content = Encoding.ASCII.GetBytes("content");
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(signer.SignData(content, name));

        Assert.Equal(expectedOid, cms.SignerInfos[0].DigestAlgorithm.Value);
    }

    /// <summary>An unknown digest name is refused rather than quietly falling back to SHA-256.</summary>
    [Fact]
    public void AnUnknownDigestAlgorithmIsRefused()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert) { Silent = true };

        Assert.Throws<ArgumentException>(
            () => signer.SignData(new byte[] { 1 }, "MD5"));
    }

    /// <summary>The signer's own certificate must be first, which the interface requires.</summary>
    [Fact]
    public void TheCertificateChainLeadsWithTheSignersOwnCertificate()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert);

        var chain = signer.GetCertificateChain();
        Assert.NotEmpty(chain);
        Assert.Equal(cert.RawData, chain[0]);
    }

    /// <summary>
    /// A certificate with no private key cannot sign, and saying so at construction beats failing
    /// later inside the signing call — a store lookup that returns the public certificate only is
    /// the ordinary way to arrive here.
    /// </summary>
    [Fact]
    public void ACertificateWithoutAPrivateKeyIsRefusedAtConstruction()
    {
        using X509Certificate2 withKey = SelfSigned("CN=CertStore Signer Test");
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);

        Assert.False(publicOnly.HasPrivateKey);
        Assert.Throws<ArgumentException>(() => new CertStoreExternalSigner(publicOnly));
    }

    /// <summary>
    /// <c>SignDigest</c> refuses rather than returning something that is not a CMS. The interface
    /// documents that <c>PdfSigner</c> never calls it.
    /// </summary>
    [Fact]
    public void SignDigestRefusesBecauseACmsCannotBeBuiltFromAHashAlone()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert);

        Assert.Throws<NotSupportedException>(() => signer.SignDigest(new byte[32], "SHA-256"));
    }

    /// <summary>The estimate must cover what is actually produced, or the reservation fails.</summary>
    [Fact]
    public void TheEstimatedSizeCoversTheRealSignature()
    {
        using X509Certificate2 cert = SelfSigned("CN=CertStore Signer Test");
        using var signer = new CertStoreExternalSigner(cert) { Silent = true };

        byte[] actual = signer.SignData(Encoding.ASCII.GetBytes("content"), "SHA-256");
        Assert.True(signer.EstimatedSignatureSize > actual.Length,
            $"estimate {signer.EstimatedSignatureSize} does not cover {actual.Length} actual bytes");
    }

    /// <summary>
    /// What a picker offers is what the verifier accepts as a signer: a certificate with no
    /// extended key usage, or one naming a document purpose, and a key usage that signs; not one
    /// issued for a web server — what a debugging proxy fills a personal store with — nor one
    /// whose key usage is encipherment alone.
    /// </summary>
    [Theory]
    [InlineData(null, X509KeyUsageFlags.DigitalSignature, true)]
    [InlineData("1.3.6.1.5.5.7.3.4", X509KeyUsageFlags.DigitalSignature, true)]     // e-mail protection
    [InlineData("1.3.6.1.5.5.7.3.36", X509KeyUsageFlags.NonRepudiation, true)]      // document signing
    [InlineData("1.3.6.1.5.5.7.3.1", X509KeyUsageFlags.DigitalSignature, false)]    // server authentication
    [InlineData("1.3.6.1.5.5.7.3.2", X509KeyUsageFlags.DigitalSignature, false)]    // client authentication
    [InlineData(null, X509KeyUsageFlags.KeyEncipherment, false)]
    public void ACertificateIsOfferedForSigning_OnlyWhenItsExtensionsPermitADocumentSignature(
        string? purpose, X509KeyUsageFlags usage, bool offered)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Picker Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        if (purpose != null)
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(purpose) }, false));
        using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(offered, CertStoreLookup.PermitsDocumentSigning(cert));
    }

    /// <summary>A certificate with no key usage and no extended key usage restricts nothing, and is offered.</summary>
    [Fact]
    public void ACertificateWithNeitherExtension_IsOffered()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Unrestricted", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(CertStoreLookup.PermitsDocumentSigning(cert));
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        X509Certificate2 cert = req.CreateSelfSigned(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // Round-tripped through a PFX so the private key is associated the way a store-loaded
        // certificate's is; CreateSelfSigned's handle is not always usable by CmsSigner directly.
        byte[] pfx = cert.Export(X509ContentType.Pkcs12);
        cert.Dispose();
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}