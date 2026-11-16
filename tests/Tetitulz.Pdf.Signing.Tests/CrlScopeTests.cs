// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing.Timestamping;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Wave 17, R6.1: a CRL that is the issuer's, signature-verified and current can still be the
/// wrong CRL for a certificate — a CA-only or user-only partition, a delta, a per-reason or
/// per-distribution-point list, an indirect CRL (RFC 5280 §6.3.3). Absence from one of those used to
/// read as <see cref="PdfRevocationStatus.Good"/>, so whoever assembled the <c>/DSS</c> could pick
/// the issuer's own published partition that does not list the certificate. Each kind is asserted
/// as <see cref="PdfRevocationStatus.Unknown"/> beside a control that is in scope and reads
/// <c>Good</c>, so the claim is about scope and not about appending a revision.
/// </summary>
public class CrlScopeTests
{
    private const string Field = "Signature1";
    private static readonly SecureRandom Random = new SecureRandom();
    private static long _crlNumber = 9000;

    private static readonly GeneralName PointA = new GeneralName(GeneralName.UniformResourceIdentifier, "http://crl.example.test/a.crl");
    private static readonly GeneralName PointB = new GeneralName(GeneralName.UniformResourceIdentifier, "http://crl.example.test/b.crl");

    public enum Kind { Full, Delta, CaOnly, UserOnly, SomeReasons, Indirect, PointA, PointB }

    // ================================================================ the verifier

    /// <summary>
    /// A CRL whose scope does not reach the leaf answers Unknown and leaves the chain trusted — the
    /// certificate has not been shown revoked, nor shown unrevoked — with the reason in the warnings.
    /// </summary>
    [Theory]
    [InlineData(Kind.Delta, "delta CRL")]
    [InlineData(Kind.CaOnly, "onlyContainsCACerts")]
    [InlineData(Kind.SomeReasons, "onlySomeReasons")]
    [InlineData(Kind.Indirect, "indirect CRL")]
    [InlineData(Kind.PointB, "distribution point")]
    public void ACrlOutsideTheLeafsScope_CannotVouchForIt(Kind kind, string reason)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = LeafNamingPointA(ca);
        byte[] withDss = Signed(ca, leaf, Crl(ca, kind, revoked: null));

        var r = RevokedCertificateChainStatusTests.Verify(withDss, ca.Cert);

        Assert.Equal(PdfRevocationStatus.Unknown, r.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
        Assert.Contains(r.Warnings, w => w.Contains(reason, StringComparison.Ordinal) && w.Contains("6.3.3", StringComparison.Ordinal));
    }

    /// <summary>
    /// The controls: a full CRL, a user-certificate-only partition and the leaf's own distribution
    /// point are each in scope for an end-entity certificate, and absence from them is Good.
    /// </summary>
    [Theory]
    [InlineData(Kind.Full)]
    [InlineData(Kind.UserOnly)]
    [InlineData(Kind.PointA)]
    public void ACrlInTheLeafsScope_VouchesForIt(Kind kind)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = LeafNamingPointA(ca);
        byte[] withDss = Signed(ca, leaf, Crl(ca, kind, revoked: BigInteger.ValueOf(0x5EEDBEEF)));

        var r = RevokedCertificateChainStatusTests.Verify(withDss, ca.Cert);

        Assert.Equal(PdfRevocationStatus.Good, r.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
    }

    /// <summary>A listing is a revocation whatever the partition: a delta or a CA-only CRL that names the leaf revokes it.</summary>
    [Theory]
    [InlineData(Kind.Delta)]
    [InlineData(Kind.CaOnly)]
    public void ACrlOutsideTheLeafsScopeThatListsIt_StillRevokesIt(Kind kind)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = LeafNamingPointA(ca);
        byte[] withDss = Signed(ca, leaf, Crl(ca, kind, revoked: leaf.Cert.SerialNumber));

