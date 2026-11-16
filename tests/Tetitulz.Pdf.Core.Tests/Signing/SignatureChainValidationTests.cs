// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// X.509 chain validation in <see cref="PdfSignatureValidator"/>: beyond building and
/// link-verifying the chain, each certificate's validity period, the signer's KeyUsage, and
/// issuer basic-constraints / keyCertSign are now checked. A certificate that fails any of these
/// yields <see cref="PdfSignatureChainStatus.Invalid"/> even when it is supplied as a trust anchor
/// (the cryptographic <c>IsValid</c> integrity result is independent and still reported).
/// </summary>
public class SignatureChainValidationTests
{
    private static byte[] Sign(byte[] pfx, string pwd)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("chain validation");
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = pwd,
            SignatureFieldName = "Sig",
        });
        return output.ToArray();
    }

    private static PdfSignatureValidationResult Validate(byte[] pdf, params X509Certificate[] anchors)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSignatureValidator.Validate(ms, anchors.Length == 0 ? null : anchors);
    }

    [Fact]
    public void ValidSelfSigned_AsTrustAnchor_IsTrusted()
    {
        var (pfx, pwd, cert) = TestCertificateGenerator.GenerateCustom();
        var r = Validate(Sign(pfx, pwd), cert);

        Assert.True(r.IsValid, $"Integrity: {r.ErrorMessage}");
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
    }

    [Fact]
    public void ValidSelfSigned_NoTrustAnchor_IsSelfSigned()
    {
        var (pfx, pwd, _) = TestCertificateGenerator.GenerateCustom();
        var r = Validate(Sign(pfx, pwd));

        Assert.Equal(PdfSignatureChainStatus.SelfSigned, r.ChainStatus);
    }

    [Fact]
    public void ExpiredCertificate_IsNotTrusted_EvenAsAnchor()
    {
        var (pfx, pwd, cert) = TestCertificateGenerator.GenerateCustom(
            notBefore: DateTime.UtcNow.AddYears(-2),
            notAfter: DateTime.UtcNow.AddYears(-1)); // expired

        var r = Validate(Sign(pfx, pwd), cert);

        // An expired certificate must never be Trusted (rejected either by the CMS verifier or by
        // the explicit validity check; both paths resolve to Invalid).
        Assert.Equal(PdfSignatureChainStatus.Invalid, r.ChainStatus);
    }

    [Fact]
    public void SignerWithNonSigningKeyUsage_IsNotTrusted()
    {
        // KeyUsage permits only keyEncipherment — not digitalSignature/nonRepudiation.
        var (pfx, pwd, cert) = TestCertificateGenerator.GenerateCustom(keyUsage: KeyUsage.KeyEncipherment);

        var r = Validate(Sign(pfx, pwd), cert);

        Assert.Equal(PdfSignatureChainStatus.Invalid, r.ChainStatus);
        Assert.Contains(r.Warnings, w => w.Contains("KeyUsage"));
    }

    [Fact]
    public void SignerWithDigitalSignatureKeyUsage_IsTrusted()
    {
        // A correctly-scoped signing certificate still validates.
        var (pfx, pwd, cert) = TestCertificateGenerator.GenerateCustom(keyUsage: KeyUsage.DigitalSignature);

        var r = Validate(Sign(pfx, pwd), cert);

        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
    }
}
