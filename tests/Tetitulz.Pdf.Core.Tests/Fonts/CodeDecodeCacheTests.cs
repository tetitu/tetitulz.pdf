// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="PdfFont.Decode(CMapCodec.Code)"/>: a code's text is what its bytes decode to, and a
/// font answers it from a per-code cache — the same string instance each time — so the extractors
/// decode a shown string one code at a time without copying each code's bytes out and building a
/// <c>StringBuilder</c> per glyph (a third of plain extraction's allocation on a text document,
/// GAPS.md §13b).
/// </summary>
public class CodeDecodeCacheTests
{
    [Fact]
    public void ASimpleFontDecodesACodeAsItsBytes_AndRemembersIt()
    {
        using PdfDocument doc = MinimalDoc();
        PdfFont font = PdfFont.Create(SimpleFontDict(), doc);

        var b = new CMapCodec.Code(66, 66, 1);
        Assert.Equal(font.Decode(new byte[] { 66 }), font.Decode(b));
        Assert.Equal("B", font.Decode(b));
        Assert.Same(font.Decode(b), font.Decode(b));

        // /Differences 65 /bullet: the code's text goes through the same tables as the bytes'.
        var a = new CMapCodec.Code(65, 65, 1);
        Assert.Equal("•", font.Decode(a));
        Assert.Equal(font.Decode(new byte[] { 65 }), font.Decode(a));

        // Decoding a whole string is the codes' texts in order, built from the same cache.
        Assert.Equal("•B", font.Decode(new byte[] { 65, 66 }));
    }

    [Fact]
    public void ACompositeFontDecodesACodeAsItsBytes_AndRemembersIt()
    {
        using PdfDocument doc = MinimalDoc();
        var font = Assert.IsType<PdfType0Font>(PdfFont.Create(Type0FontDict(), doc));

        var code = new CMapCodec.Code(0x0041, 0x0041, 2);
        Assert.Equal(font.Decode(new byte[] { 0x00, 0x41 }), font.Decode(code));
        Assert.Equal("A", font.Decode(code));
        Assert.Same(font.Decode(code), font.Decode(code));
        Assert.Equal(1, font.CachedCodeCount);
    }

    /// <summary>
    /// The cache stops at its ceiling and the answer does not: a font showing more distinct codes
    /// than the ceiling decodes the rest every time, correctly. A hostile composite font can show two
    /// billion codes; without the ceiling the cache would hold a string for each.
    /// </summary>
    [Fact]
    public void ACompositeFontsCacheStopsAtItsCeiling_AndStillDecodes()
    {
        using PdfDocument doc = MinimalDoc();
        var font = Assert.IsType<PdfType0Font>(PdfFont.Create(Type0FontDict(), doc));

        int beyond = PdfType0Font.MaxCachedCodes + 100;
        for (int value = 0x4E00; value < 0x4E00 + beyond; value++)
            Assert.Equal(((char)value).ToString(), font.Decode(new CMapCodec.Code(value, value, 2)));

        Assert.Equal(PdfType0Font.MaxCachedCodes, font.CachedCodeCount);
        Assert.Equal(((char)(0x4E00 + beyond - 1)).ToString(),
            font.Decode(new CMapCodec.Code(0x4E00 + beyond - 1, 0x4E00 + beyond - 1, 2)));
    }

