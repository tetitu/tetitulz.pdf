// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// DssBuilder verifies every OCSP response's signature against an
/// issuer-anchored responder cert before embedding it, and rejects stale
/// responses outright.
///
/// Otherwise an attacker-controlled OCSP endpoint could return a fabricated
/// "good" status for a revoked cert, and a stale (post-nextUpdate) response
/// would be embedded into the DSS dictionary anyway.
/// </summary>
public class DssBuilderOcspValidationTests
{
    [Fact]
    public void ValidateAndFilterOcspResponse_ForgedSignature_ReturnsNullAndSetsIssue()
    {
        // Build a real CA + subject cert pair, then sign the OCSP response
        // with a SECOND, unrelated key (the "attacker" key). The validator
        // must reject this — the response's signature does not validate
        // against either the embedded responder cert (none) or the issuer.
        var (caCert, caKey)             = GenerateRsaCert("CN=Test Issuer");
        var (subjectCert, _)            = GenerateRsaCert("CN=Test Subject", caKey, caCert);
        var (_, attackerKey)            = GenerateRsaCert("CN=Attacker");

        // Build a BasicOCSPResp signed by the attacker key with a self-
        // referential cert (so the response embeds attackerCert, but
        // attackerCert is NOT signed by the CA).
        byte[] forged = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: attackerKey,
            embedCerts: null,
            thisUpdate: DateTime.UtcNow,
            nextUpdate: DateTime.UtcNow.AddHours(1));

        byte[] result = DssBuilder.ValidateAndFilterOcspResponse(
            forged, subjectCert, caCert);

        Assert.Null(result);
        Assert.False(DssBuilder.LastOcspSignatureValid);
        Assert.NotNull(DssBuilder.LastOcspSignatureIssue);
        Assert.Contains("signature", DssBuilder.LastOcspSignatureIssue,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAndFilterOcspResponse_IssuerSignedOk_ReturnsBytes()
    {
        // Issuer signs the OCSP response directly — the most common
        // production layout. Validator must accept and set
        // LastOcspSignatureValid = true.
        var (caCert, caKey)  = GenerateRsaCert("CN=Test Issuer 2");
        var (subjectCert, _) = GenerateRsaCert("CN=Test Subject 2", caKey, caCert);

        byte[] response = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: caKey,
            embedCerts: null,
            thisUpdate: DateTime.UtcNow,
            nextUpdate: DateTime.UtcNow.AddHours(1));

        byte[] result = DssBuilder.ValidateAndFilterOcspResponse(
            response, subjectCert, caCert);

        Assert.NotNull(result);
        Assert.True(DssBuilder.LastOcspSignatureValid);
        Assert.Null(DssBuilder.LastOcspSignatureIssue);
        Assert.Null(DssBuilder.LastOcspFreshnessIssue);
    }

    [Fact]
    public void ValidateAndFilterOcspResponse_Stale_DefaultRejects()
    {
        // nextUpdate is one hour in the past. Default behaviour
        // (EmbedStaleOcspResponses = false) must reject the response
        // and surface the freshness issue.
        var (caCert, caKey)  = GenerateRsaCert("CN=Test Issuer 3");
        var (subjectCert, _) = GenerateRsaCert("CN=Test Subject 3", caKey, caCert);

        byte[] response = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: caKey,
            embedCerts: null,
            thisUpdate: DateTime.UtcNow.AddHours(-2),
            nextUpdate: DateTime.UtcNow.AddHours(-1));

        DssBuilder.EmbedStaleOcspResponses = false;
        byte[] result = DssBuilder.ValidateAndFilterOcspResponse(
            response, subjectCert, caCert);

