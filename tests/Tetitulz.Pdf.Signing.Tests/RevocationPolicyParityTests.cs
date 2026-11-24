// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The LTV embedder must never write what the verifier will not read (CLAUDE.md, Editing under a
/// signature): every OCSP response and CRL is judged here by <b>both</b> — the embedder's filter
/// (<c>DssBuilder.ValidateAndFilterOcspResponse</c> / <c>ValidateAndFilterCrl</c>) and the verifier
/// under its default options (<c>RevocationChecker</c>, handed the bytes through its online
/// fallback, which judges them now as the embedder does).
/// </summary>
/// <remarks>
/// <para><b>Three places two separate rules would disagree.</b> A CRL with no <c>nextUpdate</c>
/// older than the verifier's 365-day cap, embedded and then read as <c>Unknown</c>; an OCSP response
/// produced in the future, embedded and then refused; and an OCSP response holding a stale entry
/// about <i>another</i> certificate, refused by an embedder judging every entry while the verifier,
/// judging the entry about this one, reads it as good.
/// Each row below is one fixture judged twice. The embedder may be stricter — an OCSP response with
/// no <c>nextUpdate</c> is held to seven days, not 365 — never looser.</para>
/// </remarks>
public class RevocationPolicyParityTests
{
    private static readonly SecureRandom Random = new();
    private static int _crlNumber;

    public static IEnumerable<object[]> CrlCases => new[]
    {
        //                       thisUpdate (days), nextUpdate (days), embedded, verified good
        new object[] { "current",                 -0.05, 7.0,           true,  true  },
        new object[] { "expired",                 -90.0, -60.0,         false, false },
        new object[] { "dated in the future",      2.0,  30.0,          false, false },
        new object[] { "no nextUpdate, 10 days",  -10.0, null,          true,  true  },
        new object[] { "no nextUpdate, 300 days", -300.0, null,         true,  true  },
        // The defect: embedded, and read as Unknown by default verification of the same document.
        new object[] { "no nextUpdate, 400 days", -400.0, null,         false, false },
    };

    [Theory]
    [MemberData(nameof(CrlCases))]
    public void ACrlIsEmbeddedOnlyWhereDefaultVerificationReadsIt(
        string shape, double thisUpdateDays, double? nextUpdateDays, bool embedded, bool verified)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        DateTime now = DateTime.UtcNow;
        byte[] crl = Crl(ca, now.AddDays(thisUpdateDays),
            nextUpdateDays.HasValue ? now.AddDays(nextUpdateDays.Value) : (DateTime?)null);

