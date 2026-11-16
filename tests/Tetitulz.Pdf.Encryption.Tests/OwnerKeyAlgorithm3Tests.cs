// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// ISO 32000-1 Algorithm 3's fifty rounds hash the first <i>n</i> bytes of the previous digest, as
/// a common viewer, an independent checker, an independent implementation, another library and another reader read it (Algorithm 2 says so in words; Algorithm 3 does
/// not). This library hashed all sixteen — the same answer at 128 bits, a different <c>/O</c> at 40
/// bits under revision 3, so an owner password it wrote failed everywhere else. The expected value is
/// transcribed here independently, with its own RC4.
/// </summary>
public class OwnerKeyAlgorithm3Tests
{
    private static readonly byte[] Pad =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40R3, 5)]
    [InlineData(EncryptionAlgorithm.RC4_128, 16)]
    public void TheOwnerValue_IsAlgorithm3OverTheFirstNBytesOfEachRound(EncryptionAlgorithm algorithm, int n)
    {
        byte[] encrypted = Encrypt(algorithm, user: "user-pw", owner: "owner-pw");

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var o = Assert.IsType<PdfString>(doc.EncryptionDictionary![PdfName.O]).RawBytes;

        Assert.Equal(ExpectedOwnerValue("user-pw", "owner-pw", n), o);
    }

    /// <summary>The owner password opens the file it was written with, at 40 bits under revision 3 — the decryptor derives the same key.</summary>
    [Fact]
    public void TheOwnerPassword_OpensARevision3FortyBitFile()
    {
        byte[] encrypted = Encrypt(EncryptionAlgorithm.RC4_40R3, user: "user-pw", owner: "owner-pw");

        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), output, "owner-pw");
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Contains(TestPdfs.Plaintext.Marker, Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The decryptor carries the revision a <c>/V 1</c> file declares: revision 3 resolves to
    /// <see cref="EncryptionAlgorithm.RC4_40R3"/>, revision 2 to <see cref="EncryptionAlgorithm.RC4_40"/>
    /// — every <c>/V 1</c> was revision 2 to it, so the member had no reader (the review of batch 131).
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40R3)]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    public void TheDecryptor_ResolvesAV1FileToTheRevisionItDeclares(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(algorithm, user: "user-pw", owner: "owner-pw");

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        Assert.Equal(1, doc.EncryptionDictionary![PdfName.V] is PdfNumber v ? v.LongValue : -1);
        var resolved = PdfDecryptor.ResolveEncryption(doc, "user-pw");

        Assert.Equal(algorithm, resolved.StreamAlgorithm);
        Assert.Equal(algorithm, resolved.StringAlgorithm);
    }

    private static byte[] Encrypt(EncryptionAlgorithm algorithm, string user, string owner)
    {
        using var input = new MemoryStream(TestPdfs.Plaintext.Build());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters { UserPassword = user, OwnerPassword = owner, Algorithm = algorithm });
        return output.ToArray();
    }

    /// <summary>Algorithm 3, transcribed: steps (a) to (d) over the first <paramref name="n"/> bytes each round, (e) to (g) over the padded user password.</summary>
    private static byte[] ExpectedOwnerValue(string user, string owner, int n)
    {
        byte[] hash = MD5.HashData(Padded(owner));
        for (int i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, n).ToArray());
        byte[] key = hash.AsSpan(0, n).ToArray();

        byte[] value = Rc4(key, Padded(user));
        for (int round = 1; round <= 19; round++)
        {
            var k = new byte[n];
            for (int j = 0; j < n; j++) k[j] = (byte)(key[j] ^ round);
            value = Rc4(k, value);
        }
        return value;
    }

    private static byte[] Padded(string password)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(password);
        var padded = new byte[32];
        int copy = Math.Min(32, bytes.Length);
        Buffer.BlockCopy(bytes, 0, padded, 0, copy);
        Buffer.BlockCopy(Pad, 0, padded, copy, 32 - copy);
        return padded;
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return output;
    }
}
