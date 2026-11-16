// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A document whose <c>/P</c> is written in the <b>unsigned</b> spelling of the same 32 bits — the
/// form real producers emit — still derives the same file key and reports the same permissions.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> ISO 32000-2 §7.6.3, Table 22 defines <c>/P</c> as a signed 32-bit
/// integer, and producers routinely write the unsigned spelling of the identical bit pattern:
/// <c>4294963392</c> for <c>-3904</c>. Reading it with <c>PdfNumber.IntValue</c> is a
/// <c>double</c> → <c>int</c> cast, which on .NET Core 3.0 and later <b>saturates</b> to
/// <c>int.MaxValue</c> rather than truncating — so every permission reads as granted, and, worse,
/// <b>Algorithm 2 hashes the four bytes of <c>/P</c></b>, so the derived file key is wrong and the
/// correct password is rejected.</para>
///
/// <para><b>GAPS.md §9 recorded this as coverage debt, and it was.</b> The three
/// <c>PdfDecryptor</c> readers all go through <c>PdfPermissionValue.FromNumber</c>, and reverting
/// any one of them to the saturating read <b>survived this entire suite</b> — because every fixture
/// here is written by this library's own encryptor, which emits the signed spelling, where the two
/// readings agree. A fixture cannot be produced by the code it is meant to catch out.</para>
///
/// <para><b>The bytes are the encryptor's; only the spelling is rewritten.</b> <c>/O</c> and
/// <c>/U</c> stay exactly as computed, which is what makes this decisive: they were derived from
/// the four bytes <c>FF FF F0 C0</c>, and only a reader that recovers those same four bytes from
/// <c>4294963392</c> can reproduce the key. Rewriting the number lengthens the object, so the
/// cross-reference table and <c>startxref</c> are rebuilt from a scan rather than left stale.</para>
/// </remarks>
public sealed class UnsignedPermissionSpellingTests
{
    private const string UserPassword = "user-pw";
    private const string OwnerPassword = "owner-pw";

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void AnUnsignedlySpelledP_StillOpensWithTheCorrectPassword(EncryptionAlgorithm algorithm)
    {
        byte[] rewritten = EncryptedWithUnsignedP(algorithm, out int signedP);

        using var ms = new MemoryStream(rewritten);
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(ms);

        // The reader recovered the signed value from the unsigned spelling.
        Assert.Equal(signedP, info.PValue);

        // And the key derived from those four bytes still accepts the password, which is the half
        // that is not a permissions question at all.
        Assert.True(PdfDecryptor.TryPassword(info, UserPassword));
    }

    /// <summary>
    /// The <b>decrypt</b> path, which derives the file key from <c>/P</c> itself rather than from
    /// the <see cref="EncryptionInfo"/> a caller already read.
    /// </summary>
    /// <remarks>
    /// <b>This is a separate reader and needed its own fixture.</b> Mutating it back to the
    /// saturating read left the whole suite green even with the tests above present, because
    /// <c>TryPassword(info, …)</c> is handed a value the <i>other</i> reader produced. Algorithm 2
    /// hashes the four bytes of <c>/P</c>, so a saturated read here derives a different file key
    /// and the correct password is rejected — which is not a permissions question at all.
    /// </remarks>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void AnUnsignedlySpelledP_StillDecryptsToTheOriginalContent(EncryptionAlgorithm algorithm)
    {
        byte[] rewritten = EncryptedWithUnsignedP(algorithm, out _);

        using var input = new MemoryStream(rewritten);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, UserPassword);

