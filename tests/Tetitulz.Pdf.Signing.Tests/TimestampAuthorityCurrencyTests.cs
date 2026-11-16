// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.Helpers;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A timestamp moves the time a signer's certificate is judged at only while its authority can
/// still be relied on: the TSA certificate is not revoked, and its chain is valid at the current
/// time or a later timestamp from an authority that can be relied on covers the token.
/// </summary>
/// <remarks>
/// <para>The signer in every fixture holds a certificate that expired five years ago, and the old
/// token's time lies inside its validity period — so "the token was honoured" and "the token was
/// ignored" are the two chain statuses <c>Trusted</c> and <c>Invalid</c>, and the assessment time
/// says which clock was used.</para>
/// <para>An expired authority is the one that matters: its key is no longer guarded and no
/// revocation list names it, and a token signed with it today carries whatever time it is given.</para>
/// </remarks>
public sealed class TimestampAuthorityCurrencyTests
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddYears(-6);

    private sealed class Pki
    {
        public SigningTestCrypto.Issued Root = SigningTestCrypto.Ca("CN=Currency Root");
        public SigningTestCrypto.Issued ExpiredSigner = null!;
        public SigningTestCrypto.Issued ExpiredTsa = null!;
        public SigningTestCrypto.Issued CurrentTsa = null!;

        public Pki()
        {
            ExpiredSigner = SigningTestCrypto.Leaf(Root, "CN=Expired Signer",
                notBefore: DateTime.UtcNow.AddYears(-7), notAfter: DateTime.UtcNow.AddYears(-5));
            ExpiredTsa = SigningTestCrypto.TsaCert(Root, "CN=Expired TSA",
                notBefore: DateTime.UtcNow.AddYears(-8), notAfter: DateTime.UtcNow.AddYears(-4));
            CurrentTsa = SigningTestCrypto.TsaCert(Root, "CN=Current TSA");
        }
    }

    // ------------------------------------------------------------------ document timestamps

    /// <summary>
    /// A document timestamp from an authority whose certificate has since expired does not
    /// back-date the signature it covers; the same file read with the rule off does, so the
    /// refusal is the authority's and not the fixture's.
    /// </summary>
    [Fact]
    public void ADocumentTimestampFromAnExpiredAuthority_IsIgnored_UnlessThePolicyIsOff()
    {
        var pki = new Pki();
        byte[] signed = Sign(pki.ExpiredSigner, pki.Root);
        byte[] stamped = DocTimeStampAppender.Append(signed, pki.ExpiredTsa, LongAgo, "DocTimeStamp1", false, pki.Root.Cert);

        SignatureVerificationResult strict = VerifySignature(stamped, pki);
        Assert.False(strict.HasTrustedTimestamp, "Warnings: " + string.Join(" | ", strict.Warnings));
        Assert.Equal(PdfSignatureChainStatus.Invalid, strict.ChainStatus);
        Assert.True(strict.AssessmentTime > DateTime.UtcNow.AddHours(-1), $"Assessed at {strict.AssessmentTime:u}.");
        Assert.Contains(strict.Warnings, w => w.Contains("RequireCurrentTimestampAuthority", StringComparison.Ordinal));

        SignatureVerificationResult relaxed = VerifySignature(stamped, pki, o => o.RequireCurrentTimestampAuthority = false);
        Assert.True(relaxed.HasTrustedTimestamp, "Warnings: " + string.Join(" | ", relaxed.Warnings));
        Assert.Equal(PdfSignatureChainStatus.Trusted, relaxed.ChainStatus);
        Assert.True(Math.Abs((relaxed.AssessmentTime!.Value - LongAgo).TotalMinutes) < 5, $"Assessed at {relaxed.AssessmentTime:u}.");
    }

    /// <summary>
    /// The renewal chain: a later document timestamp from a current authority covers the old one,
    /// so the old one counts again — and it is the old one's time the signature is judged at, the
    /// only one of the two inside the signer's validity period.
    /// </summary>
    [Fact]
    public void ALaterTimestampFromACurrentAuthority_RenewsTheExpiredOne()
    {
        var pki = new Pki();
        byte[] signed = Sign(pki.ExpiredSigner, pki.Root);
        byte[] stamped = DocTimeStampAppender.Append(signed, pki.ExpiredTsa, LongAgo, "DocTimeStamp1", false, pki.Root.Cert);
        byte[] renewed = DocTimeStampAppender.Append(stamped, pki.CurrentTsa, DateTime.UtcNow.AddYears(-1), "DocTimeStamp2", false, pki.Root.Cert);

        SignatureVerificationResult result = VerifySignature(renewed, pki);

        Assert.True(result.HasTrustedTimestamp, "Warnings: " + string.Join(" | ", result.Warnings));
        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
        Assert.True(Math.Abs((result.AssessmentTime!.Value - LongAgo).TotalMinutes) < 5, $"Assessed at {result.AssessmentTime:u}.");
    }

    /// <summary>
    /// A later timestamp renews nothing when its own authority has expired too: two expired
    /// authorities vouch for each other no better than one vouches for itself.
    /// </summary>
    [Fact]
    public void ALaterTimestampFromAnotherExpiredAuthority_RenewsNothing()
    {
        var pki = new Pki();
        var alsoExpired = SigningTestCrypto.TsaCert(pki.Root, "CN=Also Expired TSA",
            notBefore: DateTime.UtcNow.AddYears(-8), notAfter: DateTime.UtcNow.AddYears(-3));
        byte[] signed = Sign(pki.ExpiredSigner, pki.Root);
        byte[] stamped = DocTimeStampAppender.Append(signed, pki.ExpiredTsa, LongAgo, "DocTimeStamp1", false, pki.Root.Cert);
        byte[] again = DocTimeStampAppender.Append(stamped, alsoExpired, DateTime.UtcNow.AddYears(-5).AddMonths(-6), "DocTimeStamp2", false, pki.Root.Cert);

        SignatureVerificationResult result = VerifySignature(again, pki);

        Assert.False(result.HasTrustedTimestamp);
        Assert.Equal(PdfSignatureChainStatus.Invalid, result.ChainStatus);
        Assert.Contains(result.Warnings, w => w.StartsWith("2 document timestamp(s) verified", StringComparison.Ordinal));
    }

    /// <summary>
    /// A current authority whose certificate the document's own <c>/DSS</c> lists as revoked is
    /// not believed; with a CRL that lists someone else it is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADocumentTimestampFromARevokedAuthority_IsIgnored(bool revoked)
    {
        var pki = new Pki();
        byte[] signed = Sign(pki.ExpiredSigner, pki.Root);
        byte[] stamped = DocTimeStampAppender.Append(signed, pki.CurrentTsa, LongAgo, "DocTimeStamp1", false, pki.Root.Cert);
        byte[] withDss = RevokedCertificateChainStatusTests.AppendDssWithCrl(stamped,
            RevokedCertificateChainStatusTests.Crl(pki.Root, revoked ? pki.CurrentTsa.Cert.SerialNumber : BigInteger.ValueOf(0x5EEDBEEF)));

        SignatureVerificationResult result = VerifySignature(withDss, pki);

        Assert.Equal(!revoked, result.HasTrustedTimestamp);
        Assert.Equal(revoked ? PdfSignatureChainStatus.Invalid : PdfSignatureChainStatus.Trusted, result.ChainStatus);
        if (revoked)
            Assert.Contains(result.Warnings, w => w.Contains("timestamp authority's certificate is revoked", StringComparison.Ordinal));
    }

    /// <summary>
    /// The authority's chain runs through an intermediate the root's CRL lists, and the token's bag
    /// carries first a copy of that intermediate — its name and key — under an attacker's root. The
    /// authority is judged along the path its chain was anchored through, so the revoked intermediate
    /// is found; a chain built apart followed the copy and asked the attacker's root about it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdversarialSameNameSameKeyIntermediate_DoesNotDivertTheAuthoritysRevocationCheck(bool revoked)
    {
        var pki = new Pki();
        var attacker = SigningTestCrypto.Ca("CN=X");
        var keys = SigningTestCrypto.Keys();
        var intermediate = SigningTestCrypto.IssuedIntermediate(pki.Root, keys, "CN=Shared TSA Intermediate");
        var copy = SigningTestCrypto.IssuedIntermediate(attacker, keys, "CN=Shared TSA Intermediate");
        var tsa = SigningTestCrypto.TsaCert(intermediate, "CN=Intermediate TSA");
        byte[] signed = Sign(pki.ExpiredSigner, pki.Root);
        byte[] stamped = DocTimeStampAppender.Append(signed, tsa, LongAgo, "DocTimeStamp1", false,
            copy.Cert, attacker.Cert, intermediate.Cert, pki.Root.Cert);
        BigInteger somebodyElse = BigInteger.ValueOf(0x5EEDBEEF);
        byte[] withDss = RevokedCertificateChainStatusTests.AppendDssWithCrls(stamped,
            RevokedCertificateChainStatusTests.Crl(intermediate, somebodyElse),
            RevokedCertificateChainStatusTests.Crl(pki.Root, revoked ? intermediate.Cert.SerialNumber : somebodyElse));

        SignatureVerificationResult result = VerifySignature(withDss, pki);

        Assert.Equal(!revoked, result.HasTrustedTimestamp);
        if (revoked)
            Assert.Contains(result.Warnings, w => w.Contains("timestamp authority's certificate is revoked", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ signature timestamps

    /// <summary>
    /// The same rule for the token a signature carries in its own CMS: from an expired authority it
    /// is ignored, with the rule off it back-dates.
    /// </summary>
    [Fact]
    public void ASignatureTimestampFromAnExpiredAuthority_IsIgnored_UnlessThePolicyIsOff()
    {
        var pki = new Pki();
        byte[] signed = SignWithOwnTimestamp(pki.ExpiredSigner, pki.Root, pki.ExpiredTsa, LongAgo);

        SignatureVerificationResult strict = VerifySignature(signed, pki);
        Assert.False(strict.HasTrustedTimestamp, "Warnings: " + string.Join(" | ", strict.Warnings));
        Assert.Equal(PdfSignatureChainStatus.Invalid, strict.ChainStatus);
        Assert.Contains(strict.Warnings, w => w.StartsWith("Signature timestamp verified, but its TSA certificate is no longer valid", StringComparison.Ordinal));

        SignatureVerificationResult relaxed = VerifySignature(signed, pki, o => o.RequireCurrentTimestampAuthority = false);
        Assert.True(relaxed.HasTrustedTimestamp, "Warnings: " + string.Join(" | ", relaxed.Warnings));
        Assert.Equal(PdfSignatureChainStatus.Trusted, relaxed.ChainStatus);
    }

    /// <summary>
    /// A document timestamp from a current authority over the signature renews the signature's own
    /// token, and the signature is judged at that token's time — the document timestamp's, a year
    /// ago, is outside the signer's validity period.
    /// </summary>
    [Fact]
    public void ADocumentTimestampOverTheSignature_RenewsItsOwnExpiredToken()
    {
        var pki = new Pki();
        byte[] signed = SignWithOwnTimestamp(pki.ExpiredSigner, pki.Root, pki.ExpiredTsa, LongAgo);
        byte[] renewed = DocTimeStampAppender.Append(signed, pki.CurrentTsa, DateTime.UtcNow.AddYears(-1), "DocTimeStamp1", false, pki.Root.Cert);

        SignatureVerificationResult result = VerifySignature(renewed, pki);

        Assert.True(result.HasTrustedTimestamp, "Warnings: " + string.Join(" | ", result.Warnings));
        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
        Assert.True(Math.Abs((result.AssessmentTime!.Value - LongAgo).TotalMinutes) < 5, $"Assessed at {result.AssessmentTime:u}.");
    }

    /// <summary>A signature's own token from a revoked authority is ignored; under a CRL that lists someone else it counts.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASignatureTimestampFromARevokedAuthority_IsIgnored(bool revoked)
    {
        var pki = new Pki();
        byte[] signed = SignWithOwnTimestamp(pki.ExpiredSigner, pki.Root, pki.CurrentTsa, LongAgo);
        byte[] withDss = RevokedCertificateChainStatusTests.AppendDssWithCrl(signed,
            RevokedCertificateChainStatusTests.Crl(pki.Root, revoked ? pki.CurrentTsa.Cert.SerialNumber : BigInteger.ValueOf(0x5EEDBEEF)));

        SignatureVerificationResult result = VerifySignature(withDss, pki);

        Assert.Equal(!revoked, result.HasTrustedTimestamp);
        if (revoked)
            Assert.Contains(result.Warnings, w => w.StartsWith("Signature timestamp: the timestamp authority's certificate is revoked", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Sign(SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued root)
    {
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("Currency fixture"));
        using var output = new MemoryStream();
        using var certificate = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = certificate,
            CertificatePassword = password,
            SignatureFieldName = "Signature1",
        });
        return output.ToArray();
    }

    /// <summary>Signs with a CMS that carries its own signature timestamp, from <paramref name="tsa"/> at <paramref name="genTime"/>.</summary>
    private static byte[] SignWithOwnTimestamp(
        SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued root, SigningTestCrypto.Issued tsa, DateTime genTime)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("Currency fixture"));
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            ExternalSigner = new TimestampingSigner(leaf, root.Cert, tsa, genTime),
            SignatureFieldName = "Signature1",
            EstimatedSignatureSize = 32768,
        });
        return output.ToArray();
    }

    private static SignatureVerificationResult VerifySignature(
        byte[] pdf, Pki pki, Action<SignatureVerificationOptions>? adjust = null)
    {
        var options = new SignatureVerificationOptions { TrustAnchors = new[] { pki.Root.Cert } };
        adjust?.Invoke(options);
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var results = new PdfSignatureVerifier().VerifyAll(doc, options);
        return results.Single(r => r.FieldName == "Signature1");
    }

    /// <summary>A signer over a local key whose CMS carries a signature timestamp the test chose the authority and time of.</summary>
    private sealed class TimestampingSigner : IExternalSigner
    {
        private readonly SigningTestCrypto.Issued _leaf;
        private readonly X509Certificate _root;
        private readonly SigningTestCrypto.Issued _tsa;
        private readonly DateTime _genTime;

        public TimestampingSigner(SigningTestCrypto.Issued leaf, X509Certificate root, SigningTestCrypto.Issued tsa, DateTime genTime)
        {
            _leaf = leaf;
            _root = root;
            _tsa = tsa;
            _genTime = genTime;
        }

        public byte[] SignData(byte[] data, string digestAlgorithm)
        {
            byte[] cms = CmsSignatureGenerator.GenerateDetachedSignature(
                data, _leaf.Private, _leaf.Cert, new List<X509Certificate> { _leaf.Cert, _root }, digestAlgorithm);
            SignerInformation signer = new CmsSignedData(cms).GetSignerInfos().GetSigners().Cast<SignerInformation>().Single();
            byte[] token = SigningTestCrypto.TimestampToken(signer.GetSignature(), _tsa, _genTime, _root);
            return CmsSignatureGenerator.AddTimestampUnsignedAttribute(new CmsSignedData(cms), token).GetEncoded();
        }

        public byte[] SignDigest(byte[] digest, string digestAlgorithm) => throw new NotSupportedException();

        public IList<byte[]> GetCertificateChain() => new[] { _leaf.Cert.GetEncoded(), _root.GetEncoded() };

        public int EstimatedSignatureSize => 32768;
    }
}
