// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Docx;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// Exporting an encrypted document is refused, not silently reported as a success.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <see cref="PdfDocument"/> loads neither the catalog nor the page
/// tree of an encrypted file, so <c>document.Pages</c> is <b>empty</b> rather than throwing.
/// <c>PdfToDocxConverter</c>'s page loop then ran zero times, its own diagnostic was never set, and
/// it returned a structurally valid, completely empty <c>.docx</c> — while <c>Export</c> reported
/// success. A batch job archiving a folder could not distinguish that from a PDF with no content,
/// so the document's whole content was lost silently.</para>
///
/// <para><b>This was the fourth of four, and the only one without the guard.</b> The Html, Text and
/// Svg export plugins each grew it, and each grew a fails-closed test of exactly this shape. That
/// per-plugin test — not a shared helper — is what stops a fifth adapter repeating the omission: a
/// helper nobody calls is precisely the shape of the defect being fixed.</para>
/// </remarks>
public class DocxExportEncryptedInputTests
{
    /// <summary>The zero-page premise the silent success depended on — pinned, so it cannot drift.</summary>
    [Fact]
    public void AnEncryptedDocument_ReportsNoPages_RatherThanThrowing()
    {
        using var doc = PdfDocument.Open(new MemoryStream(EncryptedPdf()));

        Assert.True(doc.IsEncrypted);
        Assert.Empty(doc.Pages);
    }

    [Fact]
    public void Export_OfAnEncryptedDocument_FailsClosed()
    {
        using var doc = PdfDocument.Open(new MemoryStream(EncryptedPdf()));
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => new DocxExportPlugin().Export(doc, output, null));

        Assert.Contains("encrypted", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);   // fails closed: nothing written
    }

    /// <summary>
    /// The control: an ordinary document still exports a DOCX, so the refusal above cannot pass by
    /// the plugin having started to refuse everything. Asserted on the package's own signature
    /// bytes rather than on the output being non-empty.
    /// </summary>
    [Fact]
    public void Export_OfAPlainDocument_StillProducesADocx()
    {
        using var doc = PdfDocument.Open(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("Body")));
        using var output = new MemoryStream();

        new DocxExportPlugin().Export(doc, output, null);

        byte[] bytes = output.ToArray();
        Assert.True(bytes.Length > 4, "the export produced nothing");

        // A .docx is an OOXML package — a ZIP, whose local file header is "PK\x03\x04".
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
        Assert.Equal(0x03, bytes[2]);
        Assert.Equal(0x04, bytes[3]);
    }

    internal static byte[] EncryptedPdf()
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 "
            + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
            + "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R /Encrypt 5 0 R /ID [<0102> <0102>] >>\n"
             + $"startxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
