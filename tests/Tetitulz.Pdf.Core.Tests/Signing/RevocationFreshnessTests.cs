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
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Embedded revocation data must be <em>current</em>, not merely well-signed.
/// <para>
/// A validly-signed CRL or OCSP response issued BEFORE a certificate was revoked still reports it
/// as unrevoked, and does so forever. Without a freshness check an attacker can embed such a
/// stale-but-genuine response in the /DSS and defeat revocation entirely. The gate applies only to
/// the "good" direction — a stale response that says REVOKED is still truthful, because revocation
/// is never undone by the passage of time.
/// </para>
/// </summary>
public class RevocationFreshnessTests
{
    // ---------------------------------------------------------------- CRL freshness

    [Fact]
    public void ExpiredCrl_NotListingTheCert_IsUnknownNotGood()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        // Genuine CRL, correctly signed — but it expired a month ago.
        byte[] crl = Crl(ca, BigInteger.ValueOf(999999),
            thisUpdate: DateTime.UtcNow.AddDays(-60),
            nextUpdate: DateTime.UtcNow.AddDays(-30));

        var warnings = new List<string>();
        var status = Check(PdfWithDss(Array.Empty<byte[]>(), new[] { crl }), leaf.Cert, ca.Cert, warnings);

        Assert.Equal(PdfRevocationStatus.Unknown, status);
        Assert.Contains(warnings, w => w.Contains("expired"));
    }

    [Fact]
    public void CurrentCrl_NotListingTheCert_IsGood()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));

        Assert.Equal(PdfRevocationStatus.Good,
            Check(PdfWithDss(Array.Empty<byte[]>(), new[] { crl }), leaf.Cert, ca.Cert, new List<string>()));
    }

    [Fact]
    public void StaleCrl_ListingTheCert_IsStillRevoked()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca, leaf.Cert.SerialNumber,
            DateTime.UtcNow.AddDays(-60), DateTime.UtcNow.AddDays(-30));

        // Staleness must not rescue a revoked certificate.
        Assert.Equal(PdfRevocationStatus.Revoked,
            Check(PdfWithDss(Array.Empty<byte[]>(), new[] { crl }), leaf.Cert, ca.Cert, new List<string>()));
    }

    [Fact]
    public void FutureDatedCrl_IsUnknown()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddDays(30), DateTime.UtcNow.AddDays(60));

        var warnings = new List<string>();
        Assert.Equal(PdfRevocationStatus.Unknown,
            Check(PdfWithDss(Array.Empty<byte[]>(), new[] { crl }), leaf.Cert, ca.Cert, warnings));
        Assert.Contains(warnings, w => w.Contains("future"));
    }

    [Fact]
    public void CrlWithoutNextUpdate_BeyondMaxAge_IsUnknown()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddDays(-400), nextUpdate: null);

        var warnings = new List<string>();
        Assert.Equal(PdfRevocationStatus.Unknown,
            Check(PdfWithDss(Array.Empty<byte[]>(), new[] { crl }), leaf.Cert, ca.Cert, warnings));
        Assert.Contains(warnings, w => w.Contains("nextUpdate"));
    }

    [Fact]
    public void CrlWithoutNextUpdate_WithinMaxAge_IsGood()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddDays(-10), nextUpdate: null);

        Assert.Equal(PdfRevocationStatus.Good,
            Check(PdfWithDss(Array.Empty<byte[]>(), new[] { crl }), leaf.Cert, ca.Cert, new List<string>()));
    }

    /// <summary>
    /// <c>MaxRevocationDataAge</c> of zero switches the age cap off; it does not make every CRL
    /// without a <c>nextUpdate</c> stale. The same 400-day-old CRL is unknown under the default
    /// cap (the test above) and good with the cap off.
    /// </summary>
    [Fact]
    public void CrlWithoutNextUpdate_AgeCapOfZero_IsTheCapSwitchedOff()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddDays(-400), nextUpdate: null);

        using var ms = new MemoryStream(PdfWithDss(Array.Empty<byte[]>(), new[] { crl }));
        using var doc = PdfDocument.Open(ms);
        var status = RevocationChecker.Check(doc, leaf.Cert, ca.Cert, new List<string>(),
            new SignatureVerificationOptions { MaxRevocationDataAge = TimeSpan.Zero });

        Assert.Equal(PdfRevocationStatus.Good, status);
    }

    // ---------------------------------------------------------------- OCSP freshness

    [Fact]
    public void ExpiredOcspGood_IsUnknown()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] ocsp = Ocsp(ca, leaf.Cert, CertificateStatus.Good,
            thisUpdate: DateTime.UtcNow.AddDays(-60),
            nextUpdate: DateTime.UtcNow.AddDays(-30),
            producedAt: DateTime.UtcNow.AddDays(-60));

        var warnings = new List<string>();
        Assert.Equal(PdfRevocationStatus.Unknown,
            Check(PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>()), leaf.Cert, ca.Cert, warnings));
        Assert.Contains(warnings, w => w.Contains("expired"));
    }

    [Fact]
    public void StaleOcspRevoked_IsStillRevoked()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] ocsp = Ocsp(ca, leaf.Cert,
            new RevokedStatus(DateTime.UtcNow.AddDays(-90), CrlReason.KeyCompromise),
            DateTime.UtcNow.AddDays(-60), DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-60));

        Assert.Equal(PdfRevocationStatus.Revoked,
            Check(PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>()), leaf.Cert, ca.Cert, new List<string>()));
    }

    [Fact]
    public void CurrentOcspGood_IsGood()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] ocsp = Ocsp(ca, leaf.Cert, CertificateStatus.Good,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(PdfRevocationStatus.Good,
            Check(PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>()), leaf.Cert, ca.Cert, new List<string>()));
    }

    /// <summary>
    /// Serial numbers are unique only within an issuer. Matching on the serial alone let an OCSP
    /// response from an unrelated CA speak for this certificate; CertID.MatchesIssuer pins it.
    /// </summary>
    [Fact]
    public void OcspFromDifferentIssuer_WithSameSerial_DoesNotMatch()
    {
        var realCa = SigningTestCrypto.Ca("CN=Real CA");
        var otherCa = SigningTestCrypto.Ca("CN=Other CA");
        var leaf = SigningTestCrypto.Leaf(realCa);

        // A "good" response for the SAME serial number, but keyed to a different issuer.
        byte[] ocsp = OcspForSerial(otherCa, leaf.Cert.SerialNumber, CertificateStatus.Good);

        // The response is not about this certificate, so nothing conclusive can be read from it.
        Assert.Equal(PdfRevocationStatus.Unknown,
            Check(PdfWithDss(new[] { ocsp }, Array.Empty<byte[]>()), leaf.Cert, realCa.Cert, new List<string>()));
    }

    // ---------------------------------------------------------------- intermediate CAs

    [Fact]
    public void RevokedIntermediate_MakesTheWholeChainRevoked()
    {
        var root = SigningTestCrypto.Ca("CN=Root");
        var intermediate = SigningTestCrypto.Intermediate(root, "CN=Intermediate");
        var leaf = SigningTestCrypto.Leaf(intermediate, "CN=Leaf");

        // The leaf itself is fine per the intermediate's CRL...
        byte[] leafCrl = Crl(intermediate, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));
        // ...but the ROOT has revoked the intermediate.
        byte[] intermediateCrl = Crl(root, intermediate.Cert.SerialNumber,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));

        byte[] pdf = PdfWithDss(Array.Empty<byte[]>(), new[] { leafCrl, intermediateCrl });
        var chain = new List<X509Certificate> { leaf.Cert, intermediate.Cert, root.Cert };

        var warnings = new List<string>();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var status = RevocationChecker.CheckChain(doc, chain, warnings);

        Assert.Equal(PdfRevocationStatus.Revoked, status);
        Assert.Contains(warnings, w => w.Contains("Intermediate CA") && w.Contains("REVOKED"));
    }

    [Fact]
    public void HealthyChain_IsGood()
    {
        var root = SigningTestCrypto.Ca("CN=Root");
        var intermediate = SigningTestCrypto.Intermediate(root, "CN=Intermediate");
        var leaf = SigningTestCrypto.Leaf(intermediate, "CN=Leaf");

        byte[] leafCrl = Crl(intermediate, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));
        byte[] intermediateCrl = Crl(root, BigInteger.ValueOf(888888),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));

        byte[] pdf = PdfWithDss(Array.Empty<byte[]>(), new[] { leafCrl, intermediateCrl });
        var chain = new List<X509Certificate> { leaf.Cert, intermediate.Cert, root.Cert };

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        Assert.Equal(PdfRevocationStatus.Good,
            RevocationChecker.CheckChain(doc, chain, new List<string>()));
    }

    [Fact]
    public void IntermediateChecking_CanBeDisabled()
    {
        var root = SigningTestCrypto.Ca("CN=Root");
        var intermediate = SigningTestCrypto.Intermediate(root, "CN=Intermediate");
        var leaf = SigningTestCrypto.Leaf(intermediate, "CN=Leaf");

        byte[] leafCrl = Crl(intermediate, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));
        byte[] intermediateCrl = Crl(root, intermediate.Cert.SerialNumber,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));

        byte[] pdf = PdfWithDss(Array.Empty<byte[]>(), new[] { leafCrl, intermediateCrl });
        var chain = new List<X509Certificate> { leaf.Cert, intermediate.Cert, root.Cert };
        var opts = new SignatureVerificationOptions { CheckIntermediateRevocation = false };

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        Assert.Equal(PdfRevocationStatus.Good,
            RevocationChecker.CheckChain(doc, chain, new List<string>(), opts));
    }

    // ---------------------------------------------------------------- /DSS /VRI scoping

    /// <summary>
    /// ISO 32000-2 §12.8.4.3: a /VRI entry keyed by the SHA-1 of a signature's /Contents holds the
    /// revocation data for <em>that</em> signature. Ignoring /VRI let data proving signature A's
    /// certificate good also vouch for signature B.
    /// </summary>
    [Fact]
    public void VriEntry_ScopesRevocationDataToItsOwnSignature()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        byte[] contentsA = Encoding.ASCII.GetBytes("signature-A-contents");
        byte[] contentsB = Encoding.ASCII.GetBytes("signature-B-contents");

        // Signature A's VRI says the certificate is revoked; the document-wide list says good.
        byte[] revokedCrl = Crl(ca, leaf.Cert.SerialNumber,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));
        byte[] goodCrl = Crl(ca, BigInteger.ValueOf(999999),
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1));

        byte[] pdf = PdfWithVri(Sha1Hex(contentsA), revokedCrl, goodCrl);

        // Signature A resolves through its own /VRI entry → revoked.
        Assert.Equal(PdfRevocationStatus.Revoked,
            Check(pdf, leaf.Cert, ca.Cert, new List<string>(), contentsA));

        // Signature B has no /VRI entry → falls back to the document-wide list → good.
        Assert.Equal(PdfRevocationStatus.Good,
            Check(pdf, leaf.Cert, ca.Cert, new List<string>(), contentsB));
    }

    // ---------------------------------------------------------------- helpers

    private static PdfRevocationStatus Check(
        byte[] pdf, X509Certificate signer, X509Certificate issuer, List<string> warnings,
        byte[] signatureContents = null)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return RevocationChecker.Check(doc, signer, issuer, warnings, null, null, signatureContents);
    }

    private static string Sha1Hex(byte[] data)
    {
        byte[] hash = DigestUtilities.CalculateDigest("SHA-1", data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private static byte[] Ocsp(
        SigningTestCrypto.Issued issuer, X509Certificate subject, object status,
        DateTime thisUpdate, DateTime nextUpdate, DateTime producedAt)
    {
        var basicGen = new BasicOcspRespGenerator(new RespID(issuer.Cert.SubjectDN));
        var certId = new CertificateID(
            new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer.Cert, subject.SerialNumber);
        basicGen.AddResponse(certId, (CertificateStatus)status, thisUpdate, nextUpdate, null);
        var basic = basicGen.Generate("SHA256WithRSA", issuer.Private, new[] { issuer.Cert }, producedAt);
        return new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic).GetEncoded();
    }

    private static byte[] OcspForSerial(
        SigningTestCrypto.Issued issuer, BigInteger serial, CertificateStatus status)
    {
        var basicGen = new BasicOcspRespGenerator(new RespID(issuer.Cert.SubjectDN));
        var certId = new CertificateID(
            new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer.Cert, serial);
        basicGen.AddResponse(certId, status, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1), null);
        var basic = basicGen.Generate("SHA256WithRSA", issuer.Private, new[] { issuer.Cert }, DateTime.UtcNow);
        return new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic).GetEncoded();
    }

    private static byte[] Crl(
        SigningTestCrypto.Issued issuer, BigInteger revokedSerial,
        DateTime thisUpdate, DateTime? nextUpdate)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetThisUpdate(thisUpdate);
        if (nextUpdate.HasValue) gen.SetNextUpdate(nextUpdate.Value);
        gen.AddCrlEntry(revokedSerial, thisUpdate.AddDays(-1), CrlReason.KeyCompromise);
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private)).GetEncoded();
    }

    // ---------------------------------------------------------------- PDF builders

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

    /// <summary>A /DSS carrying a per-signature /VRI entry plus a document-wide fallback list.</summary>
    private static byte[] PdfWithVri(string vriKey, byte[] vriCrl, byte[] globalCrl)
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

        Ascii("%PDF-1.7\n");
        Header(1); Ascii("<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>\nendobj\n");
        Header(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Header(3); Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Header(4); Ascii("<< /Type /DSS /CRLs [6 0 R] /VRI 5 0 R >>\nendobj\n");
        Header(5); Ascii($"<< /{vriKey} << /CRL [7 0 R] >> >>\nendobj\n");
        StreamObj(6, globalCrl);
        StreamObj(7, vriCrl);

        WriteXref(ms, offsets, 8, Ascii);
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