        Assert.Contains("plain-marker", Encoding.Latin1.GetString(output.ToArray()),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>/Perms</c> authentication (Algorithm 9's stored copy of <c>/P</c>), which is the
    /// third reader and reports through <see cref="PdfDecryptor.LastPermsValidation"/>.
    /// </summary>
    /// <remarks>
    /// A saturated read never compares equal to the authenticated value, so it announces a
    /// permission-bitmask mismatch for a document nobody touched — accusing a conforming file of
    /// tampering, which the code's own comment says the check was written to avoid. AES-256 only:
    /// <c>/Perms</c> exists for revision 6.
    /// </remarks>
    [Fact]
    public void AnUnsignedlySpelledP_DoesNotMakeThePermsCheckAccuseTheDocument()
    {
        byte[] rewritten = EncryptedWithUnsignedP(EncryptionAlgorithm.AES256, out _);

        using var input = new MemoryStream(rewritten);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, UserPassword);

        Assert.Equal("OK", PdfDecryptor.LastPermsValidation);
    }

    /// <summary>
    /// The acceptance control: the same document with the encryptor's own signed spelling, through
    /// the same rebuild, so the tests above cannot pass on a file that was broken by the rewrite.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void TheSameDocumentWithTheSignedSpelling_OpensToo(EncryptionAlgorithm algorithm)
    {
        byte[] original = Encrypt(algorithm);
        byte[] rebuilt = RebuildXref(original);

        using var ms = new MemoryStream(rebuilt);
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(ms);

        Assert.True(info.PValue < 0, $"the encryptor is expected to write a negative /P; it wrote {info.PValue}");
        Assert.True(PdfDecryptor.TryPassword(info, UserPassword));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The encryptor's output with <c>/P -n</c> replaced by the unsigned spelling of the same 32
    /// bits, and the cross-reference table rebuilt to match the new object offsets.
    /// </summary>
    private static byte[] EncryptedWithUnsignedP(EncryptionAlgorithm algorithm, out int signedP)
    {
        byte[] encrypted = Encrypt(algorithm);
        string text = Encoding.Latin1.GetString(encrypted);

        Match m = Regex.Match(text, @"/P\s+(-\d+)");
        Assert.True(m.Success, "the encryptor did not write a negative /P, so this fixture proves nothing");

        signedP = int.Parse(m.Groups[1].Value);
        uint unsigned = unchecked((uint)signedP);
        Assert.Equal(signedP, unchecked((int)unsigned));   // the two spellings really are one pattern

        string swapped = text.Substring(0, m.Index)
                       + "/P " + unsigned.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       + text.Substring(m.Index + m.Length);

        return RebuildXref(Encoding.Latin1.GetBytes(swapped));
    }

    private static byte[] Encrypt(EncryptionAlgorithm algorithm)
    {
        using var plain = new MemoryStream(MinimalPdf());
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = algorithm,
        });
        return encrypted.ToArray();
    }

    /// <summary>
    /// Rewrites the classic cross-reference table and <c>startxref</c> from a scan for
    /// <c>N 0 obj</c>, so a file whose bytes moved still parses.
    /// </summary>
    /// <remarks>
    /// Applied to the control as well as to the rewritten file. Rebuilding one and not the other
    /// would make the rebuild itself the difference between them, which is precisely the kind of
    /// second variable that makes a comparison prove nothing.
    /// </remarks>
    private static byte[] RebuildXref(byte[] file)
    {
        string text = Encoding.Latin1.GetString(file);

        int xrefAt = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
        Assert.True(xrefAt >= 0, "this fixture only handles a classic cross-reference table");

        string trailerText = text.Substring(text.IndexOf("trailer", xrefAt, StringComparison.Ordinal));
        int eof = trailerText.IndexOf("startxref", StringComparison.Ordinal);
        Assert.True(eof >= 0, "no startxref after the trailer");
        trailerText = trailerText.Substring(0, eof).TrimEnd('\r', '\n');

        string body = text.Substring(0, xrefAt + 1);

        var offsets = new SortedDictionary<int, int>();
        foreach (Match m in Regex.Matches(body, @"(?m)^(\d+)\s+0\s+obj"))
            offsets[int.Parse(m.Groups[1].Value)] = m.Index;

        Assert.NotEmpty(offsets);
        int size = 0;
        foreach (int n in offsets.Keys) size = Math.Max(size, n);
        size += 1;

        var sb = new StringBuilder(body);
        int startxref = sb.Length;
        sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        for (int n = 1; n < size; n++)
        {
            if (offsets.TryGetValue(n, out int off)) sb.AppendFormat("{0:D10} 00000 n \n", off);
            else sb.Append("0000000000 65535 f \n");
        }
        sb.Append(trailerText).Append('\n');
        sb.Append("startxref\n").Append(startxref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] MinimalPdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (plain-marker) Tj ET\n";
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
