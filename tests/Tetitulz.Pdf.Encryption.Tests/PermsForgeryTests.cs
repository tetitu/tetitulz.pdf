// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// ISO 32000-2 §7.6.4.3.4 Algorithm 11: the <c>/Perms</c> entry exists so that <c>/P</c> — which sits
/// in the <b>plaintext</b> <c>/Encrypt</c> dictionary where anyone can edit it — can be authenticated
/// against a value only the file key can decrypt.
/// </summary>
/// <remarks>
/// <para><b>Why /P needs authenticating at all.</b> For V5/R6 the file encryption key derives from the
/// password and the <c>/U</c> // <c>/O</c> salts. It does <b>not</b> derive from <c>/P</c>. So editing
/// <c>/P</c> in a hex editor does not disturb decryption in any way: the document still opens, every
/// stream still decrypts, and the only thing that changes is the permission bitmask the consuming
/// application reads. <c>/Perms</c> is the entire defence, and it is a 16-byte AES-ECB block whose
/// first four bytes are <c>/P</c> little-endian, byte 8 the <c>/EncryptMetadata</c> flag as
/// <c>'T'</c>//<c>'F'</c>, and bytes 9-11 the literal tag <c>"adb"</c>.</para>
///
/// <para><b>What the writer and the reader each do.</b> <c>PdfEncryptor</c> writes all of it. The
/// decryptor decrypts the block and then compares <b>only bytes 9-11</b> against <c>"adb"</c> before
/// answering <c>"OK"</c> — so it verifies that the block decrypted, and never verifies the one value
/// the block exists to protect.</para>
///
/// <para><b>Why this matters to a caller who did everything right.</b>
/// <c>PdfDecryptor.LastPermsValidation</c>'s own documentation tells callers to refuse to honour
/// <c>/P</c> unless the slot reads <c>"OK"</c>. A caller following that rule to the letter is still
/// defeated, because the slot reads <c>"OK"</c> for a forged <c>/P</c>.</para>
/// </remarks>
public class PermsForgeryTests
{
    private const string Password = "secret";

    /// <summary>
    /// The floor. An untouched AES-256 document must validate, or every forgery assertion below
    /// passes against a check that simply always fails.
    /// </summary>
    [Fact]
    public void UntamperedAes256Document_ReportsPermsOk()
    {
        byte[] encrypted = EncryptWith(PdfPermissions.None);

        Decrypt(encrypted);

        Assert.Equal("OK", PdfDecryptor.LastPermsValidation);
    }

    /// <summary>
    /// The forgery: rewrite <c>/P</c> in the plaintext <c>/Encrypt</c> dictionary to grant everything,
    /// leaving <c>/Perms</c> — which authenticates the original value — untouched.
    /// </summary>
    /// <remarks>
    /// The patch is length-preserving (<c>-3904</c> becomes <c>-0004</c>, leading zeros being legal
    /// in a PDF integer), so no object offset moves and the cross-reference table stays correct. An
    /// attacker needs a hex editor and no key material of any kind.
    /// </remarks>
    [Fact]
    public void TamperedPValue_WithAnIntactPerms_IsRejectedNotReportedOk()
    {
        byte[] encrypted = EncryptWith(PdfPermissions.None);

        byte[] forged = ForgePermissionsBitmask(encrypted, out int originalP, out int forgedP);

        // The fixture must actually have changed something, or the assertion below is vacuous.
        Assert.NotEqual(originalP, forgedP);
        Assert.Equal(encrypted.Length, forged.Length);

        // The document still opens on the forged bitmask, which is the point: /P is not an input to
        // key derivation, so tampering with it costs the attacker nothing.
        Decrypt(forged);

        Assert.NotEqual("OK", PdfDecryptor.LastPermsValidation);
    }

    /// <summary>
    /// The same forgery, stated as the consequence rather than the mechanism: the value a caller
    /// reads must not be the forged one while the validation slot says the permissions are authentic.
    /// </summary>
    [Fact]
    public void ForgedPermissionsBitmask_IsNotBothHonouredAndReportedAuthentic()
    {
        byte[] encrypted = EncryptWith(PdfPermissions.None);
        byte[] forged = ForgePermissionsBitmask(encrypted, out int originalP, out int forgedP);

        using var ms = new MemoryStream(forged);
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(ms);
        Assert.True(PdfDecryptor.TryPassword(info, Password));

        bool permsSaysAuthentic = PdfDecryptor.LastPermsValidation == "OK";

        Assert.False(permsSaysAuthentic,
            $"/P was rewritten from {originalP} to {forgedP} — granting every permission the " +
            "document was created to withhold — and LastPermsValidation still reads \"OK\". Its own " +
            "documentation tells callers to refuse to honour /P unless it says OK, so a caller " +
            "following that rule exactly is defeated. Algorithm 11 authenticates /P in bytes 0-3 of " +
            "the decrypted /Perms block; only the \"adb\" tag in bytes 9-11 is being checked.");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Rewrites the <c>/P</c> integer in the plaintext <c>/Encrypt</c> dictionary to <c>-4</c> (every
    /// permission bit set), padded with leading zeros to the original token's exact length.
    /// </summary>
    private static byte[] ForgePermissionsBitmask(byte[] pdf, out int originalP, out int forgedP)
    {
        string text = Encoding.Latin1.GetString(pdf);

        Match m = Regex.Match(text, @"/P\s+(-?\d+)");
        Assert.True(m.Success,
            "No /P entry found in the encrypted output, so this fixture is not testing what it says.");

        string token = m.Groups[1].Value;
        originalP = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);

        bool negative = token.StartsWith("-", StringComparison.Ordinal);
        string digits = negative ? token.Substring(1) : token;

        // -4 is 0xFFFFFFFC: every defined permission bit (3..12) set.
        string forgedDigits = "4".PadLeft(digits.Length, '0');
        string forgedToken = (negative ? "-" : "-") + forgedDigits;

        Assert.Equal(token.Length, forgedToken.Length);
        forgedP = int.Parse(forgedToken, System.Globalization.CultureInfo.InvariantCulture);

        var patched = new StringBuilder(text);
        int at = m.Groups[1].Index;
        for (int i = 0; i < forgedToken.Length; i++) patched[at + i] = forgedToken[i];

        return Encoding.Latin1.GetBytes(patched.ToString());
    }

    private static void Decrypt(byte[] encrypted)
    {
        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, Password);
    }

    private static byte[] EncryptWith(PdfPermissions permissions)
    {
        using var plain = new MemoryStream(MinimalPdf());
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword  = Password,
            OwnerPassword = "owner",
            Algorithm     = EncryptionAlgorithm.AES256,
            Permissions   = permissions,
        });
        return encrypted.ToArray();
    }

    private static byte[] MinimalPdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (restricted document) Tj ET\n";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
