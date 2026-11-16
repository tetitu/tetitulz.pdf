// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.Encryption
{
    internal static class PdfEncryptionPrimitives
    {
        // ------------------------------------------------------------------
        //  Standard security handler — ISO 32000-1 §7.6.3
        // ------------------------------------------------------------------

        /// <summary>
        /// Length of the padded password block (Algorithm 2, step a): a password is truncated to
        /// or padded out to exactly 32 bytes with <see cref="PaddingBytes"/>. Also the length of
        /// the /U and /O strings.
        /// </summary>
        internal const int PasswordPadLength = 32;

        /// <summary>Bits per byte — the /Length entry is in bits, every key here is in bytes.</summary>
        internal const int BitsPerByte = 8;

        /// <summary>Smallest legal standard-handler key: 5 bytes / 40 bits.</summary>
        internal const int MinKeyBytes = 5;

        /// <summary>
        /// Largest legal standard-handler key: 16 bytes / 128 bits. The key is sliced out of an
        /// MD5 digest, so it can never exceed the digest size.
        /// </summary>
        internal const int MaxKeyBytes = 16;

        /// <summary>Size of an MD5 digest, and therefore the ceiling on a derived key.</summary>
        internal const int Md5DigestLength = 16;

        /// <summary>Byte count of the little-endian /P value hashed in by Algorithm 2, step d.</summary>
        private const int PValueByteCount = 4;

        /// <summary>
        /// Marker hashed in by Algorithm 2, step f when /EncryptMetadata is false (revision 4+):
        /// four 0xFF bytes.
        /// </summary>
        private static readonly byte[] NoMetadataMarker = { 0xFF, 0xFF, 0xFF, 0xFF };

        /// <summary>Revision 2 — the original 40-bit RC4 handler, with no key-strengthening loops.</summary>
        internal const int RevisionRc4Basic = 2;

        /// <summary>
        /// Revision 3 — variable-length RC4. Adds the 50-round key rehash and the 19-round RC4 mix
        /// used for /U and /O.
        /// </summary>
        internal const int RevisionRc4Variable = 3;

        /// <summary>Revision 4 — crypt filters and the /EncryptMetadata flag.</summary>
        internal const int RevisionCryptFilters = 4;

        /// <summary>Rounds of MD5 key rehashing for revision 3+ (Algorithm 2, step h).</summary>
        internal const int KeyRehashRounds = 50;

        /// <summary>Additional RC4 passes with XOR-modified keys for revision 3+ (Algorithms 3/5).</summary>
        internal const int Rc4MixRounds = 19;

        // ------------------------------------------------------------------
        //  AES-256 / revision 6 — ISO 32000-2 §7.6.4
        // ------------------------------------------------------------------

        /// <summary>AES block size, and equally the AES-128 key/IV length used by Algorithm 2.B.</summary>
        internal const int AesBlockBytes = 16;

        /// <summary>Length of the AES-256 file encryption key, and of the /UE and /OE entries.</summary>
        internal const int FileKeyLength = 32;

        /// <summary>Maximum password length in bytes for revision 5/6 (ISO 32000-2 §7.6.4.3.3).</summary>
        internal const int MaxPasswordBytesR6 = 127;

        /// <summary>Times (password + K + userKey) is repeated to build K1 in Algorithm 2.B.</summary>
        private const int HashR6BlockRepeats = 64;

        /// <summary>Algorithm 2.B picks the next digest from (sum of E's first 16 bytes) mod 3.</summary>
        private const int HashR6HashChoiceModulus = 3;

        /// <summary>Algorithm 2.B runs at least 64 rounds; the check is on the zero-based index.</summary>
        private const int HashR6MinRoundIndex = 63;

        /// <summary>
        /// Algorithm 2.B termination offset: stop once (last byte of E) &lt;= (i - 31), with
        /// <c>i</c> zero-based.
        /// </summary>
        /// <remarks>
        /// <para><b>Not 32.</b> The spec phrases the rule against the <i>round number</i>, which is
        /// <c>i + 1</c> here, so "the last byte of E is at most the round number minus 32" is
        /// <c>E[^1] &lt;= (i + 1) - 32</c> — this offset. Reading it against the zero-based index
        /// instead runs one round too long whenever the byte lands exactly on the boundary, which is
        /// about one file in thirty-eight.</para>
        ///
        /// <para><b>Settled by external vectors, not by re-reading the sentence.</b> Six R6 files
        /// whose two readings differ — written by two independent producers, found by generating 240
        /// files — all match this offset and none matches 32. <c>HashR6ExternalVectorTests</c> holds
        /// them.</para>
        ///
        /// <para><b>What the other reading costs, in both directions.</b> An R6 file from any other
        /// producer rejects its own correct password one file in thirty-eight, and a file written
        /// under it opens nowhere else. A suite that round-trips this implementation against itself
        /// cannot see it — a change applied to both halves of a round trip is exactly what a round
        /// trip cannot see.</para>
        /// </remarks>
        internal const int HashR6TerminationOffset = 31;

        /// <summary>
        /// The offset this library used before the external vectors arrived, kept for comparison.
        /// </summary>
        /// <remarks>
        /// <b>Not production, and not a fallback.</b> It exists so that
        /// <c>HashR6ExternalVectorTests</c> can assert that each of its six discriminating rows
        /// really does discriminate — a vector that gave the same answer under both readings would
        /// pass whatever the constant said, and would be a row silently not doing its job. It is
        /// here rather than in the test so the two readings stay stated once, side by side, at the
        /// code the argument was about.
        /// </remarks>
        internal const int HashR6SupersededTerminationOffset = 32;

        /// <summary>
        /// Bytes appended after the encryption key when deriving a per-object key
        /// (ISO 32000-1 §7.6.2 Algorithm 1): 3 of the object number then 2 of the generation
        /// number.
        /// </summary>
        internal const int ObjectKeySuffixLength = 5;

        /// <summary>
        /// Extra bytes appended for AES-V2 object keys (ISO 32000-1 §7.6.2): the ASCII string
        /// "sAlT".
        /// </summary>
        private static readonly byte[] AesObjectKeySalt = { 0x73, 0x41, 0x6C, 0x54 }; // "sAlT"

        internal static readonly byte[] PaddingBytes =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
            0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
            0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
        };

        internal static byte[] PadPassword(string password)
        {
            byte[] pwdBytes = string.IsNullOrEmpty(password)
                ? Array.Empty<byte>()
                : Tetitulz.Pdf.Core.Text.Latin1Text.Encode(password);

            var padded = new byte[PasswordPadLength];
            int copyLen = Math.Min(pwdBytes.Length, PasswordPadLength);
            Buffer.BlockCopy(pwdBytes, 0, padded, 0, copyLen);
            if (copyLen < PasswordPadLength)
                Buffer.BlockCopy(PaddingBytes, 0, padded, copyLen, PasswordPadLength - copyLen);
            return padded;
        }

        internal static byte[] ComputeEncryptionKey(string userPassword, byte[] oValue, int pValue, byte[] fileId, int revision, int keyLengthBits, bool encryptMetadata = true)
        {
            // The standard security handler derives the key from a 16-byte MD5 digest, so the
            // key length must be 5..16 bytes (40..128 bits). Clamp defensively: a keyLenBytes
            // above 16 would make the rehash (md5.ComputeHash(hash,0,keyLenBytes)) and the final
            // BlockCopy read past the 16-byte digest and throw an uncaught ArgumentException
            // (a process-crash DoS on a crafted /Encrypt dict with an in-guard-range /Length
            // like 136..256 bits). See PdfDecryptor's /Length validation (caps V<5 at 128).
            int keyLenBytes = keyLengthBits / BitsPerByte;
            if (keyLenBytes < MinKeyBytes) keyLenBytes = MinKeyBytes;
            if (keyLenBytes > MaxKeyBytes) keyLenBytes = MaxKeyBytes;

            using (var md5 = MD5.Create())
            {
                byte[] userPadded = PadPassword(userPassword);
                md5.TransformBlock(userPadded, 0, userPadded.Length, null, 0);
                md5.TransformBlock(oValue, 0, oValue.Length, null, 0);

                byte[] pBytes = BitConverter.GetBytes(pValue);
                if (!BitConverter.IsLittleEndian) Array.Reverse(pBytes);
                md5.TransformBlock(pBytes, 0, PValueByteCount, null, 0);

                md5.TransformBlock(fileId, 0, fileId.Length, null, 0);

                if (!encryptMetadata && revision >= RevisionCryptFilters)
                    md5.TransformBlock(NoMetadataMarker, 0, NoMetadataMarker.Length, null, 0);

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                byte[] hash = md5.Hash;

                if (revision >= RevisionRc4Variable)
                {
                    // After TransformFinalBlock the hash object is in a finalized state. .NET Core
                    // tolerates reusing it via ComputeHash, but the .NET Framework
                    // MD5CryptoServiceProvider throws "Hash not valid for use in specified state"
                    // unless it is re-initialized first. Initialize() makes the rehash work on both.
                    md5.Initialize();
                    for (int i = 0; i < KeyRehashRounds; i++)
                        hash = md5.ComputeHash(hash, 0, keyLenBytes);
                }

                byte[] key = new byte[keyLenBytes];
                Buffer.BlockCopy(hash, 0, key, 0, keyLenBytes);
                return key;
            }
        }

        /// <summary>
        /// ISO 32000-1 §7.6.3.4 Algorithm 3 steps (a) to (d): the RC4 key that enciphers the padded user
        /// password into <c>/O</c> — the padded owner password's MD5, re-hashed fifty times at revision 3
        /// and above, the first <paramref name="keyLenBytes"/> bytes.
        /// </summary>
        /// <remarks>
        /// <b>Each round hashes the first <i>n</i> bytes of the previous digest, not all sixteen.</b>
        /// Algorithm 3's text says "the output from the previous MD5 hash"; Algorithm 2's says "the first
        /// n bytes", and other implementations apply the first-<i>n</i> reading to both.
        /// Hashing all sixteen agrees at 128 bits (n = 16) and disagrees at 40 bits under revision 3
        /// (<c>RC4_40R3</c>): an owner password written that way fails in every other reader, and
        /// theirs here. One implementation for the encryptor
        /// (writing <c>/O</c>) and the decryptor (recovering the user password from it).
        /// </remarks>
        internal static byte[] ComputeOwnerRc4Key(string ownerPassword, int revision, int keyLenBytes)
        {
            if (keyLenBytes < MinKeyBytes) keyLenBytes = MinKeyBytes;
            if (keyLenBytes > MaxKeyBytes) keyLenBytes = MaxKeyBytes;

            byte[] ownerPadded = PadPassword(ownerPassword);
            byte[] hash;
            using (var md5 = MD5.Create())
            {
                hash = md5.ComputeHash(ownerPadded);
                if (revision >= RevisionRc4Variable)
                {
                    md5.Initialize(); // MD5CryptoServiceProvider: see ComputeEncryptionKey.
                    for (int i = 0; i < KeyRehashRounds; i++)
                        hash = md5.ComputeHash(hash, 0, keyLenBytes); // the first n bytes: Algorithm 3 (c)
                }
            }

            byte[] rc4Key = new byte[keyLenBytes];
            Buffer.BlockCopy(hash, 0, rc4Key, 0, keyLenBytes);
            return rc4Key;
        }

        internal static byte[] ComputeUserValue(byte[] encryptionKey, byte[] fileId, int revision)
        {
            if (revision == RevisionRc4Basic)
            {
                return Rc4Process(encryptionKey, PaddingBytes);
            }
            else
            {
                byte[] hash;
                using (var md5 = MD5.Create())
                {
                    md5.TransformBlock(PaddingBytes, 0, PaddingBytes.Length, null, 0);
                    md5.TransformFinalBlock(fileId, 0, fileId.Length);
                    hash = md5.Hash;
                }

                byte[] encrypted = Rc4Process(encryptionKey, hash);
                for (int i = 1; i <= Rc4MixRounds; i++)
                {
                    byte[] tempKey = new byte[encryptionKey.Length];
                    for (int j = 0; j < encryptionKey.Length; j++)
                        tempKey[j] = (byte)(encryptionKey[j] ^ (byte)i);
                    encrypted = Rc4Process(tempKey, encrypted);
                }

                // /U is 32 bytes: the 16-byte MD5-derived value, zero-padded out.
                var result = new byte[PasswordPadLength];
                Buffer.BlockCopy(encrypted, 0, result, 0, Md5DigestLength);
                return result;
            }
        }

        internal static byte[] Sha256Concat(params byte[][] parts)
        {
            using (var sha = SHA256.Create())
            {
                for (int i = 0; i < parts.Length - 1; i++)
                    sha.TransformBlock(parts[i], 0, parts[i].Length, null, 0);
                sha.TransformFinalBlock(parts[parts.Length - 1], 0, parts[parts.Length - 1].Length);
                return sha.Hash;
            }
        }

        /// <summary>
        /// ISO 32000-2 §7.6.4.3.3 Algorithm 2.B: computing a hash for revision 6 (PDF 2.0 AES-256).
        /// Replaces the plain SHA-256 used in revision 5 with an iterative key-reinforcing hash.
        /// </summary>
        /// <param name="password">UTF-8 password bytes, truncated to 127 bytes.</param>
        /// <param name="salt">8-byte validation or key salt from /U or /O.</param>
        /// <param name="userKey">First 48 bytes of /U when computing owner operations; empty for user operations.</param>
        internal static byte[] ComputeHashR6(byte[] password, byte[] salt, byte[] userKey = null)
            => ComputeHashR6(password, salt, userKey, HashR6TerminationOffset);

        /// <summary>
        /// Algorithm 2.B with the termination offset supplied, so the two readings of it can be
        /// compared against each other.
        /// </summary>
        /// <remarks>
        /// <b>Production always passes <see cref="HashR6TerminationOffset"/>; nothing else may.</b>
        /// The parameter exists because the alternative way to compare the readings is a second
        /// copy of Algorithm 2.B in a test, and a second copy of security logic is the thing this
        /// repository will not have. Every other step — the 64-fold K1, the AES-CBC, the mod-3
        /// digest choice — is shared by construction, so a measured difference can only be the
        /// predicate.
        /// </remarks>
        /// <param name="password">UTF-8 password bytes, truncated to 127 bytes.</param>
        /// <param name="salt">8-byte validation or key salt.</param>
        /// <param name="userKey">First 48 bytes of <c>/U</c> for owner operations; empty otherwise.</param>
        /// <param name="terminationOffset">
        /// Stop once the last byte of E is at most <c>(i - terminationOffset)</c>, with <c>i</c>
        /// zero-based.
        /// </param>
        internal static byte[] ComputeHashR6(
            byte[] password, byte[] salt, byte[] userKey, int terminationOffset)
        {
            if (userKey == null) userKey = Array.Empty<byte>();

            // Initial K = SHA-256(password + salt + userKey)
            byte[] K = Sha256Concat(password, salt, userKey);

            for (int i = 0; ; i++)
            {
                // K1 = (password + K + userKey) repeated 64 times
                int unitLen = password.Length + K.Length + userKey.Length;
                byte[] K1 = new byte[HashR6BlockRepeats * unitLen];
                for (int j = 0; j < HashR6BlockRepeats; j++)
                {
                    int off = j * unitLen;
                    Buffer.BlockCopy(password, 0, K1, off, password.Length);
                    Buffer.BlockCopy(K, 0, K1, off + password.Length, K.Length);
                    Buffer.BlockCopy(userKey, 0, K1, off + password.Length + K.Length, userKey.Length);
                }

                // E = AES-128-CBC(key=K[0:16], iv=K[16:32], K1) — no padding (K1 is always block-aligned)
                byte[] aesKey = new byte[AesBlockBytes];
                byte[] aesIv  = new byte[AesBlockBytes];
                Buffer.BlockCopy(K, 0, aesKey, 0, AesBlockBytes);
                Buffer.BlockCopy(K, AesBlockBytes, aesIv, 0, AesBlockBytes);
                byte[] E = AesCbcNoPadding(true, aesKey, aesIv, K1);

                // Choose next hash based on (sum of first 16 bytes of E) mod 3
                int hashChoice = 0;
                for (int j = 0; j < AesBlockBytes; j++) hashChoice += E[j];
                hashChoice %= HashR6HashChoiceModulus;

                if (hashChoice == 0)
                {
                    using var sha256 = SHA256.Create();
                    K = sha256.ComputeHash(E);           // 32 bytes — hash E, not K1
                }
                else if (hashChoice == 1)
                {
                    using var sha384 = SHA384.Create();
                    K = sha384.ComputeHash(E);           // 48 bytes — hash E, not K1
                }
                else
                {
                    using var sha512 = SHA512.Create();
                    K = sha512.ComputeHash(E);           // 64 bytes — hash E, not K1
                }

                // Termination: i >= 63 and (last byte of E) <= (i - terminationOffset)
                if (i >= HashR6MinRoundIndex &&
                    (E[E.Length - 1] & 0xFF) <= (i - terminationOffset))
                    break;
            }

            byte[] result = new byte[FileKeyLength];
            Buffer.BlockCopy(K, 0, result, 0, FileKeyLength);
            return result;
        }

        internal static byte[] TruncateUtf8(string password, int maxBytes)
        {
            if (string.IsNullOrEmpty(password))
                return Array.Empty<byte>();
            byte[] utf8 = Encoding.UTF8.GetBytes(password);
            if (utf8.Length <= maxBytes) return utf8;
            var truncated = new byte[maxBytes];
            Buffer.BlockCopy(utf8, 0, truncated, 0, maxBytes);
            return truncated;
        }

        /// <summary>
        /// The key object <paramref name="objectNumber"/>'s strings and streams are encrypted under
        /// with <paramref name="algorithm"/> (ISO 32000-2 §7.6.3): a per-object MD5 key for RC4 and
        /// AES-128 (Algorithm 1, AES salted with <c>sAlT</c>), the file key itself for AES-256 and
        /// AES-256-GCM (Algorithm 1.A; ISO/TS 32003 §5.2 — "the same key is used for all objects");
        /// null for <c>/Identity</c>, which encrypts nothing, and for an algorithm this library does not
        /// encrypt with.
        /// </summary>
        /// <remarks>
        /// One rule for the encryptor, the incremental encryptor and the decryptor, which each carried
        /// a copy of it in their dispatch: a disagreement between the writing and the reading side is
        /// a file its own password cannot open, and a round trip through a pair that share a mistake
        /// cannot show it.
        /// </remarks>
        internal static byte[] ObjectKey(byte[] fileKey, EncryptionAlgorithm algorithm, int objectNumber, int generationNumber)
            => algorithm switch
            {
                EncryptionAlgorithm.RC4_40 or EncryptionAlgorithm.RC4_40R3 or EncryptionAlgorithm.RC4_128
                    => DeriveObjectKeyMd5(fileKey, objectNumber, generationNumber, isAes: false),
                EncryptionAlgorithm.AES128 => DeriveObjectKeyMd5(fileKey, objectNumber, generationNumber, isAes: true),
                EncryptionAlgorithm.AES256 or EncryptionAlgorithm.AES256GCM => fileKey,
                _ => null,
            };

        /// <summary>
        /// <paramref name="data"/> of object <paramref name="objectNumber"/> encrypted with
        /// <paramref name="algorithm"/> under <see cref="ObjectKey"/>; <c>/Identity</c> passes it through,
        /// deliberately, and an algorithm this library does not encrypt with throws — never the
        /// plaintext written into a document whose reader decrypts everything.
        /// </summary>
        internal static byte[] EncryptData(byte[] data, int objectNumber, int generationNumber, byte[] fileKey, EncryptionAlgorithm algorithm)
        {
            if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();
            if (algorithm == EncryptionAlgorithm.Identity) return data;
            byte[] key = ObjectKey(fileKey, algorithm, objectNumber, generationNumber);
            switch (algorithm)
            {
                case EncryptionAlgorithm.RC4_40:
                case EncryptionAlgorithm.RC4_40R3:
                case EncryptionAlgorithm.RC4_128:
                    return Rc4Process(key, data);
                case EncryptionAlgorithm.AES128:
                case EncryptionAlgorithm.AES256:
                    return PdfEncryptor.AesCbcEncrypt(key, data);
                case EncryptionAlgorithm.AES256GCM:
                    return PdfEncryptor.AesGcmEncrypt(key, data);
                default:
                    throw new PdfException($"Unsupported encryption algorithm: {algorithm}");
            }
        }

        /// <summary>
        /// The inverse of <see cref="EncryptData"/>: <c>/Identity</c> (or <c>/CFM /None</c>) data is not
        /// encrypted and passes through — RC4-processing it would corrupt legitimate plaintext.
        /// </summary>
        internal static byte[] DecryptData(byte[] data, int objectNumber, int generationNumber, byte[] fileKey, EncryptionAlgorithm algorithm)
        {
            if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();
            if (algorithm == EncryptionAlgorithm.Identity) return data;
            byte[] key = ObjectKey(fileKey, algorithm, objectNumber, generationNumber);
            switch (algorithm)
            {
                case EncryptionAlgorithm.RC4_40:
                case EncryptionAlgorithm.RC4_40R3:
                case EncryptionAlgorithm.RC4_128:
                    return Rc4Process(key, data);
                case EncryptionAlgorithm.AES128:
                case EncryptionAlgorithm.AES256:
                    return PdfDecryptor.AesCbcDecrypt(key, data);
                case EncryptionAlgorithm.AES256GCM:
                    return PdfDecryptor.AesGcmDecrypt(key, data);
                default:
                    throw new PdfException($"Unsupported decryption algorithm: {algorithm}");
            }
        }

        internal static byte[] DeriveObjectKeyMd5(byte[] encryptionKey, int objectNumber, int generationNumber, bool isAes)
        {
            using (var md5 = MD5.Create())
            {
                int extraLen = isAes ? AesObjectKeySalt.Length : 0;
                byte[] input = new byte[encryptionKey.Length + ObjectKeySuffixLength + extraLen];
                Buffer.BlockCopy(encryptionKey, 0, input, 0, encryptionKey.Length);

                input[encryptionKey.Length] = (byte)(objectNumber & 0xFF);
                input[encryptionKey.Length + 1] = (byte)((objectNumber >> 8) & 0xFF);
                input[encryptionKey.Length + 2] = (byte)((objectNumber >> 16) & 0xFF);
                input[encryptionKey.Length + 3] = (byte)(generationNumber & 0xFF);
                input[encryptionKey.Length + 4] = (byte)((generationNumber >> 8) & 0xFF);

                if (isAes)
                    Buffer.BlockCopy(AesObjectKeySalt, 0, input,
                        encryptionKey.Length + ObjectKeySuffixLength, AesObjectKeySalt.Length);

                byte[] hash = md5.ComputeHash(input);
                int keyLen = Math.Min(encryptionKey.Length + ObjectKeySuffixLength, Md5DigestLength);
                byte[] objectKey = new byte[keyLen];
                Buffer.BlockCopy(hash, 0, objectKey, 0, keyLen);
                return objectKey;
            }
        }

        // ------------------------------------------------------------------
        //  Ciphers — delegated to Base, which implements them with the BCL only.
        //
        //  The standard security handler needs MD5, SHA-2, RC4 and AES-CBC. Three of those four
        //  are in System.Security.Cryptography on every target framework this library builds for,
        //  netstandard2.0 included, and RC4 is twenty lines — so none of them is a reason for
        //  BouncyCastle to be in a consumer's package graph. Only PdfPublicKeyEncryptor genuinely
        //  needs it, for CMS enveloped-data recipients.
        //
        //  Delegating rather than copying: two implementations of one cipher is the divergence
        //  shape CLAUDE.md records for the two renderers, and a one-byte difference in a keystream
        //  is a corrupt document rather than a warning. CryptoPrimitiveParityTests keeps its OWN
        //  BouncyCastle calls as the independent oracle, so it stays meaningful after this
        //  delegation instead of comparing the shared implementation with itself.
        // ------------------------------------------------------------------

        internal static byte[] Rc4Process(byte[] key, byte[] data)
            => Tetitulz.Pdf.Core.Encryption.PdfCryptoPrimitives.Rc4(key, data);

        internal static byte[] AesCbcNoPadding(bool encrypt, byte[] key, byte[] iv, byte[] data)
            => Tetitulz.Pdf.Core.Encryption.PdfCryptoPrimitives.AesCbcNoPadding(encrypt, key, iv, data);
    }
}
