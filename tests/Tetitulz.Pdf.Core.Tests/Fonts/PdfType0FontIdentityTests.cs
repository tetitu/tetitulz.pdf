// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Tests that PdfType0Font correctly exposes IsIdentityCidToGid
/// when /CIDToGIDMap is the name /Identity (not a stream).
/// Regression for VPN.pdf SymbolMT bullet rendering (□ vs •).
/// </summary>
public class PdfType0FontIdentityTests
{
    private static PdfDocument BuildMinimalDoc()
    {
        string pdfText =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
            "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
            "trailer\n<< /Size 3 /Root 1 0 R >>\n" +
            "startxref\n107\n%%EOF\n";
        byte[] raw = Encoding.ASCII.GetBytes(pdfText);
        return PdfDocument.Open(new MemoryStream(raw));
    }

    private static PdfDictionary BuildType0FontDict(string cidToGidMapValue)
    {
        // Build: CIDFont dict with /CIDToGIDMap = /Identity (or /Custom)
        var cidFontDict = new PdfDictionary();
        cidFontDict[new PdfName("Type")]       = new PdfName("Font");
        cidFontDict[new PdfName("Subtype")]    = new PdfName("CIDFontType2");
        cidFontDict[new PdfName("BaseFont")]   = new PdfName("BCDHEE+SymbolMT");
        cidFontDict[new PdfName("CIDToGIDMap")] = new PdfName(cidToGidMapValue);

        // Wrap in Type0 font dict with DescendantFonts
        var descendants = new PdfArray();
        descendants.Add(cidFontDict);

        var fontDict = new PdfDictionary();
        fontDict[new PdfName("Type")]            = new PdfName("Font");
        fontDict[new PdfName("Subtype")]         = new PdfName("Type0");
        fontDict[new PdfName("BaseFont")]        = new PdfName("BCDHEE+SymbolMT");
        fontDict[new PdfName("Encoding")]        = new PdfName("Identity-H");
        fontDict[new PdfName("DescendantFonts")] = descendants;
        return fontDict;
    }

    [Fact]
    public void IsIdentityCidToGid_True_WhenCidToGidMapIsIdentityName()
    {
        using var doc = BuildMinimalDoc();
        var fontDict = BuildType0FontDict("Identity");
        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

        Assert.True(font.IsIdentityCidToGid);
        Assert.Null(font.GetCidToGid());
    }

    [Fact]
    public void IsIdentityCidToGid_True_CaseInsensitive()
    {
        using var doc = BuildMinimalDoc();
        var fontDict = BuildType0FontDict("identity"); // lowercase
        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

        Assert.True(font.IsIdentityCidToGid);
    }

    [Fact]
    public void IsIdentityCidToGid_False_WhenCidToGidMapIsOtherName()
    {
        using var doc = BuildMinimalDoc();
        var fontDict = BuildType0FontDict("Custom");
        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

        Assert.False(font.IsIdentityCidToGid);
        Assert.Null(font.GetCidToGid());
    }

    [Fact]
    public void IsIdentityCidToGid_True_WhenNoCidToGidMap_CIDFontType2()
    {
        // PDF spec §9.7.4.2: absent CIDToGIDMap defaults to /Identity for CIDFontType2.
        using var doc = BuildMinimalDoc();

        var cidFontDict = new PdfDictionary();
        cidFontDict[new PdfName("Type")]    = new PdfName("Font");
        cidFontDict[new PdfName("Subtype")] = new PdfName("CIDFontType2");

        var descendants = new PdfArray();
        descendants.Add(cidFontDict);

        var fontDict = new PdfDictionary();
        fontDict[new PdfName("Type")]            = new PdfName("Font");
        fontDict[new PdfName("Subtype")]         = new PdfName("Type0");
        fontDict[new PdfName("DescendantFonts")] = descendants;

        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

        Assert.True(font.IsIdentityCidToGid);
        Assert.Null(font.GetCidToGid());
    }
}
