// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Regression tests for the encrypted-input hole in the whole-file rewriting operations.
///
/// <para>
/// <b>The bug.</b> <see cref="PdfSanitizer"/>, <see cref="PdfOptimizer"/> and
/// <see cref="PdfLinearizer"/> all renumber every object, then copy the trailer —
/// including <c>/Encrypt</c> — verbatim. Per-object encryption keys are derived from the
/// object number (PDF 32000-1 §7.6.2), so the output claimed to be encrypted with the
/// original parameters while every object had moved: RC4-40/RC4-128/AES-128 documents came
/// out silently corrupt. For AES-256 (where the key is not object-salted) the file survived,
/// but only *names* had been sanitized — encrypted <c>/JS</c> string bodies and <c>/EF</c>
/// streams passed through untouched, handing the caller a file they believe is clean.
/// </para>
/// <para>
/// A security tool that returns a document the caller trusts is the worst failure mode in
/// this library, so all three now fail closed, matching the guard that
/// <c>PdfRedactionEngine</c> already had.
/// </para>
/// </summary>
public class EncryptedInputFailsClosedTests
{
    [Fact]
    public void Sanitize_EncryptedInput_ThrowsInsteadOfSilentlyCorrupting()
    {
        using var input = new MemoryStream(BuildEncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfSanitizer.Sanitize(input, output));

        Assert.Contains("Encrypted PDFs are not supported", ex.Message);
        Assert.Equal(0, output.Length);   // fails closed: nothing written
    }

    [Fact]
    public void Optimize_EncryptedInput_ThrowsInsteadOfSilentlyCorrupting()
    {
        using var input = new MemoryStream(BuildEncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfOptimizer.Optimize(input, output));

        Assert.Contains("Encrypted PDFs are not supported", ex.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Linearize_EncryptedInput_ThrowsInsteadOfSilentlyCorrupting()
    {
        using var input = new MemoryStream(BuildEncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfLinearizer.Linearize(input, output));

        Assert.Contains("Encrypted PDFs are not supported", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The guard must be catchable via the shared PDF exception base.</summary>
    [Fact]
    public void EncryptedDocumentException_IsAPdfException()
    {
        using var input = new MemoryStream(BuildEncryptedPdf());
        using var output = new MemoryStream();

        Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfSanitizer.Sanitize(input, output));

        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(PdfEncryptedDocumentException)));
    }

    /// <summary>Unencrypted input must be unaffected by the new guard.</summary>
    [Fact]
    public void Sanitize_UnencryptedInput_StillSucceeds()
    {
        using var input = new MemoryStream(BuildPdf(encrypted: false));
        using var output = new MemoryStream();

        PdfSanitizer.Sanitize(input, output);

        Assert.True(output.Length > 0);
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.False(doc.IsEncrypted);
        Assert.Single(doc.Pages);
    }

    private static byte[] BuildEncryptedPdf() => BuildPdf(encrypted: true);

    /// <summary>
    /// A minimal single-page PDF, optionally carrying a trailer <c>/Encrypt</c> reference.
    /// <see cref="PdfDocument.IsEncrypted"/> is driven purely by that trailer key, so a real
    /// crypto handler is not needed to exercise the guard — which keeps this test
    /// self-contained (no Tetitulz.Pdf.Encryption reference, no on-disk fixture).
    /// </summary>
    private static byte[] BuildPdf(bool encrypted)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        const string content = "BT /F1 12 Tf 100 700 Td (hi) Tj ET";

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Contents 4 0 R /Resources << >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);

        // Standard security handler dictionary (RC4-128, R=3) — structurally valid so the
        // parser records it; the guard fires before any decryption is attempted.
        int off5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 " +
                   "/O (0123456789ABCDEF0123456789ABCDEF) " +
                   "/U (FEDCBA9876543210FEDCBA9876543210) >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 6\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);

        pdf.Append(encrypted
            ? "trailer\n<< /Size 6 /Root 1 0 R /Encrypt 5 0 R "
              + "/ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\n"
            : "trailer\n<< /Size 6 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
