// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// <see cref="PdfGcmCipher"/> against fixed NIST CAVP GCM test vectors (AES-256, 96-bit IV, empty
/// AAD, 128-bit tag) — <c>gcmEncryptExtIV256.rsp</c>, downloaded from
/// https://csrc.nist.gov/CSRC/media/Projects/Cryptographic-Algorithm-Validation-Program/documents/mac/gcmtestvectors.zip.
///
/// <para>A hand round trip proving encrypt-then-decrypt recovers the plaintext is not evidence of
/// correctness: encrypting with a bug and decrypting with the same bug still round-trips, and the
/// two target-framework paths (native <c>AesGcm</c> on net10.0, BouncyCastle's
/// <c>GcmBlockCipher</c> on netstandard2.0) could agree with each other while both disagreeing with
/// every other GCM implementation a real PDF reader uses. Fixed known-answer ciphertext and tag
/// bytes are what catches that; whichever target framework this assembly is running under, its own
/// path is what gets exercised.</para>
/// </summary>
public class PdfGcmCipherTests
{
    /// <summary>gcmEncryptExtIV256.rsp, [PTlen = 128] [AADlen = 0], Count = 0 — one AES block.</summary>
    [Fact]
    public void Encrypt_NistVector_OneBlockPlaintext_MatchesKnownCiphertextAndTag()
    {
        byte[] key = Hex("31bdadd96698c204aa9ce1448ea94ae1fb4a9a0b3c9d773b51bb1822666b8f22");
        byte[] iv = Hex("0d18e06c7c725ac9e362e1ce");
        byte[] plaintext = Hex("2db5168e932556f8089a0622981d017d");
        byte[] expectedCiphertext = Hex("fa4362189661d163fcd6a56d8bf0405a");
        byte[] expectedTag = Hex("d636ac1bbedd5cc3ee727dc2ab4a9489");

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[PdfGcmCipher.TagLength];
        PdfGcmCipher.Encrypt(key, iv, plaintext, ciphertext, tag);

        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);
    }

    /// <summary>gcmEncryptExtIV256.rsp, [PTlen = 104] [AADlen = 0] — 13 bytes, not block-aligned.
    /// GCM needs no padding, unlike the CBC modes elsewhere in this project.</summary>
    [Fact]
    public void Encrypt_NistVector_NonBlockAlignedPlaintext_MatchesKnownCiphertextAndTag()
    {
        byte[] key = Hex("82c4f12eeec3b2d3d157b0f992d292b237478d2cecc1d5f161389b97f999057a");
        byte[] iv = Hex("7b40b20f5f397177990ef2d1");
        byte[] plaintext = Hex("982a296ee1cd7086afad976945");
        byte[] expectedCiphertext = Hex("ec8e05a0471d6b43a59ca5335f");
        byte[] expectedTag = Hex("113ddeafc62373cac2f5951bb9165249");

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[PdfGcmCipher.TagLength];
        PdfGcmCipher.Encrypt(key, iv, plaintext, ciphertext, tag);

        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);
    }

    [Theory]
    [InlineData(
        "31bdadd96698c204aa9ce1448ea94ae1fb4a9a0b3c9d773b51bb1822666b8f22",
        "0d18e06c7c725ac9e362e1ce",
        "fa4362189661d163fcd6a56d8bf0405a",
        "d636ac1bbedd5cc3ee727dc2ab4a9489",
        "2db5168e932556f8089a0622981d017d")]
    [InlineData(
        "82c4f12eeec3b2d3d157b0f992d292b237478d2cecc1d5f161389b97f999057a",
        "7b40b20f5f397177990ef2d1",
        "ec8e05a0471d6b43a59ca5335f",
        "113ddeafc62373cac2f5951bb9165249",
        "982a296ee1cd7086afad976945")]
    public void TryDecrypt_NistVector_RecoversKnownPlaintext(
        string keyHex, string ivHex, string ciphertextHex, string tagHex, string plaintextHex)
    {
        bool ok = PdfGcmCipher.TryDecrypt(
            Hex(keyHex), Hex(ivHex), Hex(ciphertextHex), Hex(tagHex), out byte[] plaintext);

        Assert.True(ok);
        Assert.Equal(Hex(plaintextHex), plaintext);
    }

    /// <summary>The authentication half of AEAD: a flipped ciphertext byte must fail closed, never
    /// return "successfully decrypted" garbage.</summary>
    [Fact]
    public void TryDecrypt_TamperedCiphertext_FailsClosed()
    {
        byte[] key = Hex("31bdadd96698c204aa9ce1448ea94ae1fb4a9a0b3c9d773b51bb1822666b8f22");
        byte[] iv = Hex("0d18e06c7c725ac9e362e1ce");
        byte[] ciphertext = Hex("fa4362189661d163fcd6a56d8bf0405a");
        byte[] tag = Hex("d636ac1bbedd5cc3ee727dc2ab4a9489");
        ciphertext[0] ^= 0x01;

        bool ok = PdfGcmCipher.TryDecrypt(key, iv, ciphertext, tag, out byte[] plaintext);

        Assert.False(ok);
        Assert.Null(plaintext);
    }

    /// <summary>Same, for a tampered tag with the ciphertext left untouched.</summary>
    [Fact]
    public void TryDecrypt_TamperedTag_FailsClosed()
    {
        byte[] key = Hex("31bdadd96698c204aa9ce1448ea94ae1fb4a9a0b3c9d773b51bb1822666b8f22");
        byte[] iv = Hex("0d18e06c7c725ac9e362e1ce");
        byte[] ciphertext = Hex("fa4362189661d163fcd6a56d8bf0405a");
        byte[] tag = Hex("d636ac1bbedd5cc3ee727dc2ab4a9489");
        tag[0] ^= 0x01;

        bool ok = PdfGcmCipher.TryDecrypt(key, iv, ciphertext, tag, out byte[] plaintext);

        Assert.False(ok);
        Assert.Null(plaintext);
    }

    /// <summary>Round trip on random data, independent of the fixed vectors above — catches a bug
    /// the fixed vectors' specific byte patterns happen not to trigger.</summary>
    [Fact]
    public void EncryptThenDecrypt_RandomData_RoundTrips()
    {
        var random = new Random(12345);
        byte[] key = new byte[32];
        byte[] iv = new byte[PdfGcmCipher.IvLength];
        byte[] plaintext = new byte[137]; // arbitrary, non-block-aligned
        random.NextBytes(key);
        random.NextBytes(iv);
        random.NextBytes(plaintext);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[PdfGcmCipher.TagLength];
        PdfGcmCipher.Encrypt(key, iv, plaintext, ciphertext, tag);

        bool ok = PdfGcmCipher.TryDecrypt(key, iv, ciphertext, tag, out byte[] recovered);

        Assert.True(ok);
        Assert.Equal(plaintext, recovered);
        Assert.NotEqual(plaintext, ciphertext); // sanity: it actually encrypted something
    }

    /// <summary>
    /// Test projects in this repo are net10.0-only (CLAUDE.md), so <see cref="PdfGcmCipher"/>'s own
    /// <c>NETSTANDARD2_0</c> branch — the one this test targets — never actually compiles into a
    /// test binary. This calls BouncyCastle's <see cref="GcmBlockCipher"/> with the EXACT same
    /// <see cref="AeadParameters"/>/<see cref="KeyParameter"/> call sequence that branch uses,
    /// unconditionally, so the risk that actually matters — is this BouncyCastle usage pattern
    /// correct? — is checked even though the conditional compilation itself is not.
    /// </summary>
    [Fact]
    public void BouncyCastleGcmUsagePattern_MatchesNistVector_TheNetstandard20BranchWouldProduce()
    {
        byte[] key = Hex("31bdadd96698c204aa9ce1448ea94ae1fb4a9a0b3c9d773b51bb1822666b8f22");
        byte[] iv = Hex("0d18e06c7c725ac9e362e1ce");
        byte[] plaintext = Hex("2db5168e932556f8089a0622981d017d");
        byte[] expectedCiphertext = Hex("fa4362189661d163fcd6a56d8bf0405a");
        byte[] expectedTag = Hex("d636ac1bbedd5cc3ee727dc2ab4a9489");

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), PdfGcmCipher.TagLength * 8, iv));
        byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        len += cipher.DoFinal(output, len);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[PdfGcmCipher.TagLength];
        Buffer.BlockCopy(output, 0, ciphertext, 0, plaintext.Length);
        Buffer.BlockCopy(output, plaintext.Length, tag, 0, PdfGcmCipher.TagLength);

        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);

        // And the decrypt half, including tag verification.
        var decipher = new GcmBlockCipher(new AesEngine());
        decipher.Init(false, new AeadParameters(new KeyParameter(key), PdfGcmCipher.TagLength * 8, iv));
        byte[] decOutput = new byte[decipher.GetOutputSize(output.Length)];
        int decLen = decipher.ProcessBytes(output, 0, output.Length, decOutput, 0);
        decLen += decipher.DoFinal(decOutput, decLen);
        var recovered = new byte[decLen];
        Buffer.BlockCopy(decOutput, 0, recovered, 0, decLen);
        Assert.Equal(plaintext, recovered);

        // Tampering the tag must throw InvalidCipherTextException — the exact type
        // PdfGcmCipher.TryDecrypt's NETSTANDARD2_0 branch catches to fail closed.
        output[output.Length - 1] ^= 0x01;
        var tamperCipher = new GcmBlockCipher(new AesEngine());
        tamperCipher.Init(false, new AeadParameters(new KeyParameter(key), PdfGcmCipher.TagLength * 8, iv));
        byte[] tamperOutput = new byte[tamperCipher.GetOutputSize(output.Length)];
        int tamperLen = tamperCipher.ProcessBytes(output, 0, output.Length, tamperOutput, 0);
        Assert.Throws<InvalidCipherTextException>(() => tamperCipher.DoFinal(tamperOutput, tamperLen));
    }

    private static byte[] Hex(string s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return bytes;
    }
}
