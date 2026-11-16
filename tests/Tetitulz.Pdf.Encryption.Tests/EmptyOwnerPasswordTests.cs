// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// An empty owner password must never make an encrypted document openable with no password —
/// for <b>any</b> algorithm.
/// </summary>
/// <remarks>
/// <para>
/// ISO 32000-1 §7.6.3.3 Algorithm 3 step (a) replaces an empty owner password with the user
/// password. <c>PdfEncryptor</c> applied that to the RC4 and AES-128 arm only; the AES-256 arm
/// passed the empty string straight into <c>ComputeAes256Keys</c>, so <c>/O</c> and <c>/OE</c> were
/// derived from <c>""</c>. <c>PdfDecryptor.ValidateAndDeriveAes256Key</c> then authenticated the
/// empty password against <c>/O</c> and handed back the file key from <c>/OE</c>.
/// </para>
/// <para>
/// <b>Choosing the strongest algorithm in the enum was what removed the protection</b> — the same
/// parameters under <c>RC4_128</c> and <c>AES128</c> correctly refused. And
/// <c>ValidateParameters</c>' own doc comment asserted the opposite as a property of the method:
/// "an empty owner password is permitted; when empty it defaults to the user password during key
/// derivation", which is exactly what the AES-256 path did not do.
/// </para>
/// <para>
/// <b>Why this is a theory over every algorithm rather than one AES-256 fact.</b> The existing
/// suite round-trips matching passwords, which cannot see a key derived from the wrong secret: the
/// same wrong secret is used to encrypt and to decrypt, so it agrees with itself. Only asking
/// whether a <em>different</em> password opens the file can distinguish them, and asking it of
/// every algorithm is what stops the next arm being added without step (a).
/// </para>
/// </remarks>
public class EmptyOwnerPasswordTests
{
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void EmptyOwnerPassword_DoesNotLeaveTheDocumentOpenableWithNoPassword(
        EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(algorithm, userPassword: "secret", ownerPassword: "");

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();

        Exception opened = Record.Exception(() => PdfDecryptor.Decrypt(input, output, ""));

        Assert.True(opened != null || output.Length == 0,
            $"{algorithm}: the empty password opened a document whose user password is \"secret\". " +
            "An empty OWNER password must fall back to the user password (§7.6.3.3 Algorithm 3a), " +
            "never become a second, blank way in.");
    }

    /// <summary>The real user password must still work — the guard above must not pass by breaking encryption outright.</summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void EmptyOwnerPassword_StillOpensWithTheUserPassword(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(algorithm, userPassword: "secret", ownerPassword: "");

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "secret");

        Assert.True(output.Length > 0, $"{algorithm}: the correct user password must still decrypt");
    }

    private static byte[] Encrypt(EncryptionAlgorithm algorithm, string userPassword, string ownerPassword)
    {
        using var plain = new MemoryStream(MinimalPdf());
        using var encrypted = new MemoryStream();

        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword  = userPassword,
            OwnerPassword = ownerPassword,
            Algorithm     = algorithm,
        });

        return encrypted.ToArray();
    }

    private static byte[] MinimalPdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (confidential) Tj ET\n";
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
