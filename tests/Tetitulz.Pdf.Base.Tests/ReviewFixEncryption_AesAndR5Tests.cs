// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Review fixes for the Encryption assembly:
///   #3 AES-CBC must FAIL CLOSED on bad PKCS#7 padding (no silent no-padding fallback) unless the
///      caller explicitly opts in via <see cref="PdfDecryptor.AllowAesCbcNoPaddingFallback"/>.
///   #4 Revision 5 (Adobe extension) password validation must use the single-SHA-256 Algorithm 2.A,
///      NOT the iterative Algorithm 2.B used by revision 6.
/// </summary>
public class ReviewFixEncryption_AesAndR5Tests
{
    // ---------------------------------------------------------------- #3 AES-CBC fail-closed

    [Fact]
    public void AesCbc_ValidPadding_Decrypts()
    {
        byte[] key = RandomBytes(16);
        byte[] iv  = RandomBytes(16);
        byte[] plain = System.Text.Encoding.ASCII.GetBytes("hello AES-CBC PKCS7 padded plaintext!");
        byte[] cipher = EncryptPkcs7(key, iv, plain);

        byte[] data = Concat(iv, cipher);
        byte[] result = PdfDecryptor.AesCbcDecryptForTest(key, data);

        Assert.Equal(plain, result);
        Assert.Null(PdfDecryptor.LastAesCbcPaddingFallback);
    }

