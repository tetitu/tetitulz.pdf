// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Xunit;
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Review fix (finding #1): RevocationChecker.VerifyOcspSignature must NOT accept a BasicOCSPResponse
/// signed by an arbitrary cert embedded in the response just because its key verifies. Per RFC 6960
/// §4.2.2.2 a delegated responder cert must (a) be issued by the certificate's issuer AND (b) carry
/// the id-kp-OCSPSigning EKU (1.3.6.1.5.5.7.3.9). The previous code accepted any embedded cert whose
/// public key verified the response — a forged "good" could be minted with an attacker-controlled
/// (or merely EKU-less) cert.
/// </summary>
public class ReviewFixSigning_OcspResponderTests
{
    private static PdfRevocationStatus Check(byte[] pdf, X509Certificate signer, X509Certificate issuer)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return RevocationChecker.Check(doc, signer, issuer, new List<string>());
    }

    [Fact]
    public void ForgedDelegatedResponder_AttackerIssuedCert_NotTrusted()
    {
        // Real CA issues the signer. An ATTACKER root issues a responder cert (so it is NOT
        // anchored to the CA). The response is correctly signed by the attacker responder key and
        // the responder cert is embedded — a forged "good". The validator must reject (Unknown).
        var (ca, caKey)        = SelfSignedCa("CN=Real CA");
        var (signer, _)        = IssuedCert("CN=Signer", caKey, ca, ocspEku: false);
        var (attackerCa, atkCaKey) = SelfSignedCa("CN=Attacker CA");
        var (responder, respKey)   = IssuedCert("CN=Rogue Responder", atkCaKey, attackerCa, ocspEku: true);

        byte[] ocsp = BuildOcsp(ca, signer, respKey, embed: new[] { responder }, CertificateStatus.Good);
        byte[] pdf  = PdfWithDss(new[] { ocsp });

        Assert.Equal(PdfRevocationStatus.Unknown, Check(pdf, signer, ca));
    }

    [Fact]
    public void DelegatedResponder_IssuerSigned_ButNoOcspEku_NotTrusted()
    {
        // Responder cert IS issued by the real CA (anchored) and its key signs the response, but it
        // LACKS the id-kp-OCSPSigning EKU. Any end-entity cert the CA signed (e.g. a TLS server
        // cert) would otherwise be usable to forge a good status. Must be rejected (Unknown).
        var (ca, caKey)      = SelfSignedCa("CN=CA EKU");
        var (signer, _)      = IssuedCert("CN=Signer EKU", caKey, ca, ocspEku: false);
        var (responder, respKey) = IssuedCert("CN=Responder NoEKU", caKey, ca, ocspEku: false);

        byte[] ocsp = BuildOcsp(ca, signer, respKey, embed: new[] { responder }, CertificateStatus.Good);
        byte[] pdf  = PdfWithDss(new[] { ocsp });

        Assert.Equal(PdfRevocationStatus.Unknown, Check(pdf, signer, ca));
    }

    [Fact]
    public void DelegatedResponder_IssuerSigned_WithOcspEku_TrustedGood()
    {
        // Legitimate delegated responder: issued by the real CA AND carrying id-kp-OCSPSigning.
        // This must still be accepted (Good) so the check does not break conforming delegation.
        var (ca, caKey)      = SelfSignedCa("CN=CA Good");
        var (signer, _)      = IssuedCert("CN=Signer Good", caKey, ca, ocspEku: false);
        var (responder, respKey) = IssuedCert("CN=Good Responder", caKey, ca, ocspEku: true);

        byte[] ocsp = BuildOcsp(ca, signer, respKey, embed: new[] { responder }, CertificateStatus.Good);
        byte[] pdf  = PdfWithDss(new[] { ocsp });

        Assert.Equal(PdfRevocationStatus.Good, Check(pdf, signer, ca));
    }

    [Fact]
    public void IssuerDirectlySigns_StillTrusted()
    {
        // The issuer signs the response directly (no embedded responder cert). Must stay accepted.
        var (ca, caKey) = SelfSignedCa("CN=CA Direct");
        var (signer, _) = IssuedCert("CN=Signer Direct", caKey, ca, ocspEku: false);

        byte[] ocsp = BuildOcsp(ca, signer, caKey, embed: null, CertificateStatus.Good);
        byte[] pdf  = PdfWithDss(new[] { ocsp });

        Assert.Equal(PdfRevocationStatus.Good, Check(pdf, signer, ca));
    }

    /// <summary>
    /// The verifier half: a delegated responder — issued by the CA, carrying the EKU —
    /// whose certificate is outside its validity period at the assessment time cannot sign (RFC 6960
    /// §4.2.2.2 by way of RFC 5280 §6.1.3's validity period): Unknown, rather than its "good" trusted.
    /// The same kind of responder inside its period is the control; the embedder asks the one helper.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DelegatedResponder_OutsideItsValidityAtAssessment_NotTrusted(bool expired)
    {
        var (ca, caKey) = SelfSignedCa("CN=CA Validity");
        var (signer, _) = IssuedCert("CN=Signer Validity", caKey, ca, ocspEku: false);
        var (outside, outsideKey) = expired
            ? IssuedCert("CN=Expired Responder", caKey, ca, ocspEku: true, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-1))
            : IssuedCert("CN=Future Responder", caKey, ca, ocspEku: true, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(30));
        var (current, currentKey) = IssuedCert("CN=Current Responder", caKey, ca, ocspEku: true);

        byte[] byOutside = BuildOcsp(ca, signer, outsideKey, embed: new[] { outside }, CertificateStatus.Good);
        byte[] byCurrent = BuildOcsp(ca, signer, currentKey, embed: new[] { current }, CertificateStatus.Good);

        Assert.Equal(PdfRevocationStatus.Unknown, Check(PdfWithDss(new[] { byOutside }), signer, ca));
        Assert.Equal(PdfRevocationStatus.Good, Check(PdfWithDss(new[] { byCurrent }), signer, ca));
    }

    /// <summary>
    /// The verifier's side of the one matcher: a "good" for the signer's serial under another CA's
    /// name and key hashes — signed by the real CA, current — is about a different certificate
    /// (serials are unique within an issuer) and reads Unknown; the same CertID under the signer's
    /// issuer is the control that reads Good.
    /// </summary>
    [Fact]
    public void GoodOcspForTheSameSerialUnderAnotherIssuer_IsNotAboutThisCertificate()
    {
        var (ca, caKey)  = SelfSignedCa("CN=CA Hashes");
        var (otherCa, _) = SelfSignedCa("CN=Other CA Hashes");
        var (signer, _)  = IssuedCert("CN=Signer Hashes", caKey, ca, ocspEku: false);

        byte[] underOther  = BuildOcsp(ca, signer, caKey, embed: null, CertificateStatus.Good, certIdIssuer: otherCa);
        byte[] underIssuer = BuildOcsp(ca, signer, caKey, embed: null, CertificateStatus.Good, certIdIssuer: ca);

        Assert.Equal(PdfRevocationStatus.Unknown, Check(PdfWithDss(new[] { underOther }), signer, ca));
        Assert.Equal(PdfRevocationStatus.Good, Check(PdfWithDss(new[] { underIssuer }), signer, ca));
    }

    // ---------------------------------------------------------------- crypto helpers

    private static (X509Certificate cert, AsymmetricKeyParameter key) SelfSignedCa(string cn)
    {
        var random = new SecureRandom();
        var kp = Helpers.SigningTestCrypto.Keys();

        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        var dn = new X509Name(cn);
        gen.SetSubjectDN(dn);
        gen.SetIssuerDN(dn);
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        gen.SetPublicKey(kp.Public);
        gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        var cert = gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", kp.Private, random));
        return (cert, kp.Private);
    }

    private static (X509Certificate cert, AsymmetricKeyParameter key) IssuedCert(
        string cn, AsymmetricKeyParameter issuerKey, X509Certificate issuerCert, bool ocspEku,
        DateTime? notBefore = null, DateTime? notAfter = null)
    {
        var random = new SecureRandom();
        var kp = Helpers.SigningTestCrypto.Keys();

        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        gen.SetSubjectDN(new X509Name(cn));
        gen.SetIssuerDN(issuerCert.SubjectDN);
        gen.SetNotBefore(notBefore ?? DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(notAfter ?? DateTime.UtcNow.AddYears(1));
        gen.SetPublicKey(kp.Public);
        if (ocspEku)
            gen.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                new ExtendedKeyUsage(KeyPurposeID.IdKPOcspSigning));
        var cert = gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuerKey, random));
        return (cert, kp.Private);
    }

    private static byte[] BuildOcsp(X509Certificate issuer, X509Certificate subject,
        AsymmetricKeyParameter signingKey, X509Certificate[] embed, CertificateStatus status,
        X509Certificate certIdIssuer = null)
    {
        var basicGen = new BasicOcspRespGenerator(new RespID(issuer.SubjectDN));
        // The CertID's issuer hashes are the issuer's unless a test wants the serial under another CA.
        var certId = new CertificateID(new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), certIdIssuer ?? issuer, subject.SerialNumber);
        basicGen.AddResponse(certId, status, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1), null);
        var basic = basicGen.Generate("SHA256WithRSA", signingKey, embed, DateTime.UtcNow);
        var resp = new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic);
        return resp.GetEncoded();
    }

    // ---------------------------------------------------------------- PDF builder

    private static byte[] PdfWithDss(byte[][] ocsps)
    {
        var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Header(int n) { offsets[n] = ms.Length; Ascii($"{n} 0 obj\n"); }
        void StreamObj(int n, byte[] data)
        {
            Header(n);
            Ascii($"<< /Length {data.Length} >>\nstream\n");
            ms.Write(data, 0, data.Length);
            Ascii("\nendstream\nendobj\n");
        }

        int next = 5;
        var ocspNums = ocsps.Select(_ => next++).ToList();
        string ocspArr = "/OCSPs [" + string.Join(" ", ocspNums.Select(x => $"{x} 0 R")) + "]";

        Ascii("%PDF-1.7\n");
        Header(1); Ascii("<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>\nendobj\n");
        Header(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Header(3); Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Header(4); Ascii($"<< /Type /DSS {ocspArr} >>\nendobj\n");
        for (int i = 0; i < ocsps.Length; i++) StreamObj(ocspNums[i], ocsps[i]);

        long xref = ms.Length;
        Ascii($"xref\n0 {next}\n0000000000 65535 f \n");
        for (int i = 1; i < next; i++)
            Ascii($"{offsets[i]:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {next} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
