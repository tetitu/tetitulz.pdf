// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// What the two entry points into key derivation promise a caller — <c>TryPassword</c>'s answer,
/// the diagnostic slots' scope, and the exception type a malformed crypt filter produces.
/// </summary>
public class DecryptorEntryPointContractTests
{
    /// <summary>
    /// <c>TryPassword</c> is documented as the fast path for brute force. On an owner-only
    /// document — no user password, restricted permissions, the most common encrypted shape in the
    /// wild — the empty-user-password fallback inside <c>ValidateAndDeriveKey</c> matched
    /// unconditionally, so the FIRST candidate a caller tried came back true and was reported as
    /// the owner password. It is wrong, and no further candidate is ever tested.
    /// </summary>
    [Fact]
    public void TryPassword_OnAnEmptyUserPasswordDocument_RejectsAWrongCandidate()
    {
        EncryptionInfo info = InfoFor(userPassword: "", ownerPassword: "letmein");

        Assert.False(PdfDecryptor.TryPassword(info, "not-the-owner-password"));
    }

    /// <summary>
    /// The control: the real owner password, and the (empty) real user password, must still be
    /// accepted — the check must reject the wrong candidate, not every candidate.
    /// </summary>
    [Fact]
    public void TryPassword_StillAcceptsTheRealCredentials()
    {
        EncryptionInfo info = InfoFor(userPassword: "", ownerPassword: "letmein");

        Assert.True(PdfDecryptor.TryPassword(info, "letmein"), "the owner password was rejected");
        Assert.True(PdfDecryptor.TryPassword(info, ""), "the (empty) user password was rejected");
    }

    /// <summary>
    /// And the document still opens with no password at all: the <c>Decrypt</c> path keeps the
    /// empty-user fallback, because there opening the document is the goal.
    /// </summary>
    [Fact]
    public void Decrypt_StillOpensAnEmptyUserPasswordDocument()
    {
        byte[] encrypted = Encrypt(userPassword: "", ownerPassword: "letmein");

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "");

        Assert.Contains("plain-marker", Encoding.Latin1.GetString(output.ToArray()));
    }

    /// <summary>
    /// <c>LastPermsValidation</c>'s documented rule is "refuse to honour /P unless it says OK", so a
    /// verdict must never be inherited from a previous file. <c>ResolveEncryption</c> resets it;
    /// <c>TryPassword</c> — the second way into the same code — did not, so an AES-256 document's
    /// "OK" was still in the slot while the caller was testing passwords against an RC4 document,
    /// where /Perms does not apply and was never checked.
    /// </summary>
    [Fact]
    public void TryPassword_DoesNotInheritThePermsVerdictFromThePreviousDocument()
    {
        EncryptionInfo aes256 = InfoFor("secret", "secret", EncryptionAlgorithm.AES256);
        Assert.True(PdfDecryptor.TryPassword(aes256, "secret"));
        Assert.Equal("OK", PdfDecryptor.LastPermsValidation);   // the slot is dirty going in…

        EncryptionInfo rc4 = InfoFor("secret", "secret", EncryptionAlgorithm.RC4_128);
        PdfDecryptor.TryPassword(rc4, "secret");

        Assert.NotEqual("OK", PdfDecryptor.LastPermsValidation);
        Assert.Equal("SkippedNonAes256Key", PdfDecryptor.LastPermsValidation);
    }

    /// <summary>
    /// An unknown <c>/CFM</c> is attacker-controllable, and every caller in this assembly catches
    /// <see cref="PdfException"/> only: <c>PdfIncrementalEncryption.TryCreate</c>'s contract is to
    /// return false and <c>PdfEncryptedRoundTrip.Run</c>'s is to report rather than throw. An
    /// <see cref="InvalidOperationException"/> escaped both.
    /// </summary>
    [Fact]
    public void UnknownCryptFilterMethod_IsRefusedAsAPdfException()
    {
        byte[] pdf = MalformedCfmPdf();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => PdfDecryptor.Decrypt(input, output, "x"));
        Assert.Contains("BogusCFM", ex.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static EncryptionInfo InfoFor(
        string userPassword, string ownerPassword,
        EncryptionAlgorithm algorithm = EncryptionAlgorithm.RC4_128)
    {
        byte[] encrypted = Encrypt(userPassword, ownerPassword, algorithm);
        using var ms = new MemoryStream(encrypted);
        return PdfDecryptor.GetEncryptionInfo(ms);
    }

    private static byte[] Encrypt(
        string userPassword, string ownerPassword,
        EncryptionAlgorithm algorithm = EncryptionAlgorithm.RC4_128)
    {
        using var plain = new MemoryStream(MinimalPdf());
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = userPassword,
            OwnerPassword = ownerPassword,
            Algorithm = algorithm,
        });
        return encrypted.ToArray();
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

    /// <summary>
    /// A V=4 document whose crypt filter names a method the spec does not define.
    /// </summary>
    /// <remarks>
    /// The bogus name is deliberately <b>not</b> <c>/AESV4</c>: ISO/TS 32003 standardizes exactly
    /// that name for AES-GCM (see <c>Tetitulz.Pdf.Encryption.PdfDecryptor._knownCfmNames</c>), which
    /// would make this fixture "a real one with a deliberately wrong password" instead of
    /// "unrecognized CFM", without a compile error. <c>BogusCFM</c> is chosen to be unambiguously not
    /// a real crypt-filter method name, so a future spec extension is unlikely to collide with it.
    /// </remarks>
    private static byte[] MalformedCfmPdf()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string u = new string('b', 64);
        string o = new string('a', 64);

        sb.Append("%PDF-1.6\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj("4 0 obj\n<< /Filter /Standard /V 4 /R 4 /Length 128 /P -1 "
          + $"/O <{o}> /U <{u}> /CF << /StdCF << /CFM /BogusCFM >> >> /StmF /StdCF /StrF /StdCF >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat(
            "trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R "
            + "/ID [<0102030405060708090a0b0c0d0e0f10> <0102030405060708090a0b0c0d0e0f10>] >>\n"
            + "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
