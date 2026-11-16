// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The <b>legacy</b> algorithm-selection ladder in <c>PdfDecryptor</c> — the <c>V=1</c>/<c>V=2</c>
/// branches that predate crypt filters, and the <c>else</c> that refuses everything else.
///
/// <para><b>Found by mutation, not by reading.</b> Two deliberate mutations of that ladder survived
/// the whole <c>Encryption.Tests</c> suite: selecting <c>RC4_40</c> where <c>RC4_128</c> is
/// declared, and <b>accepting an unsupported <c>V</c>/<c>R</c> combination instead of refusing
/// it</b>. The reason is uniform — every existing RC4-128 test goes through <c>PdfEncryptor</c>,
/// which writes <c>V=4</c> crypt filters and therefore takes the branch <i>above</i> this ladder,
/// and the only legacy fixture in the suite (<c>TestPdfs.LegacyRc4</c>) is 40-bit. So the V=2 path
/// had no fixture at all, and neither did the refusal.</para>
///
/// <para>The second gap matters more than its size: CLAUDE.md records the guarantee as "an
/// unrecognised encryption combination fails <b>with a reason</b> — never a silent downgrade to
/// the AES128 default", and nothing was checking it.</para>
///
/// <para><b>The algorithms below are transcribed independently</b> from ISO 32000-1 §7.6.3, as
/// <c>TestPdfs.LegacyRc4</c> does for R=2. Building the fixture with the library's own primitives
/// would make the test circular: it would pass with both sides wrong in the same way.</para>
/// </summary>
public class LegacyAlgorithmSelectionTests
{
    private const string Marker = "LEGACYRC4MARKER";
    private const string UserPassword = "";
    private const string OwnerPassword = "owner-secret";
    private const int PValue = -3904;

