// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The two encryptors in this assembly must write the same <c>/P</c> from the same
/// <see cref="PdfPermissions"/>, and the certificate one must wrap a seed its recipient can
/// actually recover — neither of which was asked while ISO 32000-1 Table 22 was transcribed twice
/// and the recipient certificate was loaded through an obsolete, undisposed constructor.
/// </summary>
public class PublicKeyPermissionAndHandlerTests
{
    // ================================================================
    //  /P — one transcription of Table 22, checked against the table itself
    // ================================================================

    /// <summary>
    /// The expected <c>/P</c>, derived here from ISO 32000-1 Table 22 rather than from either
    /// encryptor: bits 1-2 shall be 0, bits 7-8 and 13-32 shall be 1, and the granted permissions
    /// occupy bits 3-6 and 9-12. Comparing the two encryptors to EACH OTHER would have passed
    /// while both were wrong, which is how <c>PdfEncryptor</c> once granted printing on every
    /// document regardless of the caller's flags.
    /// </summary>
    private static int ExpectedPerTable22(PdfPermissions permissions)
    {
        int p = unchecked((int)0xFFFFF0C0);   // bits 7-8 and 13-32 set; 1-2 and the rest clear
        if (permissions.HasFlag(PdfPermissions.Print))                    p |= 1 << 2;   // bit 3
        if (permissions.HasFlag(PdfPermissions.Modify))                   p |= 1 << 3;   // bit 4
        if (permissions.HasFlag(PdfPermissions.Copy))                     p |= 1 << 4;   // bit 5
        if (permissions.HasFlag(PdfPermissions.AnnotateAndForms))         p |= 1 << 5;   // bit 6
        if (permissions.HasFlag(PdfPermissions.FillForms))                p |= 1 << 8;   // bit 9
        if (permissions.HasFlag(PdfPermissions.ExtractForAccessibility))  p |= 1 << 9;   // bit 10
        if (permissions.HasFlag(PdfPermissions.Assemble))                 p |= 1 << 10;  // bit 11
        if (permissions.HasFlag(PdfPermissions.PrintHighQuality))         p |= 1 << 11;  // bit 12
        return p;
    }

    [Theory]
    [InlineData(PdfPermissions.None)]
    [InlineData(PdfPermissions.All)]
    [InlineData(PdfPermissions.Print)]
    [InlineData(PdfPermissions.Print | PdfPermissions.Copy)]
    [InlineData(PdfPermissions.Modify | PdfPermissions.Assemble)]
    [InlineData(PdfPermissions.FillForms | PdfPermissions.ExtractForAccessibility | PdfPermissions.PrintHighQuality)]
    public void BothEncryptorsWriteTheTable22PermissionValue(PdfPermissions permissions)
    {
        int expected = ExpectedPerTable22(permissions);

        Assert.Equal(expected, CertificateEncryptedP(permissions));
        Assert.Equal(expected, PasswordEncryptedP(permissions));
    }

    private static int CertificateEncryptedP(PdfPermissions permissions)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, RecipientCertificate(out _),
            permissions, EncryptionAlgorithm.AES256);
        return PValueOf(output.ToArray());
    }

    private static int PasswordEncryptedP(PdfPermissions permissions)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = "u",
            OwnerPassword = "o",
            Algorithm     = EncryptionAlgorithm.AES256,
            Permissions   = permissions,
        });
        return PValueOf(output.ToArray());
    }

    // ================================================================
    //  The recipient certificate is disposed after wrapping — not before it is used
    // ================================================================

    /// <summary>
    /// <c>WrapForRecipient</c> now disposes the <see cref="X509Certificate2"/> it loads (it held an
    /// unmanaged certificate context per recipient per call until the finalizer ran). The risk in
    /// that change is disposing something BouncyCastle still needs, which no "an encrypted file
    /// came back" assertion would catch — so this decrypts the CMS blob with the recipient's
    /// private key and checks the seed content it wrapped: a 20-byte seed followed by 4 bytes of
    /// big-endian permissions (ISO 32000-2 Table 27). This test used to read the permissions from
    /// the first four bytes, where the encryptor used to put them — the same transcription error on
    /// both sides, which is what <c>PublicKeyEnvelopeLayoutTests</c> exists to rule out.
    /// </summary>
    [Fact]
    public void Encrypt_WrapsASeedTheRecipientsPrivateKeyCanRecover()
    {
        byte[] certDer = RecipientCertificate(out RSAParameters privateKey);

        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, certDer,
            PdfPermissions.Print, EncryptionAlgorithm.AES256);

        byte[] blob = FirstRecipientBlob(output.ToArray());
        var enveloped = new CmsEnvelopedData(blob);

        byte[]? content = null;
        foreach (RecipientInformation recipient in enveloped.GetRecipientInfos().GetRecipients())
            content = recipient.GetContent(DotNetUtilities.GetRsaKeyPair(privateKey).Private);

        Assert.NotNull(content);
        Assert.Equal(24, content.Length);   // 20-byte seed + 4-byte permission mask

        int wrappedPermissions = (content[20] << 24) | (content[21] << 16) | (content[22] << 8) | content[23];
        Assert.Equal(ExpectedPerTable22(PdfPermissions.Print), wrappedPermissions);
    }

    // ================================================================
    //  helpers
    // ================================================================

    private static byte[] RecipientCertificate(out RSAParameters privateKey)
    {
        using var rsa = RSA.Create(2048);
        privateKey = rsa.ExportParameters(includePrivateParameters: true);
        var request = new CertificateRequest("CN=Permission Test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return cert.Export(X509ContentType.Cert);
    }

    private static int PValueOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return doc.EncryptionDictionary.GetInteger(PdfName.P) ?? throw new InvalidOperationException("the output has no /P");
    }

    private static byte[] FirstRecipientBlob(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var recipients = (PdfArray)doc.EncryptionDictionary[new PdfName("Recipients")];
        return ((PdfString)recipients[0]).RawBytes;
    }
}