    /// <summary>
    /// A standard-14 font with no <c>/Widths</c> works its canonical name out once, not once a
    /// glyph: the name's string surgery, asked by every <c>GetWidth</c>, was most of plain
    /// extraction's allocation on a hundred-thousand-show document. The widths are the AFM's.
    /// </summary>
    [Fact]
    public void AStandard14FontsName_IsWorkedOutOnce()
    {
        using PdfDocument doc = MinimalDoc();
        var font = Assert.IsAssignableFrom<PdfSimpleFont>(PdfFont.Create(SimpleFontDict(), doc));

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(667, font.GetWidth(66));   // Helvetica's B
            Assert.Equal(222, font.GetWidth(105));  // and i
        }
        Assert.Equal(1, font.Standard14NameComputations);
    }

    /// <summary>
    /// <see cref="PdfFont.NextCode"/> steps through a string as <see cref="PdfFont.SplitCodes"/>
    /// splits it — a simple font a byte a code, a composite font by its codespace, a trailing
    /// fragment as one short code — so a caller walking it needs no list.
    /// </summary>
    [Fact]
    public void NextCode_StepsAsSplitCodesSplits()
    {
        using PdfDocument doc = MinimalDoc();
        byte[] bytes = { 0x00, 0x41, 0x00, 0x42, 0x43 };
        foreach (PdfFont font in new[] { PdfFont.Create(SimpleFontDict(), doc), PdfFont.Create(Type0FontDict(), doc) })
        {
            var split = font.SplitCodes(bytes);
            var stepped = new System.Collections.Generic.List<CMapCodec.Code>();
            for (int offset = 0; offset < bytes.Length;)
            {
                int length = font.NextCode(bytes, offset, out CMapCodec.Code code);
                Assert.Equal(code.ByteLength, length);
                stepped.Add(code);
                offset += length;
            }
            Assert.Equal(split, stepped);
        }
        Assert.Equal(3, PdfFont.Create(Type0FontDict(), doc).SplitCodes(bytes).Count);   // 2 + 2 + a 1-byte fragment
    }

    [Fact]
    public void TheBaseRebuildsACodesBytesBigEndianInItsLength()
    {
        Assert.Equal(new byte[] { 0x12, 0x34 }, PdfFont.CodeBytes(new CMapCodec.Code(0x1234, 0, 2)));
        Assert.Equal(new byte[] { 0x41 }, PdfFont.CodeBytes(new CMapCodec.Code(0x41, 0x41, 1)));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x41 }, PdfFont.CodeBytes(new CMapCodec.Code(0x41, 0x41, 3)));
    }

    // ----------------------------------------------------------------

    private static PdfDocument MinimalDoc()
    {
        string pdfText =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
            "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
            "trailer\n<< /Size 3 /Root 1 0 R >>\n" +
            "startxref\n107\n%%EOF\n";
        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(pdfText)));
    }

    private static PdfDictionary SimpleFontDict()
    {
        var differences = new PdfArray();
        differences.Add(new PdfNumber(65));
        differences.Add(new PdfName("bullet"));
        var encoding = new PdfDictionary();
        encoding[new PdfName("Type")] = new PdfName("Encoding");
        encoding[new PdfName("BaseEncoding")] = new PdfName("WinAnsiEncoding");
        encoding[new PdfName("Differences")] = differences;

        var font = new PdfDictionary();
        font[new PdfName("Type")] = new PdfName("Font");
        font[new PdfName("Subtype")] = new PdfName("Type1");
        font[new PdfName("BaseFont")] = new PdfName("Helvetica");
        font[new PdfName("Encoding")] = encoding;
        return font;
    }

    private static PdfDictionary Type0FontDict()
    {
        var cidFont = new PdfDictionary();
        cidFont[new PdfName("Type")] = new PdfName("Font");
        cidFont[new PdfName("Subtype")] = new PdfName("CIDFontType2");
        cidFont[new PdfName("BaseFont")] = new PdfName("Arial");
        cidFont[new PdfName("CIDToGIDMap")] = new PdfName("Identity");
        var descendants = new PdfArray();
        descendants.Add(cidFont);

        var font = new PdfDictionary();
        font[new PdfName("Type")] = new PdfName("Font");
        font[new PdfName("Subtype")] = new PdfName("Type0");
        font[new PdfName("BaseFont")] = new PdfName("Arial");
        font[new PdfName("Encoding")] = new PdfName("Identity-H");
        font[new PdfName("DescendantFonts")] = descendants;
        return font;
    }
}
