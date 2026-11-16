// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Adversarial input: an <c>/Encrypt</c> dictionary with no <c>/O</c> entry (or an <c>/O</c> that
/// is not a string).
/// </summary>
/// <remarks>
/// <para>
/// §7.6.3.3 Algorithm 2 step (c) hashes the 32-byte <c>/O</c> entry into the encryption key, so a
/// document without one has no derivable key. <c>GetBytesFromDict</c> returns <c>null</c> for both
/// shapes and nothing checked it: <c>ComputeEncryptionKey</c> then dereferenced <c>null</c> inside
/// <c>md5.TransformBlock(oValue, 0, oValue.Length, …)</c>, so a ~400-byte crafted file threw a raw
/// <see cref="NullReferenceException"/> out of every entry point — including
/// <c>PdfIncrementalEncryption.TryCreate</c>, whose contract is to return <c>false</c>, and
/// <c>PdfEncryptedRoundTrip.Run</c>, documented as reporting failures rather than throwing. A
/// server catching <see cref="PdfException"/> around document open took an unhandled exception
/// instead. The missing <c>/U</c> case was already handled — <c>PasswordMatches</c> returns false —
/// and the V=5 path guards both, so this is V&lt;5 only: RC4-40, RC4-128 and AES-128, i.e. most
/// encrypted PDFs in the wild.
/// </para>
/// <para>
/// The assertion is on the guard's own evidence — a typed <see cref="PdfException"/> naming
/// <c>/O</c> — not on "it did not crash", and never on a wall clock.
/// </para>
/// </remarks>
public class MalformedEncryptDictionaryTests
{
    [Theory]
    [InlineData(null)]           // /O absent entirely
    [InlineData("/O /NotAString")] // /O present but not a string
    public void EncryptDictionaryWithoutO_IsRefusedWithATypedReason(string? oEntry)
    {
        byte[] pdf = BuildEncryptedPdfWithoutOwnerValue(oEntry);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => PdfDecryptor.Decrypt(input, output, "anything"));

        Assert.Contains("/O", ex.Message);
    }

    /// <summary>
    /// The brute-force fast path must refuse for the same reason. It bypasses
    /// <c>ResolveEncryption</c> entirely, which is how the two paths diverge if the guard is not
    /// at the one point they share.
    /// </summary>
    [Fact]
    public void TryPasswordOnADictionaryWithoutO_IsRefusedWithATypedReason()
    {
        byte[] pdf = BuildEncryptedPdfWithoutOwnerValue(null);

        using var input = new MemoryStream(pdf);
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(input);

        var ex = Assert.Throws<PdfException>(() => PdfDecryptor.TryPassword(info, "anything"));

        Assert.Contains("/O", ex.Message);
    }

    /// <summary>
    /// The refusal must be specific to the malformed dictionary: an <c>/Encrypt</c> that DOES
    /// carry <c>/O</c> must still reach ordinary password validation, i.e. report a wrong password
    /// rather than the structural refusal above. Without this control the guard could pass by
    /// rejecting every encrypted document.
    /// </summary>
    [Fact]
    public void AWellFormedEncryptDictionary_StillReportsAWrongPasswordInstead()
    {
        byte[] pdf = BuildEncryptedPdfWithoutOwnerValue(
            "/O <" + new string('a', 64) + ">"); // 32 arbitrary bytes: present, wrong

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => PdfDecryptor.Decrypt(input, output, "anything"));

        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/O", ex.Message);
    }

    /// <summary>
    /// A V=1 / R=2 RC4-40 document whose <c>/Encrypt</c> carries <c>/U</c> but whose <c>/O</c> is
    /// supplied by <paramref name="oEntry"/> (null = the key is absent altogether).
    /// </summary>
    private static byte[] BuildEncryptedPdfWithoutOwnerValue(string? oEntry)
    {
        var sb = new StringBuilder();
        var offsets = new Dictionary<int, int>();
        void Obj(int number, string body) { offsets[number] = sb.Length; sb.Append(body); }

        // 32 bytes of hex for /U — well-formed, simply not matching anything.
        string u = new string('b', 64);

        sb.Append("%PDF-1.4\n");
        Obj(1, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(3, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj(4, "4 0 obj\n<< /Filter /Standard /V 1 /R 2 /P -1 "
             + (oEntry == null ? "" : oEntry + " ")
             + $"/U <{u}> >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        for (int n = 1; n <= 4; n++)
            sb.AppendFormat("{0:D10} 00000 n \n", offsets[n]);
        sb.AppendFormat(
            "trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R /ID [<0102030405060708090a0b0c0d0e0f10>"
            + " <0102030405060708090a0b0c0d0e0f10>] >>\nstartxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
