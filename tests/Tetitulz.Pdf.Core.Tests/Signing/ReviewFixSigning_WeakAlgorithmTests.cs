// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
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
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Review fix (finding #5): CmsSignatureVerifier must FLAG SHA-1 (and weak signature-algorithm
/// OIDs) via <c>WeakAlgorithm</c> so caller policy can act, and optionally hard-reject them under
/// <see cref="CmsSignatureVerifier.RejectWeakAlgorithms"/>. Strong SHA-256 paths stay unflagged and
/// fully valid; MD5/MD2 digests remain hard-rejected.
/// </summary>
[Collection(WeakAlgorithmPolicyCollection.Name)]
public class ReviewFixSigning_WeakAlgorithmTests
{
    [Fact]
    public void Sha256_NotFlaggedWeak_AndValid()
    {
        var (cert, key) = SelfSigned("CN=Strong");
        byte[] content = System.Text.Encoding.ASCII.GetBytes("strong content");
        byte[] cms = SignCms(content, cert, key, CmsSignedDataGenerator.DigestSha256);

        var r = CmsSignatureVerifier.VerifyDetachedSignature(cms, content);

        Assert.True(r.IsValid, r.Error);
        Assert.False(r.WeakAlgorithm);
        Assert.Null(r.WeakAlgorithmReason);
    }

    /// <summary>
    /// With rejection explicitly disabled, a SHA-1 CMS signature is integrity-valid and flagged.
    /// </summary>
    /// <remarks>
    /// RENAMED from <c>Sha1_FlaggedWeak_ButStillValidByDefault</c> on 2026-08-12: the body sets
    /// <c>RejectWeakAlgorithms = false</c> explicitly, and that is no longer the default, so the
    /// old name asserted something the test never exercised. It survives the polarity flip
    /// precisely because it opts out — which is what makes it the control for the new default.
    /// </remarks>
    [Fact]
    public void Sha1_FlaggedWeak_ButStillValidWhenRejectionIsDisabled()
    {
        var (cert, key) = SelfSigned("CN=Sha1 Signer");
        byte[] content = System.Text.Encoding.ASCII.GetBytes("legacy sha1 content");
        byte[] cms = SignCms(content, cert, key, CmsSignedDataGenerator.DigestSha1);

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = false; // opt out; true is the default since 2026-08-12
            var r = CmsSignatureVerifier.VerifyDetachedSignature(cms, content);

            Assert.True(r.IsValid, r.Error);               // opted out: still integrity-valid
            Assert.True(r.WeakAlgorithm);                  // but surfaced as weak
            Assert.NotNull(r.WeakAlgorithmReason);
            Assert.Contains("SHA-1", r.WeakAlgorithmReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = saved;
        }
    }

    [Fact]
    public void Sha1_RejectedUnderStrictPolicy()
    {
        var (cert, key) = SelfSigned("CN=Sha1 Strict");
        byte[] content = System.Text.Encoding.ASCII.GetBytes("strict policy content");
        byte[] cms = SignCms(content, cert, key, CmsSignedDataGenerator.DigestSha1);

        bool saved = CmsSignatureVerifier.RejectWeakAlgorithms;
        try
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = true;
            var r = CmsSignatureVerifier.VerifyDetachedSignature(cms, content);

            Assert.False(r.IsValid);
            Assert.True(r.WeakAlgorithm);
            Assert.Contains("policy", r.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CmsSignatureVerifier.RejectWeakAlgorithms = saved;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static (X509Certificate cert, AsymmetricKeyParameter key) SelfSigned(string cn)
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

    private static byte[] SignCms(byte[] content, X509Certificate cert, AsymmetricKeyParameter key, string digestOid)
    {
        var gen = new CmsSignedDataGenerator();
        gen.AddSigner(key, cert, digestOid); // no signed attributes → signs content directly
        gen.AddCertificates(CollectionUtilities.CreateStore(new List<X509Certificate> { cert }));
        var signed = gen.Generate(new CmsProcessableByteArray(content), false);
        return signed.GetEncoded();
    }
}
