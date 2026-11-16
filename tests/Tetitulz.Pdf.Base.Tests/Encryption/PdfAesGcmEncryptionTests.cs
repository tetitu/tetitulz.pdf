// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// AES-GCM (ISO/TS 32003, PDF 2.0's <c>/CFM /AESV4</c>) through the full
/// <see cref="PdfEncryptor"/>/<see cref="PdfDecryptor"/> pipeline — the security handler's V=6/R=7
/// declaration, the encrypt dictionary shape, the minimum-version gate, and the round trip that
/// matters most for an authenticated cipher: tampered ciphertext must fail closed rather than
/// decrypt to garbage silently. <see cref="PdfGcmCipherTests"/> covers the cipher primitive itself
/// against NIST test vectors; these tests are about the PDF-level wiring around it.
/// </summary>
public class PdfAesGcmEncryptionTests
{
    private const string UserPassword = "user-secret";
    private const string OwnerPassword = "owner-secret";

    private static byte[] Encrypt(byte[] plain, string user = UserPassword, string owner = OwnerPassword) =>
        EncryptWithVersion(plain, out _, user, owner);

    private static byte[] EncryptWithVersion(
        byte[] plain, out PdfVersion writtenVersion, string user = UserPassword, string owner = OwnerPassword)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = user,
            OwnerPassword = owner,
            Algorithm = EncryptionAlgorithm.AES256GCM,
        });
        byte[] result = output.ToArray();

        using var doc = PdfDocument.Open(new MemoryStream(result));
        writtenVersion = doc.Version;
        return result;
    }

    [Fact]
    public void Encrypt_WritesVAndRForAesGcm()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("v6r7"));

        var info = PdfDecryptor.GetEncryptionInfo(new MemoryStream(encrypted));
        Assert.Equal(6, info.V);
        Assert.Equal(7, info.R);
    }

    [Fact]
    public void Encrypt_WritesCfmAesv4InTheCryptFilterDictionary()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("cfm"));

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var encryptDict = doc.ResolveObject(doc.Trailer[PdfName.Encrypt]) as PdfDictionary;
        Assert.NotNull(encryptDict);

        var cfDict = doc.ResolveObject(encryptDict[PdfName.CF]) as PdfDictionary;
        var stdCf = doc.ResolveObject(cfDict?[PdfName.StdCF]) as PdfDictionary;
        var cfm = doc.ResolveObject(stdCf?[PdfName.CFM]) as PdfName;

        Assert.Equal("AESV4", cfm?.Value);
    }

    /// <summary>PDF 2.0-only per ISO/TS 32003's own title — the same floor AES-256 already uses.</summary>
    [Fact]
    public void Encrypt_RaisesVersionToPdf20()
    {
        byte[] pdf14 = MinimalPdfBuilder.CreateMinimalPdf("upgrade-me"); // header is %PDF-1.4
        EncryptWithVersion(pdf14, out PdfVersion written);

        Assert.Equal(2, written.Major);
        Assert.Equal(0, written.Minor);
    }

    [Fact]
    public void Sign_ThenVerify_UserPassword_Decrypts()
    {
        byte[] plain = MinimalPdfBuilder.CreateMinimalPdf("Hello AES-GCM");
        byte[] encrypted = Encrypt(plain);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, UserPassword);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string content = System.Text.Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Assert.Contains("Hello AES-GCM", content);
    }

    [Fact]
    public void Decrypt_OwnerPassword_AlsoDecrypts()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("owner-open-marker"));

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, OwnerPassword);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string content = System.Text.Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Assert.Contains("owner-open-marker", content);
    }

    [Fact]
    public void Decrypt_WrongPassword_Throws()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("wrong-pw"));

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        Assert.Throws<PdfException>(() => PdfDecryptor.Decrypt(input, output, "not-the-password"));
    }

    /// <summary>
    /// The property AES-GCM exists to provide and AES-CBC does not: tampering with even one
    /// ciphertext byte must be DETECTED, not silently decrypted to different-but-plausible bytes.
    /// </summary>
    [Fact]
    public void Decrypt_TamperedStreamContent_ThrowsRatherThanReturningGarbage()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf(
            "This content stream is long enough to survive a single flipped byte structurally."));

        // Flip a byte inside the content stream's ciphertext. The exact offset does not matter —
        // GCM authenticates the whole ciphertext, so any single-byte change anywhere in it must
        // fail the tag check.
        byte[] tampered = (byte[])encrypted.Clone();
        int streamStart = System.Text.Encoding.ASCII.GetString(tampered).IndexOf("stream\n") + "stream\n".Length;
        tampered[streamStart + 20] ^= 0xFF;

        using var input = new MemoryStream(tampered);
        using var output = new MemoryStream();
        Assert.Throws<PdfException>(() => PdfDecryptor.Decrypt(input, output, UserPassword));
    }

    [Fact]
    public void RoundTrip_ReEncryptsWithAesGcm()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("round-trip"));
        var before = PdfDecryptor.GetEncryptionInfo(new MemoryStream(encrypted));

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run(
            input, output,
            new EncryptedRoundTripOptions { OpenPassword = UserPassword },
            (s, d) => s.CopyTo(d));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(EncryptionAlgorithm.AES256GCM, result.AlgorithmIn);
        Assert.Equal(EncryptionAlgorithm.AES256GCM, result.AlgorithmOut);

        var after = PdfDecryptor.GetEncryptionInfo(new MemoryStream(output.ToArray()));
        Assert.Equal(before.V, after.V);
        Assert.Equal(before.R, after.R);

        // And the round-tripped output must still be genuinely readable.
        using var decOutput = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(output.ToArray()), decOutput, UserPassword);
        using var doc = PdfDocument.Open(new MemoryStream(decOutput.ToArray()));
        string content = System.Text.Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Assert.Contains("round-trip", content);
    }
}
