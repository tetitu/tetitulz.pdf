// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// The <c>password</c> overloads of <see cref="PdfOperations.ExtractStructuredText(Stream,String)"/>
/// really read an encrypted document, and the shipped signatures still refuse one.
/// </summary>
/// <remarks>
/// <para><b>Both halves are the test.</b> That the password overload works is half; that the
/// original still declines is the other, because these are <b>additive</b> overloads — appending
/// <c>password = null</c> to a shipped signature is a binary break for an already-compiled
/// consumer, so the pair must remain a pair. A test that only exercised the new form would not
/// notice the old one being quietly widened away.</para>
/// <para>The assertion is on <b>extracted characters</b>, not on "a list came back": an empty layout
/// list is exactly what a failed decrypt produces, so a count-free assertion would pass with the
/// password ignored.</para>
/// </remarks>
public sealed class EncryptedStructuredTextTests
{
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";

    public EncryptedStructuredTextTests() => PdfEncryptionOperations.Register();

    [Fact]
    public void ExtractStructuredText_WithThePassword_ReadsTheEncryptedDocument()
    {
        byte[] encrypted = Encrypt(TextPdf());

        using var input = new MemoryStream(encrypted, writable: false);
        IReadOnlyList<PdfPageTextLayout> pages =
            PdfOperations.ExtractStructuredText(input, UserPassword);

        PdfPageTextLayout page = Assert.Single(pages);
        string text = string.Concat(page.Lines.Select(l => l.Text));
        Assert.Contains("Encrypted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractStructuredText_WithTheOwnerPassword_AlsoReadsIt()
    {
        using var input = new MemoryStream(Encrypt(TextPdf()), writable: false);

        IReadOnlyList<PdfPageTextLayout> pages =
            PdfOperations.ExtractStructuredText(input, OwnerPassword);

        // The text itself, not "something came back": an empty layout list is precisely what a
        // failed decrypt produces, so a non-empty check passes with the password ignored.
        Assert.Contains("Encrypted", string.Concat(pages.Single().Lines.Select(l => l.Text)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped signature calls the extractor directly, and the extractor refuses an encrypted
    /// document, naming the password overload: an empty layout from pages it could not read is a
    /// confident wrong answer (<c>Base.Operations.EncryptedRead</c>).
    /// </summary>
    /// <remarks>
    /// <para>The no-password overload does not delegate to the password one with <c>null</c>: that
    /// routes through <c>WithDecrypted</c>, and with a decryptor registered
    /// <c>EnsureDecryptedStream</c> throws <c>PdfException("Invalid password")</c> for a null
    /// password, a different refusal from the one the shipped form makes. "Additive" means
    /// <i>the old path is untouched</i>, not merely that the old signature still exists — the trap
    /// for the remaining entries on <c>FacadePasswordCoverageTests.KnownWithoutPassword</c>.</para>
    /// </remarks>
    [Fact]
    public void ExtractStructuredText_WithoutAPassword_RefusesTheEncryptedDocument()
    {
        PdfOperations.ExtractStructuredText(new MemoryStream(TextPdf(), writable: false));
        Assert.Null(PdfOperations.LastExtractionError);   // control: clean slate on this thread

        using var input = new MemoryStream(Encrypt(TextPdf()), writable: false);
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(
            () => PdfOperations.ExtractStructuredText(input));

        Assert.Contains("ExtractStructuredText(Stream, string password)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The single-page overload takes the password before its optional tail.</summary>
    [Fact]
    public void ExtractStructuredText_SinglePageWithThePassword_ReadsIt()
    {
        using var input = new MemoryStream(Encrypt(TextPdf()), writable: false);

        PdfPageTextLayout page = PdfOperations.ExtractStructuredText(input, 0, UserPassword);

        Assert.Contains("Encrypted", string.Concat(page.Lines.Select(l => l.Text)),
            StringComparison.Ordinal);
    }

    /// <summary>An unencrypted document is unaffected by passing a password.</summary>
    [Fact]
    public void ExtractStructuredText_WithAPasswordOnAnUnencryptedDocument_StillReadsIt()
    {
        using var input = new MemoryStream(TextPdf(), writable: false);

        IReadOnlyList<PdfPageTextLayout> pages =
            PdfOperations.ExtractStructuredText(input, "irrelevant");

        Assert.Contains("Encrypted", string.Concat(pages.Single().Lines.Select(l => l.Text)),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private static byte[] Encrypt(byte[] plain)
    {
        using var input  = new MemoryStream(plain, writable: false);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output,
            new EncryptionParameters
            {
                UserPassword  = UserPassword,
                OwnerPassword = OwnerPassword,
                Algorithm     = EncryptionAlgorithm.AES128,
                Permissions   = PdfPermissions.All,
            });
        return output.ToArray();
    }

    private static byte[] TextPdf()
    {
        const string content = "BT /F1 18 Tf 72 700 Td (Encrypted text) Tj ET";
        var sb = new System.Text.StringBuilder();
        var offsets = new List<int>();

        sb.Append("%PDF-1.7\n");

        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Length ").Append(content.Length).Append(" >>\nstream\n")
          .Append(content).Append("\nendstream\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