        var r = RevokedCertificateChainStatusTests.Verify(withDss, ca.Cert);

        Assert.Equal(PdfRevocationStatus.Revoked, r.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Invalid, r.ChainStatus);
    }

    /// <summary>
    /// A leaf with no distribution points of its own names none, so a point-scoped CRL cannot vouch
    /// for it — and a full CRL still can, which is the control that the leaf itself is sound.
    /// </summary>
    [Fact]
    public void APointScopedCrl_CannotVouchForALeafWithoutDistributionPoints()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        var scoped = RevokedCertificateChainStatusTests.Verify(Signed(ca, leaf, Crl(ca, Kind.PointA, revoked: null)), ca.Cert);
        var full = RevokedCertificateChainStatusTests.Verify(Signed(ca, leaf, Crl(ca, Kind.Full, revoked: null)), ca.Cert);

        Assert.Equal(PdfRevocationStatus.Unknown, scoped.RevocationStatus);
        Assert.Equal(PdfRevocationStatus.Good, full.RevocationStatus);
    }

    /// <summary>
    /// The scope question asked directly, so a wrong answer names its reason: the leaf's own point is
    /// in scope, another point and the CA-only partition are not.
    /// </summary>
    [Fact]
    public void TheScopeQuestion_NamesWhyACrlCannotVouch()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = LeafNamingPointA(ca);
        var parser = new X509CrlParser();

        Assert.Null(Cms.CrlScope.WhyItCannotVouchFor(parser.ReadCrl(Crl(ca, Kind.PointA, revoked: null)), leaf.Cert));
        Assert.Null(Cms.CrlScope.WhyItCannotVouchFor(parser.ReadCrl(Crl(ca, Kind.Full, revoked: null)), leaf.Cert));
        Assert.Contains("distribution point", Cms.CrlScope.WhyItCannotVouchFor(parser.ReadCrl(Crl(ca, Kind.PointB, revoked: null)), leaf.Cert), StringComparison.Ordinal);
        Assert.Contains("onlyContainsCACerts", Cms.CrlScope.WhyItCannotVouchFor(parser.ReadCrl(Crl(ca, Kind.CaOnly, revoked: null)), leaf.Cert), StringComparison.Ordinal);
        Assert.Null(Cms.CrlScope.WhyItCannotVouchFor(parser.ReadCrl(Crl(ca, Kind.CaOnly, revoked: null)), ca.Cert));
        Assert.Contains("onlyContainsUserCerts", Cms.CrlScope.WhyItCannotVouchFor(parser.ReadCrl(Crl(ca, Kind.UserOnly, revoked: null)), ca.Cert), StringComparison.Ordinal);
    }

    // ================================================================ the embedder

    /// <summary>
    /// The LTV embedder refuses a CRL the verifier would read as Unknown, naming the scope, while its
    /// signature verdict stays true — the list is genuine, it is simply not this certificate's. The
    /// control is the full CRL, embedded as fetched.
    /// </summary>
    [Fact]
    public void TheEmbedderRefusesACrlOutsideTheSubjectsScope_AndKeepsOneInIt()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        Assert.Null(DssBuilder.ValidateAndFilterCrl(Crl(ca, Kind.CaOnly, revoked: null), leaf.Cert, ca.Cert));
        Assert.True(DssBuilder.LastCrlSignatureValid);
        Assert.Contains("onlyContainsCACerts", DssBuilder.LastCrlSignatureIssue, StringComparison.Ordinal);
        Assert.Null(DssBuilder.LastCrlFreshnessIssue);

        byte[] full = Crl(ca, Kind.Full, revoked: null);
        Assert.Same(full, DssBuilder.ValidateAndFilterCrl(full, leaf.Cert, ca.Cert));
        Assert.Null(DssBuilder.LastCrlSignatureIssue);
    }

    /// <summary>An out-of-scope CRL that lists the subject is honest evidence and is embedded.</summary>
    [Fact]
    public void TheEmbedderKeepsAnOutOfScopeCrlThatListsTheSubject()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        byte[] listing = Crl(ca, Kind.CaOnly, revoked: leaf.Cert.SerialNumber);
        Assert.Same(listing, DssBuilder.ValidateAndFilterCrl(listing, leaf.Cert, ca.Cert));
        Assert.Null(DssBuilder.LastCrlSignatureIssue);
    }

    // ================================================================ fixtures

    /// <summary>An end-entity certificate whose <c>cRLDistributionPoints</c> names <see cref="PointA"/>.</summary>
    private static SigningTestCrypto.Issued LeafNamingPointA(SigningTestCrypto.Issued issuer)
    {
        var kp = SigningTestCrypto.Keys();
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ValueOf(DateTime.UtcNow.Ticks & 0x7FFFFFFF));
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetSubjectDN(new X509Name("CN=Point A Signer"));
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        gen.SetPublicKey(kp.Public);
        gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        gen.AddExtension(X509Extensions.CrlDistributionPoints, false,
            new CrlDistPoint(new DistributionPoint(new DistributionPointName(new GeneralNames(PointA)), null, null)));
        return new SigningTestCrypto.Issued
        {
            Cert = gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random)),
            Private = kp.Private,
        };
    }

    /// <summary>A real, signed, current CRL of the given scope, optionally listing <paramref name="revoked"/>.</summary>
    private static byte[] Crl(SigningTestCrypto.Issued issuer, Kind kind, BigInteger? revoked)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddHours(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(7));
        gen.AddExtension(X509Extensions.CrlNumber, false,
            new CrlNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _crlNumber))));
        if (revoked != null)
            gen.AddCrlEntry(revoked, DateTime.UtcNow.AddHours(-1), CrlReason.KeyCompromise);

        switch (kind)
        {
            case Kind.Delta:
                gen.AddExtension(X509Extensions.DeltaCrlIndicator, true, DerInteger.ValueOf(1));
                break;
            case Kind.CaOnly:
                gen.AddExtension(X509Extensions.IssuingDistributionPoint, true,
                    new IssuingDistributionPoint(null, onlyContainsUserCerts: false, onlyContainsCACerts: true, null, false, false));
                break;
            case Kind.UserOnly:
                gen.AddExtension(X509Extensions.IssuingDistributionPoint, true,
                    new IssuingDistributionPoint(null, onlyContainsUserCerts: true, onlyContainsCACerts: false, null, false, false));
                break;
            case Kind.SomeReasons:
                gen.AddExtension(X509Extensions.IssuingDistributionPoint, true,
                    new IssuingDistributionPoint(null, false, false, new ReasonFlags(ReasonFlags.KeyCompromise), false, false));
                break;
            case Kind.Indirect:
                gen.AddExtension(X509Extensions.IssuingDistributionPoint, true,
                    new IssuingDistributionPoint(null, false, false, null, true, false));
                break;
            case Kind.PointA:
                gen.AddExtension(X509Extensions.IssuingDistributionPoint, true,
                    new IssuingDistributionPoint(new DistributionPointName(new GeneralNames(PointA)), false, false, null, false, false));
                break;
            case Kind.PointB:
                gen.AddExtension(X509Extensions.IssuingDistributionPoint, true,
                    new IssuingDistributionPoint(new DistributionPointName(new GeneralNames(PointB)), false, false, null, false, false));
                break;
        }

        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random)).GetEncoded();
    }

    /// <summary>A document signed by <paramref name="leaf"/> with <paramref name="crl"/> in an appended <c>/DSS</c>.</summary>
    private static byte[] Signed(SigningTestCrypto.Issued ca, SigningTestCrypto.Issued leaf, byte[] crl)
    {
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);
        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);
        return RevokedCertificateChainStatusTests.AppendDssWithCrl(signed, crl);
    }
}
