// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Text.Tests;

/// <summary>
/// Exporting an encrypted document is refused, not silently reported as a success.
///
/// <para><b>Why this exists.</b> <see cref="PdfDocument"/> loads neither the catalog nor the page
/// tree of an encrypted file, so <c>document.Pages</c> is <b>empty</b> rather than throwing. The
/// extractor then saw zero pages, this wrote zero bytes and returned normally. A batch job
/// archiving a folder with <c>PdfOperations.Export(input, output, "txt")</c> got a 0-byte .txt for
/// the password-protected member — indistinguishable from a PDF that genuinely contained no text —
/// and the archive silently lost that document's entire content.</para>
/// </summary>
public class TextExportEncryptedInputTests
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
            () => new TextExportPlugin().Export(doc, output, null));

        Assert.Contains("encrypted", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The control: an ordinary document still exports its text, so the refusal above cannot pass
    /// by the plugin having started to refuse everything. Asserted on the page's own words, not on
    /// the output being non-empty.
    /// </summary>
    [Fact]
    public void Export_OfAPlainDocument_StillCarriesThePagesText()
    {
        using var doc = PdfDocument.Open(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("PlainMarker")));
        using var output = new MemoryStream();

        new TextExportPlugin().Export(doc, output, null);

        Assert.Contains("PlainMarker", Encoding.UTF8.GetString(output.ToArray()),
            System.StringComparison.Ordinal);
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