        Assert.Null(result);
        Assert.NotNull(DssBuilder.LastOcspFreshnessIssue);
        Assert.Contains("stale", DssBuilder.LastOcspFreshnessIssue,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>EmbedStaleOcspResponses</c> is the opt-in that reverses
    /// <see cref="ValidateAndFilterOcspResponse_Stale_DefaultRejects"/>: the same bytes that were
    /// rejected are now embedded. Both halves are asserted from one fixture, so the flag is
    /// provably the pivot — an implementation that ignored it (in either direction) fails here.
    ///
    /// <para>Also asserted: what comes back is the response <b>verbatim</b>. "Non-null" would let
    /// a validator hand callers a re-encoded or truncated body, and what is embedded into the DSS
    /// must be the responder-signed bytes or the signature no longer verifies.</para>
    /// </summary>
    [Fact]
    public void ValidateAndFilterOcspResponse_Stale_OptInLenientMode_Embeds()
    {
        var (caCert, caKey)  = GenerateRsaCert("CN=Test Issuer 4");
        var (subjectCert, _) = GenerateRsaCert("CN=Test Subject 4", caKey, caCert);

        byte[] response = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: caKey,
            embedCerts: null,
            thisUpdate: DateTime.UtcNow.AddHours(-2),
            nextUpdate: DateTime.UtcNow.AddHours(-1));

        // Default: rejected outright.
        DssBuilder.EmbedStaleOcspResponses = false;
        Assert.Null(DssBuilder.ValidateAndFilterOcspResponse(response, subjectCert, caCert));

        // Opt in: the very same bytes are now accepted, unchanged.
        DssBuilder.EmbedStaleOcspResponses = true;
        try
        {
            byte[] result = DssBuilder.ValidateAndFilterOcspResponse(
                response, subjectCert, caCert);

            Assert.Equal(response, result);

            // ...and the audit trail still says why it was questionable.
            Assert.Contains("stale", DssBuilder.LastOcspFreshnessIssue,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(DssBuilder.LastOcspSignatureValid);
        }
        finally
        {
            DssBuilder.EmbedStaleOcspResponses = false;
        }
    }

    /// <summary>
    /// A response is evidence for the certificate its <c>SingleResponse</c> names. Embedded, a
    /// responder's answer about another certificate under the same CA — well signed, current — would
    /// have <c>TryBuildDss</c> report revocation data obtained for a certificate the document then
    /// held no evidence about. The control is the same response judged for the
    /// certificate it is about.
    /// </summary>
    [Fact]
    public void ValidateAndFilterOcspResponse_AResponseAboutAnotherCertificate_IsRefused()
    {
        var (caCert, caKey) = GenerateRsaCert("CN=Test Issuer 5");
        var (asked, _)      = GenerateRsaCert("CN=Asked About", caKey, caCert);
        var (other, _)      = GenerateRsaCert("CN=Answered About", caKey, caCert);

        byte[] response = BuildBasicOcspRespBytes(
            caCert, other,
            signingKey: caKey,
            embedCerts: null,
            thisUpdate: DateTime.UtcNow,
            nextUpdate: DateTime.UtcNow.AddHours(1));

        Assert.Null(DssBuilder.ValidateAndFilterOcspResponse(response, asked, caCert));
        Assert.Contains(asked.SerialNumber.ToString(), DssBuilder.LastOcspSubjectIssue, StringComparison.Ordinal);
        Assert.Null(DssBuilder.LastOcspSignatureIssue);

        Assert.Equal(response, DssBuilder.ValidateAndFilterOcspResponse(response, other, caCert));
        Assert.Null(DssBuilder.LastOcspSubjectIssue);
    }

    /// <summary>
    /// The serial alone is not the certificate — serials are unique within an issuer. A
    /// <c>CertID</c> carrying the subject's serial under another CA's name and key hashes, signed by
    /// the subject's own issuer, is refused: the matcher re-derives the issuer hashes as the verifier
    /// does. The control is the same <c>CertID</c> under the right issuer.
    /// </summary>
    [Fact]
    public void ValidateAndFilterOcspResponse_TheSubjectsSerialUnderAnotherIssuer_IsRefused()
    {
        var (caCert, caKey)  = GenerateRsaCert("CN=Test Issuer 6");
        var (otherCa, _)     = GenerateRsaCert("CN=Other Issuer 6");
        var (subjectCert, _) = GenerateRsaCert("CN=Test Subject 6", caKey, caCert);

        byte[] underOther = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: caKey,
            embedCerts: null,
            thisUpdate: DateTime.UtcNow,
            nextUpdate: DateTime.UtcNow.AddHours(1),
            certIdIssuer: otherCa);
        Assert.Null(DssBuilder.ValidateAndFilterOcspResponse(underOther, subjectCert, caCert));
        Assert.NotNull(DssBuilder.LastOcspSubjectIssue);

        byte[] underIssuer = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: caKey,
            embedCerts: null,
            thisUpdate: DateTime.UtcNow,
            nextUpdate: DateTime.UtcNow.AddHours(1),
            certIdIssuer: caCert);
        Assert.Equal(underIssuer, DssBuilder.ValidateAndFilterOcspResponse(underIssuer, subjectCert, caCert));
    }

    /// <summary>
    /// A delegated responder signs under a certificate that is valid when the response is relied on
    /// (RFC 6960 §4.2.2.2 by way of RFC 5280 §6.1.3's validity period). Issued by the CA and carrying
    /// the OCSP-signing EKU, but expired or not yet valid: refused, where it was embedded. The control
    /// is the same kind of responder inside its period.
    /// </summary>
    [Theory]
    [InlineData(true,  "expired")]
    [InlineData(false, "not valid before")]
    public void ValidateAndFilterOcspResponse_ADelegatedResponderOutsideItsValidity_IsRefused(bool expired, string reason)
    {
        var (caCert, caKey)  = GenerateRsaCert("CN=Test Issuer 7");
        var (subjectCert, _) = GenerateRsaCert("CN=Test Subject 7", caKey, caCert);
        var (outside, outsideKey) = expired
            ? GenerateDelegatedResponder("CN=Expired Responder", caKey, caCert, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-1))
            : GenerateDelegatedResponder("CN=Future Responder", caKey, caCert, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(30));
        var (current, currentKey) =
            GenerateDelegatedResponder("CN=Current Responder", caKey, caCert, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        byte[] byOutside = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: outsideKey,
            embedCerts: new[] { outside },
            thisUpdate: DateTime.UtcNow,
            nextUpdate: DateTime.UtcNow.AddHours(1));
        Assert.Null(DssBuilder.ValidateAndFilterOcspResponse(byOutside, subjectCert, caCert));
        Assert.Contains(reason, DssBuilder.LastOcspSignatureIssue, StringComparison.Ordinal);
        Assert.False(DssBuilder.LastOcspSignatureValid);

        byte[] byCurrent = BuildBasicOcspRespBytes(
            caCert, subjectCert,
            signingKey: currentKey,
            embedCerts: new[] { current },
            thisUpdate: DateTime.UtcNow,
            nextUpdate: DateTime.UtcNow.AddHours(1));
        Assert.Equal(byCurrent, DssBuilder.ValidateAndFilterOcspResponse(byCurrent, subjectCert, caCert));
        Assert.True(DssBuilder.LastOcspSignatureValid);
    }

    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    /// <summary>A CA-issued responder certificate carrying id-kp-OCSPSigning, valid over the given window.</summary>
    private static (X509Certificate cert, AsymmetricKeyParameter key) GenerateDelegatedResponder(
        string commonName, AsymmetricKeyParameter issuerKey, X509Certificate issuerCert,
        DateTime notBefore, DateTime notAfter)
    {
        var random = new SecureRandom();
        var keyPair = Helpers.SigningTestCrypto.Keys();

        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        certGen.SetSubjectDN(new X509Name(commonName));
        certGen.SetIssuerDN(issuerCert.SubjectDN);
        certGen.SetNotBefore(notBefore);
        certGen.SetNotAfter(notAfter);
        certGen.SetPublicKey(keyPair.Public);
        certGen.AddExtension(X509Extensions.ExtendedKeyUsage, false,
            new ExtendedKeyUsage(KeyPurposeID.IdKPOcspSigning));
        var cert = certGen.Generate(new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            "SHA256WithRSA", issuerKey, random));
        return (cert, keyPair.Private);
    }

    private static (X509Certificate cert, AsymmetricKeyParameter key)
        GenerateRsaCert(string commonName,
            AsymmetricKeyParameter issuerKey = null,
            X509Certificate issuerCert = null)
    {
        var random = new SecureRandom();
        var keyPair = Helpers.SigningTestCrypto.Keys();

        var certGen = new X509V3CertificateGenerator();
        certGen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        certGen.SetSubjectDN(new X509Name(commonName));
        certGen.SetIssuerDN(issuerCert != null ? issuerCert.SubjectDN : new X509Name(commonName));
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);

        var signingKey = issuerKey ?? keyPair.Private;
        var sigFactory = new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            "SHA256WithRSA", signingKey, random);
        var cert = certGen.Generate(sigFactory);
        return (cert, keyPair.Private);
    }

    private static byte[] BuildBasicOcspRespBytes(
        X509Certificate issuer,
        X509Certificate subject,
        AsymmetricKeyParameter signingKey,
        X509Certificate[] embedCerts,
        DateTime thisUpdate,
        DateTime nextUpdate,
        X509Certificate certIdIssuer = null)
    {
        var respId = new RespID(issuer.SubjectDN);
        var basicGen = new BasicOcspRespGenerator(respId);

        // The CertID's issuer hashes are the issuer's unless a test wants the serial under another CA.
        var certId = new CertificateID(
            new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1),
            certIdIssuer ?? issuer, subject.SerialNumber);
        basicGen.AddResponse(certId, CertificateStatus.Good, thisUpdate, nextUpdate, null);

        var basicResp = basicGen.Generate(
            "SHA256WithRSA", signingKey, embedCerts, DateTime.UtcNow);

        // Wrap the BasicOCSPResp in an OCSPResp envelope for parsing parity
        // with what FetchOcspResponse would have got off the wire.
        var ocspGen = new OCSPRespGenerator();
        var ocspResp = ocspGen.Generate(OcspRespStatus.Successful, basicResp);
        return ocspResp.GetEncoded();
    }
}
