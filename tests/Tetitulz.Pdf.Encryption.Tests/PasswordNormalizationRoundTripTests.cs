// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// SASLprep (ISO 32000-2 §7.6.4.3.3) is applied to a revision-5+ password on <b>both</b> sides, at
/// the one key-derivation site each, and to a revision 2–4 password on <b>neither</b>.
/// </summary>
/// <remarks>
/// <para>Two mirror-image defects hide behind ASCII passwords, which the normalisation leaves
/// unchanged. An encryptor hashing the raw string while every decrypt door normalises would make
/// an AES-256 password holding a no-break space, a soft hyphen or a zero-width space produce a
/// document its own password cannot open — here or in a common viewer, which normalises too. And
/// decrypt doors normalising <i>unconditionally</i> would turn an RC4 or AES-128 password with the
/// same character — which §7.6.3.3 Algorithm 2 hashes as raw Latin-1 bytes — into a space before
/// <c>PadPassword</c>, and refuse it.</para>
/// <para>The mapped characters are from RFC 4013 §2.1 (non-ASCII space → U+0020) and the
/// soft-ignored set (dropped). Each theory row is one of them, so a fix that handled one mapping
/// class and not the other shows up as a row.</para>
/// </remarks>
public class PasswordNormalizationRoundTripTests
{
    /// <summary>A no-break space (mapped to a space), a soft hyphen and a zero-width space (both dropped).</summary>
    public static readonly TheoryData<string> MappedCodePoints = new() { " ", "­", "​" };

    /// <summary>
    /// An AES-256 document encrypted under a password with a mapped code point opens with that same
    /// password: encryptor and decryptor both hash the normalised one.
    /// </summary>
    [Theory]
    [MemberData(nameof(MappedCodePoints))]
    public void AnAes256PasswordWithAMappedCodePoint_RoundTrips(string mapped)
    {
        string password = "secret" + mapped + "word";
        byte[] encrypted = Encrypt(EncryptionAlgorithm.AES256, password, password);

        byte[] decrypted = Decrypt(encrypted, password);

        AssertIsPlainPdf(decrypted);
    }

    /// <summary>
    /// A revision 2–4 document under the same password opens too: those take the raw Latin-1 bytes,
    /// and the decrypt doors do not normalise the candidate before <c>PadPassword</c>.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128, " ")]
    [InlineData(EncryptionAlgorithm.AES128, " ")]
    [InlineData(EncryptionAlgorithm.RC4_40, " ")]
    public void ARevision2To4PasswordWithANoBreakSpace_RoundTrips(EncryptionAlgorithm algorithm, string mapped)
    {
        string password = "secret" + mapped + "word";
        byte[] encrypted = Encrypt(algorithm, password, password);

        byte[] decrypted = Decrypt(encrypted, password);

        AssertIsPlainPdf(decrypted);
    }

    /// <summary>
    /// The two sides normalise alike: under AES-256 a candidate that SASLprep maps to the same string
    /// as the real password authenticates, and one that differs does not. A no-break space and a
    /// space are one password here, which is what the standard asks for.
    /// </summary>
    [Fact]
    public void TheAes256EncryptorAndDecryptorNormalizeAlike()
    {
        byte[] encrypted = Encrypt(EncryptionAlgorithm.AES256, "a b", "a b");
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(new MemoryStream(encrypted));

        Assert.True(PdfDecryptor.TryPassword(info, "a b"), "the password as typed");
        Assert.True(PdfDecryptor.TryPassword(info, "a b"), "its SASLprep form — the same password");
        Assert.False(PdfDecryptor.TryPassword(info, "ab"), "a different password (the control)");
    }

    /// <summary>
    /// A revision 2–4 password is <b>not</b> normalised, so a no-break space and a space are two
    /// different passwords — as every other reader of such a file treats them. Mapping the candidate
    /// would make these two one.
    /// </summary>
    [Fact]
    public void ARevision2To4PasswordIsTakenAsItsRawBytes()
    {
        byte[] encrypted = Encrypt(EncryptionAlgorithm.RC4_128, "a b", "a b");
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(new MemoryStream(encrypted));

        Assert.True(PdfDecryptor.TryPassword(info, "a b"), "the password as typed");
        Assert.False(PdfDecryptor.TryPassword(info, "a b"), "a space is a different byte under revision 3");
    }

    /// <summary>
    /// R5.2: an AES-CBC payload too short to hold even its IV decrypts to nothing, as the misaligned
    /// case does — never to its own raw bytes handed back as plaintext. A properly shaped but wrong
    /// payload still fails closed, so the method is not simply answering empty for everything.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(15)]
    public void ASubBlockAesCiphertext_DecryptsToEmptyNotToItsRawBytes(int length)
    {
        var key = new byte[16];
        var raw = new byte[length];
        for (int i = 0; i < length; i++) raw[i] = (byte)(0x41 + i);

        byte[] plain = PdfDecryptor.AesCbcDecrypt(key, raw);

        Assert.Empty(plain);
    }

    /// <summary>The control for the row above: a full IV plus one block of noise is refused, not echoed and not emptied.</summary>
    [Fact]
    public void AnAlignedButUndecryptableAesCiphertext_FailsClosed()
    {
        var key = new byte[16];
        var noise = new byte[32];
        for (int i = 0; i < noise.Length; i++) noise[i] = (byte)(0x80 + i);

        Assert.Throws<PdfException>(() => PdfDecryptor.AesCbcDecrypt(key, noise));
    }

    // ------------------------------------------------------------------ fixtures

    private static byte[] Encrypt(EncryptionAlgorithm algorithm, string userPassword, string ownerPassword)
    {
        using var plain = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            Algorithm = algorithm,
        });
        return encrypted.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);
        return output.ToArray();
    }

    /// <summary>The output is a PDF, and an unencrypted one: asking it for encryption parameters refuses.</summary>
    private static void AssertIsPlainPdf(byte[] pdf)
    {
        Assert.True(pdf.Length > 0, "nothing was written");
        var refusal = Assert.Throws<PdfException>(() => PdfDecryptor.GetEncryptionInfo(new MemoryStream(pdf)));
        Assert.Contains("not encrypted", refusal.Message, StringComparison.Ordinal);
    }
}
