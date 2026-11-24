// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="PdfFont.Create"/>'s two arguments have different contracts, and the order in which
/// it checks them is load-bearing.
///
/// <para><b>What it used to do.</b> It null-checked <c>fontDict</c> and then dereferenced
/// <c>document</c> unconditionally on the very next line, so a caller with a font dictionary and no
/// document got a bare <see cref="NullReferenceException"/> out of a factory that otherwise
/// degrades gracefully. Found while writing a fixture for an unrelated row.</para>
///
/// <para><b>And what the first fix got wrong.</b> Requiring the document <i>before</i> the
/// <c>fontDict == null</c> check refused <c>Create(null, null)</c> — a legitimate call, because a
/// page may name a font resource that is not there, the answer is a fallback font, and that path
/// never touches the document. Two existing cache tests were doing exactly that. The order is the
/// design, not an accident.</para>
/// </summary>
public class PdfFontCreateContractTests
{
    /// <summary>
    /// The case the first fix broke: no dictionary means a fallback font, and no document is
    /// needed to say so.
    /// </summary>
    [Fact]
    public void ANullDictionaryNeedsNoDocumentAndYieldsAFallbackFont()
    {
        var font = PdfFont.Create(null, null);

        Assert.NotNull(font);
        // A fallback font still answers the questions a caller asks of any font.
        Assert.Equal("A", font.Decode(new byte[] { 0x41 }));
    }

    /// <summary>
    /// The defect: a real dictionary with no document. The refusal names the parameter, rather
    /// than a <see cref="NullReferenceException"/> from inside the factory.
    /// </summary>
    [Fact]
    public void ADictionaryWithNoDocumentIsRefusedByNameRatherThanNullReferencing()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Font);
        dict.Set(PdfName.Subtype, new PdfName("Type1"));
        dict.Set(PdfName.BaseFont, new PdfName("Helvetica"));

        var ex = Assert.Throws<ArgumentNullException>(() => PdfFont.Create(dict, null));

        Assert.Equal("document", ex.ParamName);
    }

    /// <summary>
    /// The control that keeps the refusal from being "always throw": a dictionary WITH a document
    /// still builds the font it describes.
    /// </summary>
    [Fact]
    public void ADictionaryWithADocumentStillBuildsTheFont()
    {
        byte[] pdf = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /Resources << /Font << /F0 4 0 R >> >> >>\nendobj\n" +
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" +
            "trailer\n<< /Size 5 /Root 1 0 R >>\n%%EOF\n");

        using var doc = Core.Document.PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var fonts = doc.Pages[0].Resources.GetDictionary(PdfName.Font);
        var font = PdfFont.Create(doc.ResolveObject(fonts[new PdfName("F0")]) as PdfDictionary, doc);

        Assert.NotNull(font);
        Assert.Equal("Helvetica", font.BaseFont);
    }
}