    [Fact]
    public void AesCbc_TamperedCiphertext_FailsClosedByDefault()
    {
        byte[] key = RandomBytes(16);
        byte[] iv  = RandomBytes(16);
        byte[] data = BuildBadPaddingData(key, iv); // IV || ciphertext that fails PKCS#7

        Assert.False(PdfDecryptor.AllowAesCbcNoPaddingFallback); // default
        var ex = Assert.Throws<PdfException>(() => PdfDecryptor.AesCbcDecryptForTest(key, data));
        Assert.Contains("padding", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The diagnostic still records that the padded path failed.
        Assert.NotNull(PdfDecryptor.LastAesCbcPaddingFallback);
    }

    [Fact]
    public void AesCbc_TamperedCiphertext_OptInFallback_ReturnsBytesAndFlags()
    {
        byte[] key = RandomBytes(16);
        byte[] iv  = RandomBytes(16);
        byte[] data = BuildBadPaddingData(key, iv);
        int cipherLen = data.Length - 16;

        PdfDecryptor.AllowAesCbcNoPaddingFallback = true;
        try
        {
            byte[] result = PdfDecryptor.AesCbcDecryptForTest(key, data);
            Assert.NotNull(result);
            Assert.Equal(cipherLen, result.Length); // no-padding => full block length
            Assert.NotNull(PdfDecryptor.LastAesCbcPaddingFallback);
            Assert.Contains("no-padding", PdfDecryptor.LastAesCbcPaddingFallback, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            PdfDecryptor.AllowAesCbcNoPaddingFallback = false;
        }
    }

    // ---------------------------------------------------------------- #4 R5 vs R6 hashing

    [Fact]
    public void R5_PasswordValidation_UsesSingleSha256_NotR6Iterative()
    {
        const string password = "secret-r5";
        byte[] pwd = PdfEncryptionPrimitives.TruncateUtf8(password, 127);

        byte[] fileKey       = RandomBytes(32);
        byte[] validationSalt = RandomBytes(8);
        byte[] keySalt        = RandomBytes(8);

        // /U built with the R5 Algorithm 2.A single SHA-256(password || validationSalt).
        byte[] uHash = PdfEncryptionPrimitives.Sha256Concat(pwd, validationSalt);  // 32 bytes
        byte[] uValue = Concat(uHash, Concat(validationSalt, keySalt));            // 48 bytes

        // /UE = AES-256-CBC-NoPadding(key = SHA-256(password || keySalt), iv = 0, fileKey).
        byte[] ueKey = PdfEncryptionPrimitives.Sha256Concat(pwd, keySalt);
        byte[] ueValue = PdfEncryptionPrimitives.AesCbcNoPadding(true, ueKey, new byte[16], fileKey);

        var info = R5Info(uValue, ueValue, oValue: new byte[48]);

        Assert.True(PdfDecryptor.TryPassword(info, password), "correct R5 password must authenticate");
        Assert.False(PdfDecryptor.TryPassword(info, "wrong"), "wrong password must be rejected");
    }

    [Fact]
    public void R5_StructureBuiltWithR6Hash_IsRejected_ProvingR5UsesSha256()
    {
        // Build /U with the R6 *iterative* hash but declare R=5. If the decryptor still (wrongly)
        // ran ComputeHashR6 for R5 this would authenticate — it must NOT.
        const string password = "secret-r5b";
        byte[] pwd = PdfEncryptionPrimitives.TruncateUtf8(password, 127);

        byte[] validationSalt = RandomBytes(8);
        byte[] keySalt        = RandomBytes(8);
        byte[] r6Hash = PdfEncryptionPrimitives.ComputeHashR6(pwd, validationSalt);
        byte[] uValue = Concat(r6Hash, Concat(validationSalt, keySalt));

        var info = R5Info(uValue, ueValue: new byte[32], oValue: new byte[48]);

        Assert.False(PdfDecryptor.TryPassword(info, password),
            "R=5 must validate with single SHA-256, so an R6-hashed /U must not authenticate.");
    }

    private static EncryptionInfo R5Info(byte[] uValue, byte[] ueValue, byte[] oValue)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.UE, new PdfString(ueValue, false));
        dict.Set(PdfName.OE, new PdfString(new byte[32], false));
        return new EncryptionInfo
        {
            V = 5,
            R = 5,
            KeyLengthBits = 256,
            PValue = -1,
            OValue = oValue,
            UValue = uValue,
            FileId = new byte[16],
            EncryptMetadata = true,
            EncryptDict = dict,
        };
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(b);
        return b;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    // Produces IV || ciphertext (two AES blocks) that is guaranteed to FAIL PKCS#7 unpadding under
    // <paramref name="key"/>. Random ciphertext decrypts to random plaintext (valid padding ~0.4%
    // of the time), so we deterministically loop until BouncyCastle's padded decrypt actually
    // rejects it — the test then exercises the real fail-closed path with no flakiness.
    private static byte[] BuildBadPaddingData(byte[] key, byte[] iv)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            byte[] cipher = RandomBytes(32); // 2 blocks, block-aligned
            if (!Pkcs7Decryptable(key, iv, cipher))
                return Concat(iv, cipher);
        }
        throw new InvalidOperationException("Could not construct bad-padding ciphertext.");
    }

    private static bool Pkcs7Decryptable(byte[] key, byte[] iv, byte[] cipher)
    {
        try
        {
            var c = new PaddedBufferedBlockCipher(new CbcBlockCipher(new AesEngine()), new Pkcs7Padding());
            c.Init(false, new ParametersWithIV(new KeyParameter(key), iv));
            var output = new byte[c.GetOutputSize(cipher.Length)];
            int len = c.ProcessBytes(cipher, 0, cipher.Length, output, 0);
            c.DoFinal(output, len);
            return true; // padding validated
        }
        catch
        {
            return false; // padding rejected — exactly what we want
        }
    }

    private static byte[] EncryptPkcs7(byte[] key, byte[] iv, byte[] plain)
    {
        var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new AesEngine()), new Pkcs7Padding());
        cipher.Init(true, new ParametersWithIV(new KeyParameter(key), iv));
        var output = new byte[cipher.GetOutputSize(plain.Length)];
        int len = cipher.ProcessBytes(plain, 0, plain.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len == output.Length) return output;
        var trimmed = new byte[len];
        Buffer.BlockCopy(output, 0, trimmed, 0, len);
        return trimmed;
    }
}
