// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

public class PdfEncryptorTests
{
    [Fact]
    public void Encrypt_RC4_ProducesEncryptedPdf()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Encrypt Test");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var parameters = new EncryptionParameters
        {
            UserPassword = "user123",
            OwnerPassword = "owner456",
            Algorithm = EncryptionAlgorithm.RC4_128,
            Permissions = PdfPermissions.Print | PdfPermissions.Copy
        };

        PdfEncryptor.Encrypt(input, output, parameters);

        output.Position = 0;
        Assert.True(output.Length > 0);

        // Verify the output PDF has /Encrypt
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.IsEncrypted);
    }

    [Fact]
    public void Encrypt_AES128_ProducesEncryptedPdf()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("AES128 Test");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var parameters = new EncryptionParameters
        {
            UserPassword = "user",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All
        };

        PdfEncryptor.Encrypt(input, output, parameters);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.IsEncrypted);
    }

    [Fact]
    public void Encrypt_AES256_ProducesEncryptedPdf()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("AES256 Test");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var parameters = new EncryptionParameters
        {
            UserPassword = "user",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.AES256,
            Permissions = PdfPermissions.Print
        };

        PdfEncryptor.Encrypt(input, output, parameters);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.IsEncrypted);
    }

    [Fact]
    public void EncryptThenDecrypt_RC4_RoundTrip()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Round Trip RC4");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "pass",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.RC4_128
        });

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(encrypted, decrypted, "pass");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void EncryptThenDecrypt_AES128_RoundTrip()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Round Trip AES");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "pass",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.AES128
        });

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(encrypted, decrypted, "pass");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void EncryptThenDecrypt_AES256_RoundTrip()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Round Trip AES256");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "mypass",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.AES256
        });

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(encrypted, decrypted, "mypass");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void Decrypt_WithOwnerPassword_Works()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Owner Password Test");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "user",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.RC4_128
        });

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(encrypted, decrypted, "owner");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
    }

    [Fact]
    public void Encrypt_EmptyUserPassword_AllowsOpenWithoutPassword()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("No Password Open");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.RC4_128,
            Permissions = PdfPermissions.Print
        });

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        // Should decrypt with empty password
        PdfDecryptor.Decrypt(encrypted, decrypted, "");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
    }

    [Fact]
    public void Encrypt_RC4_40_ProducesEncryptedPdf()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("RC4-40 Test");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var parameters = new EncryptionParameters
        {
            UserPassword = "user",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.RC4_40,
            Permissions = PdfPermissions.Print | PdfPermissions.Copy
        };

        PdfEncryptor.Encrypt(input, output, parameters);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.IsEncrypted);

        // Verify V=1, R=2
        var enc = doc.EncryptionDictionary;
        Assert.Equal(1, enc.GetInteger(PdfName.V));
        Assert.Equal(2, enc.GetInteger(PdfName.R));
    }

    [Fact]
    public void EncryptThenDecrypt_RC4_40_RoundTrip()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Round Trip RC4-40");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "pass",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.RC4_40
        });

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(encrypted, decrypted, "pass");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void EncryptThenDecrypt_AES128_EncryptMetadataFalse_RoundTrip()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("EncryptMetadata False");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "pass",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.AES128,
            EncryptMetadata = false
        });

        encrypted.Position = 0;
        using var encDoc = PdfDocument.Open(encrypted);
        Assert.True(encDoc.IsEncrypted);
        // Verify /EncryptMetadata false is in the encrypt dict
        var emObj = encDoc.EncryptionDictionary[PdfName.EncryptMetadata];
        Assert.NotNull(emObj);
        Assert.IsType<PdfBoolean>(emObj);
        Assert.False(((PdfBoolean)emObj).Value);
        encDoc.Dispose();

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(encrypted, decrypted, "pass");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void EncryptThenDecrypt_AES256_WithOwnerPassword_RoundTrip()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("AES256 Owner");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "user",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.AES256
        });

        encrypted.Position = 0;
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(encrypted, decrypted, "owner");

        decrypted.Position = 0;
        using var doc = PdfDocument.Open(decrypted);
        Assert.False(doc.IsEncrypted);
        Assert.True(doc.Pages.Count > 0);
    }

    /// <summary>
    /// Every member keeps the value it shipped with. A member's value is compiled into each caller
    /// as a constant, so renumbering one changes what a caller built against an earlier assembly
    /// asks for: inserting <see cref="EncryptionAlgorithm.RC4_40R3"/> before <c>AES128</c> made a
    /// request for AES-128 one for 40-bit RC4. A new member goes last and is added here.
    /// </summary>
    [Fact]
    public void EncryptionAlgorithm_HasExpectedValues()
    {
        Assert.Equal(0, (int)EncryptionAlgorithm.RC4_40);
        Assert.Equal(1, (int)EncryptionAlgorithm.RC4_128);
        Assert.Equal(2, (int)EncryptionAlgorithm.AES128);
        Assert.Equal(3, (int)EncryptionAlgorithm.AES256);
        Assert.Equal(4, (int)EncryptionAlgorithm.AES256GCM);
        Assert.Equal(5, (int)EncryptionAlgorithm.Identity);
        Assert.Equal(6, (int)EncryptionAlgorithm.RC4_40R3);
        Assert.Equal(7, System.Enum.GetValues(typeof(EncryptionAlgorithm)).Length);
    }

    [Fact]
    public void EncryptionParameters_DefaultEncryptMetadata_IsTrue()
    {
        var p = new EncryptionParameters();
        Assert.True(p.EncryptMetadata);
    }

    // Per PDF spec ֲ§7.6.3.3 an empty owner password is valid; key derivation falls
    // back to the user password in that case.
    [Fact]
    public void Encrypt_EmptyOwnerPassword_IsPermitted()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Empty Owner Pwd");

        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), encrypted, new EncryptionParameters
        {
            UserPassword = "user",
            OwnerPassword = "",          // empty — must not throw
            Algorithm = EncryptionAlgorithm.RC4_128
        });

        encrypted.Position = 0;
        using var doc = PdfDocument.Open(encrypted);
        Assert.True(doc.IsEncrypted);
    }

    [Fact]
    public void Encrypt_NullOwnerPassword_Throws()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Null Owner Pwd");

        Assert.Throws<ArgumentException>(() =>
            PdfEncryptor.Encrypt(new MemoryStream(pdfBytes), new MemoryStream(),
                new EncryptionParameters
                {
                    UserPassword = "user",
                    OwnerPassword = null,
                    Algorithm = EncryptionAlgorithm.RC4_128
                }));
    }
}
