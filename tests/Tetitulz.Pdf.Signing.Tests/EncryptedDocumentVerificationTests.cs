// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfSignatureVerifier.VerifyAll(PdfDocument)"/> on an <b>encrypted</b> document.
///
/// <para><b>The defect.</b> <c>PdfDocument.Load</c> deliberately stops at the trailer for an
/// encrypted file — it loads neither the catalog nor the AcroForm, because objects in an
/// <c>/ObjStm</c> are still ciphertext at that point. <c>VerifyAll</c> then hit
/// <c>document.AcroForm == null</c> and returned an empty list, which is the correct answer for an
/// unsigned document and a <i>wrong</i> one here: a signed contract under a password reported "no
/// signatures", indistinguishable from "this document is not signed". That is a wrong answer to a
/// security question, not a missing feature.</para>
///
/// <para>The list is still empty — this verifier holds no decryptor and must not guess — but the
/// reason now reaches <see cref="PdfSignatureVerifier.LastVerificationError"/>, the diagnostic slot
/// the class already documents for exactly this ("populated even when the failure is reported
/// through a result object").</para>
/// </summary>
public sealed class EncryptedDocumentVerificationTests
{
    [Fact]
    public void EncryptedDocument_ReportsWhyNoSignatureWasFound()
    {
        using var doc = Load(MinimalPdf(encrypted: true));
        Assert.True(doc.IsEncrypted, "The fixture is not being read as encrypted, so the branch " +
                                     "under test is never reached and the assertions below are vacuous.");

        var results = new PdfSignatureVerifier().VerifyAll(doc);

        Assert.Empty(results);
        Assert.NotNull(PdfSignatureVerifier.LastVerificationError);
        Assert.Contains("encrypted", PdfSignatureVerifier.LastVerificationError,
                        StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The control, and the half that stops the diagnostic from becoming noise: an ordinary unsigned
    /// document must still report <c>null</c>. "No signatures" is the truth there, and a
    /// diagnostic on every unsigned file would train a caller to ignore the slot.
    /// </summary>
    [Fact]
    public void UnencryptedDocumentWithNoForm_ReportsNothing()
    {
        using var doc = Load(MinimalPdf(encrypted: false));
        Assert.False(doc.IsEncrypted);

        var results = new PdfSignatureVerifier().VerifyAll(doc);

        Assert.Empty(results);
        Assert.Null(PdfSignatureVerifier.LastVerificationError);
    }

    private static PdfDocument Load(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary>
    /// A three-object PDF, optionally carrying a trailer <c>/Encrypt</c>.
    ///
    /// <para>No real cryptography is needed and none is used: <c>PdfDocument.Load</c> keys
    /// <c>IsEncrypted</c> off the presence of the trailer entry and returns before touching the
    /// catalog, which is precisely the state the verifier was mis-reading. Building a genuinely
    /// encrypted file would need <c>Tetitulz.Pdf.Encryption</c>, and this project must not
    /// reference it — the assembly under test does not.</para>
    /// </summary>
    private static byte[] MinimalPdf(bool encrypted)
    {
        var sb = new StringBuilder();
        var offsets = new int[5];

        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.7\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        if (encrypted)
            Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 " +
                   "/O <0000000000000000000000000000000000000000000000000000000000000000> " +
                   "/U <0000000000000000000000000000000000000000000000000000000000000000> >>");

        int count = encrypted ? 5 : 4;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(count).Append("\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++) sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(count).Append(" /Root 1 0 R");
        if (encrypted) sb.Append(" /Encrypt 4 0 R /ID [<01> <01>]");
        sb.Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
