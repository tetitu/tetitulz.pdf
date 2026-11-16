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
/// Tests for CID-to-Unicode decoding for non-embedded Type0 CID fonts.
/// Covers: CMapParser.RegisterPredefinedCMap / LookupPredefined, and
/// PdfType0Font using a registered predefined CMap when /ToUnicode is absent.
/// </summary>
public class CidUnicodeDecodeTests
{
    // ---- helpers ----

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

    // Build a minimal Japan1 Type0 font dict (no ToUnicode, no embedded font)
    private static PdfDictionary BuildJapan1Type0FontDict()
    {
        var csInfo = new PdfDictionary();
        csInfo[new PdfName("Registry")]   = new PdfString("Adobe");
        csInfo[new PdfName("Ordering")]   = new PdfString("Japan1");
        csInfo[new PdfName("Supplement")] = new PdfNumber(5);

        var cidFontDict = new PdfDictionary();
        cidFontDict[new PdfName("Type")]          = new PdfName("Font");
        cidFontDict[new PdfName("Subtype")]       = new PdfName("CIDFontType0");
        cidFontDict[new PdfName("BaseFont")]      = new PdfName("HiraMinPro-W3");
        cidFontDict[new PdfName("CIDSystemInfo")] = csInfo;
        cidFontDict[new PdfName("DW")]            = new PdfNumber(1000);

        var descendants = new PdfArray();
        descendants.Add(cidFontDict);

        var fontDict = new PdfDictionary();
        fontDict[new PdfName("Type")]            = new PdfName("Font");
        fontDict[new PdfName("Subtype")]         = new PdfName("Type0");
        fontDict[new PdfName("BaseFont")]        = new PdfName("HiraMinPro-W3-Identity-V");
        fontDict[new PdfName("Encoding")]        = new PdfName("Identity-V");
        fontDict[new PdfName("DescendantFonts")] = descendants;
        return fontDict;
    }

    // Minimal CMap bytes that map CID 0x42 → U+4E2D (中)
    private static byte[] BuildMinimalJapan1CMap()
    {
        string cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CMapName /Adobe-Japan1-UCS2 def\n" +
            "/CMapType 1 def\n" +
            "1 begincodespacerange\n" +
            "<0000> <FFFF>\n" +
            "endcodespacerange\n" +
            "1 beginbfchar\n" +
            "<0042> <4E2D>\n" +    // CID 0x42 → U+4E2D (中)
            "endbfchar\n" +
            "endcmap\n" +
            "CMapName currentdict /CMap defineresource pop\n" +
            "end\nend\n";
        return Encoding.ASCII.GetBytes(cmap);
    }

    // Build a minimal CMap in the Adobe CMap resource style (begincidchar, Unicode→CID direction)
    // mirroring the real UniJIS-UTF16-H format so the reverse parsing is exercised.
    private static byte[] BuildUniJisStyleCMap()
    {
        string cmap =
            "%!PS-Adobe-3.0 Resource-CMap\n" +
            "%%BeginResource: CMap (UniJIS-UTF16-H-TEST)\n" +
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CIDSystemInfo 3 dict dup begin\n" +
            "  /Registry (Adobe) def\n" +
            "  /Ordering (Japan1) def\n" +
            "  /Supplement 7 def\n" +
            "end def\n" +
            "/CMapName /UniJIS-UTF16-H-TEST def\n" +
            "/CMapType 1 def\n" +
            "/WMode 0 def\n" +
            "3 begincodespacerange\n" +
            "<0000> <FFFF>\n" +
            "endcodespacerange\n" +
            // begincidchar: <unicode> cid  (Unicode → CID direction)
            "3 begincidchar\n" +
            "<4E2D> 1000\n" +   // U+4E2D (中) → CID 1000
            "<65E5> 1001\n" +   // U+65E5 (日) → CID 1001
            "<672C> 1002\n" +   // U+672C (本) → CID 1002
            "endcidchar\n" +
            "endcmap\n" +
            "CMapName currentdict /CMap defineresource pop\n" +
            "end\nend\n";
        return Encoding.ASCII.GetBytes(cmap);
    }

