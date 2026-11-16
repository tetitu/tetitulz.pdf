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
using Tetitulz.Pdf.Core.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfCryptoPrimitives"/> (BCL-only, in <c>Core</c>) must be <b>byte-identical</b> to the
/// BouncyCastle primitives <c>Tetitulz.Pdf.Encryption</c> uses today.
/// </summary>
/// <remarks>
/// <para><b>Why this test is the load-bearing one.</b> The standard security handler needs MD5,
/// SHA-2, RC4 and AES-CBC; three of the four are in <see cref="System.Security.Cryptography"/> on
/// every target framework here, so the only thing keeping password-based decryption out of
/// <c>Base</c> is a dependency it does not really need. Moving it down is only safe if the ciphers
/// agree exactly — a one-byte difference in a keystream is a corrupt document, not a warning.</para>
/// <para><b>Two implementations of one cipher is the divergence shape this repository keeps finding
/// defects in</b> (see CLAUDE.md on the two renderers). This is the guard that makes the pair
/// checkable rather than merely believed, and it stays after the move so a later "optimisation" to
/// either side has to prove itself.</para>
/// <para><b>Random inputs, not fixed vectors, and deliberately so.</b> A fixed vector proves the two
/// agree on one input; these sweep key lengths, IVs, and payload lengths either side of the block
/// boundary — including the empty payload and lengths that are not a multiple of 16, which is where
/// padding and buffering bugs live. The seed is fixed so a failure is reproducible.</para>
/// </remarks>
public class CryptoPrimitiveParityTests
{
    // Fixed seed: a failing case must be reproducible, and a test whose input changes per run
    // reports a different defect each time it goes red.
    private const int Seed = 20260814;

    [Theory]
    [InlineData(5)]     // RC4-40, the /V 1 key length
    [InlineData(16)]    // RC4-128
    [InlineData(32)]
    public void Rc4_MatchesBouncyCastle_AcrossPayloadLengths(int keyBytes)
    {
        var rng = new Random(Seed + keyBytes);

        foreach (int length in new[] { 0, 1, 15, 16, 17, 255, 256, 257, 4096 })
        {
            byte[] key = Bytes(rng, keyBytes);
            byte[] data = Bytes(rng, length);

            byte[] mine = PdfCryptoPrimitives.Rc4(key, data);
            byte[] theirs = BouncyRc4(key, data);

            Assert.Equal(theirs, mine);
        }
    }

    /// <summary>RC4 is symmetric: applying it twice must return the input.</summary>
    /// <remarks>
    /// A property the parity test cannot give on its own — two implementations could agree and both
    /// be wrong.
    /// <para><b>It is necessary and not sufficient, which mutation demonstrated.</b> Corrupting the
    /// generation step to <c>x = (x + 2)</c> turns all three parity cases red and leaves this one
    /// GREEN: a wrong keystream is still a deterministic keystream, so XOR-ing twice still returns
    /// the plaintext. Keep both — this pins that the cipher is an involution, the parity tests pin
    /// that it is the RIGHT involution.</para>
    /// </remarks>
    [Fact]
    public void Rc4_AppliedTwice_ReturnsThePlaintext()
    {
        var rng = new Random(Seed);
        byte[] key = Bytes(rng, 16);
        byte[] plaintext = Bytes(rng, 1000);

        Assert.Equal(plaintext, PdfCryptoPrimitives.Rc4(key, PdfCryptoPrimitives.Rc4(key, plaintext)));
    }

