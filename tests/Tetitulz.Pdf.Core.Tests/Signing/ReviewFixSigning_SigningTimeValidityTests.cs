// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing;
using Xunit;
using BcAttribute = Org.BouncyCastle.Asn1.Cms.Attribute;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Review fix (finding #2): certificate validity must NOT be assessed at the unauthenticated
/// Pkcs9AtSigningTime. That attribute is signed by the signer but vouched for by no trusted clock,
/// so a holder of an EXPIRED certificate could self-date a signature back into the cert's old
/// validity window and be reported Trusted. With no verified RFC 3161 timestamp present, validity
/// is now assessed at "now" and an expired cert resolves to Invalid regardless of the claimed time.
/// </summary>
public class ReviewFixSigning_SigningTimeValidityTests
{
    [Fact]
    public void ExpiredCert_WithSigningTimeInsideWindow_NoTimestamp_IsNotTrusted()
    {
        // Cert validity window is entirely in the past.
        DateTime notBefore = DateTime.UtcNow.AddYears(-2);
        DateTime notAfter  = DateTime.UtcNow.AddYears(-1);
        var (cert, key) = SelfSigned("CN=Expired Signer", notBefore, notAfter,
            KeyUsage.DigitalSignature);

        // The SignerInfo claims a SigningTime INSIDE the expired window (cert was valid then).
        DateTime claimed = notBefore.AddMonths(1);

        byte[] pdf = BuildSignedPdf(cert, key, claimed);

        using var ms = new MemoryStream(pdf);
        // Even offered as a trust anchor, the expired cert must not be Trusted.
        var r = PdfSignatureValidator.Validate(ms, new[] { cert });

        Assert.True(r.IsValid, "CMS integrity should still verify: " + r.ErrorMessage);
        Assert.Equal(PdfSignatureChainStatus.Invalid, r.ChainStatus);
        // The unauthenticated SigningTime is still surfaced informationally.
        Assert.NotNull(r.SignedAt);
        Assert.Contains(r.Warnings, w =>
            w.IndexOf("validity period", StringComparison.OrdinalIgnoreCase) >= 0
            || w.IndexOf("current time", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Fact]
    public void CurrentlyValidCert_WithSigningTime_IsTrusted()
    {
        // Control: a presently-valid cert with a SigningTime attribute still validates Trusted,
        // proving the check does not break the normal case.
        var (cert, key) = SelfSigned("CN=Valid Signer",
            DateTime.UtcNow.AddYears(-1), DateTime.UtcNow.AddYears(1), KeyUsage.DigitalSignature);

        byte[] pdf = BuildSignedPdf(cert, key, DateTime.UtcNow.AddMinutes(-5));

        using var ms = new MemoryStream(pdf);
        var r = PdfSignatureValidator.Validate(ms, new[] { cert });

        Assert.True(r.IsValid, r.ErrorMessage);
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
    }

    // ---------------------------------------------------------------- crypto

    private static (X509Certificate cert, AsymmetricKeyParameter key) SelfSigned(
        string cn, DateTime notBefore, DateTime notAfter, int keyUsage)
    {
        var random = new SecureRandom();
        var kp = Helpers.SigningTestCrypto.Keys();

        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        var dn = new X509Name(cn);
        gen.SetSubjectDN(dn);
        gen.SetIssuerDN(dn);
        gen.SetNotBefore(notBefore);
        gen.SetNotAfter(notAfter);
        gen.SetPublicKey(kp.Public);
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(keyUsage));
        var cert = gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", kp.Private, random));
        return (cert, kp.Private);
    }

    // Builds a detached CMS over `content` whose SignerInfo carries a Pkcs9AtSigningTime signed
    // attribute equal to `signingTime`.
    private static byte[] SignCms(byte[] content, X509Certificate cert,
        AsymmetricKeyParameter key, DateTime signingTime)
    {
        var signedAttrs = new Org.BouncyCastle.Asn1.Cms.AttributeTable(new Asn1EncodableVector
        {
            new BcAttribute(
                PkcsObjectIdentifiers.Pkcs9AtSigningTime,
                new DerSet(new Org.BouncyCastle.Asn1.Cms.Time(signingTime.ToUniversalTime())))
        });

        var gen = new CmsSignedDataGenerator();
        gen.AddSigner(key, cert, CmsSignedDataGenerator.DigestSha256, signedAttrs, null);
        var certStore = CollectionUtilities.CreateStore(new List<X509Certificate> { cert });
        gen.AddCertificates(certStore);

        var data = new CmsProcessableByteArray(content);
        var signed = gen.Generate(data, false); // detached
        return signed.GetEncoded();
    }

    // Builds a minimal PDF with a single signature field, signing everything outside the /Contents
    // placeholder. The placeholder is sized to EXACTLY the real signature length so the ByteRange
    // gap equals 2*sigLen (well within the validator's "gap larger than placeholder" tolerance).
    private static byte[] BuildSignedPdf(X509Certificate cert, AsymmetricKeyParameter key, DateTime signingTime)
    {
        // A detached CMS signature's encoded length is independent of the signed content length, so
        // we can measure it once (over a throwaway buffer) to size the placeholder exactly.
        int cmsLen = SignCms(new byte[] { 0 }, cert, key, signingTime).Length;
        int contentsHexLen = cmsLen * 2; // hex chars, no slack padding

        // Pass 1: lay out the bytes with a zero-filled /Contents and a ByteRange placeholder, while
        // recording the exact offsets of the hole and the ByteRange numbers.
        var sb = new StringBuilder();
        var offsets = new Dictionary<int, int>();
        void Obj(int n) { offsets[n] = sb.Length; sb.Append($"{n} 0 obj\n"); }

        sb.Append("%PDF-1.7\n");
        Obj(1); sb.Append("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>\nendobj\n");
        Obj(2); sb.Append("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(3); sb.Append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>\nendobj\n");

        // Signature field / value dictionary (object 4).
        Obj(4);
        sb.Append("<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig) /Rect [0 0 0 0] /V << ");
        sb.Append("/Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached ");
        // ByteRange placeholder — fixed width so total length is stable across both passes.
        int byteRangePos = sb.Length + "/ByteRange ".Length;
        sb.Append("/ByteRange [0000000000 0000000000 0000000000 0000000000] ");
        sb.Append("/Contents <");
        int contentsStart = sb.Length;                  // first hex char position
        sb.Append(new string('0', contentsHexLen));
        sb.Append("> >> >>\nendobj\n");

        int xref = sb.Length;
        sb.Append($"xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");

        byte[] bytes = Encoding.ASCII.GetBytes(sb.ToString());

        // ByteRange: [0 a c d] where a = start of hole, c = byte after hole.
        int holeStart = contentsStart - 1; // the '<' delimiter is included in the signed gap edge
        int a = contentsStart;             // bytes [0 .. contentsStart) signed (before hex)
        int c = contentsStart + contentsHexLen; // byte index of the closing '>'
        int d = bytes.Length - c;
        // Spec layout: range1 = [0, a], range2 = [c, d]; the excluded hole is the hex string.
        WriteByteRange(bytes, byteRangePos, 0, a, c, d);

        // Reconstruct the signed content (everything outside the hex hole) and sign it.
        byte[] signedContent = new byte[a + d];
        Buffer.BlockCopy(bytes, 0, signedContent, 0, a);
        Buffer.BlockCopy(bytes, c, signedContent, a, d);

        byte[] cms = SignCms(signedContent, cert, key, signingTime);
        if (cms.Length * 2 > contentsHexLen)
            throw new InvalidOperationException("Signature larger than reserved placeholder.");

        // Fill the placeholder with the CMS hex (left-justified, zero-padded remainder).
        string hex = ToHex(cms);
        for (int i = 0; i < hex.Length; i++)
            bytes[contentsStart + i] = (byte)hex[i];

        _ = holeStart;
        return bytes;
    }

    private static void WriteByteRange(byte[] bytes, int pos, int v0, int v1, int v2, int v3)
    {
        // Format matches the "[0000000000 0000000000 0000000000 0000000000]" placeholder layout.
        string s = $"[{v0:D10} {v1:D10} {v2:D10} {v3:D10}]";
        for (int i = 0; i < s.Length; i++) bytes[pos + i] = (byte)s[i];
    }

    private static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (byte b in data) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }
}
