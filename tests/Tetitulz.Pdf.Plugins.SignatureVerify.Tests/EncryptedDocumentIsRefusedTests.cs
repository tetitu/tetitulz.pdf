// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Plugins.SignatureVerify;
using Xunit;

namespace Tetitulz.Pdf.Plugins.SignatureVerify.Tests;

/// <summary>
/// An encrypted signed document must not be reported as unsigned.
///
/// <para><b>The bug.</b> The plugin opened the stream with no decryption and returned whatever
/// <c>VerifyAll</c> produced. <see cref="Tetitulz.Pdf.Core.Document.PdfDocument"/> skips the
/// catalog — and therefore the <c>/AcroForm</c> — of an encrypted file, so the verifier took its
/// "no AcroForm" branch and returned an <b>empty list</b>: the answer "this document is not
/// signed" to a security question about a document that may well be signed. A caller reading "no
/// results" as "nothing to verify" concludes the contract is unsigned; one reading it as "no
/// invalid signatures" concludes it is fine. Both are wrong, which is why an empty list is not an
/// acceptable answer here and a refusal is.</para>
/// </summary>
public class EncryptedDocumentIsRefusedTests
{
    [Fact]
    public void VerifyAll_OnAnEncryptedDocument_ThrowsInsteadOfReportingNoSignatures()
    {
        using var input = new MemoryStream(BuildPdf(encrypted: true));

        var ex = Assert.Throws<PdfVerificationException>(
            () => new SignatureVerifyPlugin().VerifyAll(input));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tetitulz.Pdf.Encryption", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The refusal must be catchable through the shared PDF exception base.</summary>
    [Fact]
    public void TheRefusal_IsAPdfException()
    {
        using var input = new MemoryStream(BuildPdf(encrypted: true));

        Assert.Throws<PdfVerificationException>(
            () => new SignatureVerifyPlugin().VerifyAll(input));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(PdfVerificationException)));
    }

    /// <summary>
    /// The control: the guard keys on encryption, not on the absence of signatures. The same
    /// fixture without <c>/Encrypt</c> comes back as an ordinary empty result, which for an
    /// unsigned document is the truth.
    /// </summary>
    [Fact]
    public void VerifyAll_OnAnUnencryptedUnsignedDocument_ReturnsAnEmptyListWithoutThrowing()
    {
        using var input = new MemoryStream(BuildPdf(encrypted: false));

        var results = new SignatureVerifyPlugin().VerifyAll(input);

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public void VerifyAll_WithANullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SignatureVerifyPlugin().VerifyAll(null!));
    }

    /// <summary>
    /// A minimal single-page PDF, optionally carrying a trailer <c>/Encrypt</c> reference.
    /// <c>PdfDocument.IsEncrypted</c> is driven purely by that trailer key, so no real crypto
    /// handler — and no Tetitulz.Pdf.Encryption reference — is needed to exercise the guard.
    /// </summary>
    private static byte[] BuildPdf(bool encrypted)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int off4 = pdf.Length;
        pdf.Append("4 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 " +
                   "/O (0123456789ABCDEF0123456789ABCDEF) " +
                   "/U (FEDCBA9876543210FEDCBA9876543210) >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 5\n0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);

        pdf.Append(encrypted
            ? "trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R "
              + "/ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\n"
            : "trailer\n<< /Size 5 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