    // Build a minimal CMap with begincidrange (Unicode range → CID range direction)
    private static byte[] BuildCidRangeCMap()
    {
        string cmap =
            "%!PS-Adobe-3.0 Resource-CMap\n" +
            "/CIDInit /ProcSet findresource begin\n" +
            "12 dict begin\n" +
            "begincmap\n" +
            "/CMapType 1 def\n" +
            "1 begincodespacerange\n" +
            "<0000> <FFFF>\n" +
            "endcodespacerange\n" +
            // begincidrange: <u_start> <u_end> cid_start
            "1 begincidrange\n" +
            "<4E00> <4E04> 2000\n" +  // U+4E00-U+4E04 → CIDs 2000-2004
            "endcidrange\n" +
            "endcmap\n" +
            "end\nend\n";
        return Encoding.ASCII.GetBytes(cmap);
    }

    // ---- CMapParser.LookupPredefined ----

    [Fact]
    public void LookupPredefined_ReturnsNull_WhenNotRegistered()
    {
        // Use a name that is unlikely to have been registered by other tests
        Assert.Null(CMapParser.LookupPredefined("Adobe-Japan1-UCS2-NOTREGISTERED", 0x42));
    }

    [Fact]
    public void LookupPredefined_ReturnsMappedUnicode_AfterRegister()
    {
        CMapParser.RegisterPredefinedCMap("Adobe-Japan1-UCS2-TEST1", BuildMinimalJapan1CMap());
        string result = CMapParser.LookupPredefined("Adobe-Japan1-UCS2-TEST1", 0x42);
        Assert.Equal("中", result);
    }

    [Fact]
    public void LookupPredefined_ReturnsNull_ForUnmappedCid()
    {
        CMapParser.RegisterPredefinedCMap("Adobe-Japan1-UCS2-TEST2", BuildMinimalJapan1CMap());
        // CID 0x9999 not in our minimal CMap
        Assert.Null(CMapParser.LookupPredefined("Adobe-Japan1-UCS2-TEST2", 0x9999));
    }

    [Fact]
    public void IsPredefinedCMapRegistered_False_WhenNotRegistered()
    {
        Assert.False(CMapParser.IsPredefinedCMapRegistered("Adobe-Japan1-UCS2-NOTREGISTEREDCHECK"));
    }

    [Fact]
    public void IsPredefinedCMapRegistered_True_AfterRegister()
    {
        CMapParser.RegisterPredefinedCMap("Adobe-Japan1-UCS2-CHECKTEST", BuildMinimalJapan1CMap());
        Assert.True(CMapParser.IsPredefinedCMapRegistered("Adobe-Japan1-UCS2-CHECKTEST"));
    }

    [Fact]
    public void LookupPredefined_ReversesBeginCidChar_UniJisStyle()
    {
        CMapParser.RegisterPredefinedCMap("UniJIS-UTF16-H-TEST", BuildUniJisStyleCMap());

        // The file maps U+4E2D → CID 1000 (Unicode→CID direction).
        // After reverse parsing we should get CID 1000 → U+4E2D (中).
        string result = CMapParser.LookupPredefined("UniJIS-UTF16-H-TEST", 1000);
        Assert.Equal("中", result);   // U+4E2D

        result = CMapParser.LookupPredefined("UniJIS-UTF16-H-TEST", 1001);
        Assert.Equal("日", result);   // U+65E5

        result = CMapParser.LookupPredefined("UniJIS-UTF16-H-TEST", 1002);
        Assert.Equal("本", result);   // U+672C
    }

    [Fact]
    public void LookupPredefined_ReversesBeginCidRange()
    {
        CMapParser.RegisterPredefinedCMap("CidRangeTest", BuildCidRangeCMap());

        // Range: U+4E00-U+4E04 → CIDs 2000-2004 → reverse: CIDs 2000-2004 → U+4E00-U+4E04
        Assert.Equal("一", CMapParser.LookupPredefined("CidRangeTest", 2000));  // U+4E00
        Assert.Equal("丁", CMapParser.LookupPredefined("CidRangeTest", 2001)); // U+4E01
        Assert.Equal("丄", CMapParser.LookupPredefined("CidRangeTest", 2004)); // U+4E04
        Assert.Null(CMapParser.LookupPredefined("CidRangeTest", 2005));  // beyond range
    }

