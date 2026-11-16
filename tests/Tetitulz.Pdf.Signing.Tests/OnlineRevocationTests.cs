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
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="SignatureVerificationOptions.OnlineRevocation"/>: a certificate the document's
/// <c>/DSS</c> does not settle is asked about online, through the embedder's fetches, and what
/// comes back is judged by the functions embedded data goes through. Off by default, and nothing is
/// fetched then; a document that settles the status is not asked; a fetch that fails is a warning,
/// never a verdict; online data is judged at the current time. The fetches are supplied here, so a
/// synthetic responder stands in for the network — the fetchers themselves are the LTV embedder's,
/// tested with it.
/// </summary>
public class OnlineRevocationTests
{
    private static readonly SecureRandom Random = new();
    private static int _crlNumber;

    [Fact]
    public void OnlineOff_LeavesTheDocumentsVerdict_AndFetchesNothing()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        int fetches = 0;
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            new SignatureVerificationOptions(), null, null,
            (s, i) => { fetches++; return Ocsp(ca, s.SerialNumber, CertificateStatus.Good); },
            s => { fetches++; return Crl(ca, null); });

        Assert.Equal(PdfRevocationStatus.NotChecked, status);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public void OnlineOcspGood_ResolvesGood_AndSaysItWasOnline()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            Online(), null, null,
            (s, i) => Ocsp(ca, s.SerialNumber, CertificateStatus.Good),
            s => throw new InvalidOperationException("CRL must not be asked when OCSP answered"));

        Assert.Equal(PdfRevocationStatus.Good, status);
        Assert.Contains(warnings, w => w.Contains("resolved online by OCSP", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlineOcspRevoked_ResolvesRevoked()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            Online(), null, null,
            (s, i) => Ocsp(ca, s.SerialNumber, new RevokedStatus(DateTime.UtcNow.AddDays(-1), CrlReason.KeyCompromise)),
            s => null);

        Assert.Equal(PdfRevocationStatus.Revoked, status);
        Assert.Contains(warnings, w => w.Contains("REVOKED", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, PdfRevocationStatus.Good)]
    [InlineData(true, PdfRevocationStatus.Revoked)]
    public void OnlineCrl_WhenNoOcspResponder_DecidesByTheCrl(bool listed, PdfRevocationStatus expected)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            Online(), null, null,
            (s, i) => null,
            s => Crl(ca, listed ? s.SerialNumber : null));

        Assert.Equal(expected, status);
    }

    /// <summary>An online "good" is held to the embedded standard: a CRL another key signed vouches for nothing.</summary>
    [Fact]
    public void OnlineCrlSignedByAStranger_IsUnknown_NotGood()
    {
        var ca = SigningTestCrypto.Ca();
        var stranger = SigningTestCrypto.Ca("CN=Some Other CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            Online(), null, null,
            (s, i) => null,
            s => Crl(stranger, null));

        Assert.Equal(PdfRevocationStatus.Unknown, status);
    }

    [Fact]
    public void AFetchThatThrows_IsAWarning_NotAVerdict()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            Online(), null, null,
            (s, i) => throw new IOException("responder unreachable"),
            s => throw new IOException("distribution point unreachable"));

        Assert.Equal(PdfRevocationStatus.NotChecked, status);
        Assert.Contains(warnings, w => w.Contains("OCSP fetch", StringComparison.Ordinal) && w.Contains("unreachable", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("CRL fetch", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIntermediateNobodyAnswersFor_DegradesAGoodLeafToUnknown()
    {
        var root = SigningTestCrypto.Ca();
        var intermediate = SigningTestCrypto.IssuedIntermediate(root, SigningTestCrypto.Keys(), "CN=Test Intermediate");
        var leaf = SigningTestCrypto.Leaf(intermediate);
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, intermediate, root), warnings,
            Online(), null, null,
            (s, i) => s.Equals(leaf.Cert) ? Ocsp(intermediate, s.SerialNumber, CertificateStatus.Good) : null,
            s => null);

        Assert.Equal(PdfRevocationStatus.Unknown, status);

        // The control: the intermediate answered for as well is Good through the whole chain.
        warnings.Clear();
        var whole = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, intermediate, root), warnings,
            Online(), null, null,
            (s, i) => Ocsp(s.Equals(leaf.Cert) ? intermediate : root, s.SerialNumber, CertificateStatus.Good),
            s => null);
        Assert.Equal(PdfRevocationStatus.Good, whole);
    }

    /// <summary>A document whose /DSS settles the status is not asked online at all.</summary>
    [Fact]
    public void ADocumentThatSettlesTheStatus_IsNotAskedOnline()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        int fetches = 0;
        var warnings = new List<string>();

        byte[] withDss = RevokedCertificateChainStatusTests.AppendDssWithCrl(SignedPdfFixtures.MinimalPdf(), Crl(ca, null));
        using var doc = PdfDocument.Open(new MemoryStream(withDss));
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            Online(), null, null,
            (s, i) => { fetches++; return null; },
            s => { fetches++; return null; });

        Assert.Equal(PdfRevocationStatus.Good, status);
        Assert.Equal(0, fetches);
    }

    /// <summary>Online data is current, so it is judged now, and a past assessment time is noted.</summary>
    [Fact]
    public void OnlineData_IsJudgedNow_WhenTheSignatureWasAssessedInThePast()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var warnings = new List<string>();

        using var doc = Open();
        var status = RevocationChecker.CheckChainWithOnlineFallback(doc, Chain(leaf, ca), warnings,
            Online(), DateTime.UtcNow.AddDays(-30), null,
            (s, i) => Ocsp(ca, s.SerialNumber, CertificateStatus.Good),
            s => null);

        // A response produced now would be "dated in the future" against a thirty-day-old assessment
        // time; judged now it is good, and the warning says which time was used.
        Assert.Equal(PdfRevocationStatus.Good, status);
        Assert.Contains(warnings, w => w.Contains("as of now", StringComparison.Ordinal));
    }

    /// <summary>
    /// Through the verifier: a signed document whose signer names no responder and no distribution
    /// point, verified with the option on, reports that nothing online could be fetched — the
    /// verifier reached the online step — and stays <see cref="PdfRevocationStatus.NotChecked"/>;
    /// with the option off the step is not taken and the warning is absent. No network is touched:
    /// a certificate with no URLs gives the embedder's fetchers nothing to fetch.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheVerifier_AsksOnlineOnlyWhenAsked(bool online)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);
        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, "Signature1");

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var result = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { ca.Cert },
            OnlineRevocation = online,
        }).Single(r => r.FieldName == "Signature1");

        Assert.Equal(PdfRevocationStatus.NotChecked, result.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
        Assert.Equal(online, result.Warnings.Any(w => w.Contains("No online revocation data could be fetched", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ fixtures

    private static SignatureVerificationOptions Online() => new() { OnlineRevocation = true };

    private static PdfDocument Open() => PdfDocument.Open(new MemoryStream(SignedPdfFixtures.MinimalPdf()));

    private static IList<X509Certificate> Chain(params SigningTestCrypto.Issued[] certs) => certs.Select(c => c.Cert).ToList();

    private static byte[] Ocsp(SigningTestCrypto.Issued issuer, BigInteger serial, object status)
    {
        var gen = new BasicOcspRespGenerator(new RespID(issuer.Cert.SubjectDN));
        var certId = new CertificateID(new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer.Cert, serial);
        gen.AddResponse(certId, (CertificateStatus)status, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddDays(1), null);
        var basic = gen.Generate("SHA256WithRSA", issuer.Private, new[] { issuer.Cert }, DateTime.UtcNow.AddMinutes(-1));
        return new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic).GetEncoded();
    }

    private static byte[] Crl(SigningTestCrypto.Issued issuer, BigInteger revokedSerial)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddHours(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(7));
        gen.AddExtension(X509Extensions.CrlNumber, false,
            new CrlNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _crlNumber))));
        if (revokedSerial != null)
            gen.AddCrlEntry(revokedSerial, DateTime.UtcNow.AddHours(-1), CrlReason.KeyCompromise);
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random)).GetEncoded();
    }
}
