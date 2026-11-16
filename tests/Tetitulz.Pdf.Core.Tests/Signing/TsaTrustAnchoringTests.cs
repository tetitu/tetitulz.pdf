// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Timestamping;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// An RFC 3161 TimeStampToken carries its own TSA certificate, so verifying the token against
/// that certificate proves only internal consistency — never who issued it. Anyone can mint a
/// self-signed certificate bearing the id-kp-timeStamping EKU and assert any GenTime.
/// <para>
/// This previously went unchecked, and the asserted time flowed straight into the
/// certificate-validity assessment. The consequence was concrete: a signer whose certificate had
/// <b>expired</b> could embed a self-made timestamp claiming a GenTime inside the old validity
/// window and the chain would be assessed as valid — defeating the "expired certificate fails
/// closed" property the validator documents.
/// </para>
/// </summary>
public class TsaTrustAnchoringTests
{
    private static readonly byte[] Data = System.Text.Encoding.ASCII.GetBytes("timestamped payload");

    [Fact]
    public void SelfSignedTsa_VerifiesButIsNotTrusted()
    {
        var tsa = SigningTestCrypto.TsaCert();
        byte[] token = SigningTestCrypto.TimestampToken(Data, tsa, DateTime.UtcNow);

        var result = TsaTokenVerifier.VerifyTimestampToken(token, Data);

        Assert.True(result.IsValid, "Token integrity must still verify: " + result.Error);
        Assert.False(result.IsTrusted, "A self-signed TSA must never be reported as trusted.");
        Assert.NotEqual(PdfSignatureChainStatus.Trusted, result.ChainStatus);
    }

    [Fact]
    public void SelfSignedTsa_IsTrustedOnlyWhenExplicitlyAnchored()
    {
        var tsa = SigningTestCrypto.TsaCert();
        byte[] token = SigningTestCrypto.TimestampToken(Data, tsa, DateTime.UtcNow);

        var result = TsaTokenVerifier.VerifyTimestampToken(
            token, Data, new[] { tsa.Cert });

        Assert.True(result.IsValid);
        Assert.True(result.IsTrusted, "An explicitly anchored TSA certificate is trusted.");
        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
    }

    [Fact]
    public void TsaUnderTrustedRoot_IsTrusted()
    {
        var root = SigningTestCrypto.Ca("CN=TSA Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Issued TSA");
        byte[] token = SigningTestCrypto.TimestampToken(Data, tsa, DateTime.UtcNow, root.Cert);

        var result = TsaTokenVerifier.VerifyTimestampToken(token, Data, new[] { root.Cert });

        Assert.True(result.IsValid, result.Error);
        Assert.True(result.IsTrusted);
        Assert.Equal(PdfSignatureChainStatus.Trusted, result.ChainStatus);
    }

    [Fact]
    public void TsaUnderUntrustedRoot_IsNotTrusted()
    {
        var root = SigningTestCrypto.Ca("CN=TSA Root");
        var otherRoot = SigningTestCrypto.Ca("CN=Unrelated Root");
        var tsa = SigningTestCrypto.TsaCert(root, "CN=Issued TSA");
        byte[] token = SigningTestCrypto.TimestampToken(Data, tsa, DateTime.UtcNow, root.Cert);

        var result = TsaTokenVerifier.VerifyTimestampToken(token, Data, new[] { otherRoot.Cert });

        Assert.True(result.IsValid);
        Assert.False(result.IsTrusted);
        Assert.Equal(PdfSignatureChainStatus.UntrustedRoot, result.ChainStatus);
    }

    [Fact]
    public void TsaWithoutTimestampingEku_IsRejectedOutright()
    {
        var tsa = SigningTestCrypto.TsaCert(withEku: false);

        // BouncyCastle's own token generator refuses to build a token from a certificate lacking
        // the timeStamping EKU, which is itself the RFC 3161 §2.3 rule. Either the token cannot be
        // produced at all, or — if it somehow is — our verifier must reject it. Both outcomes are
        // acceptable; silently accepting is not.
        try
        {
            byte[] token = SigningTestCrypto.TimestampToken(Data, tsa, DateTime.UtcNow);
            var result = TsaTokenVerifier.VerifyTimestampToken(token, Data, new[] { tsa.Cert });
            Assert.False(result.IsValid);
            Assert.Contains("timeStamping", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // Generation refused — the EKU rule held one layer earlier.
        }
    }

    [Fact]
    public void TamperedPayload_FailsMessageImprint()
    {
        var tsa = SigningTestCrypto.TsaCert();
        byte[] token = SigningTestCrypto.TimestampToken(Data, tsa, DateTime.UtcNow);

        var result = TsaTokenVerifier.VerifyTimestampToken(
            token, System.Text.Encoding.ASCII.GetBytes("different payload"), new[] { tsa.Cert });

        Assert.False(result.IsValid);
        Assert.False(result.IsTrusted);
    }

    /// <summary>
    /// The end-to-end property: an EXPIRED signer certificate must not be rescued by a timestamp
    /// nobody vouches for. The chain walk is what enforces it, so this asserts on the shared
    /// walker with the untrusted token's claimed time versus the honest "now".
    /// </summary>
    [Fact]
    public void ExpiredSigner_BackdatedByUntrustedTsa_StillFailsAtHonestTime()
    {
        var root = SigningTestCrypto.Ca("CN=Signer Root");
        var expired = SigningTestCrypto.Leaf(root, "CN=Expired Signer",
            notBefore: DateTime.UtcNow.AddYears(-5),
            notAfter: DateTime.UtcNow.AddYears(-2));

        var store = Org.BouncyCastle.Utilities.Collections.CollectionUtilities.CreateStore(
            new List<X509Certificate> { expired.Cert, root.Cert });

        // The attacker's claimed time — inside the expired certificate's old window.
        var backdated = DateTime.UtcNow.AddYears(-3);
        var atClaimedTime = CertificateChainValidator.Resolve(
            expired.Cert, store, new[] { root.Cert }, backdated, new List<string>());
        Assert.Equal(PdfSignatureChainStatus.Trusted, atClaimedTime);

        // Assessed honestly at "now" — which is what the verifier does when no TRUSTED timestamp
        // is present — the certificate is outside its validity period and the chain fails.
        var warnings = new List<string>();
        var atNow = CertificateChainValidator.Resolve(
            expired.Cert, store, new[] { root.Cert }, DateTime.UtcNow, warnings);
        Assert.Equal(PdfSignatureChainStatus.Invalid, atNow);
        Assert.Contains(warnings, w => w.Contains("validity period"));
    }

    [Fact]
    public void RequireTrustedTimestamp_IsOnByDefault()
    {
        Assert.True(new SignatureVerificationOptions().RequireTrustedTimestamp);
    }

    [Fact]
    public void TimestampAnchors_FallBackToSignerAnchors()
    {
        var root = SigningTestCrypto.Ca();
        var options = new SignatureVerificationOptions { TrustAnchors = new[] { root.Cert } };

        Assert.Same(options.TrustAnchors, options.EffectiveTimestampAnchors);
    }

    [Fact]
    public void TimestampAnchors_OverrideSignerAnchorsWhenSet()
    {
        var signerRoot = SigningTestCrypto.Ca("CN=Signer Root");
        var tsaRoot = SigningTestCrypto.Ca("CN=TSA Root");
        var options = new SignatureVerificationOptions
        {
            TrustAnchors = new[] { signerRoot.Cert },
            TimestampTrustAnchors = new[] { tsaRoot.Cert },
        };

        Assert.Same(options.TimestampTrustAnchors, options.EffectiveTimestampAnchors);
    }
}
