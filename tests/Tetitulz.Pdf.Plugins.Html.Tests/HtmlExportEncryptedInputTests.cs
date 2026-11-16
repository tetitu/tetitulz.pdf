// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// Exporting an encrypted document is refused, not silently reported as a success.
///
/// <para><b>Why this exists.</b> <see cref="PdfDocument"/> loads neither the catalog nor the page
/// tree of an encrypted file, so it reports <b>zero pages</b> rather than throwing. The converter
/// then emitted a page-less HTML skeleton and the plugin returned normally — a well-formed,
/// successful-looking export carrying none of the document. Nothing in the API said the content had
/// been lost.</para>
/// </summary>
public class HtmlExportEncryptedInputTests
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
            () => new HtmlExportPlugin().Export(doc, output, null));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The control: an ordinary document still exports its text, so the refusal above cannot pass
    /// by the plugin having started to refuse everything.
    /// </summary>
    [Fact]
    public void Export_OfAPlainDocument_StillCarriesThePagesText()
    {
        using var doc = PdfDocument.Open(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("PlainMarker")));
        using var output = new MemoryStream();

        new HtmlExportPlugin().Export(doc, output, null);

        Assert.Contains("PlainMarker", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The converter's own doors refuse it too: called directly, they wrote a page-less skeleton
    /// or, with a decryptor registered, page images with no text layer — the text extractor does
    /// not decrypt.
    /// </summary>
    [Fact]
    public void TheConvertersOwnDoors_RefuseAnEncryptedDocument_AndConvertAPlainOne()
    {
        byte[] encrypted = EncryptedPdf();
        Assert.Throws<PdfEncryptedDocumentException>(() => PdfHtmlConverter.ConvertToHtml(new MemoryStream(encrypted)));
        Assert.Throws<PdfEncryptedDocumentException>(() => PdfHtmlConverter.ConvertToLayoutHtml(new MemoryStream(encrypted)));

        byte[] plain = MinimalPdfBuilder.CreateMinimalPdf("PlainMarker");
        Assert.Contains("PlainMarker", PdfHtmlConverter.ConvertToHtml(new MemoryStream(plain)), StringComparison.Ordinal);
        Assert.Contains("PlainMarker", PdfHtmlConverter.ConvertToLayoutHtml(new MemoryStream(plain)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A conversion renders its page images through one session, however many pages — each page
    /// was rendered from a fresh stream, a whole parse, page-tree walk and font cache apiece. The
    /// images are the renderer's either way, so the session's count is what a test can see.
    /// </summary>
    [Fact]
    public void AConversionsPageImages_AreRenderedThroughOneSession()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);

        string html = PdfHtmlConverter.ConvertToHtml(new MemoryStream(pdf));
        Assert.Equal(3, CountOf(html, "data:image/png;base64,"));
        Assert.Equal(1, PdfHtmlConverter.LastRenderSessionsOpened);

        string layout = PdfHtmlConverter.ConvertToLayoutHtml(new MemoryStream(pdf));
        Assert.Equal(3, CountOf(layout, "data:image/png;base64,"));
        Assert.Equal(1, PdfHtmlConverter.LastRenderSessionsOpened);

        PdfHtmlConverter.ConvertToHtml(new MemoryStream(pdf), new HtmlConversionOptions { EmbedImages = false });
        Assert.Equal(0, PdfHtmlConverter.LastRenderSessionsOpened);
    }

    private static int CountOf(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static byte[] EncryptedPdf()
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
