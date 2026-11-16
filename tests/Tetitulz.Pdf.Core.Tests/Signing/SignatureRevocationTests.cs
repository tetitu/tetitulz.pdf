// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
/// Verify-time revocation checking from embedded /DSS (LTV) data:
/// <see cref="RevocationChecker"/> parses OCSP responses and CRLs out of the document's /DSS,
/// verifies their signatures, and reports the signer certificate's revocation status. A forged
/// (wrongly-signed) "good" response must not be trusted.
/// </summary>
public class SignatureRevocationTests
{
    private static PdfRevocationStatus Check(byte[] pdf, X509Certificate signer, X509Certificate issuer)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return RevocationChecker.Check(doc, signer, issuer, new List<string>());
    }

    [Fact]
    public void GoodOcspInDss_ReportsGood()
    {
        var (cert, key) = SelfSigned();
        byte[] ocsp = Ocsp(cert, cert, key, CertificateStatus.Good);
        byte[] pdf = PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>());

        Assert.Equal(PdfRevocationStatus.Good, Check(pdf, cert, cert));
    }

    [Fact]
    public void RevokedOcspInDss_ReportsRevoked()
    {
        var (cert, key) = SelfSigned();
        byte[] ocsp = Ocsp(cert, cert, key, new RevokedStatus(DateTime.UtcNow.AddDays(-1), CrlReason.KeyCompromise));
        byte[] pdf = PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>());

        Assert.Equal(PdfRevocationStatus.Revoked, Check(pdf, cert, cert));
    }

    [Fact]
    public void ForgedOcspSignature_IsNotTrusted()
    {
        var (cert, _) = SelfSigned();
        var (_, attackerKey) = SelfSigned(); // a "good" response signed by the wrong key
        byte[] ocsp = Ocsp(cert, cert, attackerKey, CertificateStatus.Good);
        byte[] pdf = PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>());

        Assert.Equal(PdfRevocationStatus.Unknown, Check(pdf, cert, cert));
    }

    /// <summary>
    /// A correctly signed "good" response about a <b>different</b> certificate of the same issuer
    /// says nothing about this one. Accepting it would let anyone holding a current response for
    /// any certificate the CA issued staple it to a revoked one. The control is the same response
    /// built for the signer's own serial, which is good.
    /// </summary>
    [Fact]
    public void GoodOcspForAnotherSerial_IsNotAboutThisCertificate()
    {
        var (cert, key) = SelfSigned();
        BigInteger other = cert.SerialNumber.Add(BigInteger.One);

        byte[] elsewhere = Ocsp(cert, other, key, CertificateStatus.Good);
        Assert.Equal(PdfRevocationStatus.Unknown,
            Check(PdfWithDss(new[] { elsewhere }, Array.Empty<byte[]>()), cert, cert));

        byte[] own = Ocsp(cert, cert.SerialNumber, key, CertificateStatus.Good);
        Assert.Equal(PdfRevocationStatus.Good,
            Check(PdfWithDss(new[] { own }, Array.Empty<byte[]>()), cert, cert));
    }

    /// <summary>
    /// RFC 6960's third status, "unknown" (the responder does not know the certificate), is not a
    /// "good": it is reported as unknown.
    /// </summary>
    [Fact]
    public void OcspUnknownStatus_ReportsUnknownNotGood()
    {
        var (cert, key) = SelfSigned();
        byte[] ocsp = Ocsp(cert, cert, key, new UnknownStatus());
        byte[] pdf = PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>());

        Assert.Equal(PdfRevocationStatus.Unknown, Check(pdf, cert, cert));
    }

    [Fact]
    public void RevokedCrlInDss_ReportsRevoked()
    {
        var (cert, key) = SelfSigned();
        byte[] crl = Crl(cert, key, cert.SerialNumber);
        byte[] pdf = PdfWithDss(Array.Empty<byte[]>(), new[] { crl });

        Assert.Equal(PdfRevocationStatus.Revoked, Check(pdf, cert, cert));
    }

    [Fact]
    public void CrlNotListingTheCert_ReportsGood()
    {
        var (cert, key) = SelfSigned();
        byte[] crl = Crl(cert, key, BigInteger.ValueOf(999999)); // some other serial
        byte[] pdf = PdfWithDss(Array.Empty<byte[]>(), new[] { crl });

        Assert.Equal(PdfRevocationStatus.Good, Check(pdf, cert, cert));
    }

    /// <summary>
    /// The time a response is judged at is a moment, not a wall clock: a Local time and its UTC
    /// equivalent give one answer. The response expires an hour after now; judged at a Local "now" in a
    /// zone ahead of UTC, the wall clock read as UTC passed that hour, and a current response read as
    /// stale. (On a host whose zone is UTC the two spellings are the same instant either way.)
    /// </summary>
    [Fact]
    public void ALocalAssessmentTime_IsTheSameMomentAsItsUtc()
    {
        var (cert, key) = SelfSigned();
        byte[] pdf = PdfWithDss(new[] { Ocsp(cert, cert.SerialNumber, key, CertificateStatus.Good, TimeSpan.FromMinutes(59)) },
                                Array.Empty<byte[]>());
        DateTime utc = DateTime.UtcNow;
        PdfRevocationStatus At(DateTime when)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            return RevocationChecker.Check(doc, cert, cert, new List<string>(), assessmentTime: when);
        }

        Assert.Equal(PdfRevocationStatus.Good, At(utc));
        Assert.Equal(PdfRevocationStatus.Good, At(utc.ToLocalTime()));
        Assert.Equal(PdfRevocationStatus.Unknown, At(utc.AddHours(2)));   // the potency: two hours on, it is stale
    }

    [Fact]
    public void NoDss_ReportsNotChecked()
    {
        var (cert, _) = SelfSigned();
        byte[] pdf = PdfWithoutDss();

        Assert.Equal(PdfRevocationStatus.NotChecked, Check(pdf, cert, cert));
    }

    // ---------------------------------------------------------------- crypto helpers

    private static (X509Certificate cert, AsymmetricKeyParameter key) SelfSigned(string cn = "CN=Revocation Test")
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
        var cert = gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", kp.Private, random));
        return (cert, kp.Private);
    }

    private static byte[] Ocsp(X509Certificate issuer, X509Certificate subject,
        AsymmetricKeyParameter signingKey, CertificateStatus status)
        => Ocsp(issuer, subject.SerialNumber, signingKey, status);

    private static byte[] Ocsp(X509Certificate issuer, BigInteger serial,
        AsymmetricKeyParameter signingKey, CertificateStatus status, TimeSpan? validFor = null)
    {
        var basicGen = new BasicOcspRespGenerator(new RespID(issuer.SubjectDN));
        var certId = new CertificateID(new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer, serial);
        basicGen.AddResponse(certId, status, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.Add(validFor ?? TimeSpan.FromDays(1)), null);
        var basic = basicGen.Generate("SHA256WithRSA", signingKey, new[] { issuer }, DateTime.UtcNow);
        var resp = new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic);
        return resp.GetEncoded();
    }

    private static byte[] Crl(X509Certificate issuer, AsymmetricKeyParameter key, BigInteger revokedSerial)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddMinutes(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(1));
        gen.AddCrlEntry(revokedSerial, DateTime.UtcNow.AddDays(-1), CrlReason.KeyCompromise);
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", key)).GetEncoded();
    }

    // ---------------------------------------------------------------- PDF builders

    private static byte[] PdfWithoutDss()
    {
        var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Header(int n) { offsets[n] = ms.Length; Ascii($"{n} 0 obj\n"); }

        Ascii("%PDF-1.7\n");
        Header(1); Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Header(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Header(3); Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        WriteXref(ms, offsets, 4, Ascii);
        return ms.ToArray();
    }

    private static byte[] PdfWithDss(byte[][] ocsps, byte[][] crls)
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
        var crlNums = crls.Select(_ => next++).ToList();
        string ocspArr = ocspNums.Count > 0 ? "/OCSPs [" + string.Join(" ", ocspNums.Select(x => $"{x} 0 R")) + "]" : "";
        string crlArr = crlNums.Count > 0 ? "/CRLs [" + string.Join(" ", crlNums.Select(x => $"{x} 0 R")) + "]" : "";

        Ascii("%PDF-1.7\n");
        Header(1); Ascii("<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>\nendobj\n");
        Header(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Header(3); Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Header(4); Ascii($"<< /Type /DSS {ocspArr} {crlArr} >>\nendobj\n");
        for (int i = 0; i < ocsps.Length; i++) StreamObj(ocspNums[i], ocsps[i]);
        for (int i = 0; i < crls.Length; i++) StreamObj(crlNums[i], crls[i]);

        WriteXref(ms, offsets, next, Ascii);
        return ms.ToArray();
    }

    private static void WriteXref(MemoryStream ms, Dictionary<int, long> offsets, int size, Action<string> ascii)
    {
        long xref = ms.Length;
        ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
            ascii($"{offsets[i]:D10} 00000 n \n");
        ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
