// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A PKCS#12 file that cannot be opened is reported as <see cref="PdfSigningException"/>, the
/// exception every other signing failure raises.
/// </summary>
/// <remarks>
/// A wrong password is the ordinary way to get here, and BouncyCastle reports it as an
/// <see cref="IOException"/> — so a host catching the library's exceptions around a signing call let
/// "wrong password" through as an unexpected I/O failure, and a host catching <c>IOException</c> for
/// the file it wrote to caught a password prompt instead.
/// </remarks>
public sealed class SigningCertificateLoadTests
{
    private static readonly Lazy<(byte[] Pfx, string Password)> Keys = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Certificate Load Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Certificate Load Signer");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }));

    [Fact]
    public void AWrongPfxPassword_IsASigningFailureNamingThePassword()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(() => Sign(Keys.Value.Pfx, "not-the-password", output));

        Assert.Contains("wrong password", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void BytesThatAreNotPkcs12_AreASigningFailure()
    {
        byte[] notPkcs12 = { 0x30, 0x03, 0x02, 0x01, 0x05 };   // a DER INTEGER in a SEQUENCE

        var ex = Assert.Throws<PdfSigningException>(() => Sign(notPkcs12, Keys.Value.Password, new MemoryStream()));

        Assert.Contains("PKCS#12", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The counter-signer opens its PFX through the same loader.</summary>
    [Fact]
    public void AWrongPfxPasswordForACounterSignature_IsASigningFailure()
    {
        byte[] signed = Sign(Keys.Value.Pfx, Keys.Value.Password, new MemoryStream());

        var ex = Assert.Throws<PdfSigningException>(() => PdfCounterSigner.AddCounterSignature(
            new MemoryStream(signed), new MemoryStream(), "Signature1", Keys.Value.Pfx, "not-the-password"));

        Assert.Contains("wrong password", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: the right password signs, and the signature verifies.</summary>
    [Fact]
    public void TheRightPasswordSigns()
    {
        byte[] signed = Sign(Keys.Value.Pfx, Keys.Value.Password, new MemoryStream());

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        var result = Assert.Single(new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions()));
        Assert.True(result.IsIntegrityValid);
    }

    private static byte[] Sign(byte[] pfx, string password, MemoryStream output)
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            SignatureFieldName = "Signature1",
        });
        return output.ToArray();
    }
}
