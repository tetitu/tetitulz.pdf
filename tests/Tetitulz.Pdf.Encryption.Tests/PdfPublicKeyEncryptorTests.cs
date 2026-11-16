// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// CMS public-key (certificate-based) encryption — PDF 1.7 §7.6.5.
///
/// <para><b>This class had no tests at all.</b> It is public API in the assembly that holds the
/// library's cryptography, and nothing anywhere exercised it. These are the first, and they assert
/// on the parsed <c>/Encrypt</c> dictionary and on the bytes — not on "a PDF came back", which an
/// encryptor that did nothing would also produce.</para>
///
/// <para>Certificates are generated in-process with <see cref="CertificateRequest"/>, so the tests
/// are self-contained: no fixture files, no expiry to age out, and nothing secret in the repo.</para>
/// </summary>
public class PdfPublicKeyEncryptorTests
{
    /// <summary>A self-signed RSA certificate, DER-encoded, as the encryptor takes it.</summary>
    private static byte[] MakeRecipientCertificate(string commonName = "CN=Test Recipient")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(commonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return cert.Export(X509ContentType.Cert);
    }

    private static byte[] Encrypt(byte[] pdf, byte[] cert,
                                  EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256,
                                  PdfPermissions permissions = PdfPermissions.All)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, cert, permissions, algorithm);
        return output.ToArray();
    }

    /// <summary>The /Encrypt dictionary of an encrypted document, read back from the output.</summary>
    private static PdfDictionary EncryptDictionary(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        Assert.True(doc.IsEncrypted, "the output does not declare itself encrypted");
        var dict = doc.EncryptionDictionary;
        Assert.NotNull(dict);
        return dict!;
    }

    // ================================================================
    //  What the encryptor writes
    // ================================================================

    [Fact]
    public void Encrypt_WritesTheAdobePubSecHandler()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf(), MakeRecipientCertificate());

        var dict = EncryptDictionary(encrypted);
        Assert.Equal("Adobe.PubSec", dict.GetName(PdfName.Filter)?.Value);
    }

    /// <summary>
    /// §7.6.5: <c>/adbe.pkcs7.s4</c> is the legacy RC4 form and <c>/adbe.pkcs7.s5</c> the
    /// AES one. A reader picks its decryption path from this name, so getting it wrong produces a
    /// document that no viewer can open even with the right private key.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128, "adbe.pkcs7.s4")]
    [InlineData(EncryptionAlgorithm.AES128, "adbe.pkcs7.s5")]
    [InlineData(EncryptionAlgorithm.AES256, "adbe.pkcs7.s5")]
    public void Encrypt_WritesTheSubFilterMatchingTheAlgorithm(
        EncryptionAlgorithm algorithm, string expectedSubFilter)
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf(), MakeRecipientCertificate(), algorithm);

        var dict = EncryptDictionary(encrypted);
        Assert.Equal(expectedSubFilter, dict.GetName(PdfName.SubFilter)?.Value);
    }

    /// <summary>
    /// One CMS blob per recipient — that is what lets each of them open the document independently.
    /// A single blob for two recipients would lock one of them out.
    /// </summary>
    [Fact]
    public void Encrypt_WritesOneRecipientBlobPerCertificate()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] first = MakeRecipientCertificate("CN=First");
        byte[] second = MakeRecipientCertificate("CN=Second");

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, new[] { first, second });

        var dict = EncryptDictionary(output.ToArray());
        var recipients = dict[new PdfName("Recipients")] as PdfArray;
        Assert.NotNull(recipients);
        Assert.Equal(2, recipients!.Count);

        // Two different certificates wrap the same seed differently, so the blobs must differ —
        // identical blobs would mean the recipient's key was never actually used.
        var a = Assert.IsType<PdfString>(recipients[0]);
        var b = Assert.IsType<PdfString>(recipients[1]);
        Assert.NotEqual(Convert.ToBase64String(a.RawBytes), Convert.ToBase64String(b.RawBytes));
    }

    /// <summary>
    /// The point of the exercise: the page's text must not survive as plaintext in the output.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void Encrypt_EnciphersTheContent(EncryptionAlgorithm algorithm)
    {
        const string marker = "SecretPayloadMarker";
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(marker);
        Assert.Contains(marker, Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        byte[] encrypted = Encrypt(pdf, MakeRecipientCertificate(), algorithm);

        Assert.DoesNotContain(marker, Encoding.Latin1.GetString(encrypted), StringComparison.Ordinal);
    }

    /// <summary>
    /// /V ≥ 4 selects crypt filters, so /CF, /StmF and /StrF must all be present — a reader that
    /// finds /V 4 and no /CF has nothing to decrypt with.
    /// </summary>
    [Fact]
    public void Encrypt_WithAes_WritesTheCryptFilterTriple()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf(), MakeRecipientCertificate(),
                                   EncryptionAlgorithm.AES256);

        var dict = EncryptDictionary(encrypted);
        Assert.True((dict.GetInteger(PdfName.V) ?? 0) >= 4);
        Assert.NotNull(dict[PdfName.CF] as PdfDictionary);
        Assert.NotNull(dict.GetName(PdfName.StmF));
        Assert.NotNull(dict.GetName(PdfName.StrF));
    }

    [Fact]
    public void Encrypt_RecordsThePermissionMask()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf(), MakeRecipientCertificate(),
                                   EncryptionAlgorithm.AES256, PdfPermissions.Print);

        var dict = EncryptDictionary(encrypted);
        int p = dict.GetInteger(PdfName.P) ?? 0;

        // Printing allowed, content copying not — the two bits that differ between the masks.
        Assert.True((p & (int)PdfPermissions.Print) != 0, "the print bit should be set");
        Assert.True((p & (int)PdfPermissions.Copy) == 0, "the copy bit should be clear");
    }

    // ================================================================
    //  Refusals
    // ================================================================

    /// <summary>
    /// RC4-40's key is too short to carry the derived document key, so it is refused rather than
    /// silently widened to something the /SubFilter does not describe.
    /// </summary>
    [Fact]
    public void Encrypt_WithRc440_IsRefused()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<NotSupportedException>(() =>
            PdfPublicKeyEncryptor.Encrypt(input, output, MakeRecipientCertificate(),
                PdfPermissions.All, EncryptionAlgorithm.RC4_40));
        Assert.Contains("RC4-40", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encrypt_WithNoRecipients_IsRefused()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() =>
            PdfPublicKeyEncryptor.Encrypt(input, output, Array.Empty<byte[]>()));
        Assert.Equal("recipientCertsDer", ex.ParamName);
    }

    /// <summary>
    /// Encrypting an already-encrypted document would layer one scheme over another and produce a
    /// file neither key opens.
    /// </summary>
    [Fact]
    public void Encrypt_OnAnAlreadyEncryptedDocument_IsRefused()
    {
        byte[] once = Encrypt(MinimalPdfBuilder.CreateMinimalPdf(), MakeRecipientCertificate());

        using var input = new MemoryStream(once);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() =>
            PdfPublicKeyEncryptor.Encrypt(input, output, MakeRecipientCertificate()));
        Assert.Contains("already encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Encrypt_WithNullArguments_Throws()
    {
        using var stream = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        byte[] cert = MakeRecipientCertificate();

        Assert.Throws<ArgumentNullException>(() =>
            PdfPublicKeyEncryptor.Encrypt(null!, stream, cert));
        Assert.Throws<ArgumentNullException>(() =>
            PdfPublicKeyEncryptor.Encrypt(stream, null!, cert));
        Assert.Throws<ArgumentNullException>(() =>
            PdfPublicKeyEncryptor.Encrypt(stream, stream, (byte[])null!));
    }

    /// <summary>
    /// A certificate that is not parseable DER must fail loudly. Producing a document with an
    /// unusable recipient blob would look like success and be undecryptable by anyone.
    /// </summary>
    [Fact]
    public void Encrypt_WithMalformedCertificate_Throws()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        var garbage = new byte[] { 0x30, 0x82, 0xFF, 0xFF, 0x00, 0x01, 0x02 };

        Assert.ThrowsAny<Exception>(() =>
            PdfPublicKeyEncryptor.Encrypt(input, output, garbage));
    }
}
