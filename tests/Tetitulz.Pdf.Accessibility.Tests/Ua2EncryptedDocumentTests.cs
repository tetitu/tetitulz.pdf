// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfUa2StructureChecker"/> does not judge an encrypted document it cannot read, and
/// says so. It decrypts nothing, and the strings its checks compare are ciphertext then, each
/// object's under its own key or a fresh IV: a conformant file whose figure's <c>/Alt</c> equals
/// its annotation's <c>/Contents</c> (§8.9.4.2) was reported as a mismatch, and the standard
/// namespace's name matched nothing.
/// </summary>
public sealed class Ua2EncryptedDocumentTests
{
    [Fact]
    public void Adversarial_encrypted_document_is_not_judged_and_says_so()
    {
        var result = PdfUa2StructureChecker.Validate(new MemoryStream(Encrypt(AltMatchesContentsPdf())));

        var notice = Assert.Single(result.Violations);
        Assert.Equal("UA2-99", notice.Code);
        Assert.Contains("encrypted", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.4.2");
    }

    /// <summary>The control: the same file in the clear is judged, and conforms on this clause.</summary>
    [Fact]
    public void The_same_document_in_the_clear_is_judged()
    {
        var result = PdfUa2StructureChecker.Validate(new MemoryStream(AltMatchesContentsPdf()));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-99");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.4.2");
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var input = new MemoryStream(plain, writable: false);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = "",
            OwnerPassword = "owner",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    // 1 catalog, 2 StructTreeRoot, 3 Pages, 4 Page, 5 Document, 6 the /Alt element, 7 the
    // annotation whose /Contents is the same text, 8 the PDF 2.0 namespace, 9 the /OBJR.
    private static byte[] AltMatchesContentsPdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K 6 0 R >>",
            "<< /Type /StructElem /S /Annot /P 5 0 R /Alt (Photo of a cat) /K 9 0 R >>",
            "<< /Type /Annot /Subtype /Text /Contents (Photo of a cat) /Rect [0 0 10 10] >>",
            "<< /Type /Namespace /NS (http://iso.org/pdf2/ssn) >>",
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>",
        };
        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