    // ---- PdfType0Font with predefined CMap ----

    [Fact]
    public void PdfType0Font_UsesRegisteredPredefinedCMap_WhenNoToUnicode()
    {
        // Register a minimal Japan1 CMap with a single entry: CID 0x42 → U+4E2D
        CMapParser.RegisterPredefinedCMap("Adobe-Japan1-UCS2", BuildMinimalJapan1CMap());
        try
        {
            using var doc = BuildMinimalDoc();
            var fontDict = BuildJapan1Type0FontDict();
            var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

            // Decode the 2-byte CID 0x0042
            string result = font.Decode(new byte[] { 0x00, 0x42 });
            Assert.Equal("中", result);
        }
        finally
        {
            // Clean up — re-register empty to reset (RegisterPredefinedCMap overwrites)
            CMapParser.RegisterPredefinedCMap("Adobe-Japan1-UCS2", System.Array.Empty<byte>());
        }
    }

    [Fact]
    public void PdfType0Font_UsesAliasName_UniJisUtf16H()
    {
        // Register under the actual file name from the Adobe CMap resources repo,
        // not the traditional "Adobe-Japan1-UCS2" key.
        // The begincidchar entries map U+4E2D → CID 1000, so CID 1000 should decode to "中".
        CMapParser.RegisterPredefinedCMap("UniJIS-UTF16-H", BuildUniJisStyleCMap());
        try
        {
            using var doc = BuildMinimalDoc();
            var fontDict = BuildJapan1Type0FontDict();
            var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

            string result = font.Decode(new byte[] { 0x03, 0xE8 }); // CID 1000 = 0x03E8
            Assert.Equal("中", result);
        }
        finally
        {
            CMapParser.RegisterPredefinedCMap("UniJIS-UTF16-H", System.Array.Empty<byte>());
        }
    }

    [Fact]
    public void PdfType0Font_FallsBackToIdentity_WhenNoPredefinedCMap_AndNoToUnicode()
    {
        // Ensure Adobe-Japan1-UCS2 is not registered for this test by using a font
        // with a different (non-standard) ordering so it skips predefined lookup.
        using var doc = BuildMinimalDoc();

        var csInfo = new PdfDictionary();
        csInfo[new PdfName("Registry")]   = new PdfString("Custom");
        csInfo[new PdfName("Ordering")]   = new PdfString("CustomOrdering");
        csInfo[new PdfName("Supplement")] = new PdfNumber(0);

        var cidFontDict = new PdfDictionary();
        cidFontDict[new PdfName("Type")]          = new PdfName("Font");
        cidFontDict[new PdfName("Subtype")]       = new PdfName("CIDFontType0");
        cidFontDict[new PdfName("BaseFont")]      = new PdfName("CustomFont");
        cidFontDict[new PdfName("CIDSystemInfo")] = csInfo;

        var descendants = new PdfArray();
        descendants.Add(cidFontDict);

        var fontDict = new PdfDictionary();
        fontDict[new PdfName("Type")]            = new PdfName("Font");
        fontDict[new PdfName("Subtype")]         = new PdfName("Type0");
        fontDict[new PdfName("BaseFont")]        = new PdfName("CustomFont");
        fontDict[new PdfName("Encoding")]        = new PdfName("Identity-H");
        fontDict[new PdfName("DescendantFonts")] = descendants;

        var font = new PdfType0Font(fontDict, doc, toUnicode: null, cmapMaxCodeBytes: 2);

        // For a non-standard ordering with no predefined CMap, falls back to (char)code.
        // CID 0x0041 = 65 → identity decode → 'A'
        string result = font.Decode(new byte[] { 0x00, 0x41 });
        Assert.Equal("A", result);
    }
}