        bool wasEmbedded = DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, ca.Cert) != null;
        var status = VerifiedOnline(leaf, ca, ocsp: null, crl: crl);

        Assert.True(embedded == wasEmbedded, $"{shape}: embedded {wasEmbedded}, expected {embedded}");
        Assert.True(verified == (status == PdfRevocationStatus.Good), $"{shape}: verifier said {status}");
        Assert.False(wasEmbedded && status != PdfRevocationStatus.Good,
            $"{shape}: embedded, but default verification reads it as {status}");
    }

    public static IEnumerable<object[]> OcspCases => new[]
    {
        //                         thisUpdate (min), nextUpdate (min), producedAt (min), embedded, verified good
        new object[] { "current",                      -10.0, 1440.0,    -1.0,   true,  true  },
        new object[] { "expired",                    -2880.0, -1440.0, -2880.0,  false, false },
        // The defect: producedAt was never read by the embedder.
        new object[] { "produced in the future",       -10.0, 1440.0,  1440.0,   false, false },
        new object[] { "no nextUpdate, 3 days",      -4320.0,   null,  -4320.0,  true,  true  },
        // Stricter is allowed: the embedder holds a response with no nextUpdate to seven days.
        new object[] { "no nextUpdate, 30 days",    -43200.0,   null, -43200.0,  false, true  },
    };

    [Theory]
    [MemberData(nameof(OcspCases))]
    public void AnOcspResponseIsEmbeddedOnlyWhereDefaultVerificationReadsIt(
        string shape, double thisUpdateMinutes, double? nextUpdateMinutes, double producedAtMinutes,
        bool embedded, bool verified)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        DateTime now = DateTime.UtcNow;
        byte[] ocsp = Ocsp(ca, ca.Private, carried: null, now.AddMinutes(producedAtMinutes),
            (leaf.Cert.SerialNumber, now.AddMinutes(thisUpdateMinutes),
             nextUpdateMinutes.HasValue ? now.AddMinutes(nextUpdateMinutes.Value) : (DateTime?)null));

        bool wasEmbedded = DssBuilder.ValidateAndFilterOcspResponse(ocsp, leaf.Cert, ca.Cert) != null;
        var status = VerifiedOnline(leaf, ca, ocsp, crl: null);

        Assert.True(embedded == wasEmbedded, $"{shape}: embedded {wasEmbedded}, expected {embedded} ({DssBuilder.LastOcspFreshnessIssue})");
        Assert.True(verified == (status == PdfRevocationStatus.Good), $"{shape}: verifier said {status}");
        Assert.False(wasEmbedded && status != PdfRevocationStatus.Good,
            $"{shape}: embedded, but default verification reads it as {status}");
    }

    /// <summary>
    /// A response may answer about several certificates. Its entry about another one being stale
    /// says nothing about this one: the verifier reads the entry about this certificate, and the
    /// embedder — which judged every entry — refused a response that was current evidence. The
    /// control is the same pair with this certificate's entry the stale one.
    /// </summary>
    [Fact]
    public void AStaleEntryAboutAnotherCertificateDoesNotRefuseAResponseAboutThisOne()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var other = SigningTestCrypto.Leaf(ca, "CN=Another Signer");
        DateTime now = DateTime.UtcNow;
        var current = (now.AddMinutes(-10), (DateTime?)now.AddDays(1));
        var stale = (now.AddDays(-3), (DateTime?)now.AddDays(-2));

        byte[] otherStale = Ocsp(ca, ca.Private, null, now.AddMinutes(-1),
            (leaf.Cert.SerialNumber, current.Item1, current.Item2),
            (other.Cert.SerialNumber, stale.Item1, stale.Item2));
        Assert.Equal(otherStale, DssBuilder.ValidateAndFilterOcspResponse(otherStale, leaf.Cert, ca.Cert));
        Assert.Null(DssBuilder.LastOcspFreshnessIssue);
        Assert.Equal(PdfRevocationStatus.Good, VerifiedOnline(leaf, ca, otherStale, null));

        byte[] thisStale = Ocsp(ca, ca.Private, null, now.AddMinutes(-1),
            (leaf.Cert.SerialNumber, stale.Item1, stale.Item2),
            (other.Cert.SerialNumber, current.Item1, current.Item2));
        Assert.Null(DssBuilder.ValidateAndFilterOcspResponse(thisStale, leaf.Cert, ca.Cert));
        Assert.Contains("stale", DssBuilder.LastOcspFreshnessIssue, StringComparison.Ordinal);
        Assert.Equal(PdfRevocationStatus.Unknown, VerifiedOnline(leaf, ca, thisStale, null));
    }

    /// <summary>
    /// The responder, judged by the one search both sides now make
    /// (<c>OcspSingleResponse.WhyTheResponderCannotVouch</c>): a delegated responder the CA issued
    /// with id-kp-OCSPSigning is accepted by both; one without the EKU, or issued by another CA, is
    /// refused by both, each saying why.
    /// </summary>
    [Theory]
    [InlineData("delegated with the EKU",    true,  true,  true,  null)]
    [InlineData("delegated without the EKU", true,  false, false, "id-kp-OCSPSigning")]
    [InlineData("issued by another CA",      false, true,  false, "did not issue")]
    public void TheResponderIsJudgedAlikeOnBothSides(
        string shape, bool issuedByTheCa, bool ocspEku, bool accepted, string reason)
    {
        var ca = SigningTestCrypto.Ca();
        var otherCa = SigningTestCrypto.Ca("CN=Other CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        var responder = Responder(issuedByTheCa ? ca : otherCa, ocspEku);
        DateTime now = DateTime.UtcNow;

        byte[] ocsp = Ocsp(ca, responder.Private, new[] { responder.Cert }, now.AddMinutes(-1),
            (leaf.Cert.SerialNumber, now.AddMinutes(-10), now.AddDays(1)));

        bool wasEmbedded = DssBuilder.ValidateAndFilterOcspResponse(ocsp, leaf.Cert, ca.Cert) != null;
        var warnings = new List<string>();
        var status = VerifiedOnline(leaf, ca, ocsp, null, warnings);

        Assert.True(accepted == wasEmbedded, $"{shape}: embedded {wasEmbedded} ({DssBuilder.LastOcspSignatureIssue})");
        Assert.True(accepted == (status == PdfRevocationStatus.Good), $"{shape}: verifier said {status}");
        if (reason != null)
        {
            Assert.Contains(reason, DssBuilder.LastOcspSignatureIssue, StringComparison.Ordinal);
            Assert.Contains(warnings, w => w.Contains(reason, StringComparison.Ordinal));
        }
        else
        {
            Assert.Null(DssBuilder.LastOcspSignatureIssue);
        }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// The verifier's reading of <paramref name="ocsp"/> or <paramref name="crl"/>, judged now, with
    /// default options: a document with no <c>/DSS</c>, so the online fallback asks the fetchers,
    /// which hand back the fixture instead of reaching a network.
    /// </summary>
    private static PdfRevocationStatus VerifiedOnline(
        SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued ca, byte[] ocsp, byte[] crl,
        List<string> warnings = null)
    {
        using var doc = PdfDocument.Open(new MemoryStream(SignedPdfFixtures.MinimalPdf()));
        return RevocationChecker.CheckChainWithOnlineFallback(doc,
            new List<X509Certificate> { leaf.Cert, ca.Cert }, warnings ?? new List<string>(),
            new SignatureVerificationOptions { OnlineRevocation = true }, null, null,
            (s, i) => s.Equals(leaf.Cert) ? ocsp : null,
            s => s.Equals(leaf.Cert) ? crl : null);
    }

    private static byte[] Ocsp(
        SigningTestCrypto.Issued issuer, Org.BouncyCastle.Crypto.AsymmetricKeyParameter signingKey,
        X509Certificate[] carried, DateTime producedAt,
        params (BigInteger serial, DateTime thisUpdate, DateTime? nextUpdate)[] entries)
    {
        var gen = new BasicOcspRespGenerator(new RespID(issuer.Cert.SubjectDN));
        foreach (var (serial, thisUpdate, nextUpdate) in entries)
        {
            var certId = new CertificateID(new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer.Cert, serial);
            gen.AddResponse(certId, CertificateStatus.Good, thisUpdate, nextUpdate, null);
        }
        var basic = gen.Generate("SHA256WithRSA", signingKey, carried ?? Array.Empty<X509Certificate>(), producedAt);
        return new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic).GetEncoded();
    }

    private static byte[] Crl(SigningTestCrypto.Issued issuer, DateTime thisUpdate, DateTime? nextUpdate)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetThisUpdate(thisUpdate);
        if (nextUpdate.HasValue) gen.SetNextUpdate(nextUpdate.Value);
        gen.AddExtension(X509Extensions.CrlNumber, false,
            new CrlNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _crlNumber))));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random)).GetEncoded();
    }

    /// <summary>A responder certificate issued by <paramref name="issuer"/>, valid now.</summary>
    private static SigningTestCrypto.Issued Responder(SigningTestCrypto.Issued issuer, bool ocspEku)
    {
        var keys = SigningTestCrypto.Keys();
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(120, Random));
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetSubjectDN(new X509Name("CN=Delegated Responder"));
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddDays(30));
        gen.SetPublicKey(keys.Public);
        gen.AddExtension(X509Extensions.ExtendedKeyUsage, false, ocspEku
            ? new ExtendedKeyUsage(KeyPurposeID.IdKPOcspSigning)
            : new ExtendedKeyUsage(KeyPurposeID.IdKPClientAuth));
        return new SigningTestCrypto.Issued
        {
            Cert = gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random)),
            Private = keys.Private,
        };
    }
}
