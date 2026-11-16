// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// An <c>/Encrypt</c> dictionary with no <c>/O</c> — or an <c>/O</c> that is not a string — must be
/// refused through each entry point's own contract, never as a <see cref="NullReferenceException"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetBytesFromDict</c> returns <see langword="null"/> for an absent or non-string entry, and
/// nothing between there and <c>PdfEncryptionPrimitives.ComputeEncryptionKey</c> checked it: §7.6.3.3
/// Algorithm 2 step (c) mixes the 32 bytes of <c>/O</c> into the key, so that null reached
/// <c>md5.TransformBlock(oValue, 0, oValue.Length, …)</c> unguarded. <c>/U</c> was already handled
/// (<c>PasswordMatches</c> returns false for a null), and the V=5 path guarded both — so only
/// RC4-40, RC4-128 and AES-128 were affected, which is the majority of encrypted PDFs in the wild.
/// </para>
/// <para>
/// <b>The exception type is the whole finding.</b> Every caller of this assembly is documented
/// around <c>PdfException</c> and nothing else: <c>PdfIncrementalEncryption.TryCreate</c> promises a
/// <see langword="bool"/> and catches only <c>PdfException</c>/<c>IOException</c>, and
/// <c>PdfEncryptedRoundTrip.Run</c> promises "failures are reported, not thrown, except for argument
/// errors". A ~400-byte crafted file therefore took a server's document-open path down with an
/// unhandled exception. Asserting merely that "something was thrown" would still pass with the
/// defect present, so each case asserts the <em>type</em>.
/// </para>
/// </remarks>
public class MissingOwnerValueTests
{
    /// <summary>
    /// The <c>Decrypt</c> path refuses with a typed, catchable <c>PdfException</c> that names the
    /// missing entry — not the <c>NullReferenceException</c> it used to propagate.
    /// </summary>
    [Fact]
    public void MalformedEncryptDictWithoutOwnerValue_DecryptThrowsPdfExceptionNamingTheEntry()
    {
        using var input = new MemoryStream(EncryptedPdfWithoutOwnerValue());
        using var output = new MemoryStream();

        Exception ex = Record.Exception(() => PdfDecryptor.Decrypt(input, output, "anything"));

        Assert.NotNull(ex);
        Assert.IsAssignableFrom<PdfException>(ex);
        Assert.Contains("/O", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>TryPassword</c> fast path is the second, separate way into the same key derivation,
    /// and it must refuse with the same typed <c>PdfException</c> the <c>Decrypt</c> path raises —
    /// not the <c>NullReferenceException</c> it used to propagate, and not a bare
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a throw rather than <see langword="false"/>.</b> §7.6.3.3 Algorithm 2 step (c) mixes
    /// the 32 bytes of <c>/O</c> into the key, and Table 21 makes <c>/O</c> <em>required</em> for
    /// the standard security handler — so a dictionary without one is a malformed <em>document</em>,
    /// and no password can ever derive a key from it. <see langword="false"/> means "this password
    /// did not match", a claim about the <em>password</em> that the code cannot support here: a
    /// brute-force caller reading it would grind through the rest of its list for nothing. Throwing
    /// also keeps this entry point consistent with <c>Decrypt</c>, which refuses the identical
    /// input the identical way (asserted above).
    /// </para>
    /// <para>
    /// This is the one point where the two sessions that fixed this defect disagreed — the other
    /// asserted <see langword="false"/> here. The finding this test class documents is that the
    /// refusal be <em>typed</em> rather than a <c>NullReferenceException</c>, and that is preserved
    /// either way; the resolution follows the spec's "required entry" reading, and matches
    /// <c>MalformedEncryptDictionaryTests.TryPasswordOnADictionaryWithoutO_IsRefusedWithATypedReason</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void MalformedEncryptDictWithoutOwnerValue_TryPasswordThrowsPdfExceptionNotNullReference()
    {
        using var input = new MemoryStream(EncryptedPdfWithoutOwnerValue());
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(input);

        var ex = Assert.Throws<PdfException>(() => PdfDecryptor.TryPassword(info, "anything"));

        Assert.Contains("/O", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>TryCreate</c>'s documented contract — <see langword="false"/> with a reason — must hold for
    /// this input. It catches <c>PdfException</c>/<c>IOException</c> only, so the old
    /// <c>NullReferenceException</c> escaped straight through a method whose whole shape promises it
    /// does not throw.
    /// </summary>
    [Fact]
    public void MalformedEncryptDictWithoutOwnerValue_TryCreateReturnsFalseWithAReason()
    {
        using var input = new MemoryStream(EncryptedPdfWithoutOwnerValue());

        bool created = true;
        Exception ex = Record.Exception(() =>
            created = PdfIncrementalEncryption.TryCreate(input, "anything", out _, out _));

        Assert.Null(ex);
        Assert.False(created);

        // Re-run to read the reason out: `out` parameters cannot be assigned inside a lambda.
        input.Position = 0;
        Assert.False(PdfIncrementalEncryption.TryCreate(input, "anything", out var encryptor, out string error));
        Assert.Null(encryptor);
        Assert.False(string.IsNullOrEmpty(error));
    }

    /// <summary>
    /// A well-formed <c>/Encrypt</c> that differs only by carrying an <c>/O</c> must still be
    /// refused for a <em>wrong password</em> rather than for a missing entry — otherwise the guard
    /// above could pass by rejecting every V&lt;5 document, and the two failures would be
    /// indistinguishable to a caller reading the message.
    /// </summary>
    [Fact]
    public void EncryptDictWithOwnerValue_StillFailsOnThePasswordRatherThanOnTheMissingEntry()
    {
        using var input = new MemoryStream(EncryptedPdfWithOwnerValue());
        using var output = new MemoryStream();

        Exception ex = Record.Exception(() => PdfDecryptor.Decrypt(input, output, "wrong"));

        Assert.NotNull(ex);
        Assert.IsAssignableFrom<PdfException>(ex);
        Assert.DoesNotContain("has no /O", ex.Message, StringComparison.Ordinal);
    }

    private static byte[] EncryptedPdfWithoutOwnerValue() =>
        CraftedPdf("<< /Filter /Standard /V 1 /R 2 /P -1 /U <" + new string('A', 64) + "> >>");

    private static byte[] EncryptedPdfWithOwnerValue() =>
        CraftedPdf("<< /Filter /Standard /V 1 /R 2 /P -1 /O <" + new string('B', 64) + ">"
                   + " /U <" + new string('A', 64) + "> >>");

    /// <summary>
    /// A minimal V=1 / R=2 "encrypted" document whose /Encrypt dictionary is supplied verbatim, so
    /// one builder covers both the malformed and the well-formed shape. The body objects are
    /// plaintext, which is irrelevant: every assertion above is decided during key derivation,
    /// before a single object is touched.
    /// </summary>
    private static byte[] CraftedPdf(string encryptDictBody)
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj("4 0 obj\n" + encryptDictBody + "\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat(
            "trailer\n<< /Size {0} /Root 1 0 R /Encrypt 4 0 R /ID [<{1}> <{1}>] >>\nstartxref\n{2}\n%%EOF\n",
            offsets.Count + 1, new string('C', 32), xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