    /// <summary>ISO 32000-1 Table 20's 32-byte padding string.</summary>
    private static readonly byte[] Pad =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
    };

    private static readonly byte[] FileId =
    {
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00
    };

    // ---- independently transcribed primitives -------------------------------------------

    private static byte[] Padded(string password)
    {
        byte[] pw = Encoding.GetEncoding("iso-8859-1").GetBytes(password ?? "");
        var result = new byte[32];
        int n = Math.Min(pw.Length, 32);
        Array.Copy(pw, result, n);
        Array.Copy(Pad, 0, result, n, 32 - n);
        return result;
    }

    private static byte[] Md5(params byte[][] parts)
    {
        using var md5 = MD5.Create();
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p);
        return md5.ComputeHash(all.ToArray());
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
        var outBytes = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            outBytes[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return outBytes;
    }

    /// <summary>Algorithm 3, revision 3: the <c>/O</c> value.</summary>
    private static byte[] OwnerValue(int keyBytes)
    {
        byte[] digest = Md5(Padded(OwnerPassword));
        for (int i = 0; i < 50; i++) digest = Md5(SubArray(digest, 16));   // R >= 3

        byte[] key = SubArray(digest, keyBytes);
        byte[] value = Rc4(key, Padded(UserPassword));
        for (int i = 1; i <= 19; i++) value = Rc4(Xor(key, (byte)i), value);
        return value;
    }

    /// <summary>Algorithm 2, revision 3: the file encryption key.</summary>
    private static byte[] FileKey(byte[] oValue, int keyBytes)
    {
        byte[] p = BitConverter.GetBytes(PValue);      // little-endian, four bytes
        byte[] digest = Md5(Padded(UserPassword), oValue, p, FileId);
        for (int i = 0; i < 50; i++) digest = Md5(SubArray(digest, keyBytes));   // R >= 3
        return SubArray(digest, keyBytes);
    }

    /// <summary>Algorithm 5, revision 3: the <c>/U</c> value.</summary>
    private static byte[] UserValue(byte[] key)
    {
        byte[] value = Rc4(key, Md5(Pad, FileId));
        for (int i = 1; i <= 19; i++) value = Rc4(Xor(key, (byte)i), value);

        var padded = new byte[32];
        Array.Copy(value, padded, 16);
        Array.Copy(Pad, 0, padded, 16, 16);            // arbitrary padding, §7.6.3.4
        return padded;
    }

    /// <summary>Algorithm 1: the per-object key.</summary>
    private static byte[] ObjectKey(byte[] fileKey, int obj, int gen)
    {
        var extended = new List<byte>(fileKey)
        {
            (byte)(obj & 0xFF), (byte)((obj >> 8) & 0xFF), (byte)((obj >> 16) & 0xFF),
            (byte)(gen & 0xFF), (byte)((gen >> 8) & 0xFF)
        };
        byte[] digest = Md5(extended.ToArray());
        return SubArray(digest, Math.Min(fileKey.Length + 5, 16));
    }

    private static byte[] SubArray(byte[] src, int n)
    {
        var r = new byte[n];
        Array.Copy(src, r, n);
        return r;
    }

    private static byte[] Xor(byte[] key, byte b)
    {
        var r = new byte[key.Length];
        for (int i = 0; i < key.Length; i++) r[i] = (byte)(key[i] ^ b);
        return r;
    }

    private static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (byte x in b) sb.Append(x.ToString("X2"));
        return sb.ToString();
    }

    // ---- fixture ------------------------------------------------------------------------

    /// <summary>
    /// A legacy encrypted document with no crypt filters, so <c>PdfDecryptor</c> must pick the
    /// algorithm from <c>/V</c> and <c>/R</c> alone.
    /// </summary>
    private static byte[] BuildLegacy(int v, int r, int keyBits)
    {
        int keyBytes = keyBits / 8;
        byte[] o = OwnerValue(keyBytes);
        byte[] key = FileKey(o, keyBytes);
        byte[] u = UserValue(key);

        string content = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET\n";
        byte[] encrypted = Rc4(ObjectKey(key, 4, 0), Encoding.ASCII.GetBytes(content));

        var bytes = new List<byte>();
        var offsets = new List<int>();
        void Ascii(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
        void Obj(string body) { offsets.Add(bytes.Count); Ascii(body); }

        Ascii("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");

        offsets.Add(bytes.Count);
        Ascii($"4 0 obj\n<< /Length {encrypted.Length} >>\nstream\n");
        bytes.AddRange(encrypted);
        Ascii("\nendstream\nendobj\n");

        // /Length is written only for V=2; §7.6.1 Table 20 makes it meaningless for V=1 (always 40).
        string lengthEntry = v == 1 ? "" : $" /Length {keyBits}";
        Obj($"5 0 obj\n<< /Filter /Standard /V {v} /R {r}{lengthEntry} "
            + $"/O <{Hex(o)}> /U <{Hex(u)}> /P {PValue} >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = bytes.Count;
        Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) Ascii($"{off:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Encrypt 5 0 R "
              + $"/ID [<{Hex(FileId)}> <{Hex(FileId)}>] >>\nstartxref\n{xref}\n%%EOF\n");

        return bytes.ToArray();
    }

    // ---- the two gaps -------------------------------------------------------------------

    /// <summary>
    /// <c>V=2 /R=3 /Length 128</c> — legacy RC4-128, no crypt filters. Decryption only produces
    /// the marker if the ladder picks a <b>16-byte</b> key; with the 40-bit branch the RC4 stream
    /// is wrong from its first byte and the content is noise.
    ///
    /// <para>This is the case the surviving mutation exposed: every other RC4-128 test in this
    /// suite writes <c>V=4</c> crypt filters and never reaches the ladder.</para>
    /// </summary>
    [Fact]
    public void LegacyRc4_128IsDecryptedWithA128BitKeyNotThe40BitOne()
    {
        byte[] pdf = BuildLegacy(v: 2, r: 3, keyBits: 128);

        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(pdf), output, UserPassword);

        string text = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains(Marker, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The neighbouring rung, so the test above is a claim about <i>key length</i> rather than
    /// about legacy decryption working at all: <c>V=2 /R=3 /Length 40</c> also round-trips.
    /// </summary>
    [Fact]
    public void LegacyRc4_40IsStillDecryptedWhenThatIsWhatTheFileDeclares()
    {
        byte[] pdf = BuildLegacy(v: 2, r: 3, keyBits: 40);

        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(pdf), output, UserPassword);

        Assert.Contains(Marker, Encoding.ASCII.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The ladder's <c>else</c>, which had no test: a <c>V</c> below the crypt-filter threshold
    /// that names no algorithm this library implements is <b>refused with a reason</b>, never
    /// silently downgraded. <c>V=3</c> is an unpublished Adobe extension.
    ///
    /// <para><b>Scope, and it is narrower than "any unsupported V/R".</b> Only <c>V &lt; 4</c>
    /// reaches this ladder — <c>V &gt;= 4</c> is the crypt-filter branch above it, and a
    /// <i>higher unknown</i> version such as <c>V=7</c> is accepted there rather than refused.
    /// Whether that leniency is right is a design question and is raised as its own row rather
    /// than asserted here; what is pinned is the refusal that genuinely exists.</para>
    /// </summary>
    [Theory]
    [InlineData(3, 3)]     // V=3: unpublished algorithm
    [InlineData(2, 9)]     // a revision no version defines
    public void AnUnsupportedLegacyVersionRevisionPairIsRefusedWithAReason(int v, int r)
    {
        byte[] pdf = BuildLegacy(v, r, keyBits: 128);

        var ex = Assert.Throws<PdfException>(() =>
            PdfDecryptor.Decrypt(new MemoryStream(pdf), new MemoryStream(), UserPassword));

        // The reason must name what it could not honour - "unsupported" alone tells a caller
        // nothing about which document feature to look at.
        Assert.Contains($"V={v}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"R={r}", ex.Message, StringComparison.Ordinal);
    }
}
