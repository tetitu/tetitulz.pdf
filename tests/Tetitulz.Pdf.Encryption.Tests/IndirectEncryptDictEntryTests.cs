// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Entries of the <c>/Encrypt</c> dictionary written as <b>indirect references</b> must be read.
/// </summary>
/// <remarks>
/// <para><b>In a security handler, "absent" is never neutral — it selects a default.</b> Every read
/// in <c>PdfDecryptor</c> tested the raw value's type, so a legal <c>/StmF 5 0 R</c> (§7.3.10)
/// read as absent, and absent <c>/StmF</c> means <b>Identity</b>: that half of the document is not
/// encrypted at all. The decryptor therefore passed ciphertext through as though it were plaintext
/// — <i>and reported</i> "absent, so this half of the document is not encrypted", which is a false
/// statement about the file rather than a silent failure. That diagnostic is the only channel by
/// which a host learns whether a document presenting itself as encrypted really is.</para>
///
/// <para>The same read shape covered <c>/V</c>, <c>/R</c>, <c>/Length</c>, <c>/P</c>, <c>/ID</c> and
/// <c>/EncryptMetadata</c>, all of which feed Algorithm 2 — a wrong value derives a wrong file key,
/// so <b>a correct password is rejected</b>. <c>/ID</c> is the one a real file is most likely to
/// write indirectly.</para>
///
/// <para><b>What is deliberately NOT resolved, and why it is a real distinction:</b> <c>/O</c>,
/// <c>/U</c>, <c>/UE</c>, <c>/OE</c> and <c>/Perms</c> are <i>strings</i>. §7.6.2 exempts strings in
/// the encryption dictionary from encryption — the dictionary, not some other object it points at —
/// so a string reached through a reference would be an ordinary encrypted object needing the very
/// key it exists to derive. An indirect <c>/O</c> is not a document we fail to read; it is one
/// nobody can read.</para>
/// </remarks>
public class IndirectEncryptDictEntryTests
{
    /// <summary>
    /// An indirect <c>/StmF</c> must resolve to its crypt filter, not be reported as "this half of
    /// the document is not encrypted".
    /// </summary>
    [Fact]
    public void IndirectStreamFilter_IsNotReportedAsUnencrypted()
    {
        byte[] pdf = EncryptedShell(
            "/StmF 5 0 R /StrF /StdCF /CF << /StdCF << /CFM /AESV2 >> >>",
            extraObject: "5 0 obj\n/StdCF\nendobj\n");

        Decrypt(pdf);

        string slot = PdfDecryptor.LastCryptFilterFallback;

        // The whole point: no "not encrypted" claim about a document whose /StmF names a real,
        // present crypt filter.
        Assert.True(slot == null || !slot.Contains("not encrypted"),
            "the decryptor reported an encrypted document as unencrypted: " + slot);
    }

    /// <summary>
    /// The control, and the reason the assertion above is not vacuous: a genuinely absent
    /// <c>/StmF</c> really does report that the streams are not encrypted. If the fix had been
    /// "never report", this would go red.
    /// </summary>
    [Fact]
    public void AbsentStreamFilter_IsStillReportedAsUnencrypted()
    {
        byte[] pdf = EncryptedShell("/StrF /StdCF /CF << /StdCF << /CFM /AESV2 >> >>");

        Decrypt(pdf);

        string slot = PdfDecryptor.LastCryptFilterFallback;

        Assert.NotNull(slot);
        Assert.Contains("not encrypted", slot);
    }

    /// <summary>
    /// <c>/V</c>, <c>/R</c> and <c>/Length</c> written indirectly must reach
    /// <see cref="EncryptionInfo"/> — they feed Algorithm 2, and a wrong value rejects a correct
    /// password.
    /// </summary>
    [Fact]
    public void IndirectVersionAndRevision_AreReported()
    {
        byte[] pdf = EncryptedShell(
            "/StmF /StdCF /StrF /StdCF /CF << /StdCF << /CFM /AESV2 >> >>",
            indirectVersion: true,
            extraObject: "5 0 obj\n4\nendobj\n6 0 obj\n128\nendobj\n");

        using var ms = new MemoryStream(pdf);
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(ms);

        Assert.Equal(4, info.V);
        Assert.Equal(4, info.R);
        Assert.Equal(128, info.KeyLengthBits);
    }

    // ------------------------------------------------------------------

    private static void Decrypt(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try { PdfDecryptor.Decrypt(input, output, "irrelevant"); }
        catch (Tetitulz.Pdf.Core.PdfException)
        {
            // These shells carry a deliberately unopenable /Encrypt dictionary. Crypt-filter
            // resolution happens before any password check, so the slot is written either way —
            // the same reasoning CryptFilterDiagnosticTests states for its own fixtures.
        }
    }

    /// <summary>
    /// A document with a hand-written <c>/Encrypt</c> dictionary, which is all the crypt-filter
    /// and parameter reads touch.
    /// </summary>
    private static byte[] EncryptedShell(
        string cryptFilterKeys, bool indirectVersion = false, string extraObject = null)
    {
        string hex = new string('a', 64);
        string v = indirectVersion ? "5 0 R" : "4";
        string len = indirectVersion ? "6 0 R" : "128";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj($"4 0 obj\n<< /Filter /Standard /V {v} /R 4 /Length {len} /P -1 {cryptFilterKeys} "
          + $"/O <{hex}> /U <{hex}> >>\nendobj\n");

        if (extraObject != null)
            foreach (string body in SplitObjects(extraObject))
                Obj(body);

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R /Encrypt 4 0 R "
                      + "/ID [<0123456789abcdef0123456789abcdef> <0123456789abcdef0123456789abcdef>] >>\n"
                      + "startxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static System.Collections.Generic.IEnumerable<string> SplitObjects(string objects)
    {
        int at = 0;
        while (at < objects.Length)
        {
            int end = objects.IndexOf("endobj\n", at, System.StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return objects.Substring(at, end - at + "endobj\n".Length);
            at = end + "endobj\n".Length;
        }
    }
}