    [Theory]
    [InlineData(16)]    // AES-128, /V 4
    [InlineData(32)]    // AES-256, /V 5
    public void AesCbcNoPadding_MatchesBouncyCastle(int keyBytes)
    {
        var rng = new Random(Seed + keyBytes);

        // Whole blocks only — that is the contract, and it is what /UE and /OE carry.
        foreach (int length in new[] { 16, 32, 48, 256 })
        {
            byte[] key = Bytes(rng, keyBytes);
            byte[] iv = Bytes(rng, 16);
            byte[] data = Bytes(rng, length);

            Assert.Equal(
                BouncyAesCbcNoPadding(true, key, iv, data),
                PdfCryptoPrimitives.AesCbcNoPadding(true, key, iv, data));

            Assert.Equal(
                BouncyAesCbcNoPadding(false, key, iv, data),
                PdfCryptoPrimitives.AesCbcNoPadding(false, key, iv, data));
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void AesCbcPkcs7_MatchesBouncyCastle_IncludingTheBlockBoundary(int keyBytes)
    {
        var rng = new Random(Seed + keyBytes);

        // 0 and 16 are the cases that separate a correct PKCS#7 from a plausible one: both add a
        // FULL block of padding, which a naive implementation omits.
        foreach (int length in new[] { 0, 1, 15, 16, 17, 31, 32, 1000 })
        {
            byte[] key = Bytes(rng, keyBytes);
            byte[] iv = Bytes(rng, 16);
            byte[] plaintext = Bytes(rng, length);

            byte[] mine = PdfCryptoPrimitives.AesCbcPkcs7(true, key, iv, plaintext);
            byte[] theirs = BouncyAesCbcPkcs7(true, key, iv, plaintext);
            Assert.Equal(theirs, mine);

            // ...and the round trip returns exactly the plaintext, so "they agree" is not two
            // implementations agreeing on the same mistake.
            Assert.Equal(plaintext, PdfCryptoPrimitives.AesCbcPkcs7(false, key, iv, mine));
        }
    }

    /// <summary>
    /// Decryption fails closed on bad padding. ISO 32000 makes conforming AES-CBC always
    /// PKCS#7-padded, so a padding error means tampered, truncated, or wrong-key ciphertext — it
    /// must not be silently re-read as unpadded plaintext.
    /// </summary>
    [Fact]
    public void AesCbcPkcs7_TamperedCiphertext_ThrowsRatherThanReturningGarbage()
    {
        var rng = new Random(Seed);
        byte[] key = Bytes(rng, 16);
        byte[] iv = Bytes(rng, 16);

        byte[] ciphertext = PdfCryptoPrimitives.AesCbcPkcs7(true, key, iv, Bytes(rng, 64));
        ciphertext[ciphertext.Length - 1] ^= 0xFF;   // corrupt the final padded block

        Assert.Throws<CryptographicException>(
            () => PdfCryptoPrimitives.AesCbcPkcs7(false, key, iv, ciphertext));
    }

    /// <summary>The no-padding entry point refuses a partial block instead of quietly padding it.</summary>
    [Fact]
    public void AesCbcNoPadding_PartialBlock_IsRefused()
    {
        var rng = new Random(Seed);
        Assert.Throws<ArgumentException>(() => PdfCryptoPrimitives.AesCbcNoPadding(
            true, Bytes(rng, 16), Bytes(rng, 16), Bytes(rng, 17)));
    }

    // ------------------------------------------------------------------
    //  The BouncyCastle side, called exactly as the Encryption assembly calls it
    // ------------------------------------------------------------------

    private static byte[] BouncyRc4(byte[] key, byte[] data)
    {
        var engine = new RC4Engine();
        engine.Init(true, new KeyParameter(key));
        byte[] output = new byte[data.Length];
        engine.ProcessBytes(data, 0, data.Length, output, 0);
        return output;
    }

    private static byte[] BouncyAesCbcNoPadding(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        var cipher = new BufferedBlockCipher(new CbcBlockCipher(new AesEngine()));
        cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
        return Run(cipher, data);
    }

    private static byte[] BouncyAesCbcPkcs7(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        var cipher = new PaddedBufferedBlockCipher(
            new CbcBlockCipher(new AesEngine()), new Pkcs7Padding());
        cipher.Init(encrypt, new ParametersWithIV(new KeyParameter(key), iv));
        return Run(cipher, data);
    }

    private static byte[] Run(BufferedBlockCipher cipher, byte[] data)
    {
        byte[] output = new byte[cipher.GetOutputSize(data.Length)];
        int len = cipher.ProcessBytes(data, 0, data.Length, output, 0);
        len += cipher.DoFinal(output, len);
        if (len == output.Length) return output;

        var trimmed = new byte[len];
        Buffer.BlockCopy(output, 0, trimmed, 0, len);
        return trimmed;
    }

    private static byte[] Bytes(Random rng, int count)
    {
        var b = new byte[count];
        rng.NextBytes(b);
        return b;
    }
}
