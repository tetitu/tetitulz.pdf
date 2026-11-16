// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;

namespace Tetitulz.Pdf.Core.Encryption
{
    /// <summary>
    /// The symmetric ciphers ISO 32000-1's <b>standard security handler</b> needs — RC4 and
    /// AES-CBC — implemented with <b>no external dependency</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Password-based PDF encryption lives in
    /// <c>Tetitulz.Pdf.Encryption</c> because that assembly references BouncyCastle, and CLAUDE.md
    /// keeps BouncyCastle out of every consumer's package graph. But the standard security handler
    /// does not actually need it: it needs MD5, SHA-2, RC4 and AES-CBC, and three of those four are
    /// in <see cref="System.Security.Cryptography"/> on every target this library builds for,
    /// <c>netstandard2.0</c> included. Only RC4 is absent — because it is obsolete — and RC4 is
    /// twenty lines.</para>
    /// <para><b>What still needs BouncyCastle, and therefore stays out of Core.</b> Public-key
    /// (certificate) encryption, which needs CMS enveloped-data recipients. That is
    /// <c>PdfPublicKeyEncryptor</c>, and it is the only part of the encryption assembly with a real
    /// claim on the dependency.</para>
    /// <para><b>These must stay byte-identical to the BouncyCastle path.</b> Two implementations of
    /// one cipher is the divergence shape this repository keeps finding defects in, so
    /// <c>CryptoPrimitiveParityTests</c> compares the two over random keys, IVs and lengths rather
    /// than trusting that AES is AES. Do not "optimise" either side without re-running it.</para>
    /// <para><b>RC4 is not a recommendation.</b> It is required to read files written under
    /// revisions 2–4 of the standard handler, which exist in the wild in enormous numbers. Nothing
    /// here should be used to *write* new documents in a scheme this weak.</para>
    /// </remarks>
    public static class PdfCryptoPrimitives
    {
        /// <summary>AES block size in bytes; also the length of the IV a PDF prefixes to AES ciphertext.</summary>
        public const int AesBlockBytes = 16;

        /// <summary>
        /// RC4 (ARC4) keystream applied to <paramref name="data"/>. The cipher is symmetric, so the
        /// same call both encrypts and decrypts.
        /// </summary>
        /// <remarks>
        /// Transcribed from the algorithm itself rather than adapted from any implementation: the
        /// key-scheduling and generation loops are four lines each, and a second transcription is
        /// what makes the parity test meaningful evidence instead of a tautology.
        /// </remarks>
        public static byte[] Rc4(byte[] key, byte[] data)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (key.Length == 0)
                throw new ArgumentException("An RC4 key must have at least one byte.", nameof(key));

            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;

            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }

            var output = new byte[data.Length];
            for (int n = 0, x = 0, y = 0; n < data.Length; n++)
            {
                x = (x + 1) & 0xFF;
                y = (y + s[x]) & 0xFF;
                (s[x], s[y]) = (s[y], s[x]);
                output[n] = (byte)(data[n] ^ s[(s[x] + s[y]) & 0xFF]);
            }
            return output;
        }

        /// <summary>
        /// AES-CBC with <b>no padding</b>. <paramref name="data"/> must be a whole number of
        /// blocks; §7.6.3.3's Algorithm 2.A uses this shape for the 32-byte <c>/UE</c> and
        /// <c>/OE</c> key blobs, which are never padded.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="data"/> is not a multiple of <see cref="AesBlockBytes"/>. Silently
        /// padding it would produce a longer result than the caller's format allows.
        /// </exception>
        public static byte[] AesCbcNoPadding(bool encrypt, byte[] key, byte[] iv, byte[] data)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (iv == null) throw new ArgumentNullException(nameof(iv));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length % AesBlockBytes != 0)
                throw new ArgumentException(
                    $"AES-CBC without padding needs a whole number of {AesBlockBytes}-byte blocks; " +
                    $"got {data.Length} bytes.", nameof(data));

            if (data.Length == 0) return Array.Empty<byte>();

            using var aes = CreateAes(key, iv, PaddingMode.None);
            using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }

        /// <summary>
        /// AES-CBC with PKCS#7 padding — the scheme §7.6.2 mandates for <c>/V 4</c> and <c>/V 5</c>
        /// stream and string data.
        /// </summary>
        /// <remarks>
        /// <b>Decryption fails closed.</b> A padding error is not recoverable data: per ISO 32000 a
        /// conforming AES-CBC payload is always PKCS#7-padded, so bad padding means the ciphertext
        /// is tampered, truncated, or the key is wrong. A <see cref="CryptographicException"/>
        /// propagates rather than being retried without padding — historically that retry could
        /// re-interpret tampered ciphertext as valid plaintext. A caller that must tolerate a
        /// non-conforming producer decides that itself, above this layer.
        /// </remarks>
        public static byte[] AesCbcPkcs7(bool encrypt, byte[] key, byte[] iv, byte[] data)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (iv == null) throw new ArgumentNullException(nameof(iv));
            if (data == null) throw new ArgumentNullException(nameof(data));

            using var aes = CreateAes(key, iv, PaddingMode.PKCS7);
            using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }

        /// <summary>
        /// AES-ECB on a single block, no padding — §7.6.4.4's <c>/Perms</c> check value, which is
        /// exactly 16 bytes and is deliberately unchained so a reader can verify it without the
        /// rest of the document.
        /// </summary>
        /// <remarks>
        /// ECB is the right mode <i>here and nowhere else</i>: it is one block, so the pattern
        /// leakage that makes ECB unusable for real data cannot arise. Do not reach for this
        /// anywhere the payload can exceed one block.
        /// </remarks>
        public static byte[] AesEcbNoPadding(bool encrypt, byte[] key, byte[] data)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length % AesBlockBytes != 0)
                throw new ArgumentException(
                    $"AES-ECB without padding needs a whole number of {AesBlockBytes}-byte blocks; " +
                    $"got {data.Length} bytes.", nameof(data));

            if (data.Length == 0) return Array.Empty<byte>();

            using Aes aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }

        private static Aes CreateAes(byte[] key, byte[] iv, PaddingMode padding)
        {
            Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = padding;
            aes.Key = key;   // throws for a key length AES does not define, which is the right answer
            aes.IV = iv;
            return aes;
        }
    }
}
